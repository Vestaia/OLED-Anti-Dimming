# Legacy histogram PCA model

Version 6 and earlier remain loadable for rollback. New calibrations use the
[color-cluster model](color-clusters.md); the following describes the preserved PCA path.

Version 6 calibrations use only a 9 x 8 x 128 HSV histogram (9216 bins), compressed
into the same 14 principal components. No separate brightness histogram is used.
HSV is derived from nonnegative linear BT.2020 signals, not PQ or sRGB code values.

- Hue wraps circularly. Nine centers at 0, 40, ..., 320 degrees place three bins
  on pure R, G, and B, with two centers between each pair of primaries.
- Saturation uses S squared as its coordinate. Bin centers are sqrt(i/7),
  placing more resolution near high saturation and less near neutral.
- Value is max(R,G,B), encoded as sqrt(value_nits/10000). Its 128 centers
  correspond to 10000*(i/127)^2 nits, concentrating resolution near low values.
- Trilinear weights distribute mass across neighboring bins. Contributions to
  the lowest saturation row use a canonical hue, preserving neutral continuity.
  Values beyond the descriptor domain share its endpoint; rendering is not clipped.

Calibration counts exact pattern coverage, including black outside windows and
the RGB mosaic reference probe. Runtime uses the configured aspect-aware grid.
The PCA basis is learned without camera measurements from planned patterns,
random distributions, licensed photos, and original synthetic UI images. Sparse
covariance products preserve the 14-component budget without a dense matrix.
The basis stays frozen during calibration and refinement. Components are relative
to the black histogram. Inference interpolates measured log gains in normalized
PCA coordinates and applies one uniform linear gain to the entire scene.

The GPU projects each sampled pixel's occupied soft bins directly into PCA before
workgroup reduction. This avoids allocating 36 KB for a 9216-bin shared histogram,
which alone would exceed the D3D11 SM5 shared-memory limit. Each group emits
16 padded floats; the second dispatch reduces these and evaluates the gain.
No full histogram is read back to the CPU. Older RGB layouts retain their original
shared-histogram projection path.

JSON version 6 / runtime APL6 identifies the HSV layout. Versions 2-5 remain
loadable with their original RGB layouts. Create a new calibration to adopt HSV;
old coordinates cannot be reused as HSV coordinates. A local source snapshot is
retained in build/pre-hsv-sources.zip for experimental rollback.

Tests cover primary hue centers, hue wrapping, neutral hue collapse, nonlinear
saturation/value coordinates, distribution preservation, PCA/model round trips,
legacy layouts, and CPU/GPU agreement for several scenes and sampling grids.
Existing timing reports describe earlier RGB implementations; HSV performance
has not yet been benchmarked. This encoding change does not guarantee monotonic
or perceptually uniform corrections: PCA still compresses the distribution and
interpolation still depends on calibration coverage.
