# Weighted k-means benchmark, 2026-10-08

RTX 4070 Ti; D3D11 compute shader GPU timestamp queries. Two captured HDR desktops, about 25,000 aspect-aware samples each. Full 9 x 8 x 128 soft HSV histogram, weighted by pixel population, including black.

Coordinates: x = S^2 cos(H), y = S^2 sin(H), z = sqrt(V/10000); brightness weight lambda = 1. V is maximum linear BT.2020 channel signal, not luminance. This metric is an experimental choice.

Four Lloyd iterations, medians in microseconds:

| Snapshot | Clusters | Clustering only | Histogram + clustering, batches | Histogram + clustering, idle then 500 Hz | Geometric RMS |
|---|---:|---:|---:|---:|---:|
| display-1 | 4 | 24.1 | 60.0 | 249.9 | 0.1768 |
| display-1 | 8 | 30.9 | 64.5 | 107.5 | 0.1180 |
| display-2 | 4 | 33.6 | 105.6 | 268.3 | 0.0726 |
| display-2 | 8 | 33.1 | 88.8 | 307.2 | 0.0489 |

Eight clusters lower geometric RMS by approximately 33% versus four on these captures. This is distribution quantization error, not predicted dimming error. CPU references generally stabilize within 3-7 iterations on these two snapshots; initialization and floating-point boundary ties affect that count. At four iterations GPU centers agree with CPU references within 0.0000281 coordinate units and population mass within 0.000027. The histogram path rounds soft contributions to 1/4096 pixel units.

The batched histogram-plus-clustering path uses about 3.0-5.3% of a 2 ms frame interval, before any correction lookup or full-screen filter pass. Idle-then-paced results use about 5.4-15.4%. These are GPU execution-time ratios, not measured GPU utilization. Idle clock probes ranged from 210 to 705 MHz, P3. Other desktop workload was present; clocks were not forced. Batched measurements can also change with clocks; do not assume all batched rows are P0. Repeated work on the same snapshot is not a moving-video workload.

Implementation: 128-thread assignment groups over 72 chunks, separately for each cluster, then a reduction dispatch for cluster means and population weights. GPU histogram construction clears 9216 bins and performs eight soft-bin integer atomic additions per sampled pixel. Its measured isolated cost was approximately 40-63 microseconds in this run. A first serial-per-cluster implementation was rejected because of poor parallelism.

Includes GPU histogram construction and four clustering iterations with a seed reset/copy. Excludes CPU seed generation, actual display texture sampling, source color conversion to BT.2020, covariance/spread calculation, distribution matching, dimming model inference and full-screen correction. The input samples are already uploaded to GPU. Histogram construction and clustering are timed together in pipeline rows. No readback occurs within timed actions. CPU checks/readbacks occur afterwards.

Recommendation: retain eight clusters as an experimental candidate, but this implementation does not yet meet a guaranteed 5% total filter budget. Investigate clustering sampled pixels directly to avoid histogram atomic contention, and warm starts with fewer iterations. Neither optimization's temporal accuracy has been established here. Cluster identities can change order; scene matching must be permutation-independent. Clustering alone does not resolve calibration sparsity.


Local reproducible experiment and raw results: `build/kmeans/`. Application model unchanged.

## Direct 60,000-sample follow-up

Eight iterations, no histogram: eight clusters took 102-108 us in batches, 95-96 us paced at 500 Hz after warmup, and 511-752 us median when pacing began after idle. RGB-to-continuous-HSV coordinate conversion cost about 2.5 us. Assignment is computed once per point rather than once per cluster. The normal-clock analysis cost already occupies about 5% of the frame budget before correction lookup/rendering.

Two captured desktop grids were resampled to 60k entries; a synthetic 60k-color input used 20% bright / 80% dim content. GPU/CPU eight-iteration center error below 2.74e-7; weights normalized. Eight-cluster RMS was within 0.84% of converged CPU RMS, but convergence required 18-53 iterations with these seeds. This is geometric error, not calibration error. Previous histogram results used fewer input samples/iterations and a different kernel; avoid treating the timings as an equal-workload comparison. GPU low-clock wake behavior is significant.

Full local report, raw CSV/JSON, sources and reproduction files: `build/kmeans-raw/`. No application changes.
