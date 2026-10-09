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
    public static double[] SceneHistogram(CalibrationScene scene, int bins = HistogramPca.Bins)
    {
        var h = new double[bins];
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
            var probeHistogram = ExactProbeHistogram(side, bins);
            for (int i = 0; i < h.Length; i++)
                h[i] += probeHistogram[i] / total;
        }
        else
            HistogramPca.Add(h, [.4, .4, .4], probePixels / total);
        return h;
    }
    public static double[] SceneClusters(CalibrationScene scene,int version=8) {
        var colors=SceneColors(scene);
        if(version>=11)return ScaledColorClusters.Fit(colors.Select(p=>CubeTransport.Map([p.Key.Item1,p.Key.Item2,p.Key.Item3],p.Value)));
        return version>=8 ? ScaledColorClusters.Fit(colors.Select(p=>ScaledColorClusters.Map([p.Key.Item1,p.Key.Item2,p.Key.Item3],p.Value))) : ColorClusters.Fit(colors.Select(p=>ColorClusters.Map([p.Key.Item1,p.Key.Item2,p.Key.Item3],p.Value)));
    }
    public static Dictionary<(double,double,double),double> SceneColors(CalibrationScene scene,bool includeProbe=true)
    {
        var colors = new Dictionary<(double,double,double), double>();
        void Add(double[] p,double w) { if(w<=0)return;var key=(p[0],p[1],p[2]);colors[key]=colors.GetValueOrDefault(key)+w; }
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
                Add(pixels[y * width + x], count / total);
            }
        long probePixels = xs.probeSize * ys.probeSize;
        Add([0,0,0],(total-colored-probePixels)/total);
        if (includeProbe && scene.mosaic_probe)
        {
            foreach(var entry in ExactProbeColors(side))Add([entry.Key.Item1,entry.Key.Item2,entry.Key.Item3],entry.Value/total);
        }
        else if(includeProbe)
            Add([.4,.4,.4],probePixels/total);
        return colors;
    }
    static readonly Dictionary<int, Dictionary<(double,double,double),double>> probeColors = new();
    static Dictionary<(double,double,double),double> ExactProbeColors(int side) {
        lock(probeColors) {
            if(probeColors.TryGetValue(side,out var result))return result;
            result=new();for(int y=0;y<side;y++)for(int x=0;x<side;x++){var p=ProbePattern.Pixel(x,y);var key=(p[0],p[1],p[2]);result[key]=result.GetValueOrDefault(key)+1;}
            probeColors[side]=result;return result;
        }
    }
    static readonly Dictionary<(int side, int bins), double[]> exactProbeHistograms = new();
    static double[] ExactProbeHistogram(int side, int bins)
    {
        lock (exactProbeHistograms)
        {
            if (exactProbeHistograms.TryGetValue((side, bins), out var cached))
                return cached;
            var h = new double[bins];
            for (int y = 0; y < side; y++)
                for (int x = 0; x < side; x++)
                    HistogramPca.Add(h, ProbePattern.Pixel(x, y));
            exactProbeHistograms.Add((side, bins), h);
            return h;
        }
    }
    public static void AddBenchmarks(string root, string folder, CalibrationMetadata meta)
    {
        Directory.CreateDirectory(folder);
        var assets = Path.Combine(root, "assets", "benchmarks");
        meta.sources = JsonSerializer.Deserialize<JsonElement[]>(File.ReadAllText(Path.Combine(assets, "manifest.json")))!;
        foreach (var source in meta.sources)
        {
            var path = Path.Combine(assets, source.GetProperty("file").GetString()!);
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            string license = source.GetProperty("license").GetString()!;
            if (hash != source.GetProperty("sha256").GetString() || !(license.StartsWith("PD-") || license == "CC0-1.0"))
                throw new Exception("Benchmark license manifest or image checksum failed");
            using var original = Image.FromFile(path);
            int width = 640, height = Math.Max(1, (int)Math.Round(width * (double)meta.display_height / meta.display_width));
            using var resized = new Bitmap(width, height);
            using (var g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                double scale = Math.Max(width / (double)original.Width, height / (double)original.Height);
                float drawWidth = (float)(original.Width * scale), drawHeight = (float)(original.Height * scale);
                g.DrawImage(original, (width - drawWidth) / 2, (height - drawHeight) / 2, drawWidth, drawHeight);
            }
            var pixels = new double[width * height][];
            double Linear(double v) => v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4);
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    var c = resized.GetPixel(x, y);
                    double r = Linear(c.R / 255.0), g = Linear(c.G / 255.0), b = Linear(c.B / 255.0);
                    pixels[y * width + x] = [.627404 * r + .329283 * g + .0433136 * b, .069097 * r + .91954 * g + .0113612 * b, .0163916 * r + .0880132 * g + .895595 * b];
                }
            string name = "photo-" + source.GetProperty("name").GetString() + "-" + Path.GetFileName(folder), asset = Path.GetFullPath(Path.Combine(folder, name + ".bin"));
            Texture(asset, pixels, width, height);
            foreach (double area in new[] { 1.0 })
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
        StampProbe(meta, meta.scenes.Where(r => r.asset.StartsWith(Path.GetFullPath(folder).Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)));
        meta.public_domain_benchmarks = true;
    }
}
