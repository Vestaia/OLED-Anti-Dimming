# Changelog

## 0.2.0 (beta)

- Added an original monitor icon for the executable, application window, and system tray.
- Added optional startup at Windows sign-in: automatically apply the saved calibration and remain in the tray.
- Closing the window now hides it in the tray. Tray actions support Exit and Disable filter and exit.
- Read actual filter status from DWM instead of assuming the filter is disabled on launch.
- Replaced the validation heatmap with Calibration quality, showing RMS and maximum relative camera brightness error (%).

When upgrading from a version with a resident hook, sign out once before applying the new filter. Existing calibration data is retained.

## 0.1.0 (beta)

- Initial beta: automatic webcam calibration, histogram PCA content analysis, adaptive sampling, and system-wide OLED dimming compensation.
