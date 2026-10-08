// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;

namespace OledCalibration;

static partial class ManagedCalibration
{
    public static void CorrectionHeatmap(string output, CalibrationModel model)
    {
        double[] areas = [.02, .06, .10, .15, .18, .22, .26, .30, .34, .38, .42, .50, .60, .70, .80, .90, 1];
        var colors = new List<(string label, double[] rgb)> { ("R", [1, 0, 0]), ("G", [0, 1, 0]), ("B", [0, 0, 1]), ("White", [1, 1, 1]) };
        foreach (double nits in new[] { 25.0, 50, 75, 100, 150, 200, 350, 750 })
            colors.Add(($"{nits:g}n W", [nits / 250, nits / 250, nits / 250]));
        foreach (double nits in new[] { 500.0, 1000, 2500 })
            foreach (var (label, rgb) in new List<(string, double[])> { ("W", [1, 1, 1]), ("R", [1, 0, 0]), ("G", [0, 1, 0]), ("B", [0, 0, 1]) })
                colors.Add(($"{label} {nits:g}n", rgb.Select(v => v * nits / 250).ToArray()));
        const int cell = 55, left = 100, top = 90;
        using var bitmap = new Bitmap(left + cell * areas.Length + 10, top + colors.Count * 38 + 20);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.White);
        using var font = new Font("Segoe UI", 9);
        g.DrawString("Scene brightness correction (%)", font, Brushes.Black, 10, 12);
        g.DrawString("Model prediction; window area across columns", font, Brushes.DimGray, 10, 35);
        for (int a = 0; a < areas.Length; a++)
            g.DrawString($"{areas[a] * 100:g}%", font, Brushes.Black, left + a * cell, 65);
        for (int row = 0; row < colors.Count; row++)
        {
            var color = colors[row];
            g.DrawString(color.label, font, Brushes.Black, 4, top + row * 38 + 8);
            var moments = Moments([color.rgb]);
            for (int a = 0; a < areas.Length; a++)
            {
                var scene = new CalibrationScene { area = areas[a], moments = moments };
                double boost = 100 * (Math.Exp(model.Predict(scene)) - 1);
                double strength = Math.Clamp(boost / 150, 0, 1);
                using var brush = new SolidBrush(Color.FromArgb((int)(255 - 130 * strength), (int)(255 - 75 * strength), 255));
                int x = left + a * cell, y = top + row * 38;
                g.FillRectangle(brush, x, y, cell - 3, 34);
                g.DrawString($"+{boost:0.#}", font, Brushes.Black, x + 3, y + 8);
            }
        }
        bitmap.Save(Path.Combine(output, "corrections-heatmap.png"), System.Drawing.Imaging.ImageFormat.Png);
    }
    public static void CalibrationQualitySummary(string output, CalibrationMetadata meta, List<Dictionary<string, string>> rows)
    {
        var lookup = meta.scenes.ToDictionary(scene => scene.name);
        var closing = Closing(rows);
        var summary = new Dictionary<string, object>
        {
            ["units"] = "relative_camera_brightness_percent"
        };
        foreach (string role in new[] { "raw", "predicted_adaptive", "calibrated" })
        {
            var errors = rows.Where(row => row["role"] == role && lookup.ContainsKey(row["name"]))
                .GroupBy(row => row["name"]).Select(group => group.Last())
                .Where(row => ClosingError(closing, lookup[row["name"]]) <= .5)
                .Select(row => RelativeBrightnessError(Value(row, "camera_code"), Value(row, "reference_code")))
                .Where(double.IsFinite).ToArray();
            summary[role] = QualityMetrics(errors);
        }
        File.WriteAllText(Path.Combine(output, "validation-summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
    }
    internal static double RelativeBrightnessError(double measured, double reference) =>
        reference > 0 && double.IsFinite(reference) && double.IsFinite(measured) ? 100 * (measured - reference) / reference : double.NaN;
    internal static object QualityMetrics(double[] errors) => new
    {
        samples = errors.Length,
        rms = errors.Length == 0 ? (double?)null : Math.Sqrt(errors.Average(v => v * v)),
        max_abs = errors.Length == 0 ? (double?)null : errors.Max(Math.Abs)
    };
}
