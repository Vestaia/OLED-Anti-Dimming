# Histogram performance - 2026-10-08

RTX 4070 Ti, NVIDIA driver 596.49, 2560 x 1440 FP16 scRGB, 307 centers, 14 PCA components.
Both neutral 100-nit and mixed bright/dark/color content were tested. Balanced uses 25,000 requested sample cells.

| Path | Analysis + inference | Full API path | Paced 500 Hz |
|---|---:|---:|---:|
| Previous 8^3 + 256 | 34.1 us | 124.3 us | 120.7 us |
| 16^3 + 1,024 without optimization | 193.0 us | 280.0 us | 270.3 us |
| 8^3 + 256 with local PCA | 12.4 us | 101.3 us | 98.7 us |
| 16^3 + 1,024 with local PCA experiment | 20.8 us | 109.8 us | 106.5 us |
| Adopted 16^3 + 1,024 production shader | 20.9 us | 113.1 us | 106.0 us |

These are means of each scene's median, not pooled percentiles. API path includes a full pristine copy, analysis, inference and the full-resolution pixel pass. Seven warmed 256-frame batches are used per mode. Paced timing uses 250 frames, submitted every 2 ms. GPU work is measured using D3D11 timestamp queries; elapsed GPU timelines can still include driver/submission gaps. Separate stage timings cannot simply be summed.

Sustained comparison clock logs were P0, 2,445 MHz graphics / 10,501 MHz memory. No clock or power settings were changed. The original first runs began at P5 before warming. A separate 10-second cooldown test showed P3 with variable graphics clocks and 5,001 MHz memory. There, the original current pathus last-100-frame median was 260 us and the larger unoptimized pathus was 600 us. Optimized variants measured 324/489 us at different graphics clocks; those idle measurements cannot establish an optimization speed ratio. One current-path wakeup frame reached 2.04 ms.

At 500 Hz, a 100 us frame corresponds to 5% of a 2 ms frame-time budget. The adopted shaderus approximately 113 us full API result is about 5.7% of that budget at P0. This is not a measured percentage of GPU resources, gaming contention, presentation latency, or DWM overhead. The below-5% target has not been established.

The isolated comparison uses equal 307-center loops. The larger experimental basis is dense synthetic data; production timing uses a learned 5,120-entry basis with the same center count. This is a cost comparison, not a physical calibration accuracy comparison. Local PCA output matched the reference gains within 1.2e-7 over the six scene/grid combinations. CPU/GPU regressions subsequently covered neutral, mixed brightness, mixed colors, aspect-aware sample counts and all three histogram formats.

## Why local PCA helps

The old final workgroup serially scans every group's histogram, then projects the global histogram. The adopted first pass projects each group's local histogram and writes 16 padded floats. The final pass reduces those vectors and subtracts the black reference once. Linearity preserves the same features, apart from floating-point rounding. At Balanced density, intermediate storage drops from approximately 2 MiB for the large histogram to a few KiB.

The 16^3 variant was retained after this optimization. Keep the interpolation at 14 components. New PCA datasets contain 6,643 unlabeled distributions, including 20 licensed validation photos and 48 original UI mock layouts; default camera scene count is unchanged. The full preparation test took 2.46 seconds and retained 63.1% of histogram variance. Variance retention is not a brightness-error metric.

## Reproduction

`tools/prepare_histogram_benchmark.py MODEL.json OUTPUT_DIRECTORY` writes reference and optimized cost-workload shaders/binaries. Compile `tests/native/histogram_benchmark.cpp` with that directory on the include path; link d3d11.lib, d3dcompiler.lib and dxgi.lib. Run the executable with model-folder, shader-path, variant-label and output.csv arguments; append `--idle` for the cooldown test. The benchmark does not inject into DWM or touch the camera. Repeating the benchmark will vary with clocks, driver, desktop activity and CPU submission behavior.

Raw CSV measurements, clock logs and PCA dataset statistics accompany this local report.
