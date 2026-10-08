# OLED Anti-Dimming

A Windows calibration utility that reduces content-dependent OLED dimming using relative webcam measurements and a GPU display filter. Initial beta: calibration and filtering are implemented; compatibility and performance are still being tested.

## Existing features

- **APL-dependent dimming compensation.** Counters brightness changes caused by average picture level and the scene's color distribution. Applies a uniform multiplier in linear color space to preserve channel ratios.
- **PCA analysis of screen-space content.** Samples pixels into a joint three-dimensional RGB histogram, then uses principal component analysis to describe the distribution for the learned dimming model. Colored mixtures retain distribution information rather than being reduced to their average color alone.
- **System-wide application.** Corrects the selected display through a Desktop Window Manager hook derived from [dwm_lut](https://github.com/ledoge/dwm_lut). Content that bypasses DWM composition is outside the filter's coverage.
- **Fully automatic calibration after setup.** Select a display and webcam, position the camera, and start calibration. The utility generates patterns, measures latency and settling, matches relative brightness, fits the model, and validates the result. No colorimeter or Python installation is required for the application. Exposure must remain locked; cameras without working API exposure control require manual setup.
- **Adaptive sampling algorithms.** Coarse measurements identify dimming regions; additional patterns and refinement samples target poorly covered regions and validation errors. Flat regions are skipped to reduce measurement time.

## Planned features

- **Custom EOTF targets.** Currently targets PQ (SMPTE ST 2084). Planned options include custom highlight roll-offs and advanced, headroom-aware tone mapping, including investigation of SMPTE ST 2094-50. Apple's [Headroom Adaptive Gain Curve](https://developer.apple.com/documentation/colorsync/headroom-adaptive-gain-curve) uses this metadata standard; its [EDR rendering pipeline](https://developer.apple.com/videos/play/wwdc2021/10161/) is a related reference for adapting content to available display headroom. These tone-mapping options are not implemented yet.
- **Improve calibration speed.** Reduce measurement and convergence time while retaining sufficient coverage of color and brightness distributions.

## Using the application

1. Enable HDR or Advanced Color management in Windows as appropriate for your display.
2. Place the camera as close as possible to the center of the screen. The center reference patch should fill the camera's view.
3. Select the camera and display, set the panel peak-brightness sampling limit (default 1,000 nits), then create a new calibration.
4. Review the correction and validation summaries, then choose **Apply system wide**.

Windows requests administrator access for the application. Windows manages ICC profiles and VCGT calibration; this utility does not apply them separately. Calibration data persists under `%LOCALAPPDATA%\OledCalibration\data`, independently of executable versions. Packaged helpers are extracted automatically on first launch.

The sampling limit bounds calibration stimuli; it does not clamp output. Compensation is limited by the monitor's available brightness headroom. Relative webcam matching cannot establish absolute luminance or accurate colorimetry, and the display's physical full-screen brightness limit remains unavoidable.

## Build from source

Requires Windows x64, Visual Studio C++ build tools, the Windows SDK (including Debugging Tools for Windows), the .NET 10 SDK, and Python 3 for build-time hook source generation. Python is not a runtime application dependency.

```powershell
./build-single.ps1 -OutputDirectory build/single
```

Launch `build/single/OledCalibration.exe`. The compressed, self-contained package contains the .NET runtime and native helpers in a single distributable executable. Build paths currently assume standard Visual Studio and Windows SDK locations.

Private DWM hooks are tied to the installed Windows build. Current support targets Windows 11 24H2 and later with matching Microsoft symbols; older Windows support is deferred. Updating an already loaded hook requires signing out and back in. The latest overlay-register fix has passed isolated regression tests, but live desktop/game validation remains pending. See [hook stability](docs/hook-stability.md).

## Development notes

- [PCA histogram model](docs/histogram-pca.md)
- [Calibration design and experiments](docs/fast-calibration.md)
- [Research record](docs/research.md)
- [Third-party components and licenses](THIRD_PARTY_NOTICES.md)

Earlier experimental documents and reports describe previous models and sampling strategies. The current application uses the PCA histogram model exclusively. Third-party sources are vendored with their original licenses. The DWM hook is a GPLv3 derivative; see its license and the third-party notices before redistribution.
