# Adaptive calibration and informative mixture design

Status: proposed design, unvalidated. This supersedes the previous fixed nine-minute acquisition allocation. Ten minutes is a default ceiling, not a target. Extended calibration requires explicit user opt-in. Assume ten independent camera measurements per second.

## Initial measurements

Begin with four native-resolution, constant-signal expanding-window families: red, green, blue, and white. Native resolution refers to rendering; it does not require dense sampling of every window size. Use a stable ROI inside the initial 1% window. Initial area candidates: 1%, 10%, 20%, 40%, 60%, 80%, 100%. These are startup anchors rather than assumed monitor knees.

Fit a continuous piecewise response with flat, sloped, and flat segments when supported by observations, but allow other shapes. Probe midpoints of uncertain intervals, preferably near candidate change points. Large intervals that pass an independent midpoint check within a noise-aware tolerance can remain sparsely sampled. An endpoint-only check cannot exclude an interior feature. Prioritize interval width, model disagreement, correction sensitivity, and measured residual.

Physical linear dimming may appear curved in camera values. Fit in a calibrated camera domain or jointly model its stable transfer; otherwise use piecewise smooth camera-response curves without labeling their slopes as physical luminance slopes. Readings need settling and exposure timestamps. A brief up/down or repeated-anchor check distinguishes static response from history. Smooth visual transitions may connect sampled areas; do not treat transition frames as settled samples.

Keep small fixed witness patches of several colors/signal levels to measure how a changing load affects other outputs. Include their load and excluded area in the stimulus model. Reference each witness to its own low-load observation. If witness patches do not fit reliably within the camera view, sequential probes require additional time.

Four sweeps at one signal level only establish that signal slice. Before claiming a useful HDR correction, verify representative midtones and the boosted signal range. Add signal levels only where output response or corrected-load predictions require them. Red, green, blue, and white values must have an explicit linear-light/wire-format contract; equal code values do not establish equal light or power.

## Sparse hue and saturation refinement

Avoid a window-size x hue x saturation Cartesian grid. First test missing hues (cyan, magenta, yellow) and partly saturated colors where the current model is most uncertain. Hue is periodic, and at zero saturation it is irrelevant; do not spend samples on duplicate neutral states.

At a new color, use the current model to predict its dimming-onset and plateau areas. Make paired queries bracketing the predicted onset, then one near the predicted plateau or the maximum reachable area. If no bracket exists, search coarsely. Refine only when observations disagree with interpolation or when uncertainty affects correction. Thus each sampled color initially gets two or three area queries, not a full area sweep. Rotate colors across hue/saturation space and retain independent spot checks in supposedly smooth regions.

R/G/B/W anchors do not determine arbitrary WOLED color mixing. Interpolation across them is a prior. Learn q(R,G,B), a joint-color effective-load function, and aggregate across pixels as P=mean_i q(X_i). Its units are arbitrary; normalize black and a reference color. White-subpixel conversion is absorbed in q. Do not interpret min(R,G,B) as known firmware behavior.

## Mixed scenes as experimental design

An image is not naturally an eigenvector. Instead, represent a checkerboard scene by its color occupancy vector h over a small current palette. Entries are nonnegative area fractions; they sum to the chosen bright area, with the remainder black. Keep witness patches fixed. Initial expectation: P=sum_j h_j q(c_j), with predicted witness response F(probe,P,history).

Use patterns to test this expectation, not arbitrary N-color images. Start with two-color compositions; use three or more only to access a missing direction or verify generalization. Use sufficiently large native-resolution tiles to avoid unintended optical color blending, and occasionally rearrange the same histogram to test spatial dependence.

### Equal-load contrasts

Find feasible pairs h+ and h- satisfying the same total bright area and the same predicted P, but differing strongly in saturation/neutral content or color composition. Apply both near the active dimming transition, where output is sensitive to errors in predicted load. If equal-load compositions produce different witness responses beyond repeatability and predicted uncertainty, the model needs refinement.

A feasible contrast is delta h=h+-h-, satisfying sum_j delta h_j=0 and sum_j q(c_j) delta h_j=0. Generate candidates in this nullspace and enforce nonnegative area fractions. A palette with only two colors and unequal q generally has no nonzero equal-area/equal-load contrast; use at least three colors or relax the area constraint deliberately. Nullspace dimension and physical feasibility determine useful N, not an arbitrary number of checkerboard colors.

Equal-load differences test model failure but cannot identify a common load bias. Also test a few changed-load mixtures and multiple total areas, comparing predicted versus measured witness response. Hold out full compositions and test correction on them rather than reusing all scenes for fitting.

