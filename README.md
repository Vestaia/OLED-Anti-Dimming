# OLED Anti-Dimming

A Windows utility that reduces content-dependent OLED dimming through webcam calibration and a GPU display filter. Currently in beta.

![OLED Anti-Dimming application and correction summary](assets/documentation/application.png)

## Tutorial

![Example camera placement during calibration](assets/documentation/camera-setup.png)

1. Enable HDR or Advanced Color management in Windows as appropriate for your display.
2. Place the camera close to the center of the screen. The center reference patch should fill the camera's view.
3. Select your camera and display, enter the panel peak brightness (default 1,000 nits), then click **New calibration**.
4. Review the results, then click **Apply system wide**.
5. Optionally enable **Start with Windows** to apply the filter at sign-in and stay in the system tray.

Calibration data is stored in `%LOCALAPPDATA%\OledCalibration\data`.

## Existing features

- **APL-dependent dimming compensation** with uniform linear brightness correction.
- **PCA screen-content analysis** that captures distributions of colors and brightnesses.
- **System-wide filtering** on the selected display through DWM.
- **Automatic webcam calibration** after camera setup; no colorimeter required.
- **Adaptive sampling and refinement** focused on dimming regions and validation errors.
- **Single executable**, with no Python runtime dependency.
- **System tray and startup support:** closing the window keeps the app in the tray.

## Planned features

- **Custom EOTF targets:** currently PQ; planned custom roll-offs and advanced headroom-aware tone mapping, including SMPTE ST 2094-50 as used by Apple's [Headroom Adaptive Gain Curve](https://developer.apple.com/documentation/colorsync/headroom-adaptive-gain-curve).
- **Faster calibration** through improved sampling and convergence.

## Build from source

Requires Windows x64, Visual Studio C++ build tools, the Windows SDK with Debugging Tools, .NET 10 SDK, and Python 3 for build-time source generation.

```powershell
./build-single.ps1 -OutputDirectory build/single
```

Launch `build/single/OledCalibration.exe`. Current hook support targets Windows 11 24H2 and later with matching Microsoft symbols.

## Design

- **DWM hooks:** intercept composed frames using a hook derived from [dwm_lut](https://github.com/ledoge/dwm_lut), preserving graphics state and an uncorrected frame cache.
- **Display subsampling:** analyze an aspect-aware grid: Quality ~60,000 cells, Balanced ~25,000, Performance ~10,000, or Custom.
- **3D color histogram:** map linear BT.2020 samples into a soft 8 ? 8 ? 8 RGB histogram, retaining mixed-color distributions.
- **PCA generation:** learn 14 components from representative and synthetic histograms, then freeze the basis during calibration.
- **Adaptive gamut sampling:** begin with vertices, edge and face centers, grayscale points, spaced interior colors, and clustered mixtures; refine poorly covered or inaccurate regions.
- **Correction model:** fit measured relative brightness gains and apply one scene-dependent multiplier in linear light.

![Example measured gamut-space sampling](assets/documentation/gamut-sampling.png)

*Example color distributions from a completed calibration, viewed from two angles.*

See the [full filter design](docs/filter-design.md), [PCA model](docs/histogram-pca.md), and [adaptive sampling](docs/gamut-sampling.md) for implementation details and limitations.

## Development

Run `./test.ps1` for offline regression checks. See [tests](tests/README.md) and [hook stability](docs/hook-stability.md). Live hardware compatibility and performance validation remain ongoing.

## License

[GPLv3 only](LICENSE) (`GPL-3.0-only`). Third-party components retain their original licenses; see [third-party notices](THIRD_PARTY_NOTICES.md).
