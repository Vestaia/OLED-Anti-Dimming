# Adaptive gamut sampling

The current experimental calibration uses the [smoothed HSV model](smoothed-hsv.md).
The details below describe earlier compatible models.


The current experimental calibration uses the [smoothed HSV model](smoothed-hsv.md).
The details below describe earlier compatible models.


Click **New calibration**. New calibrations use the color-cluster model.
The reference square remains 100 nits. Panel peak sets
nominal pattern bounds, not a cap on correction or displayed signals.

## Coarse coverage

Pattern generation uses square-root linear BT.2020 RGB coordinates, scaled to
the selected peak; interior coverage is ranked in brightness-weighted circular HSV. It is not perceptually uniform.

- All eight vertices, twelve edge centers and six face centers.
- Black-to-white diagonal at eight equal coordinate intervals, plus the 100-nit
  reference when distinct.
- Eight additional interior points chosen by deterministic greedy maximin search
  against existing points. This improves spacing without claiming global optimality.
- Twelve weighted checkerboard distributions, each using 2-5 centers with
  independently perturbed tile colors. Spreads are 0.025, 0.075 or 0.15 in cube
  coordinates. Centers include darker and brighter populations with varying hues;
  the tiles do not all have the same brightness.

Initial sweeps use 100%, 70%, 40%, 20%, 10%, and 5% windows in descending
order for solids and mixtures. Once a matched correction is within the existing
1% unity-gain threshold, smaller windows are skipped and retained as inferred
zero-correction anchors. Brightness-level skipping remains enabled. The all-black
surround needs only one area because its distribution is unchanged. Express uses
fewer interior samples and skips mixtures, with the same descending areas.
No initial patterns are set aside for validation.

## Exploration and reinforcement

Refinement candidates and fresh validation patterns are generated on demand: random individual gamut
points and random 2-5-mode clustered distributions with random window areas.
For refinement, a pool of 128 candidates is ranked by model confidence using exact pattern
cluster distributions in brightness-weighted circular HSV coordinates. Confidence is the sum of
inverse squared-distance support from the nearest 12 samples, divided by one
plus a distance-weighted local log-gain slope. Nearby samples raise confidence;
steep correction changes lower it. Lowest-confidence candidates are selected
first. Planned samples provide additional support during batch selection to
avoid concentrating an entire round in one region. This is a sampling heuristic,
not a probabilistic uncertainty estimate.

Six exploratory checks accompany the first validation; four fresh checks
accompany each refinement round. Each round selects up to twelve new training
patterns. Validation checks are generated separately on the spot: RGB coordinates
are uniform in the square-root-weighted gamut cube, with random mixtures and
random window sizes independent of confidence. Refinement and validation have no
15% window floor; the minimum numerical area is one display pixel. Validation matches are retained and the
model is refitted before choosing the next batch. Camera errors do not guide
sample selection or terminate refinement. Histogram comparisons prevent new
training points from duplicating old distributions or their validation checks.
The current model initializes correction trials.

Cluster geometry and brightness weighting remain fixed throughout calibration.
No unlabeled PCA training stage is needed; new measured labels reinforce the
correction model without rotating its coordinate system.

Refinement is bounded by the UI round limit. Camera matching uses four times
the synchronized noise floor. Unconverged searches do not become training
labels, and reference changes greater than 5% produce a GUI-console warning and update the target.
`gamut-status.json` records the confidence strategy; calibration quality reports
pre-update validation errors separately. Training/model checkpoints are retained
after each completed fit.

## Verification

Run `./test.ps1` for offline geometry, histogram, model, camera-controller and GPU
regressions. The initial Git commit preserves previous prototypes for reference.
Physical accuracy and calibration duration require testing on the target monitor.

## Random mosaic probe experiment

New calibrations use a fixed, per-display-pixel RGB mosaic. Adjacent pixel
pairs have complementary random linear RGB values between 0 and 200 nits, giving
exactly 100-nit mean input in each channel before PQ encoding. This is a nominal
signal mean, not measured luminance or an assumption about camera linearity.
The hash pattern remains fixed during references, matches and latency tests.
Matching measures aggregate camera response and cannot guarantee that each color
in the mosaic has equal relative dimming. Color-specific residuals can remain.

