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
        if (version is not (2 or 3) || (version == 3 && (!r.TryGetProperty("interpolation", out var interpolation) || interpolation.GetString() != "shepard3")))
            throw new InvalidDataException("Unsupported calibration interpolation model.");
        if (!r.TryGetProperty("histogram_pca", out var encoder) || encoder.ValueKind == JsonValueKind.Null)
            throw new InvalidDataException("Create a new PCA calibration. This model has no histogram encoder.");
        var model = new CalibrationModel { Centers = r.GetProperty("centers").EnumerateArray().Select(a => a.EnumerateArray().Select(v => v.GetDouble()).ToArray()).ToArray(), Coefficients = r.GetProperty("coefficients").EnumerateArray().Select(v => v.GetDouble()).ToArray(), Scale = r.GetProperty("scale").EnumerateArray().Select(v => v.GetDouble()).ToArray(), Histogram = JsonSerializer.Deserialize<HistogramPca>(encoder.GetRawText()), Baseline = r.GetProperty("baseline").GetDouble(), LocalNeighbors = r.TryGetProperty("interpolation", out var kind) && kind.GetString() == "shepard3" };
        const int dimensions = HistogramPca.Components;
        if (model.Histogram?.Basis is not { Length: dimensions } basis ||
            basis.Any(row => row is null || row.Length != HistogramPca.Bins || row.Any(v => !double.IsFinite(v))) ||
            model.Scale.Length != dimensions || model.Scale.Any(v => !double.IsFinite(v) || v <= 0) ||
            model.Centers.Length == 0 || model.Centers.Length > 2048 || model.Centers.Length != model.Coefficients.Length ||
            model.Centers.Any(row => row.Length != dimensions || row.Any(v => !double.IsFinite(v))) ||
            model.Coefficients.Any(v => !double.IsFinite(v)) || !double.IsFinite(model.Baseline))
            throw new InvalidDataException("Calibration model has invalid dimensions or numeric values. Create a new calibration.");
        return model;
    }
    public bool LocalNeighbors;
    public HistogramPca? Histogram;
    public double Predict(CalibrationScene scene) => Predict((Histogram ?? throw new Exception("PCA encoder is missing")).Project(scene.Histogram()));
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
        var z = state.Select((v, j) => v / Scale[j]).ToArray();
        if (LocalNeighbors)
        {
            double weighted = 0, total = 0;
            for (int i = 0; i < Centers.Length; i++)
            {
                double distance = 0;
                for (int j = 0; j < 14; j++)
                    distance += Math.Pow(z[j] - Centers[i][j], 2);
                double weight = 1 / Math.Pow(distance + .0001, 3);
                weighted += weight * Coefficients[i];
                total += weight;
            }
            return Math.Max(0, weighted / total);
        }
        return Math.Max(0, Centers.Select((v, i) => Kernel(z, v) * Coefficients[i]).Sum() - Baseline);
    }
    public string Json(string monitor) => JsonSerializer.Serialize(new { histogram_pca = Histogram, version = LocalNeighbors ? 3 : 2, interpolation = LocalNeighbors ? "shepard3" : "gaussian_rbf", features = 14, peak = 250, epsilon = .3, baseline = Baseline, scale = Scale, centers = Centers, coefficients = Coefficients, samples = Centers.Length - 1, monitor_device = monitor, domain = "14-component PCA of soft joint RGB histogram, sqrt-linear BT.2020 coordinates" }, new JsonSerializerOptions { WriteIndented = true });
}
