// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using System.Security.Cryptography;

namespace OledCalibration;

static partial class ManagedCalibration
{
    public static double[] Moments(double[][] pixels)
    {
        var m = new double[14];
        foreach (var p in pixels)
        {
            for (int j = 0; j < 3; j++)
            {
                m[j] += p[j];
                m[j + 3] += p[j] * p[j];
            }
            double lo = p.Min(), hi = p.Max();
            m[6] += lo;
            m[7] += hi;
            m[8] += lo * lo;
            m[9] += hi * hi;
            m[10] += hi - lo;
            for (int j = 0; j < 3; j++)
                m[11 + j] += hi > new[] { .25, .5, .75 }[j] ? 1 : 0;
        }
        return m.Select(v => v / pixels.Length).ToArray();
    }
    static void Texture(string path, double[][] pixels, int width, int height)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write(width);
        w.Write(height);
        foreach (var p in pixels)
        {
            foreach (var v in p)
                w.Write((float)v);
            w.Write(1f);
        }
    }
    static (double[][] pixels, int width, int height) Texture(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        int width = r.ReadInt32(), height = r.ReadInt32();
        var p = new double[width * height][];
        for (int i = 0; i < p.Length; i++)
        {
            p[i] = [r.ReadSingle(), r.ReadSingle(), r.ReadSingle()];
            r.ReadSingle();
        }
        return (p, width, height);
    }
    public static double[] SceneHistogram(CalibrationScene scene)
    {
        var h = new double[HistogramPca.Bins];
        double[][] pixels;
        int width, height;
        if (File.Exists(scene.asset))
            (pixels, width, height) = Texture(scene.asset);
        else if (scene.asset == "" && scene.moments.Length == 14)
        {
            pixels = [scene.moments.Take(3).ToArray()];
            width = height = 1;
        }
        else
            throw new FileNotFoundException("Histogram calibration requires its pattern asset", scene.asset);
        // Count exact raster coverage of every source texel. Point sampling is
        // separable, so this costs O(display width + height + texture pixels),
        // rather than sampling or walking the full display for every scene.
        float half = MathF.Sqrt((float)scene.area) * .5f;
        int displayWidth = scene.display_width, displayHeight = scene.display_height;
        int side = ProbePattern.Side(displayWidth, displayHeight);
        int left = (displayWidth - side) / 2, top = (displayHeight - side) / 2;
        (long[] all, long[] probe, long probeSize) Counts(int length, int texels, int origin)
        {
            var all = new long[texels];
            var probe = new long[texels];
            long probeSize = 0;
            for (int i = 0; i < length; i++)
            {
                float uv = (i + .5f) / length;
                bool inProbe = scene.mosaic_probe ? i >= origin && i < origin + side : MathF.Abs(uv - .5f) <= .05f;
                if (inProbe)
                    probeSize++;
                if (MathF.Abs(uv - .5f) > half)
                    continue;
                float source = (uv - .5f) / (2 * half) + .5f;
                int texel = Math.Clamp((int)(source * texels), 0, texels - 1);
                all[texel]++;
                if (inProbe)
                    probe[texel]++;
            }
            return (all, probe, probeSize);
        }
        var xs = Counts(displayWidth, width, left);
        var ys = Counts(displayHeight, height, top);
        double total = (double)displayWidth * displayHeight;
        long colored = 0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                long count = xs.all[x] * ys.all[y] - xs.probe[x] * ys.probe[y];
                if (count == 0)
                    continue;
                colored += count;
                HistogramPca.Add(h, pixels[y * width + x], count / total);
            }
        long probePixels = xs.probeSize * ys.probeSize;
        HistogramPca.Add(h, [0, 0, 0], (total - colored - probePixels) / total);
        if (scene.mosaic_probe)
        {
            var probeHistogram = ExactProbeHistogram(side);
            for (int i = 0; i < h.Length; i++)
                h[i] += probeHistogram[i] / total;
        }
        else
            HistogramPca.Add(h, [.4, .4, .4], probePixels / total);
        return h;
    }
    static readonly Dictionary<int, double[]> exactProbeHistograms = new();
    static double[] ExactProbeHistogram(int side)
    {
        lock (exactProbeHistograms)
        {
            if (exactProbeHistograms.TryGetValue(side, out var cached))
                return cached;
            var h = new double[HistogramPca.Bins];
            for (int y = 0; y < side; y++)
                for (int x = 0; x < side; x++)
                    HistogramPca.Add(h, ProbePattern.Pixel(x, y));
            exactProbeHistograms.Add(side, h);
            return h;
        }
    }
    public static void AddBenchmarks(string root, string folder, CalibrationMetadata meta)
    {
        var assets = Path.Combine(root, "assets", "benchmarks");
        meta.sources = JsonSerializer.Deserialize<JsonElement[]>(File.ReadAllText(Path.Combine(assets, "manifest.json")))!;
        foreach (var source in meta.sources)
        {
            var path = Path.Combine(assets, source.GetProperty("file").GetString()!);
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            if (hash != source.GetProperty("sha256").GetString() || !source.GetProperty("license").GetString()!.StartsWith("PD-"))
                throw new Exception("Benchmark license manifest or image checksum failed");
            using var original = Image.FromFile(path);
            using var resized = new Bitmap(320, 180);
            using (var g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(original, 0, 0, 320, 180);
            }
            var pixels = new double[320 * 180][];
            double Linear(double v) => v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4);
            for (int y = 0; y < 180; y++)
                for (int x = 0; x < 320; x++)
                {
                    var c = resized.GetPixel(x, y);
                    double r = Linear(c.R / 255.0), g = Linear(c.G / 255.0), b = Linear(c.B / 255.0);
                    pixels[y * 320 + x] = [.627404 * r + .329283 * g + .0433136 * b, .069097 * r + .91954 * g + .0113612 * b, .0163916 * r + .0880132 * g + .895595 * b];
                }
            string name = "photo-" + source.GetProperty("name").GetString(), asset = Path.GetFullPath(Path.Combine(folder, name + ".bin"));
            Texture(asset, pixels, 320, 180);
            foreach (double area in new[] { .4, 1 })
                meta.scenes.Add(new()
                {
                    name = name + "_" + Number(area),
                    scene = name,
                    training = false,
                    asset = asset.Replace('\\', '/'),
                    area = area,
                    moments = Moments(pixels)
                });
        }
        meta.public_domain_benchmarks = true;
    }
}
