# Short slope-guided hue and saturation experiment

This is a general sampling procedure driven by a monitor's measured primary curves, not hardcoded onset percentages. Current defaults are intentionally sparse for iteration and do not establish full-gamut coverage.

Generate a plan from the initial primary-surround CSV:

```powershell
python experiments/plan_hsv.py build/color-observations-168428359.csv build/hsv-plan.csv
./build.ps1
cd build
./hdr-probe.exe --hsv-calibrate hsv-plan.csv
```

The planner estimates camera-response slopes along each primary's area curve. For a candidate HSV color, its linear RGB components weight these slopes to prioritize area samples. These weights are a sampling heuristic, not an additive RGB power assumption. Use 40% and full area for every color, one additional high-slope area, and 15% for the primary/selected-HSV window plots. There is no strict one-minute cutoff. The small finite plan and bounded per-point search keep iteration short; a missing closing reference means that color is incomplete.

Initial palette: six saturated hue anchors, three selected colors at different hues and partial saturations, three half-saturated primaries, and neutral. This gives a sparse hue sweep at saturation 1, saturation sweeps at three hues using neutral/half/full saturation, and measurements across several area fractions. Hue is defined in linear BT.2020 RGB here; these are signal coordinates, not perceptual equal-luminance colors.

Unlike the earlier fixed-white-probe color-load experiment, the center probe now has the tested hue/saturation. Its geometry and camera ROI remain fixed. For each color, establish its own low-load reference with an appropriate fixed exposure. Hold hue, saturation, and exposure constant during that color's area sweep. Render the same color in the surround, then physically search for a signal level matching the center reference. Camera-code ratios are never treated as luminance ratios. Scaling the complete window changes load, and the feedback loop measures that corrected scene.

Each requested area produces a raw observation, a camera-match search, and a separate three-frame remeasurement of the corrected window. The closing reference flags drift. Reference transitions receive an additional 200 ms beyond the measured timing gate; other measurements use the adaptive stabilization gate and three fresh frames. Timing measured with white does not guarantee identical transitions at every hue and exposure; these short tests expose where further characterization is needed.

Plot the recorded CSV with:

```powershell
python experiments/plot_hsv.py build/hsv-observations-<id>.csv --output-dir reports/hsv-<id>
```

Outputs: measured primary/selected-HSV raw-versus-corrected window graphs, a 40%-window hue graph, three 40%-window saturation graphs, validation summary, and a sparse signal-correction model. Each curve uses its own camera reference/exposure; hue/saturation plots show camera-code departure from that reference, not equal-luminance comparisons between colors. Connected lines are guides between samples, not densely validated responses.

`experiments/hsv_model.py` exposes `HSVModel.signal(hue, saturation, area)`. It interpolates log signal over each measured area's knots, then tentatively interpolates between neighboring color anchors in linear RGB direction. Only completed anchors passing reference-drift and remeasured-error checks enter the model. Interior hue/saturation interpolation and other brightness slices remain unvalidated. Keep raw observations and reject weak anchors rather than claiming all-color uniformity from a short sparse pass.


## Agnostic surface experiment

The next model fits log(input signal / 100) as a function of window fraction, periodic hue, and saturation. It does not estimate RGBW conversion or additive panel power. Each target is obtained by observation matching against the same color's 1% reference. Camera codes remain ordering/equality observations, never photometric ratios.

Two candidates are compared by four-fold whole-color holdout: a regularized tensor cubic B-spline (window, saturation, periodic hue) and a smoothing thin-plate radial-basis surface in (window, S cos H, S sin H, S). The spline folds hue coefficients periodically and blends hue weights to uniform at zero saturation. Both enforce zero correction at the 1% baseline. Smoothness is selected using held-out groups rather than training residual. No monotonic constraint is imposed on the correction.

`plan_dense.py` selects 12 hues at three nonzero saturations plus neutral. All groups include 40% and full area; one additional area is prioritized using disagreement between candidate fits, slope, and curvature of the preliminary empirical surface. Neutral and selected saturated colors also include 15%. This is a coverage-first weighted-density prototype, not a fully adaptive online sampler. Matches warm-start from the previous fitted estimate, but feedback determines the measured result. The camera-code match floor is tightened from 1 to 0.35. An explicit exposure-settling delay fixes a previously overwritten presentation timestamp adjustment.

`plan_validation.py` tests unseen hue/saturation combinations and green/blue window sizes, applying predicted signals without feedback. Closing references reject drifted groups. `heatmap_surface.py` makes separately labeled interpolated correction maps and measured raw-versus-predicted error maps, sharing error scales. Camera-code errors across exposures are diagnostics, not perceptual error units.

Commands:

```powershell
python experiments/plan_dense.py
# Run hdr-probe.exe --hsv-calibrate dense-plan.csv from build
python experiments/agnostic_surface.py build/hsv-observations-ID.csv reports/surface-ID/model.json
python experiments/plan_validation.py reports/surface-ID/model.json build/validation-plan.csv
# Run hdr-probe.exe --hsv-calibrate validation-plan.csv from build
python experiments/heatmap_surface.py reports/surface-ID/model.json build/hsv-observations-VALIDATION.csv reports/surface-ID
```

The serialized model and `Surface.signal(hue, saturation, area)` are an experimental callable for the generator. Coverage is one nominal 100 signal slice, uniform colored windows, and the current monitor mode. Mixed-color scene distributions require their own variables and held-out tests; a uniform HSV surface alone does not identify that behavior.

Agnostic surface result: see ../reports/surface-170952609/README.md. Selected the circular hue/saturation RBF with smoothing 0.01 and a nominal-signal lower bound. The unconstrained smooth fits rang below nominal in a flat region; the boost-only guard avoids that artifact. A targeted refinement pass drifted and was excluded. Both candidate families remain viable: guarded group-heldout error is 0.0334 RBF versus 0.0352 spline, a modest difference. Guarded optical validation uses 55 directly measured predictions and 17 explicitly marked exact nominal-signal observations already measured in the same groups. Raw/predicted camera-code RMS is 6.155/0.560; these are not luminance units.
