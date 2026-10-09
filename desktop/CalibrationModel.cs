// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;

namespace OledCalibration;
// Normalized inverse-distance interpolation over histogram PCA coordinates.
sealed class CalibrationModel
{
    private CalibrationModel()
    {
        Centers = [];
        Coefficients = [];
        Scale = [];
    }
    public static CalibrationModel Load(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var r = json.RootElement;
        int version = r.GetProperty("version").GetInt32();
        if(version is 12 or 13 or 14) {
            var smooth= FromSmoothHistogram(r.GetProperty("centers").Deserialize<double[][]>()!,r.GetProperty("coefficients").Deserialize<double[]>()!,r.GetProperty("peak_content_nits").GetDouble());
            int[] shape=version==14?[12,6,16]:[12,12,64];int histogramDimensions=shape[0]*shape[1]*shape[2];
            if(r.GetProperty("interpolation").GetString()!=(version==12?"laplace-hsv-gaussian-spike":"laplace-hsv-cubic") || r.GetProperty("features").GetInt32()!=histogramDimensions || smooth.Centers.Length>8192 || !double.IsFinite(smooth.PeakContentNits) || smooth.PeakContentNits<=0 || smooth.Centers.Any(a=>a.Length!=histogramDimensions || a.Any(v=>!double.IsFinite(v)||v<0) || Math.Abs(a.Sum()-1)>1e-5))throw new InvalidDataException("Invalid smoothed HSV model");
            var widths=r.GetProperty("smoothing").Deserialize<double[]>()!;
            if(version>=13 && (!widths.SequenceEqual(version==14?SmoothedHsv.Smoothing:new double[]{1.2,6,14}) || r.GetProperty("bandwidth").GetDouble()!=SmoothedHsv.Bandwidth || r.GetProperty("weight_power").GetInt32()!=3))throw new InvalidDataException("Invalid histogram weighting");
            if(version==14 && (!r.GetProperty("histogram_shape").Deserialize<int[]>()!.SequenceEqual(shape) || Math.Abs(r.GetProperty("distance_scale").GetDouble()-Math.Sqrt(1152.0/9216))>1e-12))throw new InvalidDataException("Invalid reduced histogram geometry");
            return FromSmoothHistogram(SmoothedHsv.Remap(smooth.Centers,widths,shape),smooth.Coefficients,smooth.PeakContentNits);
        }
        if(version is 8 or 9 or 10 or 11) {
            var cluster=new CalibrationModel {ClusterModel=true,ScaledClusters=true,TransportModel=version==11,KernelModel=version is 9 or 10,LegacyKernelCoverage=version==9,LocalNeighbors=true,Centers=r.GetProperty("centers").Deserialize<double[][]>()!,Coefficients=r.GetProperty("coefficients").Deserialize<double[]>()!,Scale=Enumerable.Repeat(1.0,49).ToArray()};
            if(r.GetProperty("interpolation").GetString()!=(version==11?"smooth-transport-cube7":version>=9?"gaussian-cluster-distance7":"scaled-cluster-distance7") || r.GetProperty("features").GetInt32()!=49 || Math.Abs(r.GetProperty("brightness_weight").GetDouble()-(version==11?CubeTransport.BrightnessSlope*100:ScaledColorClusters.BrightnessWeight))>1e-10 || Math.Abs(r.GetProperty("area_weight").GetDouble()-(version>=10?0:version==9?GaussianClusterKernel.LegacyAreaWeight:ScaledColorClusters.AreaWeight))>1e-10 || r.GetProperty("cluster_iterations").GetInt32()!=8 || cluster.Centers.Length==0 || cluster.Centers.Length>2048 || cluster.Coefficients.Length!=cluster.Centers.Length || cluster.Coefficients.Any(v=>!double.IsFinite(v)||v<0) || cluster.Centers.Any(v=>v.Length!=49 || v.Any(x=>!double.IsFinite(x)) || v[48]<0 || v[48]>1.000001 || Enumerable.Range(0,8).Any(j=>v[j*6+4]<0||v[j*6+5]<0) || Math.Abs(Enumerable.Range(0,8).Sum(j=>v[j*6+4])-1)>.0001))throw new InvalidDataException("Invalid scaled color-cluster model");
            if(version==11 && (r.GetProperty("geometry_id").GetString()!=CubeTransport.Id || Math.Abs(r.GetProperty("brightness_offset").GetDouble()-CubeTransport.Offset)>1e-12 || Math.Abs(r.GetProperty("color_scale").GetDouble()-CubeTransport.ColorScale)>1e-12 || Math.Abs(r.GetProperty("normalization").GetDouble()-CubeTransport.Normalization)>1e-12))throw new InvalidDataException("Invalid smooth transport geometry");
            return cluster;
        }
        if(version==7) {
            var cluster=new CalibrationModel { ClusterModel=true,LocalNeighbors=true,Centers=r.GetProperty("centers").Deserialize<double[][]>()!,Coefficients=r.GetProperty("coefficients").Deserialize<double[]>()!,Scale=Enumerable.Repeat(1.0,ColorClusters.Dimensions).ToArray() };
            if(r.GetProperty("interpolation").GetString()!="cluster-transport-shepard3" || r.GetProperty("features").GetInt32()!=40 || r.GetProperty("brightness_weight").GetDouble()!=10 || r.GetProperty("cluster_iterations").GetInt32()!=8 || cluster.Centers.Length==0 || cluster.Centers.Length>2048 || cluster.Coefficients.Length!=cluster.Centers.Length || cluster.Coefficients.Any(v=>!double.IsFinite(v)||v<0) || cluster.Centers.Any(v=>v.Length!=40 || v.Any(x=>!double.IsFinite(x)) || Enumerable.Range(0,8).Any(j=>v[j*5+3]<0||v[j*5+4]<0) || Math.Abs(Enumerable.Range(0,8).Sum(j=>v[j*5+3])-1)>.0001))throw new InvalidDataException("Invalid color-cluster model");
            return cluster;
        }
        if (version is not (2 or 3 or 4 or 5 or 6) || (version >= 3 && (!r.TryGetProperty("interpolation", out var interpolation) || interpolation.GetString() != "shepard3")))
            throw new InvalidDataException("Unsupported calibration interpolation model.");
        if (!r.TryGetProperty("histogram_pca", out var encoder) || encoder.ValueKind == JsonValueKind.Null)
            throw new InvalidDataException("Create a new PCA calibration. This model has no histogram encoder.");
        var model = new CalibrationModel { Centers = r.GetProperty("centers").EnumerateArray().Select(a => a.EnumerateArray().Select(v => v.GetDouble()).ToArray()).ToArray(), Coefficients = r.GetProperty("coefficients").EnumerateArray().Select(v => v.GetDouble()).ToArray(), Scale = r.GetProperty("scale").EnumerateArray().Select(v => v.GetDouble()).ToArray(), Histogram = JsonSerializer.Deserialize<HistogramPca>(encoder.GetRawText()), Baseline = r.GetProperty("baseline").GetDouble(), LocalNeighbors = r.TryGetProperty("interpolation", out var kind) && kind.GetString() == "shepard3" };
        const int dimensions = HistogramPca.Components;
        if (model.Histogram?.Basis is not { Length: dimensions } basis ||
            basis.Any(row => row is null || row.Length != model.Histogram.InputBins || row.Any(v => !double.IsFinite(v))) ||
            model.Histogram.InputBins != (version == 6 ? HistogramPca.Bins : version == 5 ? HistogramPca.LegacyLargeBins : version == 4 ? HistogramPca.LegacyCombinedBins : HistogramPca.LegacyColorBins) ||
            model.Scale.Length != dimensions || model.Scale.Any(v => !double.IsFinite(v) || v <= 0) ||
            model.Centers.Length == 0 || model.Centers.Length > 2048 || model.Centers.Length != model.Coefficients.Length ||
            model.Centers.Any(row => row.Length != dimensions || row.Any(v => !double.IsFinite(v))) ||
            model.Coefficients.Any(v => !double.IsFinite(v)) || !double.IsFinite(model.Baseline))
            throw new InvalidDataException("Calibration model has invalid dimensions or numeric values. Create a new calibration.");
        return model;
    }
    public bool SmoothHistogram;
    public double PeakContentNits=1000;
    public static CalibrationModel FromSmoothHistogram(double[][] states,double[] gains,double peak) {
        if(states.Length!=gains.Length || gains.Any(v=>!double.IsFinite(v)))throw new InvalidDataException("Invalid histogram gains");
        var groups=states.Select((state,i)=>(state,gain:Math.Max(0,gains[i]))).GroupBy(p=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(p.state.AsSpan())))).ToArray();
        return new CalibrationModel {SmoothHistogram=true,PeakContentNits=peak,LocalNeighbors=true,Centers=groups.Select(g=>g.First().state).ToArray(),Coefficients=groups.Select(g=>g.Average(p=>p.gain)).ToArray(),Scale=Enumerable.Repeat(1.0,SmoothedHsv.Dimensions).ToArray()};
    }
    public bool LocalNeighbors;
    public bool ClusterModel;
    public bool ScaledClusters;
    public bool TransportModel;
    public bool KernelModel;
    public bool LegacyKernelCoverage;
    public int Dimensions => SmoothHistogram?SmoothedHsv.Dimensions:ScaledClusters?49:ClusterModel?40:14;
    public double[] Encode(CalibrationScene scene)=>SmoothHistogram?scene.SmoothState(PeakContentNits):ClusterModel?scene.Clusters(TransportModel?11:KernelModel?10:ScaledClusters?8:7):(Histogram??throw new InvalidDataException("Missing PCA encoder")).Project(scene.Histogram(Histogram.InputBins));
    public static CalibrationModel FromClusters(double[][] states,double[] gains) => states.FirstOrDefault()?.Length==49 ? FromScaledClusters(states,gains) : new CalibrationModel { ClusterModel=true,LocalNeighbors=true,Centers=states.Append(ColorClusters.Black()).ToArray(),Coefficients=gains.Select(v=>Math.Max(0,v)).Append(0).ToArray(),Scale=Enumerable.Repeat(1.0,40).ToArray() };
    public static CalibrationModel FromScaledClusters(double[][] states,double[] gains) {
        // Exact repeat states share one geometric contribution. Average their log
        // gains rather than treating repeat acquisitions as extra coverage.
        var groups=states.Append(ScaledColorClusters.Black()).Select((state,i)=>(state,gain:i<gains.Length?Math.Max(0,gains[i]):0)).GroupBy(p=>string.Join(",",p.state.Select(v=>v.ToString("R",System.Globalization.CultureInfo.InvariantCulture)))).ToArray();
        return new CalibrationModel {ClusterModel=true,ScaledClusters=true,LocalNeighbors=true,Centers=groups.Select(g=>g.First().state).ToArray(),Coefficients=groups.Select(g=>g.Average(p=>p.gain)).ToArray(),Scale=Enumerable.Repeat(1.0,49).ToArray()};
    }
    public static CalibrationModel FromGaussianClusters(double[][] states,double[] gains) {
        var model=FromScaledClusters(states,gains);model.KernelModel=true;return model;
    }
    public static CalibrationModel FromCubeClusters(double[][] states,double[] gains){var model=FromScaledClusters(states,gains);model.TransportModel=true;return model;}
    double Distance(double[] a,double[] b)=>SmoothHistogram?SmoothedHsv.DistanceSquared(a,b):TransportModel?CubeTransport.Cost(a,b):KernelModel?GaussianClusterKernel.Distance(a,b,LegacyKernelCoverage):ScaledClusters?ScaledColorClusters.Distance(a,b):ClusterModel?ColorClusters.Distance(a,b):a.Select((v,j)=>(v-b[j])*(v-b[j])).Sum();
    public HistogramPca? Histogram;
    public double Predict(CalibrationScene scene) => Predict(Encode(scene));
    public double[][] Centers; public double[] Coefficients, Scale; public double Baseline;
    public CalibrationModel(double[][] input, double[] values)
    {
        var x = input.Append(new double[14]).ToArray();
        var y = values.Append(0).ToArray();
        int n = x.Length;
        Scale = new double[14];
        for (int j = 0; j < 14; j++)
        {
            double mean = x.Average(v => v[j]);
            Scale[j] = Math.Max(.05, Math.Sqrt(x.Average(v => (v[j] - mean) * (v[j] - mean))));
        }
        Centers = x.Select(v => v.Select((a, j) => a / Scale[j]).ToArray()).ToArray();
        // Stored values are measured log-gains, not signed RBF coefficients.
        // Positive normalized weights preserve constants and measured bounds.
        Coefficients = y.Select(v => Math.Max(0, v)).ToArray();
        LocalNeighbors = true;
        Baseline = 0;
    }

    static double Kernel(double[] a, double[] b)
    {
        double d = 0;
        for (int j = 0; j < 14; j++)
            d += (a[j] - b[j]) * (a[j] - b[j]);
        return Math.Exp(-.09 * d);
    }
    public double Predict(double[] state)
    {
        if(SmoothHistogram)return SmoothedHsv.Predict(state,Centers,Coefficients);
        var z = state.Select((v, j) => v / Scale[j]).ToArray();
        if (LocalNeighbors)
        {
            double weighted = 0, total = 0;
            for (int i = 0; i < Centers.Length; i++)
            {
                double distance = Distance(z,Centers[i]);
                double weight = KernelModel || ScaledClusters ? 1 / Math.Pow((TransportModel?Math.Cbrt(Math.Max(0,distance)):Math.Sqrt(Math.Max(0,distance))) + .0001, 7) : 1 / Math.Pow(distance + .0001, 3);
                weighted += weight * Coefficients[i];
                total += weight;
            }
            return Math.Max(0, weighted / total);
        }
        return Math.Max(0, Centers.Select((v, i) => Kernel(z, v) * Coefficients[i]).Sum() - Baseline);
    }
    // Support rises with nearby samples; steep local log-gain slopes lower
    // confidence. Distances use the same normalized PCA coordinates as inference.
    public double Confidence(double[] state, IEnumerable<double[]>? planned = null)
    {
        var z = state.Select((v, j) => v / Scale[j]).ToArray();
        var neighbors = Centers.Select((point, i) => (point, gain: Coefficients[i]))
            .Concat((planned ?? []).Select(point => (point: point.Select((v, j) => v / Scale[j]).ToArray(), gain: Predict(point))))
            .Select(p => (p.point, p.gain, distance: SmoothHistogram?Math.Sqrt(Distance(p.point,z)):TransportModel?Math.Cbrt(Distance(p.point,z)):Distance(p.point,z)))
            .OrderBy(p => p.distance).Take(12).ToArray();
        double support = 0, slopeSum = 0, slopeWeight = 0;
        for (int i = 0; i < neighbors.Length; i++)
        {
            double wi = 1 / (neighbors[i].distance + (SmoothHistogram?SmoothedHsv.Bandwidth:.01));
            support += wi;
            for (int j = 0; j < i; j++)
            {
                double separation = TransportModel?Math.Cbrt(Distance(neighbors[i].point,neighbors[j].point)):Math.Sqrt(Distance(neighbors[i].point,neighbors[j].point));
                if (separation < 1e-8) continue;
                double weight = wi / (neighbors[j].distance + (SmoothHistogram?SmoothedHsv.Bandwidth:.01));
                slopeSum += weight * Math.Abs(neighbors[i].gain - neighbors[j].gain) / separation;
                slopeWeight += weight;
            }
        }
        double slope = slopeWeight > 0 ? slopeSum / slopeWeight : 0;
        return support / (1 + slope);
    }
    public string Json(string monitor) => SmoothHistogram ? JsonSerializer.Serialize(new {version=14,interpolation="laplace-hsv-cubic",features=SmoothedHsv.Dimensions,histogram_shape=new[]{12,6,16},distance_scale=Math.Sqrt(SmoothedHsv.Dimensions/9216.0),smoothing=SmoothedHsv.Smoothing,bandwidth=SmoothedHsv.Bandwidth,weight_power=3,peak_content_nits=PeakContentNits,peak=250,baseline=0,epsilon=.3,scale=Scale,centers=Centers,coefficients=Coefficients,samples=Centers.Length,monitor_device=monitor},new JsonSerializerOptions{WriteIndented=true}) : TransportModel ? JsonSerializer.Serialize(new {version=11,interpolation="smooth-transport-cube7",features=49,geometry_id=CubeTransport.Id,brightness_weight=CubeTransport.BrightnessSlope*100,brightness_offset=CubeTransport.Offset,color_scale=CubeTransport.ColorScale,normalization=CubeTransport.Normalization,area_weight=0,cluster_iterations=8,cluster_count=8,peak=250,baseline=0,epsilon=.3,scale=Scale,centers=Centers,coefficients=Coefficients,samples=Centers.Length-1,monitor_device=monitor,domain="smooth circular HSV, eight k-means components with isotropic RMS spread",distance="cube root of exact population-weighted Gaussian component transport cost; no coverage penalty"},new JsonSerializerOptions{WriteIndented=true}) : ScaledClusters ? JsonSerializer.Serialize(new {version=KernelModel?(LegacyKernelCoverage?9:10):8,interpolation=KernelModel?"gaussian-cluster-distance7":"scaled-cluster-distance7",features=49,brightness_weight=ScaledColorClusters.BrightnessWeight,area_weight=KernelModel?(LegacyKernelCoverage?GaussianClusterKernel.LegacyAreaWeight:0):ScaledColorClusters.AreaWeight,cluster_iterations=8,cluster_count=8,peak=250,baseline=0,epsilon=.3,scale=Scale,centers=Centers,coefficients=Coefficients,samples=Centers.Length-1,monitor_device=monitor,domain="8 reference-normalized 4D circular HSV clusters plus non-black coverage",distance=KernelModel?"Gaussian MMD squared with isotropic RMS spread; bandwidth 1; normalized unit point reference; no explicit coverage cost":"squared symmetric greedy transport length plus quadratic coverage cost"},new JsonSerializerOptions {WriteIndented=true}) : ClusterModel ? JsonSerializer.Serialize(new { version=7,interpolation="cluster-transport-shepard3",features=40,brightness_weight=10,cluster_iterations=8,cluster_count=8,peak=250,baseline=0,epsilon=.3,scale=Scale,centers=Centers,coefficients=Coefficients,samples=Centers.Length-1,monitor_device=monitor,domain="8 weighted color clusters: S^2 cos(H), S^2 sin(H), 10 sqrt(V/10000), population, RMS spread",distance="symmetric greedy mixture transport; squared component center/spread cost" },new JsonSerializerOptions { WriteIndented=true }) : JsonSerializer.Serialize(new { histogram_pca = Histogram, version = Histogram?.InputBins == HistogramPca.Bins ? 6 : Histogram?.InputBins == HistogramPca.LegacyLargeBins ? 5 : Histogram?.InputBins == HistogramPca.LegacyCombinedBins ? 4 : LocalNeighbors ? 3 : 2, interpolation = LocalNeighbors ? "shepard3" : "gaussian_rbf", features = 14, peak = 250, epsilon = .3, baseline = Baseline, scale = Scale, centers = Centers, coefficients = Coefficients, samples = Centers.Length - 1, monitor_device = monitor, domain = "14-component PCA of soft HSV histogram (9 hue, 8 square saturation, 128 sqrt value), linear BT.2020 signals" }, new JsonSerializerOptions { WriteIndented = true });
}
