# Dimming model experiments and candidate designs

Updated 2026-10-09. This is an R&D record, not a commitment to a new design.
Development paused after the cube-root transport experiment. The scalar-impact
and loaded-baseline approaches below are proposals; neither is implemented.

## Objective and constraints

Predict a uniform linear-light brightness multiplier from the screen's pixel
distribution. Preserve distinctions relevant to panel dimming without requiring
a colorimeter, a long calibration, a high-dimensional interpolation problem, or
an expensive per-frame filter. Small population differences can be approximated:
600 of 60,000 samples is one percentage point of screen area.

HSV is a candidate representation because WOLED white-subpixel use can make
neutral and saturated content impose different loads. Increasing V at fixed H/S
and increasing S are plausible load-ordering assumptions, not universal verified
monitor properties. Correction gain itself need not be monotonic with load.
The algorithm must calibrate each monitor; current-monitor observations do not
establish behavior for all monitors.

## Approaches attempted

| Approach | Benefit | Finding / limitation |
|---|---|---|
| Primary/window sweeps, hue/saturation interpolation, brightness-density summaries | Fast, interpretable initial calibration | Useful first framework; scalar averages lose color/brightness mixtures and need increasingly specific refinements |
| Joint RGB histograms, later RGB plus a separate brightness histogram | Captures multimodal populations | Thousands of dimensions; resolution and inference cost tradeoffs |
| PCA of histogram distributions, including UI and real-image examples | Compact projection and GPU inference | Nearby PCA states were not reliably nearby in HSV; distinct grayscale signals could acquire inappropriate colored neighbors |
| HSV histogram (9 H × 8 S × 128 V), without a separate brightness histogram | Better explicit color/brightness separation before compression | PCA still blurred useful geometry; increasing histogram resolution did not remove that structural concern |
| Eight k-means clusters, eight iterations, centers/population/RMS spread | Reasonable compact distribution representation; preserves visible modes | Component matching, spread, population scaling, and distance normalization became difficult |
| Greedy cluster transport and revised normalized geometry | Explicit geometric distance | Interpolation dips remained; greedy assignment is not optimal; broad components vs narrower splits can incur artificial costs |
| Fixed Fourier kernel embedding | Pixel-averaged features independent of cluster partition | Finite frequency features can produce false similarity; abandoned before runtime integration |
| Analytic Gaussian MMD over k-means components | Equivalent identical splits invariant; includes isotropic spread | Bounded color distance and population-squared cost; explicit coverage term incorrectly reordered neighbors |
| Gaussian MMD without coverage cost | Removes binary black/gray discontinuity | Population attenuation and interpolation artifacts remain |
| Smooth HSV mapping and cube-root component transport | Refit reference norms; aggregate population cost before root preserves identical splits | Exact solver increased runtime; heatmap still has dips between measured anchors; not selected as final design |

The explicit coverage feature counted pixels above 0.01 nit. A dim gray background
could abruptly change coverage to 100%, so it was removed from newer models.
No future candidate should identify an explicit foreground window to calculate
scene distance.

Gaussian MMD scales a changed population a by a² in squared cost and a in distance
when the rest of the distribution is identical. Component transport instead sums
mass times component cost. Taking one cube root after that sum gives cube-root
population scaling without rewarding identical component splits. This does not
guarantee equivalence between differently approximated broad distributions.

### Significant results and artifacts

- At 34% area / 75-nit white, fitted clusters retained roughly 66% black and 33%
  near 75 nits. A magenta neighbor retained about 68% black and 31% near 777 nits.
  K-means had not erased their differences.
- Gaussian-only costs correctly favored grayscale neighbors. The separate coverage
  penalty reversed that ranking. Direct empirical kernel comparison reproduced
  the ranking without k-means, implicating the metric rather than clustering.