### Eigenvector interpretation

For model parameters theta, compute the predicted witness-response Jacobian J(h)=d y_hat(h)/d theta. The accumulated information matrix is I=sum J^T Sigma^-1 J plus regularization, with Sigma estimated from repeated measurements. Small-eigenvalue parameter directions are weakly constrained. Select a feasible pattern or paired contrast with strong response sensitivity to one of those directions; equivalently, score candidates by expected uncertainty reduction per second.

The eigenvectors are in parameter space. A realizable image excites their response directions; it need not equal an eigenvector and cannot have negative color occupancies. Information scores depend on model assumptions and need residual-driven exploration. Optimize over a cheap candidate pool initially, not an expensive continuous search.

If q alone fails, refine its hue/saturation/signal interpolation where the data point, then consider a low-rank scene state z=mean_i phi(X_i) only if additional independent residual structure remains. Select interaction basis terms through held-out predictive performance. Latent axes are not unique physical channels. Add complexity only when it improves independent tests above noise.

## Scheduler and early stopping

Every measurement must have a stated purpose: locate a change point, refine interpolation, identify a weak parameter direction, detect history/spatial effects, or independently verify correction. Stop averaging as soon as a sequential confidence interval is narrow enough for that decision, subject to a minimum repeatability check. If noise dominates, improve acquisition instead of adding color samples.

Stop default calibration when unresolved intervals and mixture residuals are below the chosen noise-aware tolerance, targeted held-out corrected scenes pass, and model complexity has stabilized. Keep a small exploration/validation allocation so a falsely confident prior cannot terminate solely on its own predictions. Enforce the ten-minute ceiling including setup if that is the product requirement. A deadline failure produces an incomplete/limited result; extended calibration is a user-selected option.

## Illustrative timing, not mandatory quotas

An economical path might use 28 initial area anchors (four colors x seven areas), eight targeted area refinements, twelve hue/saturation queries, and twelve informative/held-out mixture scenes: 60 settled states. This is a planning example, not sufficient-coverage evidence. Twelve hue queries might cover only four new colors; expand when residuals demand it. Required brightness and corrected-output checks must be counted explicitly.

At ten independent frames/s, three measured frames take 0.3 s. With an experimentally established 0.4 s settling time, 60 states cost about 42 s. Thirty additional adaptive states and twelve reference/repeat states add 29.4 s. With 30 s setup, the illustrative total is about 101 s. If settling takes 1 s, the same 102 states plus setup cost about 163 s. More averaging, HDR signal slices, camera adjustment, temporal holds, and corrected-output iterations increase these totals. Three frames are not intrinsically sufficient; sequential noise checks determine sample count.

General timing: elapsed = setup + sum_states(settling + independent_frames/10) + fitting/presentation overhead. Do not spend unused budget merely to fill it. Initial schedule numbers must be revised from the pilot rather than hardcoded.

## Limits and runtime

A plateau has low identification value for exact load but still verifies maximum dimming and reachability. Transition regions identify load most strongly; corrected scenes may move those transitions. Predict load from corrected pixels and use bounded correction iteration. GPU aggregation and the application path still require independent latency measurement.

Finite adaptive calibration does not establish coverage of all possible ten-bit images. Report held-out residuals and the validated envelope. Start with the simplest supported model, preserve black, detect saturation, and reduce the target when brightness cannot be restored. No system-wide backend or hardware calibration has been implemented yet.

## Adopted measurement geometry

User confirmed the interactive utility works and clarified the actual calibration geometry: measure one fixed point at the center of the screen while varying surrounding content. Use one stable camera ROI contained inside a fixed central probe patch. Keep probe position, area, color, and signal constant within each response experiment. Change probe color or signal only between experiments, establishing its corresponding reference. Surrounding fields, expanding annuli, hue/saturation patterns, and checkerboards must exclude the central patch and vary independently of it.

Include the central patch contribution in modeled frame load. A nominal 100% surrounding field means every pixel outside the fixed probe has the selected surround; report total-frame occupancy accurately. Initial same-color expansion can use an annulus matching the probe color, while mixed-color tests retain the same center probe and change only the surround.

Earlier proposals for several simultaneous witness patches are superseded by the single center probe requirement. When different probe responses are needed, run sequential experiments at the same center position and ROI, adapting their selection to avoid unnecessary calibration time. Comparing equal-load surround mixtures must use the same probe in both scenes.

The current interactive patch/checkerboard surface does not yet enforce an independent fixed center probe in all modes. Before automatic calibration, separate probe and surround shader parameters and always render the center probe after selecting the surround pattern. The camera ROI must remain within the probe throughout those changes.
