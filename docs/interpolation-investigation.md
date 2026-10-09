# White-brightness interpolation investigation

Saved dataset: managed-20261008-162129-518. No additional camera acquisition.
White 200 and 350 nits are unmeasured intermediate signals. Measured white
anchors at 140.625, 250, and 390.625 nits have the expected ordered dimming onset.
PCA distance nevertheless places some colored states closer to the white queries.

Cheap comparisons held the PCA basis, normalized centers, and measured gains
fixed. Entries below use the existing heatmap probe representation; correcting
its white/mosaic mismatch preserves the reversal.

| Interpolator | 200 nits at 20% | 350 nits at 20% | 200 nits at 30% | 350 nits at 30% |
|---|---:|---:|---:|---:|
| Current inverse squared-distance power 3, all samples | 13.52% | 5.13% | 15.35% | 10.80% |
| Power 6, all samples | 15.57% | 1.28% | 15.64% | 5.77% |
| Power 12, all samples | 15.64% | 0.52% | 15.64% | 4.57% |
| Current power, nearest 4 | 14.47% | 2.16% | 15.39% | 10.26% |
| Current power, nearest 8 | 14.51% | 2.41% | 15.42% | 10.28% |
| Current power, nearest 16 | 14.39% | 3.25% | 15.42% | 10.37% |

Increasing locality does not repair the ordering and can amplify the wrong
nearest neighbor. These variants were not adopted. Next investigate feature
geometry before adding interpolation complexity: change the relative weighting
of RGB and brightness histogram blocks before learning the same 14-component
basis. The blocks currently have equal mass. RGB itself includes brightness,
so truly independent color/brightness weighting would require separating
chromaticity while preserving its association with brightness. Joint PCA mixes
both blocks; individual components cannot simply be classified as color or
brightness. Any changed basis needs a fresh fit and held-out evaluation, not
reuse of the old PCA coordinates. No weighting change has been adopted yet.
