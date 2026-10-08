// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Text.Json;

namespace OledCalibration;

sealed class DisplayDefaults
{
    public string device { get; set; } = "";
    public bool hdr
    {
        get; set;
    }
    public bool detected
    {
        get; set;
    }
    public bool advancedColor
    {
        get; set;
    }
}
sealed class DeviceInventory
{
    public string[] cameras { get; set; } = [];
    public DisplayDefaults[] displays { get; set; } = [];
    public static async Task<DeviceInventory> Read(string root)
    {
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(root, "build", "device-info.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }) ?? throw new Exception("Device discovery could not start");
        string json = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new Exception("Device discovery failed");
        return JsonSerializer.Deserialize<DeviceInventory>(json) ?? throw new Exception("Invalid device inventory");
    }
}
