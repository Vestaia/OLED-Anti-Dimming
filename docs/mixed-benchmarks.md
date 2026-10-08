# Mixed-scene prototype

`experiments/plan_mixed.py` creates twelve checkerboards: 2, 3, 4, and 5 colors, each with saturated, mixed-saturation, and variable-value palettes. Two downloaded photographs provide ordinary image content. A linear float texture is sampled by the HDR shader, then encoded as PQ/BT.2020. SDR photographs are decoded from sRGB and converted to linear BT.2020 at a 100-nit nominal signal peak. Checker colors are specified directly in the generator's linear BT.2020 HSV convention.

The center remains a fixed 1% white probe. Each scene is presented at 20%, 40%, and 100% window area. Each scene/area benchmark has its own small-white reference and closing reference. Camera exposure and white balance are fixed within each group, and three fresh spatially averaged frames form each observation. Scaling changes both probe and surrounding image signals; it does not hold surround load artificially constant while correcting the probe.

The uniform-window model has no brightness/value or color-distribution dimension, so these are explicitly candidate extensions:

- Mean correction: average log correction across image colors at the total window area; nearly black pixels contribute zero. This ignores nonzero brightness variation.
- Average RGB: apply the uniform-window surface to the hue and saturation of the mean linear RGB color. This discards the distribution and its brightness amplitude.
- Brightness density: effective window area is window fraction times mean maximum RGB value. Average color correction at that effective area with maximum RGB value as a weight. This is a testable heuristic, not a physical power estimate.

These predictions are physically applied without feedback. A separate observation-match search measures a scene-specific reference correction, then independently remeasures it. The feedback column is an achievable sampled reference, not a generalized runtime model or a test of interpolation on withheld scenes.

Mean correction and brightness density are mathematically identical when every source pixel has maximum RGB value 1. Thus saturated and mixed-saturation checker variants also provide repeated identical-signal observations to test settling. Variable-value checker variants and photographs distinguish the two candidates. Saturation alone cannot substitute for a missing value/brightness dimension.

The first pass used the white transition timing gate. Two-second traces of lake, night, and a two-color checker revealed mixed-load stabilization as late as 172 ms (within a 0.35-camera-code band), and early-minus-plateau differences up to 1.43 codes. This does not isolate optical panel response from camera processing. The adopted prototype gate is max(220 ms, measured white camera-delay-plus-settling), then average three fresh frames. Reference transitions retain an extra 200 ms. Early results are archived; report the retimed acquisition as the benchmark.

The report rejects scene/area groups whose closing reference drifts by more than 0.5 camera codes. Errors are signed camera-code differences from the same probe reference, never inferred luminance ratios. Comparisons remain within groups with fixed exposure. A white probe does not establish that all colors in the image are individually uniform.

Run from the project root:

```powershell
python experiments/plan_mixed.py
./build.ps1
# Start build/hdr-probe.exe with --hsv-calibrate mixed-plan.csv, working directory build
python experiments/plot_mixed.py build/hsv-observations-ID.csv reports/mixed-ID
```

The existing plan format now accepts an optional seventh field, `asset`, containing a scene texture path. Binary textures contain little-endian uint32 width/height followed by float32 RGBA values. The shader uses point sampling, crops the expanding square consistently, and overrides the center probe. A `response` role logs fresh frame scores for two seconds, including stimulus timestamps, for timing diagnostics. No camera pictures are saved.

Image sources and authors are stored with the benchmark metadata. The Google Images page could not be fetched by the browsing tool; the photographs were located through the available image search and downloaded from the original Unsplash image endpoints. No publication or remote changes were performed.
