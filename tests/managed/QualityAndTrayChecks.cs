// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;

namespace OledCalibration;

static class QualityAndTrayChecks
{
    public static void Run()
    {
        double error = ManagedCalibration.RelativeBrightnessError(44, 40);
        if (Math.Abs(error - 10) > 1e-10 || !double.IsNaN(ManagedCalibration.RelativeBrightnessError(4, 0)))
            throw new Exception("Relative camera error normalization failed");
        using var quality = JsonDocument.Parse(JsonSerializer.Serialize(ManagedCalibration.QualityMetrics([3, -4])));
        if (Math.Abs(quality.RootElement.GetProperty("rms").GetDouble() - Math.Sqrt(12.5)) > 1e-10 || quality.RootElement.GetProperty("max_abs").GetDouble() != 4)
            throw new Exception("RMS/maximum quality calculation failed");
        using var empty = JsonDocument.Parse(JsonSerializer.Serialize(ManagedCalibration.QualityMetrics([])));
        if (empty.RootElement.GetProperty("rms").ValueKind != JsonValueKind.Null)
            throw new Exception("Missing measurements were reported as perfect calibration");
        using var window = new MainWindow();
        _ = window.Handle;
        IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
        var tabs = Descendants(window).OfType<TabControl>().Single();
        if (tabs.TabPages.Count != 2 || tabs.TabPages[1].Text != "Calibration quality")
            throw new Exception("Results tabs do not match end-user summary");
        window.Close();
        if (window.IsDisposed || window.Visible) throw new Exception("Closing the window did not preserve tray lifetime");
        window.AllowExit = true;
        window.Close();
        if (!window.IsDisposed) throw new Exception("Explicit exit did not close the window");
    }
}
