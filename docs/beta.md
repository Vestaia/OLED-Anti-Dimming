# Initial beta: 0.1.0

Launch `build/beta/OledCalibration.exe`; Windows requests administrator access.
The GUI uses .NET and the native HDR/webcam companion. Python and conda are not
required. Keep the application in this project workspace for this beta.

## White and gray refinement

Select the existing model and its display, then choose **Refine white / gray**.
This retains the color training and measures a dedicated neutral pass, without
repeating primary/mixed-color acquisition. **New calibration** includes both.

The neutral pass uses six linear shades: 2%, 6%, 15%, 35%, 65%, and 100% of the
250-nit surround stimulus scale. These are signal values, not measured luminance.
Each shade has 17 window areas: 2%, 6%, 10%, 15%, 18%, 22%, 26%, 30%, 34%, 38%,
42%, 50%, 60%, 70%, 80%, 90%, and 100%. This concentrates points in the transition
region while retaining small and large windows. A fixed center probe supplies the
relative target; its contribution is included in model fitting.

Validation uses 20%, 40%, 55%, and 85% windows for each shade. These 24 states do
not overlap the 102 training states. White-only refinement validates these
neutral states; a full calibration also validates mixed palettes and the audited
public-domain photo benchmarks. The approximate progress bar advances by planned
query completion, with short labels for synchronization, references, matching,
confirmation, fitting, and saving.

## Faster matching

The HSV/mixed/neutral acquisition loop now uses frame-driven PI-like control of
log signal, with a measured local response slope. It does not interpret camera
ratios as luminance ratios. Intermediate control observations use individual
fresh frames. Three-frame averaging and stability checks confirm the final match
and references. Neutral confirmation has a 0.175-camera-code floor; other patterns
retain the 0.35-code floor, with noise-dependent tolerances.

After an actuator change, feedback waits for the measured end-to-end delay plus
the measured response-settling horizon (at least 50 ms). This avoids correcting
against a frame from an older command. Step bounds, integral bounds, bracketing,
and two plateau confirmations constrain overshoot and windup. A failed averaged
confirmation resumes control instead of silently accepting it. Plateau searches
use single-frame intermediate observations and a final three-frame confirmation.
The monitor determines the physical brightness ceiling; only the PQ signal-domain
limits bound commands. There is no hardcoded monitor peak.

The older standalone white/color experimental workflows retain their existing
matcher; the managed beta's acquisition uses the new controller.

## Checks and limits

Build with `./build.ps1` and `./build-desktop.ps1`; the desktop output defaults to
`build/beta`. Close a running beta before replacing its executable. The previous
desktop output can be selected with `-DesktopOutput build/desktop`.

Checks run for this change:

- Native matcher tests: 81 combinations of nonlinear camera response, attenuation,
  delay, and settling dynamics with noise; plateau detection; timing and stability.
- Managed checks: fit/export, neutral coverage and independent validation areas,
  plan serialization, benchmark hashes, and heat-map generation.
- Native HDR/Webcam and .NET builds, plus existing GPU filter regressions.

Run native checks with `build/hdr-probe.exe --self-test`. Run the offline managed
checks with `dotnet build/beta/OledCalibration.dll --calibration-self-test`.

The finer acquisition and controller have not yet been measured on the physical
panel. Runtime and white/gray error improvements must be evaluated with the new
observations; simulation results are not evidence of a camera speedup. Existing
calibration is retained on cancellation. The beta still uses a 250-signal-nit
training slice and does not characterize every HDR intensity or monitor mode.

Calibration patterns, report rendering, model fitting, orchestration, and the GUI
now live in separate readable source files. Experimental Python tools remain for
research comparisons and historical reports; they are not GUI dependencies.

Final convergence now uses latched discrete observation windows: measured delay/settling plus two additional camera-frame intervals, then one settled three-frame average before changing the signal. Coarse control hands off when the estimated remaining correction is at most 1% and the camera-code error is small. Target crossings damp the coarse controller rather than forcing an early handoff. Final steps use the measured slope, typically 0.02%-0.5%, and all bracketed steps are capped at 0.5% in log signal. Integral state is discarded. Search is bounded to 24 windows; flat responses stop further increases. Noisy convergence, bounded oscillation and maximum final-step tests pass; physical validation remains outstanding.

The results panel now opens with Corrections summary (predicted scene gain across neutral/primary window sweeps), followed by Validation heat map and Error summary. Labels are abbreviated. Reports reload from the selected model directory after calibration and when the model selection changes.
The GUI enumerates webcams and defaults HDR to the selected display configuration in Windows. Display defaults refresh when the monitor selection or Windows display settings change. Enable Advanced Color management in Windows for SDR composition.

