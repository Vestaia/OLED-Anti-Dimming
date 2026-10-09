// SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;
using System.Text.Json;

namespace OledCalibration;

static partial class ManagedCalibration
{
    // Unlabeled basis learning only: these variations never add camera queries.
    static IEnumerable<double[]> PcaImageDistributions(string root, CalibrationMetadata meta)
    {
        string assets = Path.Combine(root, "assets", "benchmarks");
        var photos = JsonSerializer.Deserialize<JsonElement[]>(File.ReadAllText(Path.Combine(assets, "manifest.json")))!;
        var ui = JsonSerializer.Deserialize<JsonElement[]>(File.ReadAllText(Path.Combine(assets, "ui-manifest.json")))!;
        foreach (var source in photos.Concat(ui))
        {
            string path = Path.Combine(assets, source.GetProperty("file").GetString()!);
            string license = source.GetProperty("license").GetString()!;
            if (!(license.StartsWith("PD-", StringComparison.Ordinal) || license is "CC0-1.0" or "GPL-3.0-only") ||
                !Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).Equals(source.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("PCA image provenance or checksum failed.");
            using var image = new Bitmap(path);
            // Basis learning can subsample images. Camera pattern statistics remain exact.
            double aspect = (double)meta.display_width / meta.display_height;
            int width = Math.Max(1, (int)Math.Round(Math.Sqrt(4096 * aspect)));
            int height = Math.Max(1, (int)Math.Round(4096.0 / width));
            double sourceWidth = Math.Min(image.Width, image.Height * aspect);
            double sourceHeight = sourceWidth / aspect;
            double left = (image.Width - sourceWidth) / 2, top = (image.Height - sourceHeight) / 2;
            var pixels = new double[width * height][];
            static double Linear(double v) => v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4);
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    var color = image.GetPixel(Math.Clamp((int)(left + (x + .5) * sourceWidth / width), 0, image.Width - 1), Math.Clamp((int)(top + (y + .5) * sourceHeight / height), 0, image.Height - 1));
                    double r = Linear(color.R / 255.0), g = Linear(color.G / 255.0), b = Linear(color.B / 255.0);
                    pixels[y * width + x] = [.627404 * r + .329283 * g + .0433136 * b, .069097 * r + .91954 * g + .0113612 * b, .0163916 * r + .0880132 * g + .895595 * b];
                }
            // Include the original fullscreen 250-nit SDR distribution explicitly.
            var full = new double[HistogramPca.Bins];
            foreach (var pixel in pixels)
                HistogramPca.Add(full, pixel, 1.0 / pixels.Length);
            yield return full;
            for (int variant = 0; variant < 32; variant++)
            {
                double nits = new[] { 50.0, 100, 150, 250, 400, meta.peak_content_nits }[variant % 6];
                nits = Math.Min(nits, meta.peak_content_nits);
                double area = new[] { .05, .15, .35, .60, .85, 1.0 }[(variant / 6) % 6];
                double background = new[] { 0.0, 50, 100 }[(variant / 2) % 3] / 250;
                var histogram = new double[HistogramPca.Bins];
                var scaled = new double[3];
                foreach (var pixel in pixels)
                {
                    for (int j = 0; j < 3; j++)
                        scaled[j] = pixel[j] * nits / 250;
                    HistogramPca.Add(histogram, scaled, area / pixels.Length);
                }
                HistogramPca.Add(histogram, [background, background, background], 1 - area);
                yield return histogram;
            }
        }
    }

    static HistogramPca LearnPca(string root, string output, CalibrationMetadata meta, Action<string> log)
    {
        Step(5, "Preparing PCA model");
        // The exact validation-photo distributions are included without acquiring
        // their brightness labels. Camera validation still evaluates a frozen model.
        var photoMeta = new CalibrationMetadata { display_width = meta.display_width, display_height = meta.display_height, mosaic_probe = meta.mosaic_probe };
        AddBenchmarks(root, Path.Combine(output, "pca-photo-inputs"), photoMeta);
        int uiCount = JsonSerializer.Deserialize<JsonElement[]>(File.ReadAllText(Path.Combine(root, "assets", "benchmarks", "ui-manifest.json")))!.Length;
        var distributions = meta.scenes.Select(scene => scene.Histogram())
            .Concat(photoMeta.scenes.Select(scene => scene.Histogram()))
            .Concat(PcaImageDistributions(root, meta));
        var encoder = HistogramPca.Fit(distributions, meta.peak_content_nits);
        encoder.TrainingImages = photoMeta.scenes.Count + uiCount;
        log($"PCA basis: {encoder.TrainingSamples} unlabeled distributions; {photoMeta.scenes.Count} licensed photos, {uiCount} original UI layouts, {HistogramPca.SyntheticSamples} synthetic patterns; {HistogramPca.Components} components.");
        return encoder;
    }
}
