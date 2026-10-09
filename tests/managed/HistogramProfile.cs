// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Text.Json;
namespace OledCalibration;
static class HistogramProfile {
    public static void Run(string source,string folder) {
        Directory.CreateDirectory(folder);
        var times=new Dictionary<string,double>();
        T Time<T>(string name,Func<T> action){var sw=Stopwatch.StartNew();var result=action();times[name]=sw.Elapsed.TotalMilliseconds;Console.WriteLine($"{name}: {times[name]:F2} ms");return result;}
        Time("Apply monitor check (read + full JSON parse)",()=>{using var saved=JsonDocument.Parse(File.ReadAllText(source));return saved.RootElement.GetProperty("monitor_device").GetString();});
        var text=Time("Read original JSON",()=>File.ReadAllText(source));
        using var json=Time("Parse original JSON",()=>JsonDocument.Parse(text));var root=json.RootElement;
        var centers=Time("Deserialize anchor arrays",()=>root.GetProperty("centers").Deserialize<double[][]>()!);
        var gains=root.GetProperty("coefficients").Deserialize<double[]>()!;
        var remapped=Time("Remap smoothing",()=>SmoothedHsv.Remap(centers,root.GetProperty("smoothing").Deserialize<double[]>()!,root.GetProperty("histogram_shape").Deserialize<int[]>()!));
        var model=Time("Deduplicate and construct model",()=>CalibrationModel.FromSmoothHistogram(remapped,gains,root.GetProperty("peak_content_nits").GetDouble()));
        var upgraded=Time("Serialize current JSON",()=>model.Json("performance-fixture"));
        string current=Path.Combine(folder,"current.json");File.WriteAllText(current,upgraded);
        Time("WriteModel original (complete apply preprocessing)",()=>{Backend.WriteModel(source,Path.Combine(folder,"runtime.bin"));return true;});
        Time("WriteModel current (no remapping)",()=>{Backend.WriteModel(current,Path.Combine(folder,"current.bin"));return true;});
        times["anchors"]=centers.Length;times["binary_bytes"]=new FileInfo(Path.Combine(folder,"runtime.bin")).Length;
        File.WriteAllText(Path.Combine(folder,"managed-timings.json"),JsonSerializer.Serialize(times,new JsonSerializerOptions{WriteIndented=true}));
    }
}