Windows owns ICC profiles and gamma calibration during measurement, filtering and disable. The application does not load ICC/LUT profiles or apply VCGT curves. Filter setup disables the profile transform and removes stale staged/runtime VCGT data. The identity cube exists only to identify the target display for the hook. Historical native profile tools remain experimental, outside the application workflow.
HDR surround coverage: new calibrations and the next refinement of legacy calibrations add 132 training states plus 33 independent-area holdouts at 500, 1000 and 2500-nit content signal scales. Patterns cover RGB, white, 50%/85% gray, cyan/magenta/yellow and two-/five-color mixtures at 10%, 25%, 40% and 100% areas; holdouts use 55%. The 2500-nit value is a content assumption, not measured panel capability. Existing low-level and fine neutral coverage remains. Texture values and GPU scene moments retain the 250-nit normalization and can exceed one (2500 nits = 10). The center reference remains 100 nits; feedback changes a uniform scene gain, increasing center and surround together as the live filter does. Only the center is matched to its reference, with no requirement to restore peak highlight luminance. Acquisition has not been physically validated for the extended range.

Neutral sweeps now include 50% and 85% linear-light gray: eight shades, 136 training states and 32 independent-area checks. HDR surrounds also include both gray fractions at each content scale.
Camera compatibility: capture formats are negotiated through generic DirectShow IAMStreamConfig, selecting the largest supported mode within 854x480, then the fastest advertised frame interval. If no usable 480p-or-lower format is available, selection falls back to the lowest usable advertised resolution. When manual exposure cannot be set or read back, a native confirmation dialog requires the user to lock exposure externally; auto exposure invalidates calibration. Unsupported fixed white balance requires external lock confirmation too. User-confirmed controls remain distinct from API-verified controls, and automatic exposure searches skip externally controlled cameras. A versioned GUI may ship a sibling hdr-probe.exe, leaving the currently running calibration executable untouched. Build-only and offline matcher tests passed; physical compatibility with unsupported-control cameras remains untested.

Calibration now uses one native camera/window session across all acquisition phases, driven by atomic local request files and per-phase completion files. Each phase retains its exposure checks, reference measurements, timing measurement and existing pattern plan; camera/device initialization and window creation happen once. Cancellation and final completion close the companion and restore API-owned settings. The new versioned build was compiled and passed offline model/matcher checks without opening the camera or interrupting the running test. Physical multi-phase session validation remains outstanding.
Shutter policy: initialize each calibration phase at the longest standard DirectShow log2 exposure within a 30 fps frame (-5, 1/32 second). The existing signal/headroom checks shorten exposure as necessary; adaptive exposure searches cannot lengthen beyond -5. This replaces the previous forced 1/256-second preparation shutter. Controls confirmed externally remain untouched. Exact 1/30 second is not representable by the generic integer-log2 DirectShow exposure interface. Build and offline matcher checks passed; live flicker behavior remains unverified.
Persistent-session timeout fix: validation preparation failed before recording samples after initial/HDR/neutral phases completed. A request can reset native phase/stimulus timestamps after the outer loop cached its unsigned clock, triggering a false timeout. Tick calls now use a fresh clock after request handling, and preparation watchdogs guard against future timestamps and unpresented stimuli. Regression self-checks exercise future phase/stimulus clocks and pending presentation. Training metadata, observations and a provisional model are checkpointed before validation; final model publication still requires the existing workflow. The user-requested configurable 1000-nit test ceiling remains queued, not implemented.

