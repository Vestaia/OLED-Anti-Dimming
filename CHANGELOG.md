# Changelog

## 0.3.0 (beta)

- Added Quality (~60,000 cells), Balanced (~25,000), Performance (~10,000), and Custom screen sampling densities, with aspect-aware grids.
- Calibration now counts exact generated pattern distributions, including the full reference mosaic, instead of subsampling known patterns.
- New calibrations use normalized inverse-distance interpolation of measured corrections, avoiding the Gaussian RBF fallback toward unity in sparse regions.
- Existing models remain usable with their original interpolation. A new calibration is required to adopt exact pattern statistics and the new interpolation.

Sign out once before applying this build if a previous hook is already loaded.

## 0.2.0 (beta)

- Added an original monitor icon for the executable, application window, and system tray.
- Added optional startup at Windows sign-in: automatically apply the saved calibration and remain in the tray.
- Closing the window now hides it in the tray. Tray actions support Exit and Disable filter and exit.
- Read actual filter status from DWM instead of assuming the filter is disabled on launch.
- Replaced the validation heatmap with Calibration quality, showing RMS and maximum relative camera brightness error (%).

When upgrading from a version with a resident hook, sign out once before applying the new filter. Existing calibration data is retained.

## 0.1.0 (beta)

- Initial beta: automatic webcam calibration, histogram PCA content analysis, adaptive sampling, and system-wide OLED dimming compensation.