- Gaussian whole-filter timing on RTX 4070 Ti at 1440p / about 60k samples / 492
  model states was about 454 µs per frame in a short paced test. The first exact
  cube-transport solver took about 3.9 ms on mixed content; negative-cycle
  cancellation reduced it to about 0.75 ms. All exceed the desired 100 µs budget.
  These are exploratory GPU timings, not occupancy guarantees; idle clock ramp-up
  can cause larger spikes.
- Cube-transport reference/split tests and CPU/GPU fixtures passed. The CPU solver
  agreed with an independent LP solver over 80 random cases. Existing labels were
  replayed offline; no new camera validation established the new model's accuracy.

Local ignored artifacts: `build/scaled-clusters-comparison`,
`build/gaussian-clusters-comparison`, `build/kernel-geometry-shape`, and
`build/cube-transport`. Rollback snapshots include `build/pre-kmeans-sources.zip`,
`build/pre-normalized-clusters.zip`, and `build/pre-kernel-embedding.zip`.
The packaged cube-transport executable predates the final solver optimization;
source includes that optimization. Saved models retain version-specific behavior.

## Other alternatives discussed, not implemented

Smooth local HSV basis averages keep features interpretable, but arbitrary joint
distribution resolution still costs approximately H × S × V features. Marginals
cost H + S + V but lose which brightness belongs to which color; pairwise bases
reduce that loss without preserving every three-way relationship. Quantizing
population reduces count precision, not the number of distinguishable color regions.

Gaussian-mixture fitting adds soft assignments and covariance fitting cost beyond
k-means. Estimating covariance from existing hard assignments is cheaper, but
improves representation rather than automatically fixing the comparison metric.

## Candidate: calibrated per-color scalar impact

Learn q_monitor(H,S,V) from solid colors, then map the screen into a one-dimensional
impact distribution. A second learned function G predicts the correction from that
distribution. Color identity is intentionally discarded after q; population and
distribution shape remain. Colors with equal q become interchangeable by assumption.
We do not require every response curve to collapse onto one additive panel-power
curve or claim the assumption has been verified universally.

### Initial proposal: threshold window sweep

Keep the camera reference probe fixed. Expand a solid-color surrounding window
until its effect dims the probe by a chosen threshold; define q = 1 − crossing area.
Proposed initial threshold: 10%. A coarse sweep and bracketed crossing refinement
avoid measuring all areas. Retain flat-region skipping and zero anchors for regions
confidently inferred flat; unsampled unknown regions are not zero by default.

Limitations: a threshold discards weaker effects and subsequent slope/plateau
information. Continuous sweeps need display/camera latency compensation; settled
checkpoints with smooth transitions are easier to interpret. A 10% camera-signal
drop is not necessarily a 10% optical brightness drop.

### Preferred candidate for discussion: loaded-baseline probe

1. Use three disjoint fixed regions: camera reference probe, white loading region,
   and a 25% test region initially black.
2. Tune the white loading region once to produce some dimming, nominally around
   10%, and verify sensitivity with small load perturbations. Already-dimmed states
   can be on flat plateaus; choose a measurable slope with remaining headroom.
3. Replace black in the test region with each sampled HSV color. Keep geometry,
   probe content, white load, and camera settings fixed.
4. Measure its incremental impact relative to the loaded baseline. Prefer the
   probe signal multiplier required to restore that baseline camera reading as a
   candidate label: relative matching avoids assuming a linear camera response.
   Raw additional camera dimming is a cheaper alternative; label choice is unresolved.
5. If strong test colors saturate the response, adapt test population or baseline
   load rather than automatically equating their impacts. Cross-condition label
   normalization remains an unresolved implementation detail.

This avoids a separate onset search for every HSV sample and may expose weak loads.
Its scalar is contextual: we adopt its usefulness across scenes as a model assumption.
Do not claim equal loaded-baseline impact proves identical arbitrary-mixture behavior.

Exposure stays fixed. Use measured latency/settling and noise-based tolerances;
periodically refresh the baseline to track panel drift (existing cadence: at least
20 seconds between checks, checked before a new sample). Warn on significant
reference variation. Zero anchors mean no detectable incremental effect under
this setup, not zero physical power consumption.

