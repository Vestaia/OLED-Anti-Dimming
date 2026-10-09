// SPDX-License-Identifier: GPL-3.0-only
namespace OledCalibration;

// Distribution encoder. Coordinates are linear BT.2020 / 250 nits.
// HSV soft bins preserve distributions; hue wraps, saturation is squared,
// and value uses sqrt-linear coordinates. Legacy RGB encoders remain readable.
sealed class HistogramPca
{
    public const int LegacyColorBins = 512, LegacyCombinedBins = 768;
    public const int LegacyLargeBins = 5120;
    public const int HueBins = 9, SaturationBins = 8, ValueBins = 128;
    public const int ColorBins = HueBins * SaturationBins * ValueBins, BrightnessBins = 0, Bins = ColorBins, Components = 14;
    public const int SyntheticSamples = 4096;
    public int TrainingSamples
    {
        get; set;
    }
    public int TrainingImages
    {
        get; set;
    }
    public int TrainingDatasetVersion
    {
        get; set;
    }
    public double VarianceExplained
    {
        get; set;
    }
    public int InputBins => Basis.Length == 0 ? Bins : Basis[0].Length;
    public double[][] Basis { get; set; } = [];
    public static void Add(double[] h, double[] c, double weight = 1)
    {
        if (h.Length == Bins) {
            double r = Math.Max(0, c[0]), g = Math.Max(0, c[1]), b = Math.Max(0, c[2]);
            double value = Math.Max(r, Math.Max(g, b)), delta = value - Math.Min(r, Math.Min(g, b));
            double hue = delta == 0 ? 0 : value == r ? (g - b) / delta : value == g ? 2 + (b - r) / delta : 4 + (r - g) / delta;
            hue = ((hue / 6) % 1 + 1) % 1;
            double saturation = value > 0 ? delta / value : 0;
            double xh = hue * HueBins, xs = saturation * saturation * (SaturationBins - 1), xv = Math.Clamp(Math.Sqrt(value / 40), 0, 1) * (ValueBins - 1);
            int lh = (int)xh, ls = Math.Min(SaturationBins - 2, (int)xs), lv = Math.Min(ValueBins - 2, (int)xv);
            double th = xh - lh, ts = xs - ls, tv = xv - lv;
            for (int k = 0; k < 8; k++) {
                int dh = k & 1, ds = (k >> 1) & 1, dv = (k >> 2) & 1;
                int si = ls + ds, hi = si == 0 ? 0 : (lh + dh) % HueBins;
                h[(hi * SaturationBins + si) * ValueBins + lv + dv] += weight * (dh == 0 ? 1 - th : th) * (ds == 0 ? 1 - ts : ts) * (dv == 0 ? 1 - tv : tv);
            }
            return;
        }
        bool combined = h.Length != LegacyColorBins;
        int axis = h.Length == LegacyLargeBins ? 16 : 8;
        int colorBins = axis * axis * axis;
        int brightnessBins = h.Length - colorBins;
        double blockWeight = combined ? weight * .5 : weight;
        double xr = Math.Clamp(Math.Sqrt(Math.Max(0, c[0]) / 40) * (axis - 1), 0, axis - 1);
        double xg = Math.Clamp(Math.Sqrt(Math.Max(0, c[1]) / 40) * (axis - 1), 0, axis - 1);
        double xb = Math.Clamp(Math.Sqrt(Math.Max(0, c[2]) / 40) * (axis - 1), 0, axis - 1);
        int lr = Math.Min(axis - 2, (int)xr), lg = Math.Min(axis - 2, (int)xg), lb = Math.Min(axis - 2, (int)xb);
        double tr = xr - lr, tg = xg - lg, tb = xb - lb;
        for (int k = 0; k < 8; k++)
        {
            int r = k & 1, g = (k >> 1) & 1, b = (k >> 2) & 1;
            h[(lr + r) * axis * axis + (lg + g) * axis + lb + b] += blockWeight * (r == 0 ? 1 - tr : tr) * (g == 0 ? 1 - tg : tg) * (b == 0 ? 1 - tb : tb);
        }
        if (combined)
        {
            double luminance = .2627 * c[0] + .6780 * c[1] + .0593 * c[2];
            double brightness = Math.Clamp(Math.Sqrt(Math.Max(0, luminance) / 40) * (brightnessBins - 1), 0, brightnessBins - 1);
            int lower = Math.Min(brightnessBins - 2, (int)brightness);
            double fraction = brightness - lower;
            h[colorBins + lower] += blockWeight * (1 - fraction);
            h[colorBins + lower + 1] += blockWeight * fraction;
        }
    }
    public double[] Project(double[] h)
    {
        if (h.Length != InputBins)
            throw new ArgumentException("Histogram and saved PCA encoder must use the same binning.");
        int colorBins = InputBins == LegacyLargeBins ? 4096 : LegacyColorBins;
        bool combined = InputBins != LegacyColorBins && InputBins != Bins;
        var result = new double[Basis.Length];
        for (int j = 0; j < Basis.Length; j++)
        {
            var basis = Basis[j];
            double value = Dot(basis, h);
            result[j] = value - (combined ? .5 * (basis[0] + basis[colorBins]) : basis[0]);
        }
        return result;
    }
    static double Dot(double[] a, double[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
            sum += a[i] * b[i];
        return sum;
    }

    public static HistogramPca Fit(IEnumerable<double[]> input, double peakNits = 10000)
    {
        // Include synthetic distributions: basis learning requires no camera labels.
        var rows = new List<(int[] Indices, double[] Values)>();
        void AddRow(double[] histogram)
        {
            if (histogram.Length != Bins)
                throw new ArgumentException("PCA training histogram layout mismatch.");
            var indices = new List<int>();
            var values = new List<double>();
            for (int i = 0; i < Bins; i++)
                if (histogram[i] != 0)
                {
                    indices.Add(i);
                    values.Add(histogram[i]);
                }
            rows.Add((indices.ToArray(), values.ToArray()));
        }
        foreach (var histogram in input)
            AddRow(histogram);
        foreach (double nits in new[] { 25.0, 50, 75, 100, 150, 200, 250, 350, 500, 750, 1000, 2500, 10000, peakNits }.Where(v => v <= peakNits).Distinct())
            foreach (var color in new double[][] { [1, 1, 1], [1, 0, 0], [0, 1, 0], [0, 0, 1], [1, 1, 0], [1, 0, 1], [0, 1, 1] })
            {
                var h = new double[Bins];
                Add(h, color.Select(v => v * nits / 250).ToArray());
                AddRow(h);
            }
        var rng = new Random(2084);
        foreach (var histogram in SyntheticDistributions(peakNits))
            AddRow(histogram);
        // Covariance times v = sum(h * dot(h,v)) - n * mean * dot(mean,v).
        // Histograms are sparse before centering, so avoid densifying every row.
        var mean = new double[Bins];
        foreach (var row in rows)
            for (int k = 0; k < row.Indices.Length; k++)
                mean[row.Indices[k]] += row.Values[k] / rows.Count;
        var basis = new List<double[]>();
        // Orthogonal power iteration on covariance, without allocating a dense histogram covariance matrix.
        for (int c = 0; c < Components; c++)
        {
            var v = Enumerable.Range(0, Bins).Select(_ => rng.NextDouble() - .5).ToArray();
            for (int iteration = 0; iteration < 24; iteration++)
            {
                var next = new double[Bins];
                foreach (var row in rows)
                {
                    double dot = 0;
                    for (int k = 0; k < row.Indices.Length; k++)
                        dot += row.Values[k] * v[row.Indices[k]];
                    for (int k = 0; k < row.Indices.Length; k++)
                        next[row.Indices[k]] += row.Values[k] * dot;
                }
                double meanDot = Dot(mean, v) * rows.Count;
                for (int i = 0; i < Bins; i++)
                    next[i] -= mean[i] * meanDot;
                foreach (var old in basis)
                {
                    double dot = Dot(next, old);
                    for (int i = 0; i < Bins; i++)
                        next[i] -= old[i] * dot;
                }
                double norm = Math.Sqrt(next.Sum(a => a * a));
                if (norm < 1e-12)
                    throw new Exception("Insufficient histogram rank");
                v = next.Select(a => a / norm).ToArray();
            }
            basis.Add(v);
        }
        double totalVariance = -rows.Count * Dot(mean, mean);
        foreach (var row in rows)
            totalVariance += Dot(row.Values, row.Values);
        double capturedVariance = 0;
        foreach (var direction in basis)
        {
            double center = Dot(mean, direction);
            foreach (var row in rows)
            {
                double projection = -center;
                for (int k = 0; k < row.Indices.Length; k++)
                    projection += row.Values[k] * direction[row.Indices[k]];
                capturedVariance += projection * projection;
            }
        }
        return new HistogramPca { Basis = basis.ToArray(), TrainingSamples = rows.Count, TrainingDatasetVersion = 1, VarianceExplained = capturedVariance / totalVariance };
    }
    static IEnumerable<double[]> SyntheticDistributions(double peakNits)
    {
        var random = new Random(2084);
        for (int n = 0; n < SyntheticSamples; n++)
        {
            var histogram = new double[Bins];
            double area = n % 4 == 0 ? 1 : .025 + .975 * random.NextDouble();
            Add(histogram, [0, 0, 0], 1 - area);
            if (n % 4 == 3)
            {
                // UI-like neutral populations with an independently colored accent.
                double foreground = Math.Min(peakNits, 50 + random.NextDouble() * 350) / 250;
                double background = Math.Min(peakNits, random.NextDouble() * 100) / 250;
                double share = n % 8 == 3 ? .2 : .8, accent = .01 + .14 * random.NextDouble();
                Add(histogram, [foreground, foreground, foreground], area * share * (1 - accent));
                Add(histogram, [background, background, background], area * (1 - share) * (1 - accent));
                Add(histogram, [random.NextDouble() * foreground, random.NextDouble() * foreground, random.NextDouble() * foreground], area * accent);
            }
            else
            {
                int modes = n % 4 == 0 ? 1 : 2 + n % 5;
                var centers = Enumerable.Range(0, modes).Select(_ => Enumerable.Range(0, 3).Select(_ => random.NextDouble()).ToArray()).ToArray();
                if (n % 16 == 0)
                    centers[0] = [centers[0][0], centers[0][0], centers[0][0]];
                if (modes > 1)
                {
                    // Alternate bright/dim imbalance; keep joint color distributions.
                    for (int j = 0; j < 3; j++)
                        centers[0][j] *= .2;
                }
                int tiles = modes == 1 ? 1 : 64;
                double spread = n % 4 == 2 ? .01 + .19 * random.NextDouble() : 0;
                var color = new double[3];
                for (int tile = 0; tile < tiles; tile++)
                {
                    int mode = modes == 1 ? 0 : random.NextDouble() < (n % 8 < 4 ? .2 : .8) ? 0 : 1 + random.Next(modes - 1);
                    for (int j = 0; j < 3; j++)
                    {
                        double coordinate = Math.Clamp(centers[mode][j] + spread * (random.NextDouble() + random.NextDouble() + random.NextDouble() - 1.5), 0, 1);
                        color[j] = peakNits / 250 * coordinate * coordinate;
                    }
                    Add(histogram, color, area / tiles);
                }
            }
            yield return histogram;
        }
    }

}
