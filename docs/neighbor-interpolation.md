# Neighbor interpolation experiment

The previous regularized Gaussian RBF used signed fitted coefficients and a
black-state baseline subtraction. Its prediction can overshoot and return toward
zero log-gain when a query lies outside sampled coverage. This is unsuitable as
an implicit sparse-region fallback: a lower correction at full-screen coverage
need not reflect a measured panel response.

Options considered:

- Normalized Gaussian regression preserves measured bounds, but its fixed
  bandwidth either blends too broadly or loses numeric support in empty regions.
- Nearest-neighbor interpolation retains sparse support but jumps at boundaries.
- Normalized inverse-distance interpolation retains sparse support and varies
  continuously, with no signed coefficients or Gaussian underflow. Selected for
  this experiment. Local linear regression could extrapolate slopes but permits
  overshoot and needs more GPU work and stabilization.

New fits store measured nonnegative log-gains and a zero-state black anchor.
PCA coordinates retain their standard-deviation scaling, with a 0.05 floor.
For squared scaled distance `d`, weights are `1 / (d + 0.0001)^3`; predicted
log-gain is `sum(weight * measured_log_gain) / sum(weight)`. The small distance
floor makes coincident samples finite and continuous. Duplicate measurements
blend rather than producing a singular solve. GPU inference evaluates both sums
in the existing dispatch/reduction and applies the resulting uniform linear gain.

The result is bounded by measured values, including the black anchor. It does
not enforce monotonicity in window size: measured nonmonotonic behavior remains
possible. Far from all samples it blends their gains, rather than returning to
unity; this is a conservative interpolation choice, not verified extrapolation.
Very sparse regions can still flatten or be biased by sample density. Validation
and adaptive sampling remain necessary; no improvement on a live panel is claimed
without new measurements.

Checks cover measured-point tracking, continuity, bounded predictions, constant
zero correction, and sparse extrapolation without unity collapse. Native checks
compare CPU and GPU predictions at multiple sampling densities. Model JSON v3 and
binary APL3 distinguish measured gains from old RBF coefficients. Older v2/APL2
models keep their original inference; a new calibration adopts this model.
