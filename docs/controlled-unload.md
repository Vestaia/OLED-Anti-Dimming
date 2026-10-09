# Controlled filter unloading

Disable now removes the detours and unloads `oled-apl-hook.dll`, including its
model, pristine frame cache, D3D resources, and MinHook trampolines. Reapplying
loads a fresh filter DLL; ordinary filter updates and display changes no longer
require signing out after this unload-capable version has been installed.

A small `oled-hook-bridge.dll` remains pinned until DWM exits. It has no graphics
resources or calibration model. Its Present and DirectFlip wrappers and the
register-preserving overlay thunk are the actual detour destinations. Callback
admission uses an SRW lock: choosing the filter target and incrementing the active
count happen before releasing the shared admission lock. The count is decremented
in bridge code only after the filter callback returns completely. This avoids
unmapping a DLL while a callback epilogue is still executing inside it.

The GUI calls `OledPrepareUnload` on a remote control thread. The bridge holds
exclusive admission while MinHook restores the original function entries, then
clears the filter targets and reopens admission. Late arrivals into bridge code
call the restored original entries, never a freed trampoline. Teardown waits for
the active filter calls to finish, removes MinHook resources, and releases GPU
objects outside DllMain. The GUI waits for the control thread to return before
calling FreeLibrary, then verifies that the filter module is absent. Desktop
invalidation removes previously corrected pixels.

A failed hook removal or five-second drain timeout retains the filter DLL and
reports failure. Activation is refused once teardown has begun. Older pinned
hooks still use their disable-only export; installing this architecture requires
one final sign-out if such a hook is resident. Updating the pinned bridge itself
also requires sign-out. Exit leaves an enabled filter running; Disable filter and
exit uses the same controlled unload path.

The bridge is embedded in the compressed single executable along with the filter.
The isolated stress test exercises 50 cycles with eight concurrent callers,
checks failed-disable retention, verifies that the callback payload is unmapped,
and calls the bridge after unloading to check restored-entry forwarding. Overlay
register-preservation tests remain mandatory. These checks do not inject into DWM;
live apply/disable/video testing remains a separate hardware check.

Bridge rebuilds normalize PE header, export-directory, and debug-directory
linker timestamps. The GUI compares resident bridge identity with those fields
normalized, so older equivalent bridge builds are reusable without sign-out.
All other bytes, including code and export contents, must match. Separate builds
are checked for byte-identical normalized bridge output.
