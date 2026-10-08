# Adaptive gamut sampling

Click **New calibration**. This release supports only PCA gamut calibration.
The reference square remains 100 nits. Panel peak sets
nominal pattern bounds, not a cap on correction or displayed signals.

## Coarse coverage

The sampling cube uses the histogram encoder's square-root linear BT.2020 RGB
coordinates, scaled to the selected peak. It is not perceptually uniform.

- All eight vertices, twelve edge centers and six face centers.
- Black-to-white diagonal at eight equal coordinate intervals, plus the 100-nit
  reference when distinct.
- Eight additional interior points chosen by deterministic greedy maximin search
  against existing points. This improves spacing without claiming global optimality.
- Twelve weighted checkerboard distributions, each using 2?5 centers with
  independently perturbed tile colors. Spreads are 0.025, 0.075 or 0.15 in cube
  coordinates. Centers include darker and brighter populations with varying hues;
  the tiles do not all have the same brightness.

At the 1000-nit default there are 42 solid anchors and up to 178 initial states.
Windows are measured at 100%, 60% and 25%, descending with existing flat-region
skipping. The all-black surround needs only one area because its distribution is
unchanged. Express uses fewer interior samples, two areas and skips mixtures.

## Exploration and reinforcement

Fresh validation patterns are generated on demand: random individual gamut
points and random 2?5-mode clustered distributions with random window areas.
A pool of 128 candidates is ranked alternately by distance from measured color
points and distance from covered histogram/PCA distributions. Six exploratory
checks accompany the first validation; four fresh checks accompany each local
refinement round. These are coverage heuristics, not guarantees of optimal
experimental design. Candidates are ranked cheaply without camera measurements.
Skipped, unmeasured training states are not counted as measured coverage.

Initial validation also includes selected anchor-window interpolation checks,
cluster checks and licensed photo benchmarks. A failed, stable check triggers
nearby area bisections and a new brightness/distribution variant. Separate local
holdouts test nearby intermediate states. Histogram comparisons prevent new
training points from duplicating old distributions or their validation holdouts.
The current model initializes correction trials.

The PCA basis is learned without camera labels before acquisition and then
frozen throughout calibration/refinement. It uses the selected peak for its
synthetic training distributions. New labels therefore reinforce the correction
model without continuously rotating its input space.

A region stops receiving refinement when its camera matching error is within
1% of reference camera code, with a 0.5-code floor. This is a camera matching
criterion, **not a claim of 1% luminance or perceptual accuracy**. Checks with
unstable closing references are excluded; detected panel plateaus are not
repeatedly chased. Refinement is bounded by the UI round limit. Remaining reachable errors are saved in
`gamut-status.json`; completion does not silently mean every error converged.
After local rounds a final full validation checks for regressions elsewhere.
Training/model checkpoints are retained after each completed fit.

## Verification

Run `./test.ps1` for offline geometry, histogram, model, camera-controller and GPU
regressions. The initial Git commit preserves previous prototypes for reference.
Physical accuracy and calibration duration require testing on the target monitor.

## Random mosaic probe experiment

New PCA calibrations use a fixed, per-display-pixel RGB mosaic. Adjacent pixel
pairs have complementary random linear RGB values between 0 and 200 nits, giving
exactly 100-nit mean input in each channel before PQ encoding. This is a nominal
signal mean, not measured luminance or an assumption about camera linearity.
The hash pattern remains fixed during references, matches and latency tests.
Matching measures aggregate camera response and cannot guarantee that each color
in the mosaic has equal relative dimming. Color-specific residuals can remain.

The probe is a physical pixel square with an even side length, approximately 1%
of screen area. Width/height and integer raster boundaries are shared by native
rendering and CPU histogram generation. Tests cover 16:9, ultrawide and portrait
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