### Sampling and interpolation choices

Retain adaptive flat-region skipping; choose new HSV points from sparsity and local
impact slope rather than validation error alone. Sample neutrals without redundant
hue sweeps; handle circular hue continuously. Avoid the previous elaborate distance
normalization, but interpolation still needs axis resolution and local support.

Candidates for q: local piecewise-linear interpolation (local, no overshoot),
compact-support RBF (smooth, support selection required), and tensor B-splines
(efficient LUT generation, less direct scattered adaptive fitting). Bake an adopted
q into a GPU LUT; none has been selected yet.

### Mixed-scene training and one-dimensional compression

Generate random HSV mosaics and unequal populations, including mostly-dark and
mostly-bright distributions. Include different HSV realizations of similar scalar
impact distributions. Train G toward measured correction gain, unless a different
final label is explicitly chosen. Retain fresh validation patterns and freeze the
model during a validation pass, updating it with those labels afterward.

| Impact-distribution features | Advantages | Concern |
|---|---|---|
| 8–16 nonuniform bins, finer toward high impact | Simple, cheap | Hard boundaries can cause jumps; high-impact refinement must justify its resolution |
| Population and average impact per bin | Preserves sub-bin position | More features; within-bin spread still lost |
| 8–16 smooth cumulative thresholds plus mean | Stable; captures small high-impact minorities | Threshold placement and feature correlations |
| Fixed quantiles | Compact distribution shape | Small bright minorities can fall between quantiles |
| Mean/variance/skewness | Very small | Different multimodal distributions can collide |

Adaptive bin/threshold placement should follow sensitivity or observed impact
occupancy; high-impact preference is a starting proposal, not a proven optimum.
Freeze feature definitions before fitting G, or rebuild every stored sample when
they change. A 12-threshold smooth cumulative representation plus mean is one
candidate baseline, not an adopted choice.

## Decisions still open

- Impact label: raw incremental camera signal versus matched probe multiplier.
- Sensitive baseline selection and normalization when measurement range changes.
- HSV sampling/interpolation and reliable inferred-zero rules.
- Impact-distribution feature count and bin/threshold placement.
- Predictor G and its confidence/coverage behavior.
- Whether one impact axis is sufficient; add axes only if evidence warrants it.
- Runtime budget and calibration duration on the proposed pipeline.

Continue brainstorming before implementing or adopting a new calibration design.


## Adopted experimental histogram integration

Version 12 integrates 12 x 12 x 64 soft HSV bins, Laplace (1, 6, 9),
Gaussian bandwidth 0.004 plus a 10x exponential center spike (scale 0.00088).
The full grid and inferred unity anchors replace the 355-state cluster schedule.
CPU/GPU fixtures agree; 2,500-anchor GPU evaluation is too expensive for the
500 Hz target (~0.77 ms). See [implementation](smoothed-hsv.md).

## Adopted cubic exponential histogram weighting (2026-10-09)

Selected 12 x 12 x 64 HSV, separable Laplace scales (1.2, 6, 14), and
`exp(-(abs(d)/0.005)^3)` after offline heatmap comparisons. Removed the center
spike. Version-12 anchors are re-expressed with `L_new * inverse(L_old)` along
each axis on load; measured log gains are retained. New saves use version 13.
The managed, online calibration and GPU paths share these settings. No camera
remeasurement is needed for existing histogram calibrations.

## Adopted reduced histogram (2026-10-09)

12 x 6 x 16, Laplace (1.2, 30/11, 10/3), normalized distance
`sqrt(1152/9216) * L2`, cubic weight scale 0.005. Offline heatmaps differed
from the previous resolution by 0.33-0.44 percentage points on average.
Version-12/13 populations migrate through inverse smoothing, linear population
rebinning, and target smoothing. Measured gains and original files are retained.
New model format 14, GPU APLE, online seed OLN9.
