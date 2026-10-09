# Color-cluster model

New calibrations and refinement use model version 10. Versions 7, 8 and 9 clusters and older PCA filters remain loadable with their original geometry. Local rollback snapshots are `build/pre-normalized-clusters.zip` and `build/pre-kmeans-sources.zip`.

## Coordinates and reference scales

Linear BT.2020 pixels map to four coordinates:

```
(S^2 cos(H) / sqrt(3), S^2 sin(H) / sqrt(3), S^2 sqrt(2/3), B sqrt(V/10000))
B = 100 / (sqrt(350) - sqrt(250)) = 34.519675234711585
```

V is the maximum RGB channel in nits. The extra saturation coordinate separates saturation radius from hue displacement. A full-saturation 120-degree hue shift, saturation 0 to 1 at constant V, and brightness 250 to 350 nits each have unit squared center displacement. Hue angle is not warped; circular chord distance preserves wrapping, but is not globally linear in angular separation. Neutral colors have no hue dependence. Square-root brightness makes equal nit changes larger at lower brightness; quadratic saturation makes equal saturation changes larger near full saturation. Values above 10000 nits saturate statistical coordinates only, never the display signal.

Eight weighted Lloyd iterations follow scene-based farthest-point initialization starting at black. Each cluster stores four center coordinates, population, and RMS spread. The state has 48 cluster values plus exact non-black coverage (49 values). Coverage counts pixels above 0.01 nit max-channel brightness and is measured before clustering, so merged black/probe clusters cannot corrupt it. Calibration counts the exact pattern raster including the center mosaic and surroundings; runtime uses the selected aspect-aware sampling grid.

## Distribution cost and interpolation

Version 10 compares the distributions represented by the k-means components using
Gaussian-kernel maximum mean discrepancy (MMD), rather than transporting between
cluster centers. The kernel bandwidth is 1 in the scaled coordinate space. Each
component uses isotropic covariance `RMS_spread^2 / 4` per axis; k-means and its
existing statistics pass are unchanged. This captures total spread, not direction
or covariance correlations, and does not perform Gaussian-mixture fitting.

For two components with centers `a,b` and per-axis variances `va,vb`, their kernel
expectation is `exp(-||a-b||^2 / (2*q)) / q^2`, where `q=1+va+vb`.
Sum this over weighted component pairs to obtain `K(A,B)`. Distribution cost is:

```
N = 2 * (1 - exp(-0.5))
D = max(0, K(A,A) + K(B,B) - 2*K(A,B)) / N
```

Point distributions one coordinate unit apart have cost 1. Coverage is not a
distance input in version 10: the retained coverage field is ignored. For white
at 250 nits on black, 40% vs 60% area has squared MMD 0.10165972876408512
(root distance 0.3188412281435466), solely from the distribution mass change.
This no longer imposes a separate 10% or 20% window reference. Gaussian MMD
still scales foreground differences by population squared and saturates at large
color/brightness separations. These remain unresolved geometry limitations.

Identical weighted component splits have zero cost. Different mixtures representing
the same probability density also have zero theoretical MMD; approximate broad vs
split representations can retain small differences. Isotropic spread and only eight
components still lose distribution detail. No Fourier features or PCA are used.
Self-similarities are cached for model states and computed once per runtime scene.

Versions 8 and 9 retain their original distance including the coverage
cost when loaded, so existing saved filters keep their behavior.

Inference takes the square root of total cost and weights all states by `1 / (distance + 0.0001)^7`, without a neighborhood cutoff. Exact duplicate states share one contribution with their log gains averaged. Near-duplicate density correction is not implemented. This interpolation does not enforce monotonicity.

Adaptive sampling uses this same geometry for neighborhood support and local gain slopes. Training updates the native model after settled matches; validation freezes it until the end of each pass, then retains the labels. Skipped flat states remain unity-gain anchors. Refinement rebuilds retained measured states in the new geometry without reacquiring their labels.

## Formats and runtime

JSON versions 8/9/10 / runtime APL8/APL9/APLA store 49 state values and one log gain padded to 52 floats per model row. APL9 uses the first padding value for cached self-similarity. OLN5 (version 10), OLN4 (version 9), and OLN3 (version 8) online seeds carry 49 doubles per state. Version 7 / APL7 / OLN2 retains the former 40-value geometry and inverse-cost-cubed weighting. PCA formats APL2-6 / OLN1 are unchanged.

GPU mapping, initialization, clustering, final statistics, inference, and the existing uniform linear-light correction remain on the GPU with no frame readback. Versions 8/9 store two float4 rows per sampled pixel. CPU/GPU reference and mixed-distribution tests cover both formats. Earlier k-means microbenchmarks are not total filter performance measurements.

## Experiment record

An initial squared-per-transfer formulation was rejected because splitting a cluster could artificially lower distance. Squaring the aggregate transported length preserves the reference scales without that discount.

The selected calibration `managed-20261008-185625-856` was rebuilt offline using its existing measurements: 512 old entries become 492 unique states. At 50% green, correction changes from +27.92% to +48.22%; at 80% green, +56.78% to +77.14%; at 50% white, +25.50% to +46.35%; at 80% white, +53.94% to +74.62%. Red still reverses between 40% and 50%, and 70% and 80%; geometry normalization alone does not solve every interpolation artifact. Artifacts are local under `build/scaled-clusters-comparison`.

The Fourier prototype was abandoned because finite frequency sets can create false
similarity at large color distances. Version 9 keeps k-means and evaluates the
Gaussian kernel analytically. Offline replay of the same 492 states changes 50%
green from +48.22% to +61.48% and 80% green from +77.14% to +77.08%. Red still
reverses from 70% to 80%; this experiment changes geometry, not the measured labels
or the interpolation's lack of monotonic constraints. Results are in the local
`build/gaussian-clusters-comparison` folder; no new camera validation was performed.

Offscreen full-filter timing on an RTX 4070 Ti at 1440p, about 60k cells and 492
model states: paced 500 Hz mixed-content median 454 us for Gaussian distance versus
472 us for transport in short runs. These include original-frame copy, seeding,
all clustering passes, inference and output rendering. This exceeds the desired
100 us (5% at 500 Hz) total budget for both models. After a 10-second cooldown,
Gaussian median was 421 us with a 7.68 ms p95 during clock ramp-up. No clocks were
forced; GPU occupancy is not inferred from those elapsed times. CSV artifacts are
in `build/gaussian-clusters-comparison`; timing is exploratory, not a guarantee.

Version 10 removes the explicit non-black coverage penalty after investigation
showed threshold discontinuity and excessive area weighting. Version 9 is retained
for saved-model compatibility. New/refined models use version 10; no color-coordinate
or kernel-bandwidth changes were made.
