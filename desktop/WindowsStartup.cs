// SPDX-License-Identifier: GPL-3.0-only
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace OledCalibration;

static class WindowsStartup
{
    static string UserId => WindowsIdentity.GetCurrent().User!.Value;
    static string TaskName => "OLED Anti-Dimming - " + UserId;
    static dynamic Connect()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        service.Connect();
        return service;
    }
    public static bool IsEnabled()
    {
        dynamic service = Connect();
        try
        {
            dynamic folder = service.GetFolder("\\");
            try
            {
                try
                {
                    dynamic task = folder.GetTask(TaskName);
                    try
                    {
                        return task.Enabled;
                    }
                    finally { Marshal.FinalReleaseComObject(task); }
                }
                catch (COMException error) when (error.HResult == unchecked((int)0x80070002)) { return false; }
            }
            finally { Marshal.FinalReleaseComObject(folder); }
        }
        finally { Marshal.FinalReleaseComObject(service); }
    }
    public static void SetEnabled(bool enabled)
    {
        dynamic service = Connect();
        try
        {
            dynamic folder = service.GetFolder("\\");
            try
            {
                if (!enabled)
                {
                    try
                    {
                        folder.DeleteTask(TaskName, 0);
                    }
                    catch (COMException error) when (error.HResult == unchecked((int)0x80070002)) { }
                    return;
                }
                dynamic definition = service.NewTask(0);
                try
                {
                    definition.RegistrationInfo.Description = "Apply OLED Anti-Dimming at sign-in and stay in the system tray.";
                    definition.Principal.UserId = UserId;
                    definition.Principal.LogonType = 3; // Current user's interactive token; no password stored.
                    definition.Principal.RunLevel = 1; // Highest available: required to access DWM.
                    definition.Settings.ExecutionTimeLimit = "PT0S";
                    definition.Settings.DisallowStartIfOnBatteries = false;
                    definition.Settings.StopIfGoingOnBatteries = false;
                    definition.Settings.MultipleInstances = 2; // Ignore another instance.
                    definition.Settings.StartWhenAvailable = true;
                    dynamic trigger = definition.Triggers.Create(9); // User logon.
                    try
                    {
                        trigger.UserId = UserId;
                        trigger.Delay = "PT10S";
                    }
                    finally { Marshal.FinalReleaseComObject(trigger); }
                    dynamic action = definition.Actions.Create(0);
                    try
                    {
                        action.Path = Environment.ProcessPath ?? throw new IOException("Application path is unavailable.");
                        action.Arguments = "--startup";
                        action.WorkingDirectory = AppContext.BaseDirectory;
                    }
                    finally { Marshal.FinalReleaseComObject(action); }
                    dynamic registered = folder.RegisterTaskDefinition(TaskName, definition, 6, UserId, null, 3, null);
                    Marshal.FinalReleaseComObject(registered);
                }
                finally { Marshal.FinalReleaseComObject(definition); }
            }
            finally { Marshal.FinalReleaseComObject(folder); }
        }
        finally { Marshal.FinalReleaseComObject(service); }
    }
}
