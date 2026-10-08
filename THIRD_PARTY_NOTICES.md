# Third-party components

The DWM hook is a derivative of [ledoge/dwm_lut](https://github.com/ledoge/dwm_lut), using [lauralex's win24h2 fork](https://github.com/lauralex/dwm_lut/tree/win24h2), commit `f5268247e8d782db5d83e872ea97d34a3dd00f10`. Its GNU GPL version 3 license is in `external/dwm_lut/LICENSE`. The build-local modifications are reproducibly defined in `experiments/patch_hook.py`, and native filter source is supplied in `native/`.

[MinHook](https://github.com/TsudaKageyu/minhook) uses a BSD-style license, supplied in `external/minhook/LICENSE.txt`.

[Little CMS](https://github.com/mm2/Little-CMS), commit `15c24e7ded91184c38c46c096804fa7272765737`, uses the MIT license, supplied in `external/Little-CMS/LICENSE`.

Windows SDK debugger binaries are used from the installed Windows SDK for local symbol resolution. They are not part of the source project or a redistribution package.
