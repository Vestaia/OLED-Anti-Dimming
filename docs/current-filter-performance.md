# Current smoothed-HSV filter performance - 2026-10-09

RTX 4070 Ti, 2560 x 1440 FP16 scRGB. Latest completed calibration:
`managed-20261009-120040-655`, 2,665 unique anchors. Version-12 measurements
were remapped to (1.2, 6, 14), with `exp(-(d/0.005)^3)` weights.

## Per-frame GPU cost

D3D11 timestamp queries cover the actual `AdaptiveFilter.prepare` path, including
a complete pristine-frame copy, histogram construction, all three smoothing
passes, anchor weighting, prediction, and a full-resolution FilterPS draw.
The separate prepare numbers exclude the final draw. Synthetic uniform white
and mixed bright/dark/color tiles were used, not camera measurements or a live
DWM capture. GPU clocks/power were not overridden.

| Requested cells | Uniform white, sustained full path | Mixed tiles, sustained full path | Mixed share of 2 ms frame budget |
|---|---:|---:|---:|
| Performance: 10,000 | 0.525-0.644 ms | 0.519 ms | 26.0% |
| Balanced: 25,000 | 0.566-0.662 ms | 0.526 ms | 26.3% |
| Quality: 60,000 | 0.617-0.660 ms | 0.538 ms | 26.9% |

Previous repeat of the same uniform fixture measured 0.523-0.526,
0.551-0.587, and 0.616-0.618 ms respectively. Short runs have some clock and
background-load variation; the meaningful range is about 0.52-0.66 ms. This
is approximately 26-33% of the 500 Hz frame-time budget, above the desired
0.1 ms / 5% target. It is not a measured percentage of GPU hardware occupancy,
gaming slowdown, or presentation latency.

The 25,000-cell mixed fixture paced with Sleep(2), excluding gaps through
per-frame timestamp pairs, measured 0.526 ms. Sustained and 2 ms paced tests
reported P0, 2,445 MHz graphics / 10,501 MHz memory. Sleep(2) is not an exact
500 Hz scheduler. After cooldown, Sleep(16) pacing measured 3.44 ms at P3,
210 MHz graphics / 5,001 MHz memory. First full frames measured 1.97-3.40 ms,
including initial allocation/wakeup. GPU timestamp intervals may contain
submission gaps; these are isolated workload results, not an ETW DWM trace.

At 25,000 cells, varying only the active anchor count gave:

| Active anchors | Full GPU path, mixed tiles |
|---|---:|
| 0 | 0.126 ms |
| 100 | 0.154 ms |
| 500 | 0.203 ms |
| 1,000 | 0.280 ms |
| 2,665 | 0.589 ms |

Zero anchors is a diagnostic unity-gain case, not a usable calibrated model.
Comparisons across the complete 9,216-element histogram dominate: approximately
70-80% of full-path time in these isolation tests. Lowering screen samples has
limited leverage because every anchor still participates. Runtime anchor data
alone occupy about 98 MB and logically require about 49 GB/s of anchor reads
at 500 Hz, before cache effects and texture traffic.

## Apply initialization

Saved JSON: 746,123,237 bytes (712 MiB). Exported binary: 98,355,420 bytes
(93.8 MiB). Local timings with cached storage:

| Stage | Time |
|---|---:|
| Apply monitor check: read + parse entire JSON | 2.18 s |
| WriteModel, original version-12 input, complete | 7.94 s |
| WriteModel, version-13 input, complete | 5.44 s |
| Hook address probe, separate profiling symbol cache | 0.85 s |
| Native filter initialize, standalone existing D3D device | 0.80 s |

The separate managed breakdown was: read 0.97 s; parse 0.88 s; deserialize
anchor arrays 1.53 s; smoothing remap 1.09 s; deduplication/model construction
0.09 s. These are separate measurements, not timings to add on top of WriteModel.

Apply reads/parses the same large JSON **three times**: the monitor check,
CalibrationModel.Load inside WriteModel, and another JsonDocument in WriteModel.
The latter is unnecessary for the histogram branch, which already has the
loaded model. Preprocessing is synchronous on the GUI thread before the first
await, explaining the unresponsive application while preparing the filter.
Migration contributes, but is not the principal cost. Even version-13 input
still takes over five seconds to export without migration.

Native initialization occurs lazily in the DWM presentation callback. Shader
compilation and the roughly 94 MiB model upload therefore block the first
filtered presentation. Separate shader compilation timings:

| Entry | Compile time |
|---|---:|
| HistogramFeatures, unused legacy PCA shader | 286 ms |
| HistogramInfer, unused legacy PCA shader | 93 ms |
| FilterPS | 28 ms |
| SmoothHsvSamples | 28 ms |
| SmoothHsvConvolve | 11 ms |
| SmoothHsvWeights | 261 ms |
| SmoothHsvPredict | 8 ms |

About 379 ms of compilation is unnecessary legacy work. Compilation explains
most of the native 0.80 s initialization. Existing DWM already owns its device;
the standalone harness's separate 102 ms device creation is not part of Apply.

The measured parts suggest about 11-12 seconds to prepare and initialize an
old model. This is an estimate, not a measured complete live Apply duration:
injection, ACL updates, copying, unload, and redraw waits were inspected rather
than triggered. RefreshDesktop has an explicit 50 ms delay on each call and
DwmFlush may additionally wait for the lazy initialization. Injection has a
10-second timeout, not an unconditional 10-second delay. No live filter toggles
or camera calibration were performed.

## Highest-value next changes

1. Reuse one loaded model for display checking/export; avoid repeated JSON reads.
2. Cache the remapped binary per model/settings and use compact binary anchor
   storage instead of huge text arrays. Persist migration once rather than
   repeating it at each Apply.
3. Prepare files off the UI thread and compile shaders ahead of injection;
   compile only shaders for the selected model. Avoid compilation inside Present.
4. For frame cost, reduce the per-anchor comparison workload while preserving
   the adopted histogram distance; screen-grid reductions alone will not reach
   the budget. Approaches require separate accuracy/performance experiments.

Only profiling harnesses/documentation were added; application behavior has
not been optimized or republished in this profiling task.

## Reproduction

Managed profile:
`dotnet run --project tests/managed -c Release -- --profile-histogram MODEL_JSON build/filter-performance`

Build `tests/native/filter_profile.cpp` with the existing Visual Studio x64
C++ setup, include `native`, link d3d11/d3dcompiler/dxgi, and run from `build`.
It reads `build/filter-performance/runtime.bin` from the managed profile.
Raw outputs are in `build/filter-performance/managed-timings.json` and
`build/filter-performance/native-timings.txt`.
