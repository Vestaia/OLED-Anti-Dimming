// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;

namespace OledCalibration;

static class Backend
{
    public static string DataRoot
    {
        get
        {
            if (StandaloneRuntime.Root == null)
                return Root;
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OledCalibration", "data");
            Directory.CreateDirectory(Path.Combine(folder, "build"));
            Directory.CreateDirectory(Path.Combine(folder, "reports", "adaptive"));
            string settings = Path.Combine(folder, "build", "managed-settings.json");
            if (!File.Exists(settings))
            {
                string packages = Directory.GetParent(Root)!.FullName;
                // Preserve old reports in place; recover their absolute model
                // selection without moving files used by a running version.
                foreach (string old in Directory.GetFiles(packages, "managed-settings.json", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc))
                {
                    try
                    {
                        string? model = JsonDocument.Parse(File.ReadAllText(old)).RootElement.GetProperty("model").GetString();
                        if (File.Exists(model))
                        {
                            File.Copy(old, settings, false);
                            break;
                        }
                    }
                    catch (IOException) { }
                    catch (JsonException) { }
                    catch (KeyNotFoundException) { }
                }
            }
            return folder;
        }
    }
    public static string Root
    {
        get
        {
            if (StandaloneRuntime.Root is string packaged)
                return packaged;
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                if (File.Exists(Path.Combine(d.FullName, "build.ps1")))
                    return d.FullName;
                d = d.Parent;
            }
            throw new Exception("Place the GUI inside the project build directory.");
        }
    }
    public static async Task Run(string exe, string cwd, Action<string> log, params string[] args)
    {
        var s = new ProcessStartInfo(exe) { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            s.ArgumentList.Add(a);
        using var p = Process.Start(s) ?? throw new Exception("Could not launch " + exe);
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        log(await stdout);
        var error = await stderr;
        if (error.Length > 0)
            log(error);
        if (p.ExitCode != 0)
            throw new Exception(Path.GetFileName(exe) + " failed: " + error);
    }
    public static void WriteModel(string json, string binary)
    {
        _ = CalibrationModel.Load(json);
        using var document = JsonDocument.Parse(File.ReadAllText(json));
        var model = document.RootElement;
        if (model.GetProperty("version").GetInt32() is not (1 or 2 or 3) || model.GetProperty("features").GetInt32() != 14)
            throw new Exception("Unsupported model version or features");
        var centers = model.GetProperty("centers").EnumerateArray().ToArray();
        var coefficients = model.GetProperty("coefficients").EnumerateArray().ToArray();
        if (centers.Length != coefficients.Length || centers.Length > 2048 || centers.Length == 0)
            throw new Exception("Invalid model sample count");
        using var f = new BinaryWriter(File.Create(binary));
        bool histogram = model.TryGetProperty("histogram_pca", out var encoder) && encoder.ValueKind != JsonValueKind.Null;
        if (!histogram)
            throw new InvalidDataException("Create a new PCA calibration. This model has no histogram encoder.");
        if (model.GetProperty("version").GetInt32() == 2 && !histogram)
            throw new Exception("PCA model is missing its encoder");
        f.Write(model.GetProperty("version").GetInt32() == 3 ? 0x334c5041u : 0x324c5041u);
        f.Write((uint)centers.Length);
        foreach (var k in new[] { "peak", "baseline", "epsilon" })
            f.Write(model.GetProperty(k).GetSingle());
        foreach (var v in model.GetProperty("scale").EnumerateArray())
            f.Write(v.GetSingle());
        for (int i = 0; i < centers.Length; i++)
        {
            var values = centers[i].EnumerateArray().ToArray();
            if (values.Length != 14)
                throw new Exception("Invalid feature row");
            foreach (var v in values)
                f.Write(v.GetSingle());
            f.Write(coefficients[i].GetSingle());
            f.Write(0f);
        }
        if (histogram)
        {
            var basis = encoder.GetProperty("Basis").EnumerateArray().Select(r => r.EnumerateArray().Select(v => v.GetSingle()).ToArray()).ToArray();
            if (basis.Length != 14 || basis.Any(r => r.Length != 512))
                throw new Exception("Invalid histogram PCA basis");
            for (int bin = 0; bin < 512; bin++)
                for (int component = 0; component < 16; component++)
                    f.Write(component < 14 ? basis[component][bin] : 0f);
        }
    }
    public static async Task Apply(string root, string model, bool hdr, Screen screen, Action<string> log, int samplingCells = 25000)
    {
        if (!File.Exists(model))
            throw new Exception("Load or generate a runtime calibration model first.");
        var saved = JsonDocument.Parse(File.ReadAllText(model)).RootElement;
        if (saved.TryGetProperty("monitor_device", out var device) && !string.IsNullOrWhiteSpace(device.GetString()) && device.GetString() != screen.DeviceName)
            throw new Exception("This calibration belongs to " + device.GetString() + ". Select that display or run a new calibration for this one.");
        var stage = Path.Combine(root, "build", "filter-stage");
        Directory.CreateDirectory(stage);
        if (samplingCells < 256 || samplingCells > 1000000)
            throw new ArgumentOutOfRangeException(nameof(samplingCells));
        File.WriteAllBytes(Path.Combine(stage, "sampling.bin"), BitConverter.GetBytes((uint)samplingCells));
        var vpath = Path.Combine(stage, "vcgt.bin");
        if (File.Exists(vpath))
            File.Delete(vpath);
        WriteModel(model, Path.Combine(stage, "runtime.bin"));
        File.WriteAllBytes(Path.Combine(stage, "profile-enabled.bin"), BitConverter.GetBytes(0u));
        await Run(Path.Combine(root, "build", "hook-probe.exe"), root, log, stage);
        // The hook uses a cube to identify its target display. Color transforms
        // remain disabled; Windows owns ICC and gamma calibration.
        File.WriteAllText(Path.Combine(stage, "profile.cube"), "LUT_3D_SIZE 2\n0 0 0\n1 0 0\n0 1 0\n1 1 0\n0 0 1\n1 0 1\n0 1 1\n1 1 1\n");
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new Exception("Filter files are prepared. Restart this GUI as administrator to apply to DWM; camera calibration can run without elevation.");
        Disable();
        bool resident = Injector.IsLoaded();
        var runtime = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp", "oled-apl");
        Directory.CreateDirectory(runtime);
        var luts = Path.Combine(runtime, "luts");
        Directory.CreateDirectory(luts);
        AllowDwmRead(runtime, true);
        AllowDwmRead(luts, true);
        foreach (var old in Directory.GetFiles(luts, "*.cube"))
            File.Delete(old);
        foreach (var file in new[] { "runtime.bin", "vcgt.bin", "hook-addresses.bin", "profile-enabled.bin", "sampling.bin" })
        {
            var src = Path.Combine(stage, file);
            var dst = Path.Combine(runtime, file);
            if (File.Exists(src))
                File.Copy(src, dst, true);
            else if (File.Exists(dst))
                File.Delete(dst);
        }
        File.Copy(Path.Combine(stage, "profile.cube"), Path.Combine(luts, $"{screen.Bounds.Left}_{screen.Bounds.Top}{(hdr ? "_hdr" : "")}.cube"), true);
        var bridge = Path.Combine(runtime, "oled-hook-bridge.dll");
        var bridgeSource = Path.Combine(root, "build", "oled-hook-bridge.dll");
        if (!File.Exists(bridge) || !System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(bridgeSource)).SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(bridge))))
        {
            try
            {
                File.Copy(bridgeSource, bridge, true);
            }
            catch (IOException) { throw new Exception("The resident unload bridge has changed. Sign out once before applying this build."); }
        }
        var dll = Path.Combine(runtime, "oled-apl-hook.dll");
        string hookSource = Path.Combine(root, "build", "oled-apl-hook.dll");
        string binding = screen.DeviceName + "|" + hdr + "|" + screen.Bounds.ToString();
        string bindingPath = Path.Combine(runtime, "resident-display.txt");
        if (resident)
        {
            if (!File.Exists(bindingPath) || File.ReadAllText(bindingPath) != binding)
                throw new Exception("Changing the resident filter's display requires signing out and back in.");
            if (!System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(hookSource)).SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(dll))))
                throw new Exception("A previous hook version is resident. Sign out and back in before applying this build.");
        }
        else
        {
            File.Copy(hookSource, dll, true);
            File.WriteAllText(bindingPath, binding);
        }
        foreach (var file in Directory.GetFiles(runtime))
            AllowDwmRead(file, false);
        foreach (var file in Directory.GetFiles(luts))
            AllowDwmRead(file, false);
        var diagnostic = Path.Combine(runtime, "hook-diagnostic.log");
        File.WriteAllText(diagnostic, "");
        var access = new FileInfo(diagnostic).GetAccessControl();
        access.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-90-0"), FileSystemRights.Write | FileSystemRights.Read, AccessControlType.Allow));
        new FileInfo(diagnostic).SetAccessControl(access);
        RefreshDesktop(); // Establish uncorrected pixels before the new pristine cache is seeded.
        if (resident)
            Injector.Control(true);
        else
        {
            Injector.Load(bridge);
            Injector.Load(dll);
        }
        RefreshDesktop();
        log("DWM hook loaded for " + screen.DeviceName + "; full desktop redraw requested");
    }
    static void AllowDwmRead(string path, bool directory)
    {
        var sid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        if (directory)
        {
            var info = new DirectoryInfo(path);
            var acl = info.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(acl);
        }
        else
        {
            var info = new FileInfo(path);
            var acl = info.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            info.SetAccessControl(acl);
        }
    }
    public static void Disable()
    {
        if (Injector.IsLoaded())
        {
            Injector.Unload();
            RefreshDesktop();
        }
    }
    [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr window, IntPtr rect, IntPtr region, uint flags);
    delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("dwmapi.dll")] static extern int DwmFlush();
    sealed class RefreshOverlay : Form
    {
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get
            {
                var p = base.CreateParams;
                p.ExStyle |= 0x08000000 | 0x00000020 | 0x00000080;
                return p;
            }
        }
    }
    static void RefreshDesktop()
    {
        const uint flags = 0x0001 | 0x0004 | 0x0080 | 0x0400; // INVALIDATE | ERASE | ALLCHILDREN | FRAME
        RedrawWindow(GetDesktopWindow(), IntPtr.Zero, IntPtr.Zero, flags);
        EnumWindows((window, _) => { RedrawWindow(window, IntPtr.Zero, IntPtr.Zero, flags); return true; }, IntPtr.Zero);
        // Match dwm_lut's desktop overlay redraw technique. A nonzero layered alpha
        // keeps WinForms from suppressing the window; no input or focus is taken.
        using var overlay = new RefreshOverlay { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = SystemInformation.VirtualScreen, ShowInTaskbar = false, TopMost = true, BackColor = Color.Black, Opacity = 1.0 / 255 };
        overlay.Show();
        overlay.Update();
        DwmFlush();
        Thread.Sleep(50);
        overlay.Close();
        DwmFlush();
    }


}

