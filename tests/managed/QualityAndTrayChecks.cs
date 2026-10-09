// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;

namespace OledCalibration;

static class QualityAndTrayChecks
{
    public static void Run()
    {
        string cleanupFixture = Path.Combine(Path.GetTempPath(), "oled-cleanup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(cleanupFixture, "nested"));
        File.WriteAllText(Path.Combine(cleanupFixture, "nested", "diagnostic.log"), "test");
        if (ApplicationDataCleanup.DeleteTree(cleanupFixture) != 0 || Directory.Exists(cleanupFixture))
            throw new Exception("Cleanup did not remove nested logs and their folders");
        DeviceInventory.ValidateHdr(new DisplayDefaults { detected = true, hdr = true });
        foreach (var display in new DisplayDefaults?[] { null, new() { detected = false, hdr = true }, new() { detected = true, hdr = false, advancedColor = true } })
        {
            bool rejected = false;
            try { DeviceInventory.ValidateHdr(display); } catch (InvalidOperationException) { rejected = true; }
            if (!rejected) throw new Exception("HDR preflight accepted disabled or unknown HDR");
        }
        CheckExactPatternHistogram();
        CheckNeighborInterpolation();
        CheckValidationRetention();
        CheckBridgeIdentity();
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
        var buttons = Descendants(window).OfType<Button>().ToArray();
        if (!buttons.Any(b => b.Text == "Refine") || !buttons.Any(b => b.Text == "Delete all data") || buttons.Any(b => b.Text.Contains("white / gray")))
            throw new Exception("Calibration actions still expose obsolete refinement controls");
        var apply = buttons.Single(b => b.Text == "Apply");
        var disable = buttons.Single(b => b.Text == "Disable filter");
        if (apply.Parent != disable.Parent || apply.Parent is not TableLayoutPanel actions ||
            actions.ColumnStyles.Count != 2 || actions.ColumnStyles.Cast<ColumnStyle>().Any(c => c.SizeType != SizeType.Percent || c.Width != 50) || apply.BackColor != Color.LightBlue)
            throw new Exception("Filter actions must share an equal-width row with an accented Apply button");
        if (tabs.TabPages.Count != 2 || tabs.TabPages[1].Text != "Calibration quality")
            throw new Exception("Results tabs do not match end-user summary");
        window.Size = new Size(1200, 800);
        window.PerformLayout();
        int expandedWidth = apply.Parent!.Width;
        window.Size = new Size(800, 640);
        window.PerformLayout();
        if (apply.Parent.Width != expandedWidth || apply.Parent.Width > window.ClientSize.Width || Math.Abs(apply.Width - disable.Width) > 1)
            throw new Exception("Filter action row changed its fixed width or exceeded the window");
        if (!buttons.Any(b => b.Text == "Express calibration") || Descendants(window).OfType<CheckBox>().Any(c => c.Text == "Express calibration"))
            throw new Exception("Express calibration must be a button");
        window.Close();
        if (window.IsDisposed || window.Visible) throw new Exception("Closing the window did not preserve tray lifetime");
        window.AllowExit = true;
        window.Close();
        if (!window.IsDisposed) throw new Exception("Explicit exit did not close the window");
    }
    static void CheckBridgeIdentity()
    {
        byte[] image = File.ReadAllBytes(Path.Combine(Backend.Root, "build", "oled-hook-bridge.dll"));
        byte[] differentTimestamp = (byte[])image.Clone();
        int pe = BitConverter.ToInt32(image, 60);
        differentTimestamp[pe + 8] ^= 1;
        if (!BridgeBinary.Fingerprint(image).SequenceEqual(BridgeBinary.Fingerprint(differentTimestamp)))
            throw new Exception("Bridge linker timestamp falsely changed its identity");
        byte[] changed = (byte[])image.Clone();
        changed[^1] ^= 1;
        if (BridgeBinary.Fingerprint(image).SequenceEqual(BridgeBinary.Fingerprint(changed)))
            throw new Exception("Bridge identity accepted a substantive binary change");
    }
    static void CheckValidationRetention()
    {
        var meta = new CalibrationMetadata { scenes = [new() { name = "good", scene = "a" }, new() { name = "drift", scene = "b" }, new() { name = "plateau", scene = "c" }] };
        Dictionary<string, string> Row(string name, string role, double measured, double signal = 250) => new()
        {
            ["name"] = name,
            ["role"] = role,
            ["camera_code"] = measured.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["reference_code"] = "40",
            ["signal_nits"] = signal.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        List<Dictionary<string, string>> validation = [Row("good", "predicted_adaptive", 30, 275), Row("good", "matched", 40, 320), Row("a", "end_reference", 40),
            Row("drift", "matched", 40, 330), Row("b", "end_reference", 42.4), Row("plateau", "matched", 30, 400), Row("plateau", "panel_plateau", 30), Row("c", "end_reference", 40)];
        var training = new List<Dictionary<string, string>>();
        if (ManagedCalibration.RetainValidationMatches(training, meta, validation) != 1 || !meta.scenes[0].training || meta.scenes[1].training || meta.scenes[2].training ||
            training.Single(r => r["role"] == "matched")["signal_nits"] != "320" || training.Any(r => r["role"] == "predicted_adaptive"))
            throw new Exception("Validation retention did not select stable measured correction targets");
        if (ManagedCalibration.RetainValidationMatches(training, meta, validation) != 0 || training.Count != 2)
            throw new Exception("Validation matches were counted twice");
        if (validation[0]["camera_code"] != "30") throw new Exception("Pre-update validation evidence was altered");
    }
    static void CheckNeighborInterpolation()
    {
        double[] State(double position) { var result = new double[14]; result[0] = position; return result; }
        var model = new CalibrationModel([State(.2), State(.6), State(.9)], [.1, .2, .3]);
        if (!model.LocalNeighbors || Math.Abs(model.Predict(State(.6)) - .2) > 1e-5)
            throw new Exception("Neighbor interpolator does not track measured corrections");
        double previous = model.Predict(State(.9));
        for (double x = .901; x <= 1.2; x += .001)
        {
            double value = model.Predict(State(x));
            if (!double.IsFinite(value) || value < 0 || value > .3 + 1e-12 || Math.Abs(value - previous) > .01)
                throw new Exception("Neighbor interpolation bounds/continuity failed");
            previous = value;
        }
        if (model.Predict(State(1)) < .25 || model.Predict(State(10)) < .15)
            throw new Exception("Sparse predictions collapse toward unity gain");
        var black = new CalibrationModel([State(.2), State(.9)], [0, 0]);
        if (black.Predict(State(1)) != 0) throw new Exception("Constant correction is not preserved");
    }
    static void CheckExactPatternHistogram()
    {
        string path = Path.GetTempFileName();
        try
        {
            double[][] pixels = [[.08, .2, .7], [3, .1, .2], [.4, .4, .4], [0, 0, 0], [.2, 2, .1], [.9, .3, .2]];
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write(3); writer.Write(2);
                foreach (var pixel in pixels) { foreach (var channel in pixel) writer.Write((float)channel); writer.Write(1f); }
            }
            foreach (bool mosaic in new[] { false, true })
                foreach (double area in new[] { .01, .17, .53, 1.0 })
                {
                    var scene = new CalibrationScene { asset = path, area = area, display_width = 97, display_height = 53, mosaic_probe = mosaic };
                    var exact = ManagedCalibration.SceneHistogram(scene);
                    var raster = new double[HistogramPca.Bins];
                    int side = ProbePattern.Side(97, 53), left = (97 - side) / 2, top = (53 - side) / 2;
                    float half = MathF.Sqrt((float)area) * .5f;
                    for (int y = 0; y < 53; y++)
                        for (int x = 0; x < 97; x++)
                        {
                            float u = (x + .5f) / 97, v = (y + .5f) / 53;
                            bool probe = mosaic ? x >= left && x < left + side && y >= top && y < top + side : MathF.Abs(u - .5f) <= .05f && MathF.Abs(v - .5f) <= .05f;
                            double[] color = [0, 0, 0];
                            if (probe) color = mosaic ? ProbePattern.Pixel(x - left, y - top) : [.4, .4, .4];
                            else if (MathF.Abs(u - .5f) <= half && MathF.Abs(v - .5f) <= half)
                            {
                                int tx = Math.Clamp((int)(((u - .5f) / (2 * half) + .5f) * 3), 0, 2);
                                int ty = Math.Clamp((int)(((v - .5f) / (2 * half) + .5f) * 2), 0, 1);
                                color = pixels[ty * 3 + tx].Select(c => (double)(float)c).ToArray();
                            }
                            HistogramPca.Add(raster, color, 1.0 / (97 * 53));
                        }
                    if (exact.Zip(raster).Any(pair => Math.Abs(pair.First - pair.Second) > 1e-11) || Math.Abs(exact.Sum() - 1) > 1e-11)
                        throw new Exception("Exact pattern histogram differs from exhaustive raster coverage");
                }
        }
        finally { File.Delete(path); }
    }
}
