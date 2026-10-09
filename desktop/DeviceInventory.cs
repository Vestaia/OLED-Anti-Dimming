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
    public static async Task RequireHdr(string root, string device)
    {
        var inventory = await Read(root);
        ValidateHdr(inventory.displays.FirstOrDefault(d => d.device == device));
    }
    internal static void ValidateHdr(DisplayDefaults? display)
    {
        if (display == null || !display.detected)
            throw new InvalidOperationException("Could not verify HDR on the selected display. Enable HDR in Windows display settings and try again.");
        if (!display.hdr)
            throw new InvalidOperationException("HDR is not enabled on the selected display. Enable HDR in Windows display settings before calibrating or applying the filter.");
    }
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
