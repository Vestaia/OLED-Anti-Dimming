// SPDX-License-Identifier: GPL-3.0-only
namespace OledCalibration;

static class HistogramChecks
{
    public static void Run(string root)
    {
        string folder = Path.Combine(root, "build", "histogram-test-config"); Directory.CreateDirectory(folder);
        var white = new double[HistogramPca.Bins]; HistogramPca.Add(white, [1, 1, 1]);
        var mixed = new double[HistogramPca.Bins]; foreach (var c in new double[][] { [3, 0, 0], [0, 3, 0], [0, 0, 3] }) HistogramPca.Add(mixed, c, 1.0 / 3);
        var dim = new double[HistogramPca.Bins]; HistogramPca.Add(dim, [.38, .38, .38]);
        var bright = new double[HistogramPca.Bins]; HistogramPca.Add(bright, [.42, .42, .42]);
        if (dim.Zip(bright, (a,b) => Math.Abs(a-b)).Sum() < .5) throw new Exception("HSV value lacks shade resolution");
        var collisionA = new double[HistogramPca.Bins]; HistogramPca.Add(collisionA, [.1, .1, .1]);
        var collisionB = new double[HistogramPca.Bins]; HistogramPca.Add(collisionB, [.8, .8, .8]);
        foreach (var (rgb, hue) in new[] { (new double[] {1,0,0}, 0), (new double[] {0,1,0}, 3), (new double[] {0,0,1}, 6) }) {
            var primary = new double[HistogramPca.Bins]; HistogramPca.Add(primary, rgb);
            if (primary.Where((v,i) => i / (8*128) != hue || i / 128 % 8 != 7).Sum() > 1e-12) throw new Exception("HSV primary hue centers changed");
        }
        var seamA = new double[HistogramPca.Bins]; HistogramPca.Add(seamA, [1, .000001, 0]);
        var seamB = new double[HistogramPca.Bins]; HistogramPca.Add(seamB, [1, 0, .000001]);
        if (seamA.Zip(seamB,(a,b)=>Math.Abs(a-b)).Sum() > .00001) throw new Exception("Hue wrap is discontinuous");
        if (white.Where((v,i)=>i / 128 % 8 != 0 || i / (8*128) != 0).Sum()>1e-12) throw new Exception("Neutral hue did not collapse consistently");
        var halfSat = new double[HistogramPca.Bins]; HistogramPca.Add(halfSat, [1,.5,.5]);
        if (Math.Abs(halfSat.Select((v,i)=>v*(i/128%8)).Sum()-1.75)>1e-12) throw new Exception("Saturation is not square distributed");
        if (Math.Abs(white.Select((v,i)=>v*(i%128)).Sum()-127/Math.Sqrt(40))>1e-12) throw new Exception("Value is not square root distributed");
        var encoder = HistogramPca.Fit([white, mixed, dim, bright, collisionA, collisionB]);
        if (encoder.Project(collisionA).Zip(encoder.Project(collisionB), (a,b) => (a-b)*(a-b)).Sum()<1e-6)
            throw new Exception("PCA discarded the brightness collision distinction");
        if (encoder.Basis.Length != 14 || encoder.InputBins != HistogramPca.Bins) throw new Exception("Combined encoder changed the component budget");
        if (Math.Abs(white.Sum() - 1) > 1e-12 || Math.Abs(mixed.Sum() - 1) > 1e-12) throw new Exception("Histogram mass not conserved");
        if (encoder.Project(white).Zip(encoder.Project(mixed), (a, b) => (a - b) * (a - b)).Sum() < .001) throw new Exception("Equal-mean multimodal distributions collapsed");
        foreach (var a in encoder.Basis) foreach (var b in encoder.Basis)
        {
            double expected = ReferenceEquals(a, b) ? 1 : 0;
            if (Math.Abs(a.Zip(b, (x, y) => x * y).Sum() - expected) > 1e-8) throw new Exception("PCA basis is not orthonormal");
        }
        var hists = Enumerable.Range(1, 80).Select(i => { var h = new double[HistogramPca.Bins]; HistogramPca.Add(h, [i / 20.0, i / 20.0, i / 20.0]); return h; }).ToArray();
        var model = new CalibrationModel(hists.Select(encoder.Project).ToArray(), Enumerable.Range(1, 80).Select(i => .1 * Math.Sin(i / 80.0 * Math.PI)).ToArray()) { Histogram = encoder };
        string path = Path.Combine(folder, "runtime-model.json"); File.WriteAllText(path, model.Json("")); Backend.WriteModel(path, Path.Combine(folder, "runtime.bin"));
        var loaded = CalibrationModel.Load(path);
        if (Math.Abs(model.Predict(encoder.Project(white)) - loaded.Predict(loaded.Histogram!.Project(white))) > 1e-10) throw new Exception("Histogram model roundtrip changed predictions");
        foreach (int bins in new[] { HistogramPca.LegacyColorBins, HistogramPca.LegacyCombinedBins, HistogramPca.LegacyLargeBins })
        {
            var legacyEncoder = new HistogramPca { Basis = encoder.Basis.Select(row => row.Take(bins).ToArray()).ToArray() };
            var legacyHists = Enumerable.Range(1, 80).Select(i => { var h = new double[bins]; HistogramPca.Add(h, [i / 20.0, i / 20.0, i / 20.0]); return h; }).ToArray();
            var legacyModel = new CalibrationModel(legacyHists.Select(legacyEncoder.Project).ToArray(), Enumerable.Range(1, hists.Length).Select(i => .1 * Math.Sin(i / 80.0 * Math.PI)).ToArray()) { Histogram = legacyEncoder };
            string legacyPath = Path.Combine(folder, $"legacy-{bins}-model.json");
            File.WriteAllText(legacyPath, legacyModel.Json(""));
            var legacyLoaded = CalibrationModel.Load(legacyPath);
            var legacyScene = new CalibrationScene { area = .75, moments = [1,1,1,1,1,1,1,1,1,1,0,1,1,1], display_width = 97, display_height = 53, mosaic_probe = true };
            if (legacyLoaded.Histogram!.InputBins != bins || Math.Abs(legacyModel.Predict(legacyScene)-legacyLoaded.Predict(legacyScene))>1e-12)
                throw new Exception("Legacy histogram projection changed");
            string legacyFolder = Path.Combine(root, "build", bins == 512 ? "histogram-test-legacy" : "histogram-test-" + bins); Directory.CreateDirectory(legacyFolder);
            Backend.WriteModel(legacyPath, Path.Combine(legacyFolder, "runtime.bin"));
        }
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
