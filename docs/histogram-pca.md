# Histogram PCA model

Click **New calibration**. PCA is the only model in the current application;
Previous prototypes remain available in the initial Git commit.
No extra camera measurements are needed to learn the PCA basis. New calibrations
use [adaptive gamut sampling](gamut-sampling.md); earlier PCA reports retain their
original schedule.

## Representation

- 8 ? 8 ? 8 joint RGB histogram, preserving color mixtures rather than averaging
  them to a single color. Eight neighboring bins receive trilinear weights.
- Fixed linear BT.2020 domain, scaled by 250 nits. The encoder uses sqrt(channel /
  10000 nits) to allocate more bin resolution to low signals. This is a coordinate
  choice, not a perceptual error metric. Values beyond the encoder domain share
  its endpoint bins; displayed signals are never clipped by this encoder.
- Fourteen orthonormal PCA directions, learned without camera labels from actual
  calibration distributions plus deterministic synthetic 1?5-mode distributions,
  white levels and primary/secondary colors, bounded by the selected peak. Covariance power iteration uses no
  third-party numerical dependencies. Components are measured relative to the
  black histogram so the existing unity black anchor remains valid.
- Existing nonlinear regularized Gaussian correction fitter operates on these
  coordinates. This does not assume additive subpixel power.

The CPU builds distributions from the same 128 ? 64 grid as the GPU, respecting
window boundaries, point-sampled pattern textures, black outside the window,
gray backgrounds and the centered 100-nit probe. It does not subtract an average
color from a spatially nonuniform pattern. These are nominal, uncorrected input
scene distributions, matching runtime analysis before the uniform gain.

## Runtime

8,192 sampled pixels; group-local integer histograms; no global histogram atomics
and no CPU readback. Two compute dispatches accumulate/project the histogram and
evaluate the correction. Inference distributes RBF centers over 128 threads.
The FP16 scRGB pixel pass still multiplies all channels by one uniform gain.
Runtime binary APL2 embeds the PCA basis; older moment binaries are rejected.
PCA JSON carries the basis, so subsequent versions do not silently change it.

## Initial checks and limitations

Run `./test.ps1` for managed histogram and model checks and native GPU agreement
checks. Tests run separately from the production application.

On the local RTX 4070 Ti, the warmed 81-center run measured approximately 133 us
per complete test frame, 40 us for the pixel pass alone and 8.8 us for analysis
plus inference alone. A monitored run measured 146 us at P0 / 2445 MHz graphics /
10501 MHz memory. The original short-batch 71-us result is not a reliable budget
claim. Whole-frame timings include submission gaps and state/copy work; isolated
stage timings cannot simply be added. This POC has **not demonstrated the 100-us
complete-filter target or <5% contention cost at 500 Hz**. DWM integration, dirty
region workloads, application contention and larger measured models need further
profiling. The synthetic 512-center workload measured approximately 148 us
complete, 41 us pixel-only and 11 us analysis/inference-only. No GPU clocks or
power policies are changed.

PCA is lossy. Rare populations and variation absent from the basis can disappear;
small features are intentionally not specially protected. Spatial layout and
history are not represented. No new physical calibration has been run for this
model yet, so numerical agreement does not establish brightness accuracy.

Selected over per-frame clustering because it avoids iterative mode fitting and
mode identity jitter; selected over Fourier sketches to avoid per-pixel trig.
Both remain alternatives if measured correction errors expose PCA collisions.
