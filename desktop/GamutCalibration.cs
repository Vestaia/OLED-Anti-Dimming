// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Text.Json;

namespace OledCalibration;

static partial class ManagedCalibration
{
    // Pattern coordinates remain sqrt-linear RGB; coverage is ranked in the
    // circular HSV geometry used by the cluster model.
    public static List<double[]> GamutPoints(double peak, int interior = 8)
    {
        var points = new List<double[]>();
        void Add(double[] p)
        {
            if (!points.Any(q => Distance(p, q) < 1e-12))
                points.Add(p);
        }
        // 8 vertices, 12 edge centers, 6 face centers (cube center follows below).
        foreach (double r in new[] { 0.0, .5, 1 })
            foreach (double g in new[] { 0.0, .5, 1 })
                foreach (double b in new[] { 0.0, .5, 1 })
                    if (new[] { r, g, b }.Count(v => v == .5) < 3)
                        Add([r, g, b]);
        for (int i = 1; i < 8; i++)
            Add([i / 8.0, i / 8.0, i / 8.0]);
        double reference = Math.Sqrt(100 / peak);
        Add([reference, reference, reference]);
        // Deterministic greedy maximin selection, not a claim of a global optimum.
        var candidates = new List<double[]>();
        for (int r = 1; r < 8; r++)
            for (int g = 1; g < 8; g++)
                for (int b = 1; b < 8; b++)
                    candidates.Add([r / 8.0, g / 8.0, b / 8.0]);
        for (int i = 0; i < interior; i++)
        {
            double Geometry(double[] p,double[] q) { var a=CubeTransport.Map(p.Select(v=>v*v*peak/250).ToArray(),1);var b=CubeTransport.Map(q.Select(v=>v*v*peak/250).ToArray(),1);return (a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y)+(a.Z-b.Z)*(a.Z-b.Z)+(a.C-b.C)*(a.C-b.C); }
            var best = candidates.MaxBy(p => points.Min(q => Geometry(p, q)))!;
            Add(best);
            candidates.Remove(best);
        }
        return points;
    }
    static readonly double[] SweepAreas = [1.0, .7, .4, .2, .1, .05];
    static double Distance(double[] a, double[] b) => a.Zip(b, (x, y) => (x - y) * (x - y)).Sum();
    static CalibrationScene GamutPattern(string folder, string name, double[][] centers, double spread, double area, bool training, double peak, int seed, double[]? mixtureWeights = null)
    {
        Directory.CreateDirectory(folder);
        var random = new Random(seed);
        // Weighted 2?5-mode mixtures; independent colors within each cluster
        // vary from tile to tile, including mixtures of dark and bright modes.
        var weights = mixtureWeights ?? centers.Select(_ => .25 + random.NextDouble()).ToArray();
        if (weights.Length != centers.Length || weights.Any(v => v <= 0))
            throw new Exception("Invalid mixture weights");
        double sum = weights.Sum();
        double Gaussian() => Math.Sqrt(-2 * Math.Log(Math.Max(1e-12, random.NextDouble()))) * Math.Cos(2 * Math.PI * random.NextDouble());
        const int width = 160, height = 80, tile = 8;
        const int tiles = 200, columns = 20;
        var counts = weights.Select(w => (int)Math.Floor(w / sum * tiles)).ToArray();
        var order = Enumerable.Range(0, weights.Length).OrderByDescending(i => weights[i] / sum * tiles - counts[i]).ToArray();
        for (int i = 0, left = tiles - counts.Sum(); i < left; i++)
            counts[order[i % order.Length]]++;
        var assignments = counts.SelectMany((n, i) => Enumerable.Repeat(i, n)).ToArray();
        for (int i = assignments.Length - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (assignments[i], assignments[j]) = (assignments[j], assignments[i]);
        }
        var palette = new double[tiles][];
        for (int i = 0; i < palette.Length; i++)
        {
            int cluster = assignments[i];
            palette[i] = centers[cluster].Select(v => { double x = Math.Clamp(v + spread * Gaussian(), 0, 1); return x * x * peak / 250; }).ToArray();
        }
        var pixels = Enumerable.Range(0, width * height).Select(i => palette[i % width / tile + i / width / tile * columns]).ToArray();
        string path = Path.GetFullPath(Path.Combine(folder, name + ".bin"));
        Texture(path, pixels, width, height);
        return new CalibrationScene { name = name, scene = name, asset = path.Replace('\\', '/'), area = area, training = training, moments = Moments(pixels), gamut_points = centers, gamut_spread = spread, gamut_weights = weights.Select(v => v / sum).ToArray() };
    }
    public static CalibrationMetadata GamutSeeds(string folder, string monitor, double peak, bool express = false, int displayWidth = 2560, int displayHeight = 1440, bool mosaic = true)
    {
        var meta = new CalibrationMetadata { histogram_pca = false, color_clusters = true, cluster_geometry_version = 11, gamut_sampling_version = 1, pattern_version = 4, monitor_device = monitor, peak_content_nits = peak, express_mode = express, mosaic_probe = mosaic, display_width = displayWidth, display_height = displayHeight };
        var points = GamutPoints(peak, express ? 4 : 8);
        for (int i = 0; i < points.Count; i++)
        {
            string family = "gamut-solid-" + i;
            var source = GamutPattern(folder, family, [points[i]], 0, 1, true, peak, 2084 + i);
            foreach (double area in points[i].All(v => v == 0) ? new[] { 1.0 } : SweepAreas)
            {
                meta.scenes.Add(CopyGamut(source, family + "_" + Number(area), area, true));
            }
        }
        if (!express)
            for (int i = 0; i < 12; i++)
            {
                int modes = 2 + i % 4;
                // Include a bright and a dark mode; additional modes span the gamut.
                var centers = Enumerable.Range(0, modes).Select(k => points[(i * 7 + k * 11) % points.Count]).ToArray();
                var dark = points.Where(p => p.Max() <= .5).ToArray();
                var bright = points.Where(p => p.Max() >= .875).ToArray();
                centers[0] = dark[i % dark.Length];
                centers[1] = bright[(i * 3) % bright.Length];
                string family = "gamut-cluster-" + i;
                var source = GamutPattern(folder, family, centers, new[] { .025, .075, .15 }[i % 3], 1, true, peak, 92084 + i);
                foreach (double area in SweepAreas)
                    meta.scenes.Add(CopyGamut(source, family + "_" + Number(area), area, true));
            }
        if (!express)
        {
            var palettes = new double[][][] { [[.95, .95, .95], [.15, .15, .15]], [[1, .2, .1], [.12, .2, .15]], [[.15, .95, .8], [.2, .1, .2]] };
            for (int i = 0; i < palettes.Length; i++)
                foreach (double brightShare in new[] { .2, .8 })
                {
                    string family = $"gamut-weighted-{i}-{brightShare:g}";
                    var source = GamutPattern(folder, family, palettes[i], .025, 1, true, peak, 302084 + i, [brightShare, 1 - brightShare]);
                    foreach (double area in SweepAreas)
                        meta.scenes.Add(CopyGamut(source, family + "_" + Number(area), area, true));
                }
        }
        StampProbe(meta, meta.scenes);
        return meta;
    }
    static CalibrationScene CopyGamut(CalibrationScene source, string name, double area, bool training) => new()
    {
        name = name,
        scene = source.scene,
        asset = source.asset,
        area = area,
        training = training,
        prune_lower_saturation=source.prune_lower_saturation,
        mosaic_probe = source.mosaic_probe,
        display_width = source.display_width,
        display_height = source.display_height,
        moments = source.moments,
        gamut_points = source.gamut_points,
        gamut_spread = source.gamut_spread,
        grid_hue=source.grid_hue,grid_saturation=source.grid_saturation,grid_brightness=source.grid_brightness,
        gamut_weights = source.gamut_weights
    };
    public static List<CalibrationScene> GamutNeighbors(CalibrationModel model, CalibrationMetadata meta, string folder)
    {
        var candidates = GamutProbes(meta, folder, 12, model);
        foreach (var candidate in candidates) candidate.training = true;
        return candidates;
    }
    public static List<CalibrationScene> GamutProbes(CalibrationMetadata meta, string folder, int count = 6, CalibrationModel? model = null, bool randomSelection = false)
    {
        // Generate candidates on demand; no camera labels are required to rank
        // under-covered regions. Use both gamut coverage and distribution novelty.
        var random = new Random(2084 + meta.scenes.Count);
        var covered = meta.measured_gamut_states.Count > 0 ? meta.scenes.Where(r => meta.measured_gamut_states.Contains(r.name)).ToArray() : meta.scenes.Where(r => r.training).ToArray();
        var knownPoints = covered.SelectMany(r => r.gamut_points).ToList();
        var knownHistograms = covered.Select(r => meta.smoothed_hsv?r.SmoothState(meta.peak_content_nits):meta.color_clusters?r.Clusters(meta.cluster_geometry_version):r.Histogram()).ToList();
        var encoder = meta.histogram_basis;
        double[] Encode(double[] h) => meta.smoothed_hsv || meta.color_clusters || encoder == null ? h : encoder.Project(h);
        var knownStates = (model?.ClusterModel == true || model?.SmoothHistogram == true) ? covered.Select(model.Encode).ToList() : knownHistograms.Select(Encode).ToList();
        var candidates = new List<(double[][] points, double spread, double area, double[] state, CalibrationScene? scene)>();
        for (int i = 0; i < (randomSelection ? count : 128); i++)
        {
            int modes = i % 2 == 0 ? 1 : 2 + i % 4;
            double[][] points = Enumerable.Range(0, modes).Select(_ => Enumerable.Range(0, 3).Select(_ => random.NextDouble()).ToArray()).ToArray();
            double spread = modes == 1 ? 0 : .02 + random.NextDouble() * .13;
            double area = Math.Max(1.0 / (meta.display_width * (double)meta.display_height), random.NextDouble());
            double[] h;
            if(meta.smoothed_hsv) { h=[]; } else if(meta.color_clusters) {
                if(meta.cluster_geometry_version>=8) {
                    Func<double[],double,ScaledColorClusters.Pixel> map=meta.cluster_geometry_version>=11?CubeTransport.Map:ScaledColorClusters.Map;
                    var population=new List<ScaledColorClusters.Pixel>{map([0,0,0],1-Math.Max(.01,area)),map([.4,.4,.4],.01)};
                    population.AddRange(points.Select(p=>map(p.Select(v=>v*v*meta.peak_content_nits/250).ToArray(),Math.Max(0,area-.01)/modes)));h=ScaledColorClusters.Fit(population);
                } else {
                    var population=new List<ColorClusters.Pixel>{ColorClusters.Map([0,0,0],1-Math.Max(.01,area)),ColorClusters.Map([.4,.4,.4],.01)};
                    population.AddRange(points.Select(p=>ColorClusters.Map(p.Select(v=>v*v*meta.peak_content_nits/250).ToArray(),Math.Max(0,area-.01)/modes)));h=ColorClusters.Fit(population);
                }
            } else {
                h=new double[HistogramPca.Bins];HistogramPca.Add(h,[0,0,0],1-Math.Max(.01,area));HistogramPca.Add(h,[.4,.4,.4],.01);
                foreach(var p in points)HistogramPca.Add(h,p.Select(v=>v*v*meta.peak_content_nits/250).ToArray(),Math.Max(0,area-.01)/modes);
            }
            CalibrationScene? candidate = null;
            if (model != null) {
                candidate = GamutPattern(folder, "candidate-" + i, points, spread, area, false, meta.peak_content_nits, 82084 + meta.scenes.Count + i);
                StampProbe(meta, [candidate]);
                h = model.ClusterModel || model.SmoothHistogram ? model.Encode(candidate) : candidate.Histogram(model.Histogram!.InputBins);
            }
            candidates.Add((points, spread, area, Encode(h), candidate));
        }
        var result = new List<CalibrationScene>();
        var plannedStates = new List<double[]>();
        for (int i = 0; i < count && candidates.Count > 0; i++)
        {
            // Use confidence for calibrated models; maximin coverage bootstraps
            // candidate generation before a fitted model is available.
            double Score((double[][] points, double spread, double area, double[] state, CalibrationScene? scene) c) => model != null
                ? 1 / Math.Max(1e-12, model.Confidence(c.state, plannedStates))
                : i % 2 == 0
                ? c.points.Average(p => knownPoints.Select(q => Distance(p, q)).DefaultIfEmpty(1).Min())
                : knownStates.Select(q => Distance(c.state, q)).DefaultIfEmpty(1).Min();
            var best = randomSelection ? candidates[0] : candidates.MaxBy(Score);
            candidates.Remove(best);
            var probe = best.scene ?? GamutPattern(folder, "gamut-probe-" + Path.GetFileName(Path.GetDirectoryName(folder)) + "-" + Path.GetFileName(folder) + "-" + meta.scenes.Count + "-" + i, best.points, best.spread, best.area, false, meta.peak_content_nits, 82084 + meta.scenes.Count + i);
            probe.name = "gamut-probe-" + Path.GetFileName(Path.GetDirectoryName(folder)) + "-" + Path.GetFileName(folder) + "-" + meta.scenes.Count + "-" + i;
            probe.scene = probe.name;
            StampProbe(meta, [probe]);
            if (meta.scenes.Concat(result).Any(r => Distance(meta.smoothed_hsv?r.SmoothState(meta.peak_content_nits):meta.color_clusters?r.Clusters(meta.cluster_geometry_version):r.Histogram(),meta.smoothed_hsv?probe.SmoothState(meta.peak_content_nits):meta.color_clusters?probe.Clusters(meta.cluster_geometry_version):probe.Histogram()) < 1e-10)) {
                if (best.scene != null) File.Delete(probe.asset);
                continue;
            }
            result.Add(probe);
            knownPoints.AddRange(best.points);
            knownStates.Add((model?.ClusterModel==true || model?.SmoothHistogram==true)?model.Encode(probe):Encode(meta.smoothed_hsv?probe.SmoothState(meta.peak_content_nits):meta.color_clusters?probe.Clusters(meta.cluster_geometry_version):probe.Histogram()));
            plannedStates.Add((model?.ClusterModel==true || model?.SmoothHistogram==true)?model.Encode(probe):Encode(meta.smoothed_hsv?probe.SmoothState(meta.peak_content_nits):meta.color_clusters?probe.Clusters(meta.cluster_geometry_version):probe.Histogram()));
        }
        foreach (var unused in candidates)
            if (unused.scene != null) File.Delete(unused.scene.asset);
        return result;
    }
    static void StampProbe(CalibrationMetadata meta, IEnumerable<CalibrationScene> scenes)
    {
        foreach (var scene in scenes)
        {
            scene.prune_lower_saturation=meta.has_white_subpixel;
            scene.mosaic_probe = meta.mosaic_probe;
            scene.display_width = meta.display_width;
            scene.display_height = meta.display_height;
        }
    }
    static void MarkGamutMeasured(CalibrationMetadata meta, List<Dictionary<string, string>> observations)
    {
        var lookup = meta.scenes.ToDictionary(r => r.name);
        var closing = Closing(observations);
        var names = meta.measured_gamut_states.ToHashSet();
        foreach (var row in observations.Where(r => r["role"] == "matched" || r["role"] == "predicted_adaptive"))
            if (lookup.TryGetValue(row["name"], out var scene) && ClosingError(closing, scene) <= .05)
                names.Add(scene.name);
        meta.measured_gamut_states = names.Order().ToList();
    }
    static void GamutCheckpoint(string output, CalibrationMetadata meta, List<Dictionary<string, string>> rows, CalibrationModel fit, string monitor)
    {
        File.WriteAllText(Path.Combine(output, "checkpoint-metadata.json"), JsonSerializer.Serialize(meta));
        SaveCsv(Path.Combine(output, "checkpoint-training.csv"), rows);
        File.WriteAllText(Path.Combine(output, "checkpoint-model.json"), fit.Json(monitor));
    }
    internal static int RetainValidationMatches(List<Dictionary<string, string>> training, CalibrationMetadata meta, List<Dictionary<string, string>> validation)
    {
        var lookup = meta.scenes.ToDictionary(r => r.name);
        var closing = Closing(validation);
        var plateau = validation.Where(r => r["role"] == "panel_plateau").Select(r => r["name"]).ToHashSet();
        var existing = training.Where(r => r["role"] == "matched").Select(r => r["name"]).ToHashSet();
        var matches = validation.Where(r => r["role"] == "matched" && !existing.Contains(r["name"]) && !plateau.Contains(r["name"]) &&
            lookup.ContainsKey(r["name"]) && ClosingError(closing, lookup[r["name"]]) <= .05)
            .GroupBy(r => r["name"]).Select(g => g.Last()).ToArray();
        var groups = new HashSet<string>();
        foreach (var row in matches)
        {
            lookup[row["name"]].training = true;
            groups.Add(lookup[row["name"]].scene);
            training.Add(new(row));
        }
        training.AddRange(validation.Where(r => r["role"] == "end_reference" && (groups.Contains(r["name"]) || matches.Any(m => m["name"] == r["name"]))).Select(r => new Dictionary<string, string>(r)));
        return matches.Length;
    }
    static async Task RunGamut(string root, string output, bool fresh, string monitor, string camera, int rounds, Action<string> log, Action<Process?> track, CancellationToken token, double peak, bool express, bool hasWhiteSubpixel)
    {
        CalibrationMetadata meta;
        List<Dictionary<string, string>> rows;
        CalibrationModel fit;
        string run = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        if (fresh)
        {
            var screen = Screen.AllScreens.First(s => s.DeviceName == monitor);
            meta = HsvGridSeeds(output, monitor, peak, express, screen.Bounds.Width, screen.Bounds.Height,hasWhiteSubpixel);

            StampProbe(meta, meta.scenes);
            meta.color_clusters=false;meta.smoothed_hsv=true;meta.histogram_pca=false;
            log("Color distribution model: smoothed 12 x 6 x 16 HSV histogram; Laplace smoothing (1.2, 2.727, 3.333), resolution-normalized distance, cubic exponential scale 0.005.");
            log($"Gamut coarse pass: {meta.scenes.Count(r => r.training && r.grid_brightness>0)} measured candidates plus V=0 anchors before short stopping; nonzero sqrt-brightness grid, {(express?6:9)} hues, {(express?2:4)} saturations plus white, {(express?5:7)} window sizes");
            acquisitionPhase = "Initial coarse calibration";
            var seed=CalibrationModel.FromSmoothHistogram([meta.scenes.First(r=>r.grid_brightness==0).SmoothState(peak)],[0],peak);
            await Task.Run(()=>Plan(Path.Combine(output,"initial-plan.csv"),meta.scenes.Where(r=>r.training),seed,true),token);
            acquisitionStart = 5;
            acquisitionEnd = 55;
            rows = await Acquire(root, Path.Combine(output, "initial-plan.csv"), Path.Combine(output, "initial.csv"), monitor, camera, log, track, token);
            Step(55, "Initial coarse calibration");
            fit = Fit(rows, meta);
        }
        else
        {
            meta = JsonSerializer.Deserialize<CalibrationMetadata>(File.ReadAllText(Path.Combine(output, "metadata.json")))!;
            if (!meta.smoothed_hsv && !meta.color_clusters)
                throw new Exception("Use New calibration for the smoothed HSV model. Existing filters remain usable.");
            if (meta.pattern_version < 4)
                throw new Exception("Use New calibration to replace subsampled pattern statistics with exact pattern distributions.");
            if (meta.monitor_device != monitor || Math.Abs(meta.peak_content_nits - peak) > 1e-6 || meta.express_mode != express || meta.smoothed_hsv && meta.has_white_subpixel!=hasWhiteSubpixel)
                throw new Exception("Use New calibration when changing display, peak brightness, mode or white-subpixel setting for gamut calibration.");
            var screen = Screen.AllScreens.First(s => s.DeviceName == monitor);
            if (meta.mosaic_probe && (meta.display_width != screen.Bounds.Width || meta.display_height != screen.Bounds.Height))
                throw new Exception("Display resolution changed. Create a new calibration for the mosaic probe.");
            rows = ReadCsv(Path.Combine(output, "combined-training.csv"));
            // Retain measured gains, but rebuild their exact pattern statistics
            // in the new geometry before using them to seed refinement.
            meta.cluster_geometry_version=11;
            fit = Fit(rows, meta);
        }
        MarkGamutMeasured(meta, rows);
        GamutCheckpoint(output, meta, rows, fit, monitor);
        string validationFolder = Path.Combine(output, "gamut-validation-" + run);
        Directory.CreateDirectory(validationFolder);
        var exploratory = await Task.Run(()=>GamutProbes(meta,Path.Combine(validationFolder,"probes"),6,fit,true),token);
        meta.scenes.AddRange(exploratory);
        log($"Validating {exploratory.Count} fresh random gamut distributions");
        var holdouts = exploratory.ToArray();
        acquisitionPhase = "Initial validation";
        Plan(Path.Combine(validationFolder, "plan.csv"), holdouts, fit);
        acquisitionStart = 55;
        acquisitionEnd = 70;
        Step(55, "Initial validation");
        var validation = await Acquire(root, Path.Combine(validationFolder, "plan.csv"), Path.Combine(validationFolder, "validation.csv"), monitor, camera, log, track, token);
        MarkGamutMeasured(meta, validation);
        bool refined = false;
        for (int round = 1; round <= rounds; round++)
        {
            token.ThrowIfCancellationRequested();
            string folder = Path.Combine(output, $"gamut-round-{run}-{round}");
            int retained = RetainValidationMatches(rows, meta, validation);
            if (retained > 0)
            {
                fit = Fit(rows, meta);
                GamutCheckpoint(output, meta, rows, fit, monitor);
                log($"Added {retained} validated camera matches to the model.");
            }
            var added = await Task.Run(()=>GamutNeighbors(fit,meta,folder),token);
            if (!added.Any(r => r.training))
            {
                log("No novel gamut regions need new samples.");
                break;
            }
            refined = true;
            meta.scenes.AddRange(added);
            log($"Gamut refinement {round}: {added.Count(r => r.training)} new matches in low-confidence regions");
            acquisitionPhase = $"Refinement step {round}";
            Plan(Path.Combine(folder, "plan.csv"), added.Where(r => r.training), fit, true);
            acquisitionStart = 70 + (round - 1) * 20 / Math.Max(1, rounds);
            acquisitionEnd = acquisitionStart + 10 / Math.Max(1, rounds);
            rows.AddRange(await Acquire(root, Path.Combine(folder, "plan.csv"), Path.Combine(folder, "observations.csv"), monitor, camera, log, track, token));
            MarkGamutMeasured(meta, rows);
            Step(acquisitionEnd, acquisitionPhase);
            fit = Fit(rows, meta);
            GamutCheckpoint(output, meta, rows, fit, monitor);
            // Validation draws fresh random distributions independently of confidence.
            var freshProbes = await Task.Run(()=>GamutProbes(meta,Path.Combine(folder,"probes"),4,fit,true),token);
            meta.scenes.AddRange(freshProbes);
            var local = freshProbes.ToArray();
            Plan(Path.Combine(folder, "validation-plan.csv"), local, fit);
            acquisitionStart = acquisitionEnd;
            acquisitionEnd = 70 + round * 20 / Math.Max(1, rounds);
            validation = await Acquire(root, Path.Combine(folder, "validation-plan.csv"), Path.Combine(folder, "validation.csv"), monitor, camera, log, track, token);
            MarkGamutMeasured(meta, validation);
        }
        if (refined)
        {
            RetainValidationMatches(rows, meta, validation);
            fit = Fit(rows, meta);
            string folder = Path.Combine(output, "gamut-final-validation-" + run);
            Directory.CreateDirectory(folder);
            acquisitionPhase = "Final validation";
            var finalProbes = await Task.Run(()=>GamutProbes(meta,Path.Combine(folder,"probes"),4,fit,true),token);
            meta.scenes.AddRange(finalProbes);
            Plan(Path.Combine(folder, "plan.csv"), finalProbes, fit);
            acquisitionStart = 90;
            acquisitionEnd = 96;
            validation = await Acquire(root, Path.Combine(folder, "plan.csv"), Path.Combine(folder, "validation.csv"), monitor, camera, log, track, token);
        }
        MarkGamutMeasured(meta, validation);
        // Quality reports describe predictions made before these targets were
        // absorbed; the saved model benefits from every usable final match.
        RetainValidationMatches(rows, meta, validation);
        fit = Fit(rows, meta);
        string photoFolder = Path.Combine(output, "photo-validation-" + run);
        Directory.CreateDirectory(photoFolder);
        AddBenchmarks(root, photoFolder, meta);
        var photos = meta.scenes.Where(r => r.scene.StartsWith("photo-", StringComparison.Ordinal) && r.asset.StartsWith(photoFolder.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)).ToArray();
        acquisitionPhase = "Photo validation";
        acquisitionStart = 96;
        acquisitionEnd = 97;
        Plan(Path.Combine(photoFolder, "plan.csv"), photos, fit);
        validation = await Acquire(root, Path.Combine(photoFolder, "plan.csv"), Path.Combine(photoFolder, "validation.csv"), monitor, camera, log, track, token);
        MarkGamutMeasured(meta, validation);
        RetainValidationMatches(rows, meta, validation);
        fit = Fit(rows, meta);
        token.ThrowIfCancellationRequested();
        Step(97, "Saving");
        File.WriteAllText(Path.Combine(output, "metadata.json"), JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
        SaveCsv(Path.Combine(output, "combined-training.csv"), rows);
        CalibrationQualitySummary(output, meta, validation);
        string pending = Path.Combine(output, "runtime-model-pending.json");
        File.WriteAllText(pending, fit.Json(monitor));
        File.Move(pending, Path.Combine(output, "runtime-model.json"), true);
        File.WriteAllText(Path.Combine(output, "gamut-status.json"), JsonSerializer.Serialize(new
        {
            sampling_strategy = "local-confidence",
            round_limit = rounds,
            matched_samples = fit.Centers.Length - 1,
            confidence_neighbors = 12,
            confidence = "distance-weighted support / (1 + local log-gain slope)"
        }));
        log("Calibration saved; new samples were selected by model confidence. See calibration quality for validation results.");
        Step(100, "Complete");
    }
}
