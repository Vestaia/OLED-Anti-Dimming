# Native HDR and webcam test surface

Build: run `./build.ps1` from PowerShell. Run `build/hdr-probe.exe` with its working directory set to `build`. Requires the Visual Studio C++ tools and Windows SDK. No additional Python packages required.

This is an experimental stimulus/observation instrument. It does not yet fit q/F or apply desktop correction. The four-color and mixed-color patterns support manual exploration before implementing the adaptive scheduler. Camera frames stay in memory; only explicit numeric snapshots and device diagnostics are written locally.

## Actual rendering path

D3D11 flip-discard swap chain, R10G10B10A2_UNORM, explicitly tagged DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020. The fullscreen borderless surface uses the HDR-enabled XG27AQWMG when present. It refuses to substitute another display if that OLED has HDR disabled. If that model is absent, it uses the first HDR-enabled output.

RGB values are linear BT.2020 component intensities before shader ST.2084 encoding. The encoded range reaches 0..10000 signal nits; neither those values nor the display's metadata establish measured output. The monitor's actual gamut, brightness, Windows color transforms, and tone mapping constrain what appears on screen. No absolute luminance or full-PQ-range webcam measurement is claimed.

Startup checks the HDR color-space support and reads back GPU pixels: the initial 1000-signal-nit white patch must match the CPU PQ calculation within one 10-bit code and the surrounding black must be zero. This validates shader encoding/backbuffer contents, not optical output or final wire values.

## Controls

Focus either app window to use the keyboard:

| Key | Action |
| --- | --- |
| R / G / B / W | Constant primary or white patch |
| P | Neutral PQ code ramp from 0 to 1, left to right |
| H | BT.2020 hue horizontally, saturation vertically, at selected signal peak |
| C | Two-color checkerboard inside selected illuminated area |
| Up / Down | Change peak signal by 0.1 decade |
| Left / Right | Change illuminated area by 1 percentage point |
| Page Up / Down | Change area by 10 percentage points |
| J / K | Cycle hue / saturation for custom patch/checker colors |
| 0 / 1 / 2 / 3 / 4 / 5 / 6 / 7 / 8 | Signal 0 / .0001 / 1 / 10 / 100 / 400 / 1000 / 4000 / 10000 |
| [ / ] | Shorter / longer manual exposure |
| A | Exposure search against 10000-signal-nit R/G/B/W patches |
| S | Append current numeric ROI snapshot to samples.csv |
| Escape / close | Exit and restore original exposure, white balance, gain |

Area is a fraction of display pixels: the rectangle scales each dimension by sqrt(area). At 1%, it is 10% of screen width and height. Native-resolution rendering does not imply physical subpixel isolation. Hue/saturation mode and the PQ ramp fill the display; area applies to patch/checker modes.

## Camera setup

The local Brio 100 reports exposure -11..-2 in log2 seconds. Initial manual exposure is -7 (1/128 s), white balance 6500 K, and gain at the supported minimum. Successful Set calls and readback flags are logged. These settings are a starting point, not a validated calibration. Other internal webcam processing may still vary.

Point the camera at the OLED. Drag a small ROI inside the central patch in the preview. Leave the camera and ROI fixed. Press A to evaluate bright R/G/B/W patterns and select a fixed exposure. The search uses the maximum of each ROI's 99th-percentile channel values, shortens exposure above 235/255, and lengthens below 110/255. It stops at a bounded iteration count or device limit; inspect the displayed clipping fraction and log rather than assuming success. Each color waits 700 ms; this provisional settling time must be checked during calibration.

The search does not automatically locate the screen or verify that the ROI is inside the patch. Only start it after positioning. Test the darkest useful midtone afterward. One 8-bit webcam exposure cannot be assumed to resolve near-black and peak HDR simultaneously. Use separate fixed-exposure sessions with overlapping references if the measured range demands them; the current app provides manual exposure control but does not merge sessions.

The preview reads buffers at approximately 10 Hz, without establishing camera-frame independence or exposure/presentation synchronization. CSV snapshots use host sampling time, not camera exposure timestamps, and can include unsettled frames. These limits must be resolved before quantitative q/F fitting. The preview is positioned on a different monitor where available; moving it over the test display changes scene load.

## Implementation references

- [Microsoft HDR application guidance](https://learn.microsoft.com/en-us/windows/win32/direct3darticles/high-dynamic-range)
- [SetColorSpace1](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/nf-dxgi1_4-idxgiswapchain3-setcolorspace1)
- [Camera controls and exposure units](https://learn.microsoft.com/en-us/windows/win32/api/strmif/ne-strmif-cameracontrolproperty)
- [Sample Grabber](https://learn.microsoft.com/en-us/windows/win32/directshow/using-the-sample-grabber)

DirectShow is used here for quick access to the actual driver's controls and a local preview. Microsoft recommends Media Foundation for new applications; replace the legacy capture path when finalizing the measurement pipeline.
