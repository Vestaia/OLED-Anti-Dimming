// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace OledCalibration;

sealed class CalibrationScene
{
    public string name { get; set; } = ""; public string scene { get; set; } = ""; public string asset { get; set; } = "";
    public double area
    {
        get; set;
    }
    public bool training
    {
        get; set;
    }
    public double[] moments { get; set; } = [];
    public bool full_frame_moments
    {
        get; set;
    }
    public bool mosaic_probe
    {
        get; set;
    }
    public int display_width { get; set; } = 2560;
    public int display_height { get; set; } = 1440;
    public double[][] gamut_points { get; set; } = [];
    public double[] gamut_weights { get; set; } = [];
    public double gamut_spread
    {
        get; set;
    }
    public bool prune_lower_saturation { get; set; } = true;
    public double grid_hue { get; set; } = -1;
    public double grid_saturation { get; set; }
    public double grid_brightness { get; set; }
    readonly Dictionary<double,double[]> smoothStates=new();
    public double[] SmoothState(double peak) {if(!smoothStates.TryGetValue(peak,out var state))smoothStates[peak]=state=SmoothedHsv.Encode(this,peak);return state;}
    public string brightness_family { get; set; } = "";
    public string BrightnessFamily() => moments.Length == 14 && Math.Abs(moments[0] - moments[1]) < 1e-8 && Math.Abs(moments[0] - moments[2]) < 1e-8 && Enumerable.Range(0, 3).All(j => Math.Abs(moments[j + 3] - moments[j] * moments[j]) < 1e-6) ? "gray" : brightness_family;
    public double BrightnessLevel() => (BrightnessFamily() == "gray" ? moments[0] : moments[7]) * 250;
    readonly Dictionary<int,double[]> cachedClusters = new();
    public double[] Clusters(int version=8) { if(!cachedClusters.TryGetValue(version,out var state))cachedClusters[version]=state=ManagedCalibration.SceneClusters(this,version);return state; }
    readonly Dictionary<int, double[]> cachedHistograms = new();
    public double[] Histogram(int bins = HistogramPca.Bins)
    {
        if (!cachedHistograms.TryGetValue(bins, out var histogram))
            cachedHistograms[bins] = histogram = ManagedCalibration.SceneHistogram(this, bins);
        return histogram;
    }

}
sealed class CalibrationMetadata
{
    public bool mosaic_probe
    {
        get; set;
    }
    public int display_width { get; set; } = 2560;
    public int display_height { get; set; } = 1440;
    public List<string> measured_gamut_states { get; set; } = [];
    public int gamut_sampling_version
    {
        get; set;
    }
    public bool has_white_subpixel { get; set; } = true; // Legacy calibrations pruned lower saturation.
    public bool smoothed_hsv { get; set; }
    public bool color_clusters { get; set; }
    public int cluster_geometry_version { get; set; } = 7;
    public HistogramPca? histogram_basis
    {
        get; set;
    }
    public bool histogram_pca
    {
        get; set;
    }
    public bool background_sweeps
    {
        get; set;
    }
    public int pattern_version
    {
        get; set;
    }
    public bool express_mode
    {
        get; set;
    }
    public double peak_content_nits
    {
        get; set;
    }
    public string monitor_device { get; set; } = ""; public List<CalibrationScene> scenes { get; set; } = []; public bool public_domain_benchmarks
    {
        get; set;
    }
    public JsonElement[] sources { get; set; } = [];
}
static partial class ManagedCalibration
{
    public static Action<int, string>? Progress;
    static CameraCalibrationSession? activeSession;
    static void Step(int percent, string phase) => Progress?.Invoke(percent, phase);
    static int acquisitionStart = 5, acquisitionEnd = 55;
    static string acquisitionPhase = "Initial coarse calibration";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static string Number(double v) => v.ToString("R", Inv);
    public static List<Dictionary<string, string>> ReadCsv(string path)
    {
        using var p = new TextFieldParser(path) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true };
        p.SetDelimiters(",");
        var header = p.ReadFields() ?? throw new Exception("Empty measurements");
        var rows = new List<Dictionary<string, string>>();
        while (!p.EndOfData)
        {
            var v = p.ReadFields();
            if (v == null)
                continue;
            if (v.Length != header.Length)
                throw new Exception("Incomplete measurement row");
            rows.Add(header.Select((key, i) => (key, v[i])).ToDictionary(v => v.key, v => v.Item2));
        }
        return rows;
    }
    static double Value(Dictionary<string, string> r, string k) => double.Parse(r[k], Inv);
    static string Quote(string v) => v.Contains(',') || v.Contains('"') || v.Contains('\n') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    static void SaveCsv(string path, List<Dictionary<string, string>> rows)
    {
        var keys = rows[0].Keys.ToArray();
        File.WriteAllLines(path, new[] { string.Join(',', keys) }.Concat(rows.Select(r => string.Join(',', keys.Select(k => Quote(r[k]))))));
    }
    static Dictionary<string, double> Closing(List<Dictionary<string, string>> rows) => rows.Where(r => r["role"] == "end_reference").GroupBy(r => r["name"]).ToDictionary(g => g.Key, g => Math.Abs(Value(g.Last(), "camera_code") - Value(g.Last(), "reference_code")) / Math.Max(1e-12, Value(g.Last(), "reference_code")));
    static double ClosingError(Dictionary<string, double> closing, CalibrationScene scene) => closing.TryGetValue(scene.name, out var value) ? value : closing.GetValueOrDefault(scene.scene, 999);
    public static CalibrationModel Fit(List<Dictionary<string, string>> rows, CalibrationMetadata meta)
    {
        var lookup = meta.scenes.ToDictionary(r => r.name);
        var closing = Closing(rows);
        var good = rows.Where(r => r["role"] is "matched" or "skipped_flat" && lookup.ContainsKey(r["name"]) && (lookup[r["name"]].grid_hue>=0 && lookup[r["name"]].grid_brightness==0 || ClosingError(closing, lookup[r["name"]]) <= .05))
            .GroupBy(r => r["name"]).Select(g => g.LastOrDefault(r => r["role"] == "matched") ?? g.Last()).ToArray();
        if (good.Length < 4 || !good.Any(r=>r["role"]=="matched"))
            throw new Exception("Too few stable camera matches; previous calibration retained");
        var scenes = good.Select(r => lookup[r["name"]]).ToArray();
        if(meta.smoothed_hsv)return CalibrationModel.FromSmoothHistogram(scenes.Select(r=>r.SmoothState(meta.peak_content_nits)).ToArray(),good.Select(r=>r["role"]=="skipped_flat" || Math.Abs(Value(r,"signal_nits")/250-1)<=.01?0:Math.Log(Value(r,"signal_nits")/250)).ToArray(),meta.peak_content_nits);
        if(meta.color_clusters)return ((Func<double[][],double[],CalibrationModel>)(meta.cluster_geometry_version>=11?CalibrationModel.FromCubeClusters:meta.cluster_geometry_version>=9?CalibrationModel.FromGaussianClusters:CalibrationModel.FromClusters))(scenes.Select(r=>r.Clusters(meta.cluster_geometry_version)).ToArray(),good.Select(r=>r["role"]=="skipped_flat"?0:Math.Log(Value(r,"signal_nits")/250)).ToArray());
        if (!meta.histogram_pca)
            throw new Exception("Only PCA calibration is supported in this release.");
        var encoder = meta.histogram_basis ??= HistogramPca.Fit(meta.scenes.Select(r => r.Histogram()), meta.peak_content_nits > 0 ? meta.peak_content_nits : 10000);
        return new CalibrationModel(scenes.Select(r => encoder.Project(r.Histogram(encoder.InputBins))).ToArray(), good.Select(r => r["role"] == "skipped_flat" ? 0 : Math.Log(Value(r, "signal_nits") / 250)).ToArray()) { Histogram = encoder };
    }
    static void Plan(string path, IEnumerable<CalibrationScene> records, CalibrationModel? model = null, bool onlineTraining = false)
    {
        if (onlineTraining)
        {
            if (model == null || (!model.SmoothHistogram && !model.ClusterModel && model.Histogram == null) || !model.LocalNeighbors)
                throw new InvalidOperationException("Online training requires a neighbor model and PCA encoder.");
            using var seed = new BinaryWriter(File.Create(path + ".online.bin"));
            seed.Write(model.SmoothHistogram?0x394e4c4fu:model.TransportModel?0x364e4c4fu:model.KernelModel?(model.LegacyKernelCoverage?0x344e4c4fu:0x354e4c4fu):model.ScaledClusters?0x334e4c4fu:model.ClusterModel?0x324e4c4fu:0x314e4c4fu);
            seed.Write((uint)model.Centers.Length);
            for (int i = 0; i < model.Centers.Length; i++)
            {
                foreach (double value in model.Centers[i])
                    seed.Write(value);
                seed.Write(model.Coefficients[i]);
            }
        }
        else
            File.Delete(path + ".online.bin");
        using var stateFile = onlineTraining && model!.SmoothHistogram ? new BinaryWriter(File.Create(path+".states.bin")) : null;
        long stateIndex=0;
        using var w = new StreamWriter(path);
        w.WriteLine("name,hue,saturation,area,role,signal,asset,probe_base,sweep_group,brightness_family,brightness_level" + (onlineTraining ? stateFile!=null ? ",state_index" : "," + string.Join(',', Enumerable.Range(0, model?.Dimensions ?? 14).Select(i => "state_" + i)) : ""));
        void WriteRow(string row)
        {
            if (onlineTraining && row.Split(',').Length == 11)
                row += new string(',', stateFile!=null?1:model!.Dimensions);
            w.WriteLine(row);
        }
        bool containsZeroAnchors=false;
        foreach (var sweep in records.GroupBy(r => r.scene).OrderBy(g => g.First().grid_hue>=0?"grid":""+g.First().BrightnessFamily()).ThenByDescending(g => g.First().grid_brightness>0?g.First().grid_brightness:g.First().BrightnessLevel()).ThenByDescending(g=>g.First().grid_saturation))
        {
            bool zeroOnly=sweep.First().grid_hue>=0 && sweep.First().grid_brightness==0;containsZeroAnchors|=zeroOnly;
            if(!zeroOnly)WriteRow($"{sweep.Key},0,0,0.01,reference,250,,100,,,");
            foreach (var r in sweep.OrderByDescending(r => r.area))
            {
                if (r.asset.Contains(','))
                    throw new Exception("Pattern path must not contain a comma");
                // High-error followups must bypass inferred flatness so they can
                // correct assumptions contradicted by independent validation.
                bool canSkip = r.training && !r.name.StartsWith("adapt_", StringComparison.Ordinal);
                string group = canSkip ? r.scene : "";
                string adaptive = group + "," + (canSkip ? r.grid_hue>=0?(r.prune_lower_saturation?"hsv-grid":"hsv-grid-fixed-s"):r.BrightnessFamily() : "") + "," + Number(r.grid_brightness>0?r.grid_brightness:r.BrightnessLevel());
                if (model != null && !onlineTraining)
                    WriteRow($"{r.name},{Number(r.grid_hue)},{Number(r.grid_saturation)},{Number(r.area)},raw,250,{r.asset},100,{adaptive}");
                string signal = Number(model == null ? 250 : 250 * Math.Exp(model.Predict(r)));
                if (!onlineTraining)
                    WriteRow($"{r.name},{Number(r.grid_hue)},{Number(r.grid_saturation)},{Number(r.area)},{(model == null ? "raw" : "predicted_adaptive")},{signal},{r.asset},100,{adaptive}");
                string coordinates="";
                if(onlineTraining) {
                    var state=model!.Encode(r);
                    if(stateFile!=null){coordinates=","+stateIndex++;foreach(double value in state)stateFile.Write(value);}
                    else coordinates=","+string.Join(',',state.Select((value,i)=>Number(value/model.Scale[i])));
                }
                WriteRow($"{r.name},{Number(r.grid_hue)},{Number(r.grid_saturation)},{Number(r.area)},match,{signal},{r.asset},100,{adaptive}{coordinates}");
            }
            if(!zeroOnly)WriteRow($"{sweep.Key},0,0,0.01,end_reference,250,,100,,,");
        }
        if(containsZeroAnchors)WriteRow("grid-close,0,0,0.01,end_reference,250,,100,,,");
    }
    static async Task<List<Dictionary<string, string>>> Acquire(string root, string plan, string output, string monitor, string camera, Action<string> log, Action<Process?> track, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (activeSession == null)
            throw new InvalidOperationException("No camera session");
        await activeSession.Measure(plan, output, token);
        var rows = ReadCsv(output);
        var expected = ReadCsv(plan).Where(r => r["role"] == "end_reference").Select(r => r["name"]).ToHashSet();
        var closed = rows.Where(r => r["role"] == "end_reference").Select(r => r["name"]).ToHashSet();
        var skipped = rows.Where(r => r["role"] == "skipped_flat").Select(r => r["name"]).ToHashSet();
        closed.UnionWith(skipped);
        if (!expected.IsSubsetOf(closed))
            throw new Exception("Measurement cancelled or incomplete; previous model retained");
        log($"Completed {expected.Count} sweeps; skipped {skipped.Count} flat patterns");
        return rows;
    }
    public static string LatestValidation(string folder) => Directory.GetFiles(folder, "*validation.csv", System.IO.SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() ?? throw new Exception("No validation observations found");
    public static async Task Run(string root, string output, bool fresh, string monitor, string camera, int rounds, Action<string> log, Action<Process?> track, CancellationToken token, double peak = 1000, bool express = false, bool hasWhiteSubpixel = false)
    {
        bool mosaic = true;
        if (!fresh)
        {
            var meta = JsonSerializer.Deserialize<CalibrationMetadata>(File.ReadAllText(Path.Combine(output, "metadata.json")))!;
            if ((!meta.smoothed_hsv && !meta.color_clusters && !meta.histogram_pca) || meta.gamut_sampling_version < 1)
                throw new Exception("Create a new PCA calibration. This model uses an unsupported sampling format.");
            mosaic = meta.mosaic_probe;
        }
        await using var session = new CameraCalibrationSession(root, monitor, camera, track, log, mosaic);
        activeSession = session;
        Step(2, "Preparing calibration");
        Directory.CreateDirectory(output);
        await RunGamut(root, output, fresh, monitor, camera, rounds, log, track, token, peak, express, hasWhiteSubpixel);
    }
}
