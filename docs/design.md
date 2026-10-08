# Design and feasibility

## Model

Let X be the source frame, T_theta its correction, and H recent panel history. The observable response is camera patch intensity C(T_theta(X), H). Learn corrections that match the camera response of a fixed reference patch in a low-load reference scene.

The corrected frame changes panel load. Therefore estimating load from X and applying a single reciprocal dimming factor is generally inconsistent. During calibration, present the corrected scene, measure it, and iteratively adjust theta. At runtime evaluate predicted load from T_theta(X) when solving for theta, using bounded iterations and a validated response model. Nonmonotone or saturated regions must be detected; a root or stable solution is not guaranteed.

Begin with neutral midtone compensation indexed by a compact scene descriptor: linear RGB means, second moments, highlight fractions, spatial bright-area descriptors, and recent load. Compare a scalar APL model, additive channel-power model, and empirical descriptor model against held-out measurements. Choose the simplest that predicts relative response adequately. RGB code-value means are not linear luminance or panel power.

Then test a family of RGB transforms or 3D LUTs indexed by scene state. A 3D LUT describes per-pixel color mapping, not the full frame-dependent monitor response. Webcam channels do not establish calibrated XYZ or absolute brightness. Match each colored patch against its own reference; do not compare raw camera red and green values as equivalent luminance.

## Candidate application paths

| Approach | Benefit | Structural limitation | Initial decision |
| --- | --- | --- | --- |
| Native D3D11 HDR test presenter with in-pass correction | Known stimulus and scheduling; isolates measurement and shader cost | Only corrects its own window | First calibration prototype |
| MHC2 hardware calibration profile | Supported HDR scanout transform; avoids requiring a custom composition pass | Matrix plus per-channel 1D LUT; no direct runtime programming API; update cadence unknown | Test static boost feasibility, then update behavior |
| DWM hook based on dwm_lut architecture | Access to composed desktop and arbitrary shaders | Original disables DirectFlip/MPO; undocumented hooks and Windows build sensitivity | Separate latency experiment; no assumed solution |
| Per-application render hook | Can apply correction before presentation while preserving application scheduling | App compatibility, overlays, HDR formats, distribution constraints | Reconsider if desktop path fails |
| Desktop capture and re-present | Easy observation/prototyping | Extra frame path and capture delay; incomplete protected/overlay coverage | Measurement only, not preferred runtime |
| Legacy VCGT/gamma ramp | Existing SDR hardware path | HDR behavior undefined; no scene-dependent transform | Not an HDR backend |

MHC2 does not implement arbitrary 3D LUTs. Repeated ICC profile switching is an experiment, not a proposed per-frame API. A monitor-independent desktop solution with no additional latency remains unproven.

## Webcam measurement

Enumerate any attached camera and report capability, not guaranteed accuracy. Require fixed placement and stable ROI. Attempt to lock exposure, gain, white balance, focus, and disable HDR/local tone processing where exposed. Verify actual response over repeated patterns; drivers can report controls while image processing still varies.

Use a fixed probe patch and vary the rest of the screen. Interleave reference/probe/reference measurements and repeat in shuffled order. Record timestamps, camera settings, ROI statistics, saturation fraction, stimulus state, monitor identity/mode, and settling time. Integrate over enough frames to reduce refresh/PWM/rolling-shutter artifacts. Reject clipping and unstable captures.

A nonlinear but stable camera can compare equal-intensity patch matches without a calibrated response curve. Raw camera ratios are not light ratios. For numerical response fitting, characterize camera response or fit directly in observation space. Reflected light from the surrounding scene and camera flare can mimic patch brightening; vary patch positions and shield the camera view as a diagnostic.

Auto-only cameras can be offered a diagnostic or experimental mode. Without a demonstrated exposure-invariant measurement strategy, they cannot be promised valid calibration. Camera access is limited to explicit calibration sessions; frames should remain local.

## Physical and temporal limits

Signal boost cannot exceed panel power, thermal, or peak-output limits. A 1000-nit encoded signal does not guarantee 1000-nit emission. Determine reachability at each load before adopting a target; if unreachable, reduce the stable target or report the residual rather than increase gain indefinitely.

Separate fast content-dependent limiting from static-content dimming, thermal drift, and monitor tone mapping. Calibrate each monitor mode independently. Include scene changes and rising/falling sweeps to detect hysteresis. Rate limiting correction may prevent flicker but introduces tracking error; measure both.

Use linear-light operations for brightness-preserving scaling, with an explicit source/working/wire color-space contract. A PQ-domain transform must decode and encode correctly. Preserve black and reserve highlight headroom. If correction is chromatic or clips channels, neutral brightness results do not demonstrate color preservation.
