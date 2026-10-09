// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OledCalibration;

static class ApplicationDataCleanup
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool MoveFileEx(string existing, string? replacement, uint flags);

    public static int DeleteAll(string sourceRoot)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var targets = new List<string>
        {
            Path.Combine(local, "OledCalibration"),
            Path.Combine(windows, "Temp", "oled-apl"),
            Path.Combine(Path.GetTempPath(), ".net", "OledCalibration")
        };
        if (StandaloneRuntime.Root == null)
        {
            // Source builds share their root with the repository; delete only
            // application-created measurement/staging paths, never source files.
            targets.Add(Path.Combine(sourceRoot, "reports", "adaptive"));
            targets.Add(Path.Combine(sourceRoot, "build", "filter-stage"));
            targets.Add(Path.Combine(sourceRoot, "build", "managed-settings.json"));
        }
        int pending = 0;
        foreach (string target in targets)
            pending += DeleteTree(Path.GetFullPath(target));
        return pending;
    }

    internal static int DeleteTree(string target)
    {
        // Do not follow directory junctions or symbolic links outside our roots.
        if (!Path.IsPathFullyQualified(target)) throw new ArgumentException("Expected an absolute cleanup path.");
        if (!File.Exists(target) && !Directory.Exists(target)) return 0;
        var attributes = File.GetAttributes(target);
        bool directory = (attributes & FileAttributes.Directory) != 0;
        int pending = 0;
        if (directory && (attributes & FileAttributes.ReparsePoint) == 0)
            foreach (string child in Directory.GetFileSystemEntries(target)) pending += DeleteTree(child);
        try
        {
            if (directory) Directory.Delete(target);
            else File.Delete(target);
        }
        catch (IOException)
        {
            // The resident DWM bridge and extracted runtime can remain mapped.
            // Queue children first, then their parents, for removal at restart.
            if (!MoveFileEx(target, null, 4)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not remove or schedule cleanup of " + target);
            pending++;
        }
        return pending;
    }
}
