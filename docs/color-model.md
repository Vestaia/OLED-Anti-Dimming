# Color-load prototype

Run `build/hdr-probe.exe --color-calibrate` from the build directory for the white calibration followed by color-load acquisition. Press L in an existing session with a valid center ROI to collect the color data directly. All camera pixels in the center ROI are averaged, then three fresh frames after measured delay plus 50 ms settling. Exposure stays fixed because the measured probe stays white.

The center remains a fixed 1% white patch requested at 100 signal nits. Only the surround changes: red, green, blue, white, cyan, magenta, yellow, each at 100 linear BT.2020 component signal nits. Start with seven area anchors per color, then test interval midpoints and refine nonlinear regions down to approximately 2.5 percentage-point intervals. These observations measure how colors load the panel, not those colors' own emitted luminance or chromaticity.

Acquire eighteen held-out equal-area two-color checkerboards across six pairings and three areas. Include the fixed white center separately from the mixed surround. A final low-load reference checks drift. Numeric observations are retained as `build/color-observations-<id>.csv`. Calibration correction still applies only to the white generator experiment; color-load acquisition does not automatically apply arbitrary-color desktop correction.

## Basic q and F

Fit with `python experiments/panel_model.py build/color-observations-<id>.csv --output build/panel-model-<id>.json`. This standard-library module exposes `PanelModel.q(rgb_signal_nits)`, `F(load)`, and `predict(histogram)`.

Choose the color producing the strongest measured full-area dimming as the reference and normalize its q to 1. Fit a monotone piecewise-linear white-probe response F along that surround sweep. F's load origin includes the constant center probe, so its argument is the surrounding load. For a pure-color window, load = (area - 0.01) * q(color). Fit the other joint-color q anchors by response-curve alignment against this measured F, without camera-to-luminance ratios or extrapolation outside measured response.

Use black/R/G/B/C/M/Y/W cube vertices and tetrahedral interpolation for intermediate RGB values. White and secondary vertices are independently learned; q is not the sum of three channel functions. Interior saturation and brightness interpolation remains a prior needing further calibration. Report near-optimal q ranges, pure-sweep residuals, reference fit residual, and held-out mixture errors. The q compatibility interval is a heuristic loss range, not a statistical confidence interval.

This initial model supports a fixed white probe, fixed camera settings, and 100-component-signal anchors. Inputs beyond the 0..100 component cube are rejected. F returns None beyond measured load coverage. Even within that cube, unmeasured interior colors are tentative predictions. Higher signal levels, multiple probe colors, adaptive hue/saturation data, temperature/history, and spatial effects remain future extensions.

Example with the whole screen lit by an equal red/green checkerboard, excluding the center probe:

```python
import json
from experiments.panel_model import PanelModel

model = PanelModel(json.load(open('build/panel-model-168428359.json')))
camera_code = model.predict([
    ((100, 100, 100), 0.01),  # fixed white center
    ((100, 0, 0), 0.495),
    ((0, 100, 0), 0.495),
])
```

Frames supplied to predict must include that same white probe and nonnegative total-frame fractions summing to one. Predictions are camera codes, not nits, brightness ratios, or measured panel watts. Keep the saved raw color curves when replacing the scalar model with additional scene-state dimensions.

## Initial measured result

Dataset 168428359 contains 49 anchors, 50 adaptive checks, 18 held-out mixtures, and a final reference. Pure-color sweeps showed that saturated surrounds can produce stronger dimming than white; using white as the only measured load reference incorrectly pinned fitted q at the edge of coverage. Choosing red as the normalization reference resolved that coverage problem without changing the raw observations.

The resulting scalar joint-color model predicts the tested checkerboards with RMSE 0.538 camera codes and maximum error 1.480 codes. This provisionally supports the scalar representation for this palette and signal slice; it does not establish general WOLED additivity or validate arbitrary RGB images. Synthetic tests verify load recovery, rejection of unsupported load, and rejection of a deliberately inconsistent mixture.
