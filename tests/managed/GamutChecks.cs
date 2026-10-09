// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
namespace OledCalibration;

static partial class ManagedCalibration
{
    public static void GamutChecks(string root)
    {
        string folder = Path.Combine(root, "build", "gamut-test"); Directory.CreateDirectory(folder);
        var points = GamutPoints(1000);
        foreach (double r in new[] { 0.0, .5, 1 }) foreach (double g in new[] { 0.0, .5, 1 }) foreach (double b in new[] { 0.0, .5, 1 })
            if (!points.Any(p => Distance(p, [r, g, b]) < 1e-12)) throw new Exception("Missing cube vertex, edge/face center or gray-axis center");
        if (points.Count != points.Select(p => string.Join(',', p)).Distinct().Count()) throw new Exception("Duplicate gamut points");
        if (points.Count(p => p.All(v => v > 0 && v < 1) && p.Distinct().Count() > 1) < 8) throw new Exception("Missing maximin interior samples");
        var meta = GamutSeeds(Path.Combine(folder, "seeds"), "test", 1000);
        if (meta.scenes.Any(r => !r.training) || meta.scenes.Where(r => r.gamut_points.Any(p => p.Any(v => v > 0))).GroupBy(r => r.scene).Any(g => !g.Select(r => r.area).OrderDescending().SequenceEqual(SweepAreas)))
            throw new Exception("Coarse sweeps reserved validation states or omitted requested descending areas");
        var photoMeta = new CalibrationMetadata { display_width = 2560, display_height = 1440, mosaic_probe = true };
        AddBenchmarks(root, Path.Combine(folder, "photos"), photoMeta);
        if (photoMeta.scenes.Count != 20 || photoMeta.scenes.Any(r => r.area != 1 || !r.mosaic_probe) || photoMeta.sources.Length != 20)
            throw new Exception("Photo quality reporting must use 20 fullscreen licensed images");
        if (meta.scenes.Where(r => r.training && r.gamut_spread > 0).Select(r => r.gamut_points.Length).Distinct().Order().SequenceEqual(new[] { 2, 3, 4, 5 }) == false) throw new Exception("Missing clustered mode counts");
        foreach (var scene in meta.scenes)
        {
            var (pixels, _, _) = Texture(scene.asset);
            if (pixels.Any(p => p.Any(v => v < 0 || v > 4 + 1e-6))) throw new Exception("Pattern exceeds nominal peak");
            if (Math.Abs(scene.Histogram().Sum() - 1) > 1e-10) throw new Exception("Gamut histogram mass changed");
        }
        var weighted = meta.scenes.Where(r => r.training && r.scene.StartsWith("gamut-weighted-")).DistinctBy(r => r.asset).ToArray();
        if (weighted.Length != 6) throw new Exception("Missing uneven brightness mixtures");
        foreach (var pattern in weighted)
        {
            var (pixels, _, _) = Texture(pattern.asset);
            double Luma(double[] p) => .2627 * p[0] + .678 * p[1] + .0593 * p[2];
            var centerLevels = pattern.gamut_points.Select(p => Luma(p.Select(v => v * v * 4).ToArray())).ToArray();
            double threshold = centerLevels.Average();
            double share = pixels.Count(p => Luma(p) > threshold) / (double)pixels.Length;
            if (Math.Abs(share - pattern.gamut_weights[0]) > 1e-8) throw new Exception("Bright/dim tile fractions changed");
        }
        var training = meta.scenes.Where(r => r.training).ToArray();
        if (meta.scenes.Where(r => !r.training).Any(r => training.Any(t => Distance(r.Histogram(), t.Histogram()) < 1e-10))) throw new Exception("Validation duplicated training");
        int plannedCameraScenes = meta.scenes.Count;
        var preparationTimer = System.Diagnostics.Stopwatch.StartNew();
        meta.color_clusters=false;meta.histogram_pca=true; // Explicit legacy calibration regression.
        meta.histogram_basis = LearnPca(root, Path.Combine(folder, "pca-dataset"), meta, _ => { });
        preparationTimer.Stop();
        if (meta.scenes.Count != plannedCameraScenes || meta.histogram_basis.TrainingImages != 68 || meta.histogram_basis.TrainingSamples < 6000 || meta.histogram_basis.Basis.Length != 14 || meta.histogram_basis.InputBins != HistogramPca.Bins || !double.IsFinite(meta.histogram_basis.VarianceExplained) || meta.histogram_basis.VarianceExplained is <= 0 or > 1.000001)
            throw new Exception("Representative PCA training changed camera scheduling or lacks source coverage");
        File.WriteAllText(Path.Combine(folder, "pca-dataset-summary.json"), JsonSerializer.Serialize(new { images = meta.histogram_basis.TrainingImages, distributions = meta.histogram_basis.TrainingSamples, components = 14, bins = HistogramPca.Bins, seconds = preparationTimer.Elapsed.TotalSeconds, variance_explained = meta.histogram_basis.VarianceExplained }));
        Console.WriteLine($"PCA preparation: {meta.histogram_basis.TrainingSamples} distributions, {meta.histogram_basis.TrainingImages} images, {preparationTimer.Elapsed.TotalSeconds:F2}s");
        var anchorScenes = training.Take(5).ToArray();
        var anchorRows = anchorScenes.Select((scene, i) => new Dictionary<string, string> {
            ["name"] = scene.name, ["role"] = i == 4 ? "skipped_flat" : "matched",
            ["signal_nits"] = "500", ["camera_code"] = "40", ["reference_code"] = "40"
        }).ToList();
        // A later inferred label must not replace an actual measurement.
        anchorRows.Add(new(anchorRows[0]) { ["role"] = "skipped_flat" });
        anchorRows.AddRange(anchorScenes.Select(scene => new Dictionary<string, string> {
            ["name"] = scene.scene, ["role"] = "end_reference", ["camera_code"] = "40", ["reference_code"] = "40"
        }));
        var anchored = Fit(anchorRows, meta);
        if (anchored.Centers.Length != 6 || anchored.Coefficients[4] != 0 || Math.Abs(anchored.Coefficients[0] - Math.Log(2)) > 1e-12)
            throw new Exception("Skipped flat samples did not become zero anchors or overrode measured labels");
        var planModel = new CalibrationModel(training.Take(6).Select(r => meta.histogram_basis.Project(r.Histogram())).ToArray(), Enumerable.Repeat(Math.Log(1.5), 6).ToArray()) { Histogram = meta.histogram_basis };
        string onlinePlan = Path.Combine(folder, "online-plan.csv");
        Plan(onlinePlan, training.Take(6), planModel, true);
        var onlineRows = ReadCsv(onlinePlan);
        if (onlineRows.Any(r => r["role"] is "predicted_adaptive" or "raw") || onlineRows.Count(r => r["role"] == "match") != 6 || onlineRows.Where(r => r["role"] == "match").Any(r => !r.ContainsKey("state_13") || r["state_13"] == ""))
            throw new Exception("Online training plan is missing state coordinates or duplicates prediction measurements");
        using (var seed = new BinaryReader(File.OpenRead(onlinePlan + ".online.bin")))
        {
            if (seed.ReadUInt32() != 0x314e4c4f || seed.ReadUInt32() != planModel.Centers.Length)
                throw new Exception("Online training seed header changed");
            for (int i = 0; i < planModel.Centers.Length; i++)
            {
                foreach (double coordinate in planModel.Centers[i])
                    if (seed.ReadDouble() != coordinate) throw new Exception("Online seed coordinate mismatch");
                if (seed.ReadDouble() != planModel.Coefficients[i]) throw new Exception("Online seed gain mismatch");
            }
        }
        Plan(onlinePlan, training.Take(6), planModel);
        if (File.Exists(onlinePlan + ".online.bin") || ReadCsv(onlinePlan).Count(r => r["role"] == "predicted_adaptive") != 6)
            throw new Exception("Validation did not freeze predictions and clear online training");
        var probes = GamutProbes(meta, Path.Combine(folder, "probes"));
        if (probes.Count != 6 || probes.Any(p => p.training || meta.scenes.Any(old => Distance(old.Histogram(), p.Histogram()) < 1e-10))) throw new Exception("Exploration probes not novel");
        meta.scenes.AddRange(probes);
        var nextProbes = GamutProbes(meta, Path.Combine(folder, "another-round", "probes"));
        if (meta.scenes.Concat(nextProbes).Select(r => r.name).Distinct().Count() != meta.scenes.Count + nextProbes.Count) throw new Exception("Probe names collided across phases");
        foreach (var size in new[] { (2560, 1440), (3440, 1440), (1080, 1920), (1920, 1080) })
        {
            int side = ProbePattern.Side(size.Item1, size.Item2);
            if (side % 2 != 0 || side > Math.Min(size.Item1, size.Item2) || Math.Abs(side * (double)side / (size.Item1 * size.Item2) - .01) > .0005) throw new Exception("Probe geometry failed aspect ratio check");
            var average = new double[3];
            for (int y = 0; y < side; y++) for (int x = 0; x < side; x++) { var pixel = ProbePattern.Pixel(x, y); for (int j = 0; j < 3; j++) average[j] += pixel[j] / (side * side); }
            if (average.Any(v => Math.Abs(v - .4) > 1e-10)) throw new Exception("Mosaic does not average to 100-nit RGB");
        }
        var source = meta.scenes.First(r => r.training && r.gamut_points.Length == 1 && r.gamut_points[0].Max() > 0);
        Dictionary<string, string> Row(string role, double code) => new() { ["name"] = role == "end_reference" ? source.scene : source.name, ["role"] = role, ["camera_code"] = Number(code), ["reference_code"] = "100" };
        var validation = new List<Dictionary<string, string>> { Row("predicted_adaptive", 92), Row("end_reference", 100) };
        var added = GamutNeighbors(planModel, meta, Path.Combine(folder, "refine"));
        if (added.Count != 12 || added.Any(r => !r.training)) throw new Exception("Confidence refinement reserved validation patterns");
        var randomChecks = GamutProbes(meta, Path.Combine(folder, "random-validation"), 64, planModel, true);
        if (randomChecks.Count != 64 || randomChecks.Any(r => r.training) || !randomChecks.Any(r => r.area < .15)) throw new Exception("Random validation retained a window floor or training labels");
        var randomChecksAgain = GamutProbes(meta, Path.Combine(folder, "random-validation-repeat"), 64, planModel, true);
        if (!randomChecks.Select(r => (r.area, string.Join(',', r.gamut_points[0]))).SequenceEqual(randomChecksAgain.Select(r => (r.area, string.Join(',', r.gamut_points[0]))))) throw new Exception("Random validation depends on confidence or mutable model state");
        if (added.Any(r => meta.scenes.Any(old => Distance(old.Histogram(), r.Histogram()) < 1e-10))) throw new Exception("Refinement remeasured old state");
        if (added.Where(r => !r.training).Any(r => added.Where(t => t.training).Any(t => Distance(r.Histogram(), t.Histogram()) < 1e-10))) throw new Exception("Local validation duplicated training");
        var flatConfidence = new CalibrationModel([[0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], [1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]], [0, 0]);
        var steepConfidence = new CalibrationModel([[0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], [1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]], [0, 1]);
        var query = new double[14]; query[0] = .5;
        if (steepConfidence.Confidence(query) >= flatConfidence.Confidence(query)) throw new Exception("Steep slopes did not lower confidence");
        double before = flatConfidence.Confidence(query);
        if (flatConfidence.Confidence(query, [query]) <= before) throw new Exception("Nearby samples did not increase confidence");
        var distant = new double[14]; distant[0] = 10;
        if (flatConfidence.Confidence(distant) >= before) throw new Exception("Distant samples provided excessive confidence");
        var roundtrip = JsonSerializer.Deserialize<CalibrationMetadata>(JsonSerializer.Serialize(meta))!;
        if (roundtrip.histogram_basis == null || roundtrip.gamut_sampling_version != 1) throw new Exception("Gamut metadata roundtrip failed");
        File.WriteAllText(Path.Combine(folder, "checks.txt"), $"PASS: {points.Count} gamut anchors, {training.Length} coarse states; cube geometry, gray axis, maximin coverage, clustered modes, nominal peak, histogram mass, novel exploration/refinement, confidence density, distance and slope and metadata.\n");
    }
}
