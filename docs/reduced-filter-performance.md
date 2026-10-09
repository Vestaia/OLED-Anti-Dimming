# Reduced histogram performance - 2026-10-09

RTX 4070 Ti, 2560 x 1440 FP16 scRGB, 2,665 anchors from the same latest
completed calibration. Current model: 12 x 6 x 16, Laplace (1.2, 30/11, 10/3),
resolution-normalized L2 distance and cubic exponential weighting. Compared
against 12 x 12 x 64 using the same native harness and calibration gains.

## Per-frame measurements

GPU timestamps include pristine full-frame copy, histogram construction,
smoothing, all-anchor weighting, prediction, and full-resolution brightness
pixel pass. Synthetic uniform white and mixed bright/dark/color tiles were
measured. No live DWM filter toggles or camera measurements were performed.

| Requested samples | Previous mixed full path | Reduced mixed full path, two runs | Share of 2 ms frame budget |
|---|---:|---:|---:|
| 10,000 | 0.525 ms | 0.287-0.303 ms | 14.4-15.2% |
| 25,000 | 0.519 ms | 0.300-0.331 ms | 15.0-16.6% |
| 60,000 | 0.554 ms | 0.348-0.357 ms | 17.4-17.9% |

Balanced is about 36-42% faster, roughly 40%. The previous histogram was also
rerun in this session with the current shader's legacy branch, rather than
relying only on historical measurements. Sustained clocks in both cases were
P0, 2,445 MHz graphics / 10,501 MHz memory. No power or clock settings changed.
Uniform white measured 0.281-0.305 ms, 0.309-0.333 ms, and 0.387-0.402 ms.

Balanced mixed frames paced with Sleep(2) measured 0.289-0.297 ms, excluding
inter-frame gaps via individual timestamp pairs. Sleep(2) is not an exact 500 Hz
scheduler. The target remains 0.1 ms; it has not been reached. Frame-budget share
is not measured GPU hardware occupancy, gaming slowdown, or display latency.

After cooldown, Sleep(16) pacing measured 1.65-2.20 ms at P3, with graphics
clocks 585 and 330 MHz respectively and memory 5,001 MHz. Cold first frames
measured 1.08-2.61 ms including allocation/wakeup. These are clock-dependent
low-load measurements, not comparable peak-throughput ratios. GPU timestamps
can include submission gaps. This is an isolated workload, not a live DWM trace.

## What now dominates

Reducing dimensions eightfold does not reduce the complete frame eightfold.
Texture copy, final pixel pass, API/dispatch overhead and screen sampling remain.

| Active anchors, 25,000 samples | Reduced full path, mixed tiles |
|---|---:|
| 0 (diagnostic unity gain) | 0.143-0.155 ms |
| 100 | 0.149-0.165 ms |
| 500 | 0.183-0.185 ms |
| 1,000 | 0.199-0.208 ms |
| 2,665 | 0.309 ms |

About half the full-frame time now comes from all-anchor comparison, with the
other half remaining even when inference is removed. Small count differences
are within run/clock variance. Pruning anchors alone is therefore unlikely to
reach 0.1 ms. These are diagnostic prefixes, not predictions claimed equivalent
to the complete model.

Runtime data fell from 98,355,420 to 12,330,012 bytes, about 8x smaller. Logical
anchor reads at 500 Hz are now about 6.2 GB/s versus 49 GB/s, before cache effects.

## Initialization

| Stage | Reduced measurement |
|---|---:|
| Apply monitor check on original 746 MB JSON | 2.16 s |
| Export original JSON, including migration | 5.69 s |
| Migration alone, separate breakdown | 0.513 s |
| Export saved reduced model, no migration | 0.755 s |
| Native filter initialize on existing D3D device | 0.740-0.774 s |

Reduced JSON is 92,447,078 bytes versus the original 746,123,237 bytes. Newly
saved reduced models load/export far faster. Original files are preserved by
migration, so repeatedly applying an old JSON still incurs its large text
read/parse cost until the converted model is saved or caching is implemented.
Original input preparation alone is 2.16 + 5.69 = 7.85 seconds in this run.
This is not an end-to-end live Apply measurement; injection, ACL/copy/unload and
redraw were not triggered. Earlier standalone hook-probe time was about 0.85 s.

DWM initialization barely changes because runtime shader compilation dominates.
HistogramFeatures and HistogramInfer still compile despite being unused by
this model (about 0.38 s combined). SmoothHsvWeights compilation was 0.27 s.
Initialization remains lazy on DWM's presentation callback, so its ~0.75 s
CPU cost can stall the first filtered frame. The harness's ~0.10 s device
creation is separate and does not apply to DWM's already-existing device.

The earlier recommendations remain: avoid repeated JSON parsing, cache the
converted runtime binary, prepare files off the GUI thread, and precompile only
needed shaders outside Present. No application optimizations or republishing
were performed in this profiling task.

## Raw results / reproduction

- `build/filter-performance-reduced/managed-timings.json`
- `build/filter-performance-reduced/native-timings.txt`
- `build/filter-performance-reduced/native-repeat-timings.txt`
- `build/filter-performance-reduced/reference-native-timings.txt`

Managed: `dotnet run --project tests/managed -c Release -- --profile-histogram MODEL_JSON build/filter-performance-reduced`.
Native: build `tests/native/filter_profile.cpp` with the Visual Studio x64
setup and d3d11/d3dcompiler/dxgi libraries; run from build with
`filter-profile.exe filter-performance-reduced`. Reference folder argument:
`filter-performance`.