The probe is a physical pixel square with an even side length, approximately 1%
of screen area. Width/height and integer raster boundaries are shared by native
rendering and CPU exact distribution generation. Tests cover 16:9, ultrawide and portrait
sizes. Surround window area remains an actual screen-area fraction. References
show only the mosaic on black rather than an aspect-dependent rectangular border.
The square's actual mosaic distribution contributes to calibration histograms.

Existing white-probe calibrations keep their original reference and geometry;
a new calibration is required to try the mosaic. Mosaic refinement refuses a
changed display resolution, since the probe pattern and raster sampling change.
Previous source remains in Git history. Generated probe names include the parent
phase and metadata sequence, preventing repeated `probes` folders from creating
duplicate dictionary keys.

Native session logs explicitly identify mosaic versus solid probes. Existing
PCA reports retain their recorded reference type when refined.


## Uneven brightness populations and progress

Six additional checkerboards cover 20% bright / 80% dim and the inverse, using
neutral and colored bright/dim cluster pairs. A 20-by-10 tile atlas gives exact
20/80 area proportions before the probe overwrites the center. Tile assignments
are shuffled, with small independent color spread. Refinement preserves mixture
weights while adjusting population brightness/spread. Other random mixtures also
use stratified tile counts rather than unconstrained random draws.

Application progress text describes Camera latency calibration, Initial coarse
calibration, Initial validation, Refinement step N, Final validation and Saving.
Individual reference/matching/confirmation events still update the numeric bar
but do not replace these phase labels.

The README includes an example scatter plot from a completed calibration. Its
nominal colors illustrate coverage and do not establish luminance accuracy.

The Refine button runs additional adaptive refinement rounds across colors and distributions, using the existing measurements.

Stable matched corrections from each validation pass are retained after reporting
its pre-update predictions. Subsequent checks use new patterns; predictions alone
are never substituted for camera-matched targets. Drifted and plateau-limited
matches are excluded. Final validation matches also update the saved model.

End-user Calibration quality is measured on 20 bundled fullscreen photographs,
with natural brightness/color variation and a common 250-nit SDR white level.
There is no per-photo brightness normalization, synthetic color mixing, or window
area sweep. Images are cropped/scaled to fill the display at its aspect ratio,
with the existing camera probe still overlaid. Photographs are measured after
synthetic refinement and their usable matched corrections are then retained.
These are pre-update benchmark errors, not a guarantee of unseen-image accuracy
on later runs that already trained on the same photographs. Provenance and
public-domain/CC0 licenses are recorded in assets/benchmarks/manifest.json.

During training, each successful match immediately updates the camera session's
working neighbor model. The next pattern starts at that updated prediction;
PCA coordinates and their normalization stay fixed within the pass. A closing
reference drift above the acceptance threshold rolls back that sweep's updates.
Validation keeps predictions frozen for the entire pass, then incorporates its
usable matched measurements. The default remains three refinement rounds.

New calibrations use a 9 x 8 x 128 HSV histogram without a separate brightness
vector. Hue is circular, saturation is square distributed, and value is square
root distributed. The PCA remains 14-dimensional. Existing RGB models retain
their runtime representation; use New calibration to adopt HSV.

Calibration uses one fixed exposure selected before camera synchronization. The synchronized reference also measures temporal noise over 20 settled frames; final matches accept a difference of four times this noise, without widening the band using transient measurements. Training starts at the current model prediction without an extra raw baseline. Validation retains raw readings for comparison. A search stops after four adjustments without a noise-significant improvement and does not train on an unconverged result. Reference brightness is checked before a new sample if the last check was at least 20 seconds ago, and at the end of each pass. Each check updates the target to track display drift. Changes greater than 5% emit a GUI-console warning about a possible exposure change; a brightness change alone cannot distinguish display drift from camera exposure drift. Exposure settings remain fixed. Baselines never interrupt a sample between its raw, predicted, and matched observations. Original reference changes remain recorded in baseline/end_reference_check CSV rows.

Flat-region skips are retained as inferred zero-correction anchors at their exact pattern coordinates, including skipped brightness levels. They update the live training model immediately and are included when fitting the saved model after a successful closing reference check. They are kept distinct from camera measurements and do not count as measured validation results. Actual matched measurements take precedence over inferred anchors for the same pattern.