Training sweeps now group by surround/color/intensity and descend from the largest area. Two consecutive descending samples must match with at most 1% signal correction, agree with the raw observation within measured local response/noise, and close with stable references before smaller planned areas are skipped. Skips are recorded explicitly as skipped_flat with NaN camera measurements; they are excluded from fitting and accepted separately from completed measurements. Validation retains every planned area and can generate adaptive neighbors for missed regions. This assumes lower-area flatness after confirmed onset; non-monotonic behavior is checked through holdouts rather than treated as established. Flat-state reset/group isolation, 1% boundaries, native matching and managed plans pass offline tests. Correction/validation plots fit their tab width while retaining aspect ratio and scroll vertically. Physical speedup remains unverified. Configurable peak ceiling remains queued.
Brightness-adaptive sampling: same-pattern brightness families are ordered brightest first, with area descending within each intensity. A stable, independently raw-checked full-screen match requiring at most 1% gain establishes a lower-brightness cutoff; lower intensities in that family are recorded as skipped rather than measured. Uniform neutral patterns share a gray family; HDR primary/mixed patterns use separate fixed-palette families. Brightness cutoffs persist across acquisition phases, while validation and high-error adaptive followups bypass both early-stop rules. This relies on lower-intensity monotonicity within a fixed pattern family and is checked by unchanged holdouts. Full-screen-only and family-isolation regression checks pass; physical sampling reduction remains unverified.
Single executable distribution: build-single.ps1 creates build/single/OledCalibration.exe, self-contained win-x64 with the .NET desktop runtime. Native capture/device/symbol helpers, hook DLL, symbol libraries, public-domain benchmarks and notices are embedded. Runtime payload is extracted into a content-versioned LocalAppData/OledCalibration/packages folder; calibration reports/settings live there without requiring the source checkout. Native DLL extraction is necessary for DWM injection. Standalone verification copies only the executable outside the checkout and exercises extraction, device enumeration, matcher self-tests and benchmark decoding/checksums without opening the camera or injecting DWM.
Companion click fix: managed calibration blocks manual ROI selection and keyboard stimulus changes throughout the session, including idle gaps between phases. Legacy ROI drags no longer clear an existing region on mouse-down and cancel cleanly on capture loss. Missing camera frames and invalid measurement regions have explicit 12-second watchdogs rather than leaving the parent waiting indefinitely. The confirmed bug was mouse-down clearing ROI during HSV acquisition. Focus-change/hardware behavior still requires physical validation. Updated distribution remains a single executable.
User calibration controls: Panel peak (nits) defaults to 1000, range 250-10000; it limits nominal surround sampling only, with no filter or matching signal clamp. Express mode samples only W/R/G/B at 250 and selected peak, retains 55% area holdouts, and omits fine-gray, photo/mixed tests and adaptive rounds. Standard mixed palettes now alternate full-bright and 4% tiles; mixed holdouts also contain dark tiles. Changing mode or lowering peak on an existing calibration requires New calibration. Native measurement sessions request continuous display/system execution state to prevent sleep, restoring it on exit, and both display/companion windows are topmost. Offline tests verify bright/dark texture content, express palette/level coverage, a 700-nit ceiling and peak=250 deduplication. Single executable smoke tests pass; live calibration and power/focus behavior remain unverified.
Pattern schedule v2 supersedes percentage-gray entries: standard initial samples are W/R/G/B plus all six two-color pairings at 100, 250, 500 and 1000 signal nits, capped at selected peak; custom peak is also included if distinct. Two-color palettes contain a full-level tile and a 4%-level tile of the other color. White-only fine sweeps add 750, 350, 200, 150, 75, 50 and 25 nits, with 17 candidate areas and four heldout areas per level. Existing descending-window and brightness-family early stops remain; lower gray brightnesses skip once full-area flatness is established, while validation remains measured. No old percentage-gray seed patterns remain. Express retains W/R/G/B at 250 and selected peak. Existing models can still be applied, but refinement requires new pattern-version metadata; create a New calibration to adopt v2. Pattern coverage, neutral plans, mixed brightness and fit regression checks pass; live measurement not rerun.
Future single executable builds enable .NET bundle compression. Companion keyboard test commands and their help panel were removed; the panel shows current acquisition status. Preview, ROI, status and graph are rendered into an offscreen bitmap and blitted together, with background erasure suppressed, preventing intermediate cleared graph/text frames. Built in a separate package directory without opening the camera or replacing/restarting any running calibration. Native self-tests and compressed-package smoke test passed; physical repaint verification remains pending.
Calibration persistence fix: packaged GUI stores new reports and selected-model settings under LocalAppData/OledCalibration/data, independent of runtime package hashes. On first use it recovers the newest valid selected model from historical package settings, retaining absolute paths to old reports without moving files or disturbing other versions. Extracted native dependencies remain versioned. Refinement without a model and metadata gives actionable New calibration/select-model guidance instead of a missing-file exception. Compressed standalone smoke test verifies data root is not inside packages.
Refinement now preserves existing training rows and metadata. White refinement inserts geometric midpoint nit levels and midpoint window areas between existing neutral samples, with bounded 8-level/10-area candidate coverage; repeated refinements use the enlarged dataset to generate fresh midpoints. High-error neighbors reject duplicate area/moment states before acquisition. Validation may still remeasure existing holdouts to assess the updated model, distinct from training. Offline tests verify novel brightness and area coordinates on first and repeated white refinement; no physical refinement run performed.
Gray-background samples: standard mode adds backgrounds of 50 and 100 nits with 250- and 500-nit white regions (capped by peak), at 5/25/50/75/100% area. Full-frame scene moments explicitly include the fixed center probe. Refinement adds 350-nit regions at 15/37.5/62.5/87.5%, excludes existing full-frame states and appends observations. Express remains primary-only. New adaptive training plans now start matching from the current fitted model, and native matching seeds its local camera-response slope from the settled raw/predicted pair when their signal separation is sufficient. Existing crossing damping, latency guards, bounded coarse steps and discrete final windows remain. Offline coverage/novelty and matcher tests pass; live speedup is not established.
Single-frame acquisition supersedes three-frame averaging: use the most recent settled camera frame for reference/match observations and graph samples. Settling history is retained to detect response stability, not averaged into the result. The extra two-frame final-window guard is removed; measured camera latency and settling remain. HSV noise estimates use interframe variation where available; single-frame white preparation has no sample-mean standard-error estimate. Native regression tests pass; physical speedup/noise effects remain unverified.


Experimental histogram PCA is available alongside the moment model. See
[histogram-pca.md](histogram-pca.md) for representation, measured GPU costs and limitations.

New PCA calibrations use [adaptive gamut sampling](gamut-sampling.md), with on-demand
coverage probes and local reinforcement. The pre-PCA executable is preserved in
`releases/pre-pca`.

The current release is PCA-only, with controlled 20/80 and 80/20 brightness
mixtures and progress labels for major calibration phases. Historical moment
models require the preserved pre-PCA executable.

Hook enable/disable now uses a resident bypass API and full D3D context isolation.
See [hook-stability.md](hook-stability.md); changing resident binaries requires a
new sign-in session. GPU state regression checks pass, but the Cyberpunk crash
reproduction has not yet been retested.
