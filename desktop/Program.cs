// SPDX-License-Identifier: GPL-3.0-only
namespace OledCalibration;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        bool startup = args.Contains("--startup");
        string user = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        using var mutex = new Mutex(true, "Local\\OLED-Anti-Dimming-" + user, out bool firstInstance);
        using var showRequested = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\OLED-Anti-Dimming-Show-" + user);
        if (!firstInstance)
        {
            if (!startup)
                showRequested.Set();
            return;
        }
        ApplicationConfiguration.Initialize();
        using var context = new TrayApplicationContext(startup, showRequested);
        Application.Run(context);
    }
}
