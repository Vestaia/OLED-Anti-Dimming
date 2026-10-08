# First experiments

These are proposed experiments, not completed measurements. Extended investigation requires user approval under the supplied AGENTS.md.

1. Inventory Windows build, GPU/driver, display topology, HDR mode, monitor model/firmware, refresh rate, and camera controls. Keep monitor OSD settings fixed. Record separate profiles for separate modes.
2. Build a native HDR presenter and webcam ROI recorder. Verify stimulus color space and camera stability before fitting anything. The CSV planner is only the initial schedule generator.
3. Establish repeatability: alternate an identical patch and reference at fixed load, detect clipping, and estimate noise. Select an acceptance tolerance above measured noise and agree on the desired residual brightness variation.
4. Test reachability cheaply: compare a fixed midtone patch against its low-load reference while raising its signal under several background loads. Stop at clipping or a plateau. If targets cannot be matched, select a lower feasible target before expanding the sweep.
5. Measure neutral sweeps, then RGB and mixed-color loads, spatial layouts, rising/falling sequences, and settling dynamics. The initial CSV changes neutral background level and bright area while holding probe area constant.
6. Compare scalar APL, additive channel-power, and empirical scene models with held-out patterns. For QD-OLED, the user proposes sum_i[f(R_i)+g(G_i)+k(B_i)] as a clean starting model. WOLED is expected to be more complex, including white-subpixel behavior. Neither assumption replaces measurement. Keep the empirical iterative route available for both.
7. Apply bounded iterative correction in the presenter. Hold out layouts, saturated content, and moving scenes. Measure residual patch variation, clipping, chromatic drift, oscillation, and GPU time.
8. Compare untouched baseline, identity DWM hook, active identity LUT, static MHC2, and corrected configurations. Record presentation mode, refresh, GPU time, frame pacing, and input-to-photon latency separately. Present statistics alone cannot establish input-to-photon latency. A webcam may be too slow to resolve one refresh interval; use a suitable high-speed camera or instrument if needed.
9. Select a runtime backend only after correction feasibility and presentation behavior are measured. Add restore/disable behavior before applying system-wide transforms.

## Decision gates

- Unstable camera: fix acquisition or label the camera unsupported for reliable measurement.
- Infeasible boost: lower the target or retain measured residual; do not promise elimination.
- Additive model fails on mixed colors or layouts: use empirical state features.
- Significant temporal dependence: include history and measure tracking tradeoffs.
- MHC2 updates are slow/non-atomic: retain as static calibration only.
- DWM path changes presentation mode or adds unacceptable latency: reconsider application-specific correction or explicitly revise scope.

Current prototype: native HDR presenter and webcam acquisition are working. White and color observation-matching sweeps, camera timing, and an agnostic HSV/window correction surface have been tested locally. See hsv-prototype.md and reports for scope and measured results.

2026-10-07: adopted validation-driven local pattern generation and a deployable 14-moment whole-frame RBF, removing window area from runtime inputs. Eleven new stable matches from twelve local perturbations brought training to 108. Repeat targeted validation: raw RMS 7.033, model 1.208, feedback match 0.179 camera codes (31 accepted cases). Added GPU reduction/inference/final transform, ICC/VCGT preparation, and .NET controls. Named Microsoft PDB function resolution replaces ambiguous upstream signatures; DXGI selects the output. GPU/CPU agreement passes; live elevated DWM validation remains outstanding. See desktop-prototype.md.

2026-10-07 beta refinement: selected bounded PI-like log-signal control with an online camera-code slope, rather than derivative-heavy PID or a camera-transfer predictor. It needs no assumed radiometric transfer and gates feedback by measured delay/settling. Intermediate feedback is one frame; final confirmation is three stable frames. 81 delayed/noisy nonlinear simulations and plateau checks pass. Neutral sampling is six shades by 17 areas plus six by four nonoverlapping validation areas; a targeted GUI action reuses color training. Builds, model/plan/report checks and GPU regressions pass. Physical speed and variance gains are not yet measured.

2026-10-07 hybrid correction: physical feedback exposed coarse/final oscillation. Selected a latched discrete three-frame final matcher with extra camera-frame guard, small-step bracketing and no integral carryover. Damp coarse crossings; hand off only near the target. Final correction capped at 0.5% log signal. Native build and convergence/oscillation/step-bound tests pass.
