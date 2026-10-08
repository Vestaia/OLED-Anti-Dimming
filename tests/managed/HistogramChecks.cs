// SPDX-License-Identifier: GPL-3.0-only
namespace OledCalibration;

static class HistogramChecks
{
    public static void Run(string root)
    {
        string folder = Path.Combine(root, "build", "histogram-test-config"); Directory.CreateDirectory(folder);
        var white = new double[512]; HistogramPca.Add(white, [1, 1, 1]);
        var mixed = new double[512]; foreach (var c in new double[][] { [3, 0, 0], [0, 3, 0], [0, 0, 3] }) HistogramPca.Add(mixed, c, 1.0 / 3);
        var encoder = HistogramPca.Fit([white, mixed]);
        if (Math.Abs(white.Sum() - 1) > 1e-12 || Math.Abs(mixed.Sum() - 1) > 1e-12) throw new Exception("Histogram mass not conserved");
        if (encoder.Project(white).Zip(encoder.Project(mixed), (a, b) => (a - b) * (a - b)).Sum() < .001) throw new Exception("Equal-mean multimodal distributions collapsed");
        foreach (var a in encoder.Basis) foreach (var b in encoder.Basis)
        {
            double expected = ReferenceEquals(a, b) ? 1 : 0;
            if (Math.Abs(a.Zip(b, (x, y) => x * y).Sum() - expected) > 1e-8) throw new Exception("PCA basis is not orthonormal");
        }
        var hists = Enumerable.Range(1, 80).Select(i => { var h = new double[512]; HistogramPca.Add(h, [i / 20.0, i / 20.0, i / 20.0]); return h; }).ToArray();
        var model = new CalibrationModel(hists.Select(encoder.Project).ToArray(), Enumerable.Range(1, 80).Select(i => .1 * Math.Sin(i / 80.0 * Math.PI)).ToArray()) { Histogram = encoder };
        string path = Path.Combine(folder, "runtime-model.json"); File.WriteAllText(path, model.Json("")); Backend.WriteModel(path, Path.Combine(folder, "runtime.bin"));
        var loaded = CalibrationModel.Load(path);
        if (Math.Abs(model.Predict(encoder.Project(white)) - loaded.Predict(loaded.Histogram!.Project(white))) > 1e-10) throw new Exception("Histogram model roundtrip changed predictions");
        foreach (var field in new[] { "coefficients", "scale", "histogram_pca" })
        {
            var invalid = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            if (field == "coefficients") invalid[field]!.AsArray().RemoveAt(0);
            else if (field == "scale") invalid[field]![0] = 0;
            else invalid[field]!["Basis"]!.AsArray().RemoveAt(0);
            string invalidPath = Path.Combine(folder, "invalid-model.json");
            File.WriteAllText(invalidPath, invalid.ToJsonString());
            bool rejected = false;
            try { Backend.WriteModel(invalidPath, Path.Combine(folder, "invalid.bin")); }
            catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new Exception("Invalid model accepted: " + field);
        }
        // Expanded synthetic workload isolates inference scaling, not fit quality.
        // Keep the original 81 centers and pad with zero-weight centers.
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        var centers = json["centers"]!.AsArray(); var coefficients = json["coefficients"]!.AsArray();
        while (centers.Count < 512) { centers.Add(centers[0]!.DeepClone()); coefficients.Add(0.0); }
        string largeFolder = Path.Combine(root, "build", "histogram-test-512"); Directory.CreateDirectory(largeFolder);
        string largePath = Path.Combine(largeFolder, "runtime-model.json"); File.WriteAllText(largePath, json.ToJsonString()); Backend.WriteModel(largePath, Path.Combine(largeFolder, "runtime.bin"));
        File.WriteAllText(Path.Combine(folder, "checks.txt"), "PASS: histogram mass, equal-mean multimodal separation, orthonormal PCA, model roundtrip and binary export.\n");
    }
}
