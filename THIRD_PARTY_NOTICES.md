# Third-party components

The DWM hook is a derivative of [ledoge/dwm_lut](https://github.com/ledoge/dwm_lut), using [lauralex's win24h2 fork](https://github.com/lauralex/dwm_lut/tree/win24h2), commit `f5268247e8d782db5d83e872ea97d34a3dd00f10`. Its GNU GPL version 3 license is in `external/dwm_lut/LICENSE`. The build-local modifications are reproducibly defined in `tools/generate_hook.py`, and native filter source is supplied in `native/`.

[MinHook](https://github.com/TsudaKageyu/minhook) uses a BSD-style license, supplied in `external/minhook/LICENSE.txt`.

The standalone executable includes the .NET 10 runtime and Windows Desktop
runtime under their MIT licenses. Their license texts and runtime third-party
notices are supplied in `assets/licenses/` and embedded in the executable payload.

Windows SDK debugger binaries are obtained from the installed Windows SDK at
build time for local symbol resolution. The source repository does not contain
them; the standalone executable payload includes the debugger helper DLLs used
by the symbol resolver.
