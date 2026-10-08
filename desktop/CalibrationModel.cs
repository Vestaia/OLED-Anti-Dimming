// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;

namespace OledCalibration;
// Regularized Gaussian correction fit over histogram PCA coordinates.
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
        if (!r.TryGetProperty("histogram_pca", out var encoder) || encoder.ValueKind == JsonValueKind.Null)
            throw new InvalidDataException("Create a new PCA calibration. This model has no histogram encoder.");
        var model = new CalibrationModel { Centers = r.GetProperty("centers").EnumerateArray().Select(a => a.EnumerateArray().Select(v => v.GetDouble()).ToArray()).ToArray(), Coefficients = r.GetProperty("coefficients").EnumerateArray().Select(v => v.GetDouble()).ToArray(), Scale = r.GetProperty("scale").EnumerateArray().Select(v => v.GetDouble()).ToArray(), Histogram = JsonSerializer.Deserialize<HistogramPca>(encoder.GetRawText()), Baseline = r.GetProperty("baseline").GetDouble() };
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
        var a = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                a[i, j] = Kernel(Centers[i], Centers[j]) + (i == j ? .01 : 0);
        // Cholesky: Gaussian kernel plus positive diagonal regularization is SPD.
        for (int i = 0; i < n; i++)
            for (int j = 0; j <= i; j++)
            {
                double v = a[i, j];
                for (int k = 0; k < j; k++)
                    v -= a[i, k] * a[j, k];
                if (i == j)
                {
                    if (v <= 0 || !double.IsFinite(v))
                        throw new Exception("Calibration fit is singular");
                    a[i, j] = Math.Sqrt(v);
                }
                else
                    a[i, j] = v / a[j, j];
            }
        var b = new double[n];
        for (int i = 0; i < n; i++)
        {
            double v = y[i];
            for (int j = 0; j < i; j++)
                v -= a[i, j] * b[j];
            b[i] = v / a[i, i];
        }
        Coefficients = new double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            double v = b[i];
            for (int j = i + 1; j < n; j++)
                v -= a[j, i] * Coefficients[j];
            Coefficients[i] = v / a[i, i];
        }
        Baseline = Centers.Select((v, i) => Kernel(v, new double[14]) * Coefficients[i]).Sum();
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
        return Math.Max(0, Centers.Select((v, i) => Kernel(z, v) * Coefficients[i]).Sum() - Baseline);
    }
    public string Json(string monitor) => JsonSerializer.Serialize(new { histogram_pca = Histogram, version = Histogram == null ? 1 : 2, features = 14, peak = 250, epsilon = .3, baseline = Baseline, scale = Scale, centers = Centers, coefficients = Coefficients, samples = Centers.Length - 1, monitor_device = monitor, domain = "14-component PCA of soft joint RGB histogram, sqrt-linear BT.2020 coordinates" }, new JsonSerializerOptions { WriteIndented = true });
}
