// SPDX-License-Identifier: GPL-3.0-only
namespace OledCalibration;

sealed class TrayApplicationContext : ApplicationContext
{
    readonly MainWindow window = new();
    readonly NotifyIcon tray;
    readonly ContextMenuStrip menu = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    readonly EventWaitHandle showRequested;
    bool exiting;
    int statusTicks;

    public TrayApplicationContext(bool startup, EventWaitHandle showRequested)
    {
        this.showRequested = showRequested;
        tray = new NotifyIcon { Icon = ApplicationIcon.Monitor, Text = "OLED Anti-Dimming", ContextMenuStrip = menu, Visible = true };
        menu.Items.Add("Open", null, (_, _) => ShowWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, async (_, _) => await ExitAsync(false));
        menu.Items.Add("Disable filter and exit", null, async (_, _) => await ExitAsync(true));
        tray.DoubleClick += (_, _) => ShowWindow();
        window.FilterStatusChanged += text => tray.Text = "OLED Anti-Dimming: " + text;
        window.FormClosed += (_, _) => ExitThread();
        _ = window.Handle; // Receive queued initialization without showing the form.
        timer.Tick += (_, _) =>
        {
            if (showRequested.WaitOne(0))
                ShowWindow();
            if (++statusTicks >= 10)
            {
                statusTicks = 0;
                window.RefreshFilterStatus();
            }
        };
        timer.Start();
        window.BeginInvoke(async () =>
        {
            try
            {
                await window.InitializeAsync(startup);
            }
            catch (Exception error)
            {
                tray.ShowBalloonTip(5000, "OLED Anti-Dimming", "Could not apply the filter: " + error.Message, ToolTipIcon.Warning);
                MessageBox.Show(error.Message, "OLED calibration", MessageBoxButtons.OK, MessageBoxIcon.Error);
                window.RefreshFilterStatus();
            }
        });
        if (!startup)
            ShowWindow();
    }
    void ShowWindow()
    {
        if (exiting)
            return;
        window.Show();
        window.WindowState = FormWindowState.Normal;
        window.Activate();
        window.RefreshFilterStatus();
    }
    async Task ExitAsync(bool disableFilter)
    {
        if (exiting)
            return;
        exiting = true;
        menu.Enabled = false;
        try
        {
            await window.PrepareExitAsync(disableFilter);
            tray.Visible = false;
            window.AllowExit = true;
            window.Close();
        }
        catch (Exception error)
        {
            exiting = false;
            menu.Enabled = true;
            tray.ShowBalloonTip(5000, "OLED Anti-Dimming", error.Message, ToolTipIcon.Error);
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            tray.Dispose();
            menu.Dispose();
            window.Dispose();
        }
        base.Dispose(disposing);
    }
}
