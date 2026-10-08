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
        meta.histogram_basis = HistogramPca.Fit(meta.scenes.Select(r => r.Histogram()), 1000);
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
        var source = meta.scenes.First(r => !r.training && r.gamut_points.Length == 1 && r.gamut_points[0].Max() > 0);
        Dictionary<string, string> Row(string role, double code) => new() { ["name"] = role == "end_reference" ? source.scene : source.name, ["role"] = role, ["camera_code"] = Number(code), ["reference_code"] = "100" };
        var validation = new List<Dictionary<string, string>> { Row("predicted_adaptive", 92), Row("end_reference", 100) };
        var added = GamutNeighbors(validation, meta, Path.Combine(folder, "refine"));
        if (added.Count(r => r.training) < 3 || added.Count(r => !r.training) < 1) throw new Exception("Failed region did not get local samples");
        if (added.Any(r => meta.scenes.Any(old => Distance(old.Histogram(), r.Histogram()) < 1e-10))) throw new Exception("Refinement remeasured old state");
        if (added.Where(r => !r.training).Any(r => added.Where(t => t.training).Any(t => Distance(r.Histogram(), t.Histogram()) < 1e-10))) throw new Exception("Local validation duplicated training");
        if (GamutNeighbors([Row("predicted_adaptive", 99.6), Row("end_reference", 100)], meta, Path.Combine(folder, "flat")).Count != 0) throw new Exception("Passing region was refined");
        if (GamutNeighbors([.. validation, Row("panel_plateau", 92)], meta, Path.Combine(folder, "plateau")).Count != 0) throw new Exception("Unreachable plateau repeatedly refined");
        var roundtrip = JsonSerializer.Deserialize<CalibrationMetadata>(JsonSerializer.Serialize(meta))!;
        if (roundtrip.histogram_basis == null || roundtrip.gamut_sampling_version != 1) throw new Exception("Gamut metadata roundtrip failed");
        File.WriteAllText(Path.Combine(folder, "checks.txt"), $"PASS: {points.Count} gamut anchors, {training.Length} coarse states; cube geometry, gray axis, maximin coverage, clustered modes, nominal peak, histogram mass, novel exploration/refinement, low-error stop, plateau exclusion and metadata.\n");
    }
}
