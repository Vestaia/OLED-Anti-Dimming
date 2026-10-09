Current builds use [controlled filter unloading](controlled-unload.md). The
resident-toggle discussion below describes the earlier architecture and crash
investigation; only the admission bridge remains pinned in the current design.

# Enable/disable stability fix

## Root-cause investigation: private leaf register contract

Matching symbols and machine code for dwmcore.dll 10.0.26100.9278 show that
OverlaysEnabled only changes AL and flags. Its internal callers retain pointers
in registers normally volatile under the public Windows x64 ABI. Replacing it
with a normal C++ function (including mutex and map calls) breaks that contract.

- Crash RVA 0x266b3d is `cmp dword ptr [rdx+34h],2` in
  IsOverlayCompatibleScale, after its call to OverlaysEnabled. RDX is not reloaded.
- Crash RVA 0x22d661 is `cmp dword ptr [r9+4C8Ch],ecx` in
  InitCheckCandidatesList, after IsDFlipOnMPO calls OverlaysEnabled. R9 is not
  reloaded. IsDFlipOnMPO also retains its own plane pointer in R8.
- An isolated process maps the exact DWM DLL without initializing it, installs
  a public-ABI clobbering replacement, and reproduces an access violation in
  the actual IsDFlipOnMPO caller. It passes with the preserving wrapper.

native/overlay_hook.asm now preserves RAX upper bits, RCX/RDX/R8-R11 and
XMM0-XMM5, returning only the replacement boolean in AL. It provides x64 unwind
metadata and aligned shadow space. The regression also passed 100 concurrent
MinHook disable/enable cycles with over 226 million register probes. These tests
operate on a separate process, never inject into the live compositor.

Archived WER reports confirm both fault offsets but contain no retained dump.
This establishes a concrete hook defect and reproduces its mechanism; desktop
and Cyberpunk validation of the final enabled filter remains pending.

The first resident-toggle build did not fix this contract. The subsequent build
also fixes context-state creation: mirror D3D11's SINGLETHREADED flag and cap the
requested context-state feature level at 11_1. A single-threaded device regression
passes. Disable now calls MH_DisableHook for all detours without freeing their
trampolines or the pinned DLL.

Evidence: build/crash-evidence/overlay-disassembly.txt; regression source
tests/native/overlay_hook_test.cpp and tests/native/overlay_hook_test.asm. The regression
requires build/dwm-symbols.txt from hook-probe --inspect and a matching
hook-addresses.bin, and links the existing MinHook library plus overlay_hook.asm.

Microsoft documents that whole-program analysis can prove preservation beyond
the normal volatile-register contract:
https://learn.microsoft.com/en-us/cpp/build/x64-calling-convention?view=msvc-170

## Earlier defensive changes

Observed crash: dwm.exe / dwmcore.dll 10.0.26100.9278, exception 0xc0000005,
offset 0x266b3d while toggling during Cyberpunk performance testing. No usable
crash dump was found, so the exact crashing instruction's cause is unconfirmed.

The previous implementation remotely called FreeLibrary, followed by MinHook
teardown and COM releases under DllMain/loader lock. It also leaked PS b1/t2-t5,
render-target, vertex/raster and other pipeline state into DWM. Both are concrete
hazards addressed here; this is not a claim that either was proven as the cause.

- Hook DLL is pinned for the DWM process lifetime. Disable invokes the exported
  control function to disable detours, never unloads executing callback code.
- Callback mutable state and control commands use a recursive mutex. Disable
  returns after active filter code leaves the critical section; original DWM
  calls run outside it. DirectFlip/overlay decisions honor the bypass flag.
- Re-enable requests model/frame-cache rebuilding on the next compositor render
  callback. Remote control threads never modify D3D objects.
- D3D11.1 context state swapping isolates the entire filter pipeline and restores
  DWM state before its original Present. A GPU regression checks b1/t2, viewport
  and topology restoration. Device changes discard stale filter resources.
- DllMain no longer sleeps, tears down hooks or releases graphics objects during
  process termination. Windows reclaims the pinned module with the process.

Disable still forces desktop invalidation to remove cached correction. Re-enable
also invalidates and recreates the pristine scene cache. The pinned module holds
some bounded resources while bypassed. Updating the hook binary or changing its
bound display requires signing out and back in. Older loaded hooks lacking the
control export are refused, rather than unloaded unsafely. No DWM restart, signout,
hook injection or game test is performed automatically by this fix.

API references:
- https://learn.microsoft.com/en-us/windows/win32/api/d3d11_1/nf-d3d11_1-id3d11devicecontext1-swapdevicecontextstate
- https://learn.microsoft.com/en-us/windows/win32/api/libloaderapi/nf-libloaderapi-getmodulehandleexw