// dwm_lut's LoadLibrary/FreeLibrary mechanism, with session-scoped SYSTEM impersonation.
// No LSASS access, null DACL, indefinite wait, or automatic persistence.
static class Injector
{
    const string Name = "oled-apl-hook.dll";
    static string Marker => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp", "oled-apl", $"active-{Process.GetCurrentProcess().SessionId}.txt");
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool ImpersonateLoggedOnUser(IntPtr token);
    [DllImport("advapi32.dll")] static extern bool RevertToSelf();
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, nuint size, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, nuint size, out nuint written);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, nuint stack, IntPtr entry, IntPtr argument, uint flags, out uint id);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint timeout);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeThread(IntPtr thread, out uint result);
    [DllImport("kernel32.dll")] static extern bool VirtualFreeEx(IntPtr process, IntPtr address, nuint size, uint type);
    static Exception Error(string operation) => new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), operation);
    static Process[] Targets() => Process.GetProcessesByName("dwm").Where(p => p.SessionId == Process.GetCurrentProcess().SessionId).ToArray();
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, nuint size, out nuint read);
    public static FilterState GetStatus()
    {
        var state = FilterState.NotLoaded;
        AsSystem(() =>
        {
            foreach (var process in Targets())
                using (process)
                    foreach (ProcessModule module in process.Modules)
                    {
                        if (!module.ModuleName.Equals(Name, StringComparison.OrdinalIgnoreCase))
                            continue;
                        var local = LoadLibraryEx(module.FileName, IntPtr.Zero, 1);
                        if (local == IntPtr.Zero)
                            throw Error("Read filter state export");
                        try
                        {
                            var export = GetProcAddress(local, "OledFilterState");
                            if (export == IntPtr.Zero)
                            {
                                state = FilterState.Unknown;
                                return;
                            }
                            var handle = OpenProcess(0x0010 | 0x1000, false, (uint)process.Id);
                            if (handle == IntPtr.Zero)
                                throw Error("Read DWM filter status");
                            try
                            {
                                var bytes = new byte[4];
                                var address = new IntPtr(module.BaseAddress.ToInt64() + export.ToInt64() - local.ToInt64());
                                if (!ReadProcessMemory(handle, address, bytes, 4, out var read) || read != 4)
                                    throw Error("Read DWM filter state");
                                state = BitConverter.ToInt32(bytes) switch
                                {
                                    0 => FilterState.Disabled,
                                    1 => FilterState.Enabled,
                                    2 => FilterState.Faulted,
                                    _ => FilterState.Unknown
                                };
                            }
                            finally { CloseHandle(handle); }
                        }
                        finally { FreeLibrary(local); }
                        return;
                    }
        });
        return state;
    }
    public static bool IsLoaded()
    {
        try
        {
            foreach (var p in Targets())
                using (p)
                    foreach (ProcessModule m in p.Modules)
                        if (m.ModuleName.Equals(Name, StringComparison.OrdinalIgnoreCase))
                            return true;
        }
        catch (System.ComponentModel.Win32Exception) { return File.Exists(Marker); }
        return false;
    }
    static void AsSystem(Action operation)
    {
        Process.EnterDebugMode();
        var login = Process.GetProcessesByName("winlogon").FirstOrDefault(p => p.SessionId == Process.GetCurrentProcess().SessionId) ?? throw new Exception("Session winlogon was not found");
        using (login)
        {
            var handle = OpenProcess(0x1000, false, (uint)login.Id);
            if (handle == IntPtr.Zero)
                throw Error("Query session token");
            IntPtr token = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(handle, 0x0002 | 0x0008, out token))
                    throw Error("Open session token");
                if (!ImpersonateLoggedOnUser(token))
                    throw Error("DWM access requires SYSTEM impersonation");
                try
                {
                    operation();
                }
                finally { RevertToSelf(); }
            }
            finally { if (token != IntPtr.Zero) CloseHandle(token); CloseHandle(handle); }
        }
    }
    sealed class RemoteTimeout : Exception
    {
        public RemoteTimeout() : base("DWM hook operation timed out; check its state before retrying.") { }
    }
    static void Remote(IntPtr process, string entry, IntPtr argument) => RemoteAt(process, GetProcAddress(GetModuleHandle("kernel32.dll"), entry), argument, entry);
    static void RemoteAt(IntPtr process, IntPtr address, IntPtr argument, string entry)
    {
        var thread = CreateRemoteThread(process, IntPtr.Zero, 0, address, argument, 0, out _);
        if (thread == IntPtr.Zero)
            throw Error(entry);
        try
        {
            if (WaitForSingleObject(thread, 10000) != 0)
                throw new RemoteTimeout();
            if (!GetExitCodeThread(thread, out var code) || code == 0)
            {
                var diagnostic = Path.Combine(Path.GetDirectoryName(Marker)!, "hook-diagnostic.log");
                var detail = File.Exists(diagnostic) ? string.Join(Environment.NewLine, File.ReadAllLines(diagnostic).TakeLast(12)) : "DLL entry was not reached: Windows loader or process policy blocked loading.";
                throw new Exception("DWM rejected the hook." + Environment.NewLine + detail);
            }
        }
        finally { CloseHandle(thread); }
    }
    public static void Load(string dll) => AsSystem(() =>
    {
        var targets = Targets();
        if (targets.Length == 0)
            throw new Exception("No DWM process in this session");
        foreach (var p in targets)
            using (p)
            {
                var process = OpenProcess(0x1f0fff, false, (uint)p.Id);
                if (process == IntPtr.Zero)
                    throw Error("Open DWM");
                var bytes = Encoding.Unicode.GetBytes(dll + '\0');
                var address = VirtualAllocEx(process, IntPtr.Zero, (nuint)bytes.Length, 0x3000, 0x04);
                bool release = true;
                if (address == IntPtr.Zero)
                {
                    CloseHandle(process);
                    throw Error("Allocate DLL path");
                }
                try
                {
                    if (!WriteProcessMemory(process, address, bytes, (nuint)bytes.Length, out var written) || written != (nuint)bytes.Length)
                        throw Error("Write DLL path");
                    Remote(process, "LoadLibraryW", address);
                }
                catch (RemoteTimeout) { release = false; File.WriteAllText(Marker, "Operation uncertain"); throw; }
                finally { if (release) VirtualFreeEx(process, address, 0, 0x8000); CloseHandle(process); }
            }
        File.WriteAllText(Marker, "Loaded");
    });
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
    public static void Control(bool enable) => InvokeControl(enable, false);
    public static void Unload() => InvokeControl(false, true);
    static void InvokeControl(bool enable, bool unload) => AsSystem(() =>
    {
        bool found = false;
        foreach (var p in Targets())
            using (p)
                foreach (ProcessModule module in p.Modules)
                {
                    if (!module.ModuleName.Equals(Name, StringComparison.OrdinalIgnoreCase))
                        continue;
                    found = true;
                    var local = LoadLibraryEx(module.FileName, IntPtr.Zero, 1); // Metadata mapping in GUI, no DllMain.
                    if (local == IntPtr.Zero)
                        throw Error("Read resident filter exports");
                    try
                    {
                        var export = unload ? GetProcAddress(local, "OledPrepareUnload") : IntPtr.Zero;
                        bool canUnload = export != IntPtr.Zero;
                        if (!canUnload)
                            export = GetProcAddress(local, "OledFilterControl");
                        if (export == IntPtr.Zero)
                            throw new Exception("The loaded hook uses unsafe unloading. Sign out and back in before using this corrected build.");
                        var handle = OpenProcess(0x1f0fff, false, (uint)p.Id);
                        if (handle == IntPtr.Zero)
                            throw Error("Open DWM for filter control");
                        try
                        {
                            RemoteAt(handle, new IntPtr(module.BaseAddress.ToInt64() + export.ToInt64() - local.ToInt64()), enable ? new IntPtr(1) : IntPtr.Zero, canUnload ? "OledPrepareUnload" : "OledFilterControl");
                            if (canUnload)
                            {
                                Remote(handle, "FreeLibrary", module.BaseAddress);
                                using var verify = Process.GetProcessById(p.Id);
                                if (verify.Modules.Cast<ProcessModule>().Any(m => m.ModuleName.Equals(Name, StringComparison.OrdinalIgnoreCase)))
                                    throw new Exception("The filter DLL is still resident after teardown. Sign out before replacing it.");
                            }
                        }
                        finally { CloseHandle(handle); }
                    }
                    finally { FreeLibrary(local); } // Frees the GUI metadata mapping only.
                    break;
                }
        if (enable && !found)
            throw new Exception("No resident filter in this session");
        if (File.Exists(Marker))
            File.WriteAllText(Marker, enable ? "Enabled" : "Disabled");
    });

}
