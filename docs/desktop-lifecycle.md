# Desktop lifecycle and calibration quality

The results UI contains Corrections summary and Calibration quality. Validation
measurements remain part of fitting/refinement, but the validation heatmap is no
longer generated or displayed. RMS and maximum errors are calculated from
`100 * (camera_response - reference_response) / reference_response`, after rejecting
drifted references. These percentages describe relative camera brightness, not
absolute luminance or a linear camera-to-light conversion. Empty measurements
display a dash rather than zero error. Existing reports are recalculated from
their validation CSV when opened. Current runs use fullscreen photograph
benchmarks for this summary, reporting predictions before absorbing their
camera-matched corrections into the saved model.

Closing the window hides it in the tray and leaves active calibration running.
Double-clicking the icon or choosing Open restores it. Launching a second copy
signals the existing instance instead of creating a second controller.

- Exit stops any calibration gracefully and leaves the current filter active.
- Disable filter and exit also removes the detours and unloads the filter DLL before closing. Failure
  leaves the application running and reports the error in a tray notification.

Start with Windows registers a per-user Task Scheduler logon task with the user's
interactive token and highest available privileges. It stores no password, waits
10 seconds after sign-in, launches the selected executable with `--startup`, and
has no runtime time limit or battery restriction. Unchecking removes the task.
Keep that executable at its registered location. Startup loads the saved model
and detected HDR state, applies the filter, and remains in the tray; failures
produce a tray notification without opening the main window.

The native hook exports an atomic status word. The GUI reads it from the current
session's DWM process without executing a remote query or changing the hook,
refreshing on open, after filter operations, and periodically. A resident DLL
with disabled detours reports Disabled. Older hooks without the status export
report Unavailable, rather than guessing from stale marker files. New hooks use [controlled unloading](controlled-unload.md); older pinned hooks
require one sign-out to transition. The small admission bridge remains resident.

Offline checks cover percentage/RMS calculations, empty summaries, the two-tab
layout, and hide-versus-exit behavior. Live sign-in and filter interaction require
testing with the new executable; tests do not enable startup or inject into DWM.
