using System.Diagnostics;
using System.Text.Json;

namespace OledCalibration;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if(args.Contains("--gamut-self-test")) {ManagedCalibration.GamutChecks(Backend.Root);return;}
        if(args.Contains("--histogram-self-test")) {
            HistogramChecks.Run(Backend.Root);return;
        }
        if(args.Contains("--package-self-test"))
        {
            string root=Backend.Root;
            string data=Backend.DataRoot;
            if(StandaloneRuntime.Root!=null&&data.Contains(Path.DirectorySeparatorChar+"packages"+Path.DirectorySeparatorChar))throw new IOException("Calibration data is version-specific");
            foreach(string name in new[]{"hdr-probe.exe","device-info.exe","hook-probe.exe","oled-apl-hook.dll","dbghelp.dll","symsrv.dll"})
                if(!File.Exists(Path.Combine(root,"build",name)))throw new IOException("Missing packaged dependency: "+name);
            var devices=DeviceInventory.Read(root).GetAwaiter().GetResult();
            Backend.Run(Path.Combine(root,"build","hdr-probe.exe"),root,_=>{},"--self-test").GetAwaiter().GetResult();
            var metadata=new CalibrationMetadata();
            Directory.CreateDirectory(Path.Combine(root,"build","package-test"));
            ManagedCalibration.AddBenchmarks(root,Path.Combine(root,"build","package-test"),metadata);
            File.WriteAllText(Path.Combine(root,"package-check.txt"),$"PASS: native helpers, device discovery ({devices.cameras.Length} cameras), matcher tests and benchmark assets.\n");
            return;
        }
        if (args.Contains("--replace"))
        {
            foreach (var p in Process.GetProcessesByName("OledCalibration"))
            {
                using (p)
                {
                    if (p.Id == Environment.ProcessId)
                        continue;
                    try
                    {
                        p.CloseMainWindow();
                        p.WaitForExit(3000);
                    }
                    catch { }
                }
            }
        }
        if (args.Contains("--self-test"))
        {
            Backend.SelfTest();
            return;
        }
        if (args.Contains("--calibration-self-test"))
        {
            ManagedCalibration.VerifyCalibration(Backend.Root);
            return;
        }

        if (args.Contains("--validate-hook") || args.Contains("--validate-toggles"))
        {
            var output = Path.Combine(Backend.Root, "build", "hook-validation.log");
            File.WriteAllText(output, "");
            void report(string s) => File.AppendAllText(output, s + Environment.NewLine);
            for (int cycle = 0; cycle < (args.Contains("--validate-toggles") ? 3 : 1); cycle++)
            {
                report("Toggle cycle " + (cycle + 1));
                try
                {
                    var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(Backend.Root, "reports", "adaptive", "runtime-model.json"))).RootElement;
                    var screen = Screen.AllScreens.First(s => s.DeviceName == saved.GetProperty("monitor_device").GetString());
                    var loading = Backend.Apply(Backend.Root, Path.Combine(Backend.Root, "reports", "adaptive", "runtime-model.json"), true, screen, report);
                    while (!loading.IsCompleted)
                    {
                        Application.DoEvents();
                        Thread.Sleep(10);
                    }
                    loading.GetAwaiter().GetResult();
                    using var stimulus = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(screen.Bounds.Left + 20, screen.Bounds.Top + 20), Size = new Size(120, 120), BackColor = Color.Gray, ShowInTaskbar = false, Text = "Filter test" };
                    stimulus.Show();
                    var timer = Stopwatch.StartNew();
                    while (timer.ElapsedMilliseconds < 1200)
                    {
                        Application.DoEvents();
                        Thread.Sleep(10);
                    }
                    stimulus.Close();
                    if (!File.ReadAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp", "oled-apl", "hook-diagnostic.log")).Contains("First complete adaptive filter frame submitted"))
                        throw new InvalidOperationException("DWM loaded the hook but no filtered frame was observed.");
                    report("PASS: live DWM adaptive filter frame submitted");
                }
                catch (Exception e) { report("FAIL: " + e); }
                finally { try { Backend.Disable(); report("Filter unloaded after diagnostic"); } catch (Exception e) { report("Unload failed: " + e.Message); } }
            }
            return;
        }
        Application.Run(new MainWindow());
    }
}
