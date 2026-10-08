# 250-nit scene and pattern-generalization experiment

Scene peak and probe reference are separate. SDR photographs are decoded from sRGB to linear BT.2020, mapped to a 250-nit nominal input peak, and encoded into PQ. The fixed 1% center remains the established 100-nit nominal white probe. Applying a gain scales both scene signal and probe signal. An attempted 250-nit probe reference was unreachable for a saturated checkerboard; that failed attempt is preserved in the report. These are input-signal units, not camera measurements of emitted nits.

Sixteen training patterns comprise twelve 2-5-color checkerboards spanning hue, saturation, and value, plus four neutral-value fields. Four window fractions (20%, 40%, 70%, 100%) give 64 measured inverse-correction targets. Eight photographs (lake, night skyline, snow, beach, fruit market, forest, flowers, daytime city) are excluded from fitting and model selection. Their physical prediction measurements precede any scene-specific feedback search. The latter is an attainable-reference diagnostic, not a predictor.

We compare a regularized low-order regression and a smoothing Gaussian radial-basis model. Inputs describe the scene distribution: window area; first and second linear-RGB moments; moments of minimum and maximum RGB; chromatic range; and occupancy above three value thresholds. The known white center contribution and black exterior are represented. These are image statistics, not inferred subpixel activations or additive power. The output is log required scene-signal gain relative to 250. A four-fold whole-pattern holdout selects regularization, never photographic labels. Minimum gain remains 1 as the previously adopted boost-only correction policy; there is no upper gain clamp to manufacture a monitor clip.

The current models describe a single scene peak / probe-reference combination. They are empirical inverse corrections, not a full independent replacement for the monitor's EOTF across all HDR input values. Additional probe brightness slices and independent color probes will be needed for that larger objective.

## Runtime settling and acquisition speed

The fixed 220-ms wait was removed. After the measured camera delay plus the existing 50-ms minimum settling guard, consume each fresh camera frame. A three-frame window is accepted when frame-to-frame variation reaches the reference noise floor, residual drift is small, and the variation is no longer falling (or already far below the noise floor). The same three settled frames form the observation. No extra three-frame batch or repeated full-history wait is added.

When the displayed stimulus and camera exposure are unchanged after a previously settled observation, collect the next three fresh frames directly. Exposure changes invalidate this shortcut and retain an extra settling guard. A five-second failure limit rejects an unsettled response; it does not force acceptance. The detector records elapsed settling time, variation, and drift alongside each observation.

Tuning compared 3, 4, 5, and 6-frame histories on twelve recorded two-second mixed-scene transitions. The three-frame history was the fastest candidate and differed from the later plateau by at most 0.062 camera codes; larger histories did not reduce worst-case bias. Synthetic decaying responses, continuing drift, and above-noise oscillation are checked by native self-tests. This establishes the fastest tested configuration for these traces, not a guarantee against every possible delayed firmware response. Training acquisition median changed-trial settling was 203 ms, p90 266 ms; unchanged-state remeasurements typically use about three frame periods.

## Monitor-limited response and clipping policy

No software clamp is applied to manufacture the hard clip. Increasing PQ input continues until consecutive, settled observations no longer change measurably. The search then refines the lowest input reaching that observed plateau and independently remeasures it. It records `panel_plateau` with the remaining error and proceeds to the next scene. A plateau is not labeled a successful target match and is not used as an exact inverse target during regression fitting. Camera clipping rejects the observation rather than declaring a panel plateau. `pq_domain_limit` and `search_limit` remain distinct from an optically measured plateau.

A low-load white scan measured a plateau near 1042 nits of PQ input, with a stable closing reference. Approximately 800 nits was used as a user-provided sanity expectation, never coded as a limit. The camera cannot equate that input knee to absolute emitted luminance; the scan does not verify an 800-nit output peak. Soft rolloff remains a future selectable transform. The present monitor-limited policy retains the plateau produced by the display itself.

A separate saturated mixed-scene test with a 250-nit probe reached an input plateau near 934, retaining a 4.35-camera-code shortfall and continuing to the closing reference. Closing drift was only 0.0213 codes. An earlier attempt had a drifting reference after an exposure change and was rejected; the retry warmed the exposure/reference before repeating the test. All 64 main training groups and all 32 main photographic groups passed their closing-reference checks. Property changes can need more confirmation than ordinary rendered-content transitions; the three-frame fast path is not evidence that an exposure-change transient has finished.

## Reproduction

```powershell
python experiments/plan_mixed250.py
./build.ps1
# Run build/hdr-probe.exe --hsv-calibrate mixed250-training-plan.csv from build
python experiments/scene_surface.py reports/mixed250/training.csv reports/mixed250
# Run build/hdr-probe.exe --hsv-calibrate mixed250-validation-plan.csv from build
python experiments/plot_mixed250.py build/hsv-observations-VALIDATION.csv reports/mixed250
```

Plan fields now include optional `probe_base`. It specifies the probe input corresponding to scene signal 250; older plans omit it and keep their original equal-probe/scene behavior. A `range` query deliberately searches for an unreachable camera-code target to locate the panel plateau; it is a response-range diagnostic and does not establish physical luminance.
