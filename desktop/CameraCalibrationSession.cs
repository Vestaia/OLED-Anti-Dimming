// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;

namespace OledCalibration;

static partial class ManagedCalibration
{
    sealed class CameraCalibrationSession(string root, string monitor, string camera, Action<Process?> track, bool mosaic = false) : IAsyncDisposable
    {
        Process? process;
        readonly string requestPath = Path.Combine(root, "build", "camera-request-" + Guid.NewGuid().ToString("N"));
        public async Task Measure(string plan, string output, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string progressFile = output + ".progress", resultFile = output + ".done";
            File.Delete(resultFile);
            File.Delete(progressFile);
            if (process == null)
            {
                var companion = Path.Combine(AppContext.BaseDirectory, "hdr-probe.exe");
                if (!File.Exists(companion))
                    companion = Path.Combine(root, "build", "hdr-probe.exe");
                var start = new ProcessStartInfo(companion) { WorkingDirectory = root, UseShellExecute = false };
                start.ArgumentList.Add("--calibration-session");
                start.ArgumentList.Add(requestPath);
                start.Environment["OLED_CALIBRATION_DISPLAY"] = monitor;
                start.Environment["OLED_CALIBRATION_CAMERA"] = camera;
                start.Environment["OLED_CALIBRATION_MOSAIC"] = mosaic ? "1" : "0";
                process = Process.Start(start) ?? throw new Exception("Camera tool could not start");
                track(process);
            }
            if (process.HasExited)
                throw new Exception("Camera session closed");
            File.WriteAllLines(requestPath + ".tmp", [plan, output, progressFile, resultFile]);
            File.Move(requestPath + ".tmp", requestPath, true);
            Step(acquisitionStart, "Camera latency calibration");
            while (!File.Exists(resultFile))
            {
                token.ThrowIfCancellationRequested();
                if (process.HasExited)
                    throw new Exception("Camera acquisition stopped or failed");
                await Task.Delay(100, token);
                try
                {
                    var status = File.ReadAllText(progressFile).Trim().Split(',');
                    if (status.Length == 3 && int.TryParse(status[0], out int done) && int.TryParse(status[1], out int total) && total > 0)
                        Step(acquisitionStart + (acquisitionEnd - acquisitionStart) * Math.Clamp(done, 0, total) / total, status[2] == "Camera sync" ? "Camera latency calibration" : acquisitionPhase);
                }
                catch (IOException) { }
            }
            // The native writer may still be completing its tiny result file.
            string result = "";
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    result = File.ReadAllText(resultFile).Trim();
                }
                catch (IOException) { }
                if (result.Length > 0)
                    break;
                await Task.Delay(20, token);
            }
            if (!result.StartsWith("PASS", StringComparison.Ordinal))
                throw new Exception("Camera acquisition failed: " + result);
            File.Delete(resultFile);
            File.Delete(progressFile);
        }
        public async ValueTask DisposeAsync()
        {
            activeSession = null;
            try
            {
                if (process != null && !process.HasExited)
                {
                    process.CloseMainWindow();
                    var exit = process.WaitForExitAsync();
                    if (await Task.WhenAny(exit, Task.Delay(3000)) != exit)
                        process.Kill();
                    await process.WaitForExitAsync();
                }
            }
            finally
            {
                track(null);
                process?.Dispose();
                File.Delete(requestPath);
                File.Delete(requestPath + ".tmp");
            }
        }
    }
}
