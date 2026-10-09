# Experimental smoothed HSV model

New calibrations use model version 14. Version-12/13 histogram calibrations are
remapped automatically on load to the new smoothing and weighting, retaining
all measured corrections without another camera run. Original saved files are
left intact; subsequent saves use version 14. Older PCA/cluster representations
(versions 2-11) remain readable with their existing algorithms.

## Representation and prediction

Exact calibration-pattern populations, including the center probe and black
background, map to a 12 x 6 x 16 histogram. Runtime uses the selected aspect-aware
sampling grid. Coordinates are circular H, S² and sqrt(V / selected peak).
Trilinear splatting is followed by population-preserving separable Laplace
smoothing with bin scales (1.2, 30/11, 10/3). Neutral population is uniform across hue;
the hue-local fraction is S². There is no PCA, clustering or explicit window
detector in the new path.

Euclidean histogram distance is multiplied by sqrt(1152/9216) to retain
the previous distance convention. This normalized distance d determines weights:

`K(d) = exp(-(abs(d) / 0.005)^3)`. No center spike is used.

Normalized weights average measured log gains. Exact duplicate distributions
share one contribution, averaging their measured log gains. The resulting gain
uniformly multiplies linear-light pixels, without a software output clamp.

## Calibration

- Seven areas: 100%, 80%, 60%, 40%, 20%, 10%, 5%, measured in descending order.
- Ten nonzero levels: peak × (k/10)², k = 1…10.
- Nine hues at 40-degree intervals; saturation 100%, 75%, 50%, 25%, plus white.
- 2,590 candidates before skipping in normal mode. Express uses six hues, eight
  nonzero sqrt-brightness levels, saturation 100%/50%, and areas 100%/75%/50%/25%/10%:
  480 colored candidates plus 40 grayscale candidates. Both modes retain the same
  validation/refinement pipeline.
- V=0 anchors exist for every grid H/S/area combination, without camera readings;
  identical black-plus-probe distributions merge.
- Below 1% measured correction, anchor dominated lower brightness, saturation
  and area grid states to unity gain. Lower saturation is pruned only when
  **Has white subpixel** is checked (default unchecked). Otherwise saturation
  must match. Hue remains the same, except neutral states. Changing the setting
  requires a new calibration because previously inferred anchors cannot be
  distinguished safely from valid coverage.
- Random solid/clustered mosaic candidates are ranked using nearby support and
  local measured slopes. Validation stays frozen and is incorporated afterward.
- Training updates the native predictor after each match or inferred zero anchor.

The delayed adaptive PI adjusts the entire pattern. It estimates response slope,
reduces gain on overshoot or worsening error and accelerates consistent progress.
Final settled discrete windows remain as a fallback. Match tolerance is the
larger of 0.5% of reference camera signal and four times sync noise. Reference
checks remain spaced by at least 20 seconds.

## Current limitation

Hardware GPU checks agree with CPU histogram/prediction fixtures. A synthetic
2,500-anchor workload at 1440p and 60,000 samples measured approximately 0.77 ms
per frame after warm-up, including the frame-cache copy and compute passes but
excluding the final pixel draw. This exceeds the 0.1 ms budget for 5% of a 500 Hz
frame. Full-dimensional anchor evaluation needs optimization before adopting
this path as a performance-ready release. Synthetic anchors characterize cost,
not calibration quality; live camera testing remains necessary.

Existing 9,216-dimensional anchor populations are unsmoothed, linearly rebinned
to 1,152 dimensions, and smoothed again. Gains remain unchanged. Original
calibration files are preserved; saves/refinement use version 14.
