// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;

namespace OledCalibration;

static partial class ManagedCalibration
{
    static string CompactLabel(string name, int index)
    {
        foreach (var (prefix, label) in new[] { ("gamut-solid-", "S"), ("gamut-cluster-", "C") })
            if (name.StartsWith(prefix))
                return label + name[prefix.Length..].Split('_')[0];
        if (name.StartsWith("gamut-probe-"))
            return "P" + (index + 1);
        if (name.StartsWith("neutral-fine-nits-"))
            return name[18..] + "n";
        if (name.StartsWith("neutral-fine-"))
            return $"Gray {double.Parse(name[13..], Inv) * 100:g}%";
        if (name.StartsWith("holdout"))
            return "H" + name[7..];
        if (name.StartsWith("adapt_"))
            return "A" + (index + 1);
        if (name.StartsWith("checker"))
            return name.Replace("checker", "Mix ").Replace("_v", ".");
        return name.Replace("photo-", "");
    }
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
    public static void Heatmap(string output, CalibrationMetadata meta, List<Dictionary<string, string>> rows)
    {
        var lookup = meta.scenes.ToDictionary(r => r.name);
        var names = rows.Select(r => r["name"]).Distinct().Where(lookup.ContainsKey).ToArray();
        var scenes = names.Select(n => lookup[n].scene).Distinct().ToArray();
        var areas = names.Select(n => lookup[n].area).Distinct().Order().ToArray();
        var closing = Closing(rows);
        string[] roles = ["raw", "predicted_adaptive", "calibrated"], titles = ["Raw", "Adaptive model", "Feedback match"];
        var data = new Dictionary<(int, int, int), double>();
        foreach (var r in rows)
        {
            int role = Array.IndexOf(roles, r["role"]);
            if (role < 0 || !lookup.TryGetValue(r["name"], out var scene) || ClosingError(closing, scene) > .5)
                continue;
            data[(role, Array.IndexOf(scenes, scene.scene), Array.IndexOf(areas, scene.area))] = Value(r, "camera_code") - Value(r, "reference_code");
        }
        double limit = Math.Max(1, data.Count == 0 ? 1 : data.Values.Max(Math.Abs));
        int panel = 380, cell = 260 / Math.Max(1, areas.Length), height = Math.Max(350, 100 + scenes.Length * 38);
        using var bitmap = new Bitmap(panel * 3, height);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.White);
        using var font = new Font("Segoe UI", 10);
        g.DrawString("Relative camera-code error (not luminance %) - missing or drift-rejected: x", font, Brushes.Black, 10, 10);
        var summary = new Dictionary<string, object>();
        for (int k = 0; k < 3; k++)
        {
            int origin = k * panel;
            g.DrawString(titles[k], font, Brushes.Black, origin + 140, 38);
            for (int a = 0; a < areas.Length; a++)
                g.DrawString($"{areas[a] * 100:g}%", font, Brushes.Black, origin + 125 + a * cell, 65);
            for (int s = 0; s < scenes.Length; s++)
            {
                int y = 90 + s * 38;
                g.DrawString(CompactLabel(scenes[s], s), font, Brushes.Black, origin + 3, y + 6);
                for (int a = 0; a < areas.Length; a++)
                {
                    int x = origin + 120 + a * cell;
                    bool found = data.TryGetValue((k, s, a), out double v);
                    double f = Math.Min(1, Math.Abs(v) / limit);
                    var color = !found ? Color.LightGray : v >= 0 ? Color.FromArgb(255, (int)(255 * (1 - f)), (int)(255 * (1 - f))) : Color.FromArgb((int)(255 * (1 - f)), (int)(255 * (1 - f)), 255);
                    using var brush = new SolidBrush(color);
                    g.FillRectangle(brush, x, y, cell - 4, 34);
                    g.DrawString(found ? v.ToString("+0.00;-0.00;0.00", Inv) : "x", font, Brushes.Black, x + 12, y + 6);
                }
            }
            var values = data.Where(p => p.Key.Item1 == k).Select(p => p.Value).ToArray();
            summary[roles[k]] = new
            {
                samples = values.Length,
                rms = values.Length == 0 ? 0 : Math.Sqrt(values.Average(v => v * v)),
                max_abs = values.Length == 0 ? 0 : values.Max(Math.Abs)
            };
        }
        bitmap.Save(Path.Combine(output, "validation-heatmap.png"), System.Drawing.Imaging.ImageFormat.Png);
        File.WriteAllText(Path.Combine(output, "validation-summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
    }
}
