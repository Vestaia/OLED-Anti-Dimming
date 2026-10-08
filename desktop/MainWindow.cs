using System.Diagnostics;
using System.Text.Json;

namespace OledCalibration;

sealed class MainWindow : Form
{
    readonly string root = Backend.Root;
    readonly string dataRoot = Backend.DataRoot;
    readonly ComboBox monitors = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 340 };
    readonly TextBox model = new() { Width = 480 };
    readonly ComboBox camera = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    readonly CheckBox hdr = new() { Text = "HDR (BT.2020 / PQ)", Checked = false, AutoSize = true };
    readonly NumericUpDown rounds = new() { Minimum = 1, Maximum = 5, Value = 3, Width = 60 };
    readonly NumericUpDown peakBrightness = new() { Minimum = 250, Maximum = 10000, Value = 1000, Increment = 50, Width = 100 };
    readonly CheckBox express = new() { Text = "Express calibration", AutoSize = true };
    readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.FromArgb(22, 25, 31), ForeColor = Color.Gainsboro };
    readonly Label status = new() { Text = "Filter disabled", AutoSize = true };
    readonly ProgressBar progress = new() { Width = 500, Height = 18, Minimum = 0, Maximum = 100 };
    readonly Label progressStep = new() { Text = "Ready", AutoSize = true };
    readonly PictureBox corrections = new() { SizeMode = PictureBoxSizeMode.StretchImage };
    readonly DataGridView metrics = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White };
    readonly PictureBox chart = new() { SizeMode = PictureBoxSizeMode.StretchImage };
    readonly Button calibrate = new() { Text = "New calibration", AutoSize = true }, refine = new() { Text = "Refine high-error cases", AutoSize = true }, refineWhite = new() { Text = "Refine white / gray", AutoSize = true }, apply = new() { Text = "Apply system-wide", AutoSize = true }, disable = new() { Text = "Disable filter", AutoSize = true }, cancel = new() { Text = "Stop measurement", AutoSize = true };
    Process? job; CancellationTokenSource? calibrationCancellation; bool busy, toggling;
    public MainWindow()
    {
        Text = "OLED brightness calibration - Beta";
        Width = 1020;
        Height = 800;
        MinimumSize = new Size(800, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);
        Shown += (_, _) => BeginInvoke(() => { Show(); Activate(); });
        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(16) };
        outer.RowStyles.Add(new(SizeType.AutoSize));
        outer.RowStyles.Add(new(SizeType.Percent, 65));
        outer.RowStyles.Add(new(SizeType.Percent, 35));
        Controls.Add(outer);
        var top = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        outer.Controls.Add(top, 0, 0);
        top.Controls.Add(new Label { Text = "Place the camera as close as possible to the center of the screen. The reference square should fill the camera view.", AutoSize = true, MaximumSize = new Size(940, 0) });
        top.Controls.Add(new Label { Text = "Enable Advanced Color management in Windows display settings.", AutoSize = true, MaximumSize = new Size(940, 0) });
        top.Controls.Add(new Label { Text = "To begin, click New calibration. When it finishes, click Apply system-wide.", AutoSize = true, MaximumSize = new Size(940, 0), Margin = new Padding(3, 3, 3, 12) });
        foreach (var screen in Screen.AllScreens)
            monitors.Items.Add(screen.DeviceName + "  " + screen.Bounds.Width + " x " + screen.Bounds.Height);
        monitors.SelectedIndex = Math.Min(1, monitors.Items.Count - 1);
        model.Text = Path.Combine(dataRoot, "reports", "adaptive", "runtime-model.json");
        var managedSettings = Path.Combine(dataRoot, "build", "managed-settings.json");
        if (File.Exists(managedSettings))
        {
            try
            {
                var path = JsonDocument.Parse(File.ReadAllText(managedSettings)).RootElement.GetProperty("model").GetString();
                if (File.Exists(path))
                    model.Text = path!;
            }
            catch { }
        }

        if (File.Exists(model.Text))
        {
            try
            {
                var saved = JsonDocument.Parse(File.ReadAllText(model.Text));
                if (saved.RootElement.TryGetProperty("monitor_device", out var selected))
                {
                    var i = Array.FindIndex(Screen.AllScreens, s => s.DeviceName == selected.GetString());
                    if (i >= 0)
                        monitors.SelectedIndex = i;
                }
            }
            catch { }
        }
        top.Controls.Add(Row(new Label { Text = "Display", Width = 100 }, monitors, hdr));
        top.Controls.Add(Row(new Label { Text = "Calibration", Width = 100 }, model, Browse(model, "Calibration model|runtime-model.json|JSON|*.json")));
        top.Controls.Add(Row(new Label { Text = "Refinement rounds", Width = 160 }, rounds));
        top.Controls.Add(Row(new Label { Text = "Camera", Width = 100 }, camera));
        top.Controls.Add(Row(new Label { Text = "Panel peak (nits)", Width = 160 }, peakBrightness, express));
        top.Controls.Add(Row(calibrate, refine, refineWhite, cancel, apply, disable, status));
        top.Controls.Add(progress);
        top.Controls.Add(progressStep);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var summary = new TabPage("Corrections summary");
        summary.Controls.Add(ScrollablePlot(corrections));
        tabs.TabPages.Add(summary);
        var results = new TabPage("Validation heat map");
        results.Controls.Add(ScrollablePlot(chart));
        tabs.TabPages.Add(results);
        var measured = new TabPage("Error summary");
        measured.Controls.Add(metrics);
        tabs.TabPages.Add(measured);
        foreach (string column in new[] { "Mode", "Samples", "RMS", "Maximum" })
            metrics.Columns.Add(column, column);
        outer.Controls.Add(tabs, 0, 1);
        outer.Controls.Add(log, 0, 2);
        calibrate.Click += async (_, _) => await Calibration(true);
        refine.Click += async (_, _) => await Calibration(false);
        refineWhite.Click += async (_, _) => await Calibration(false, true);
        cancel.Click += (_, _) => Stop();
        apply.Click += async (_, _) => await HookAction(async () => { if (busy) throw new InvalidOperationException("Finish calibration first"); var s = Screen.AllScreens[monitors.SelectedIndex]; await Backend.Apply(root, model.Text, hdr.Checked, s, Write); status.Text = "DWM hook loaded"; });
        disable.Click += async (_, _) => await HookAction(() => { Backend.Disable(); status.Text = "Filter disabled"; Write("Filter bypassed; hook remains resident"); return Task.CompletedTask; });
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; Stop(); Write("Stopping measurement; close again after the camera exits."); } };

        Write("Ready. Windows manages display color profiles and gamma calibration. The filter applies scene-dependent brightness correction.");
        camera.Items.Add("Automatic");
        camera.SelectedIndex = 0;
        Shown += async (_, _) => await RefreshDevices(true);
        monitors.SelectedIndexChanged += async (_, _) => await RefreshDevices(false);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        FormClosed += (_, _) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        LoadReports();
        model.TextChanged += (_, _) => { if (File.Exists(model.Text)) LoadReports(); };
    }
    static void FitPlotWidth(PictureBox box)
    {
        if (box.Parent == null || box.Image == null)
            return;
        int width = Math.Max(1, box.Parent.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2);
        box.Size = new Size(width, Math.Max(1, (int)Math.Round(width * (double)box.Image.Height / box.Image.Width)));
    }
    static Panel ScrollablePlot(PictureBox box)
    {
        var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        panel.Controls.Add(box);
        panel.ClientSizeChanged += (_, _) => FitPlotWidth(box);
        return panel;
    }
    void DisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (!IsDisposed && IsHandleCreated)
            BeginInvoke(async () => await RefreshDevices(false));
    }
    async Task RefreshDevices(bool listCameras)
    {
        try
        {
            var inventory = await DeviceInventory.Read(root);
            if (IsDisposed)
                return;
            if (listCameras)
            {
                camera.Items.Clear();
                camera.Items.Add("Automatic");
                foreach (var name in inventory.cameras)
                    camera.Items.Add(name);
                var preferred = Array.FindIndex(inventory.cameras, n => n.Contains("Brio", StringComparison.OrdinalIgnoreCase));
                camera.SelectedIndex = preferred >= 0 ? preferred + 1 : inventory.cameras.Length > 0 ? 1 : 0;
            }
            if (monitors.SelectedIndex < 0 || monitors.SelectedIndex >= Screen.AllScreens.Length)
                return;
            var device = Screen.AllScreens[monitors.SelectedIndex].DeviceName;
            var display = inventory.displays.FirstOrDefault(d => d.device == device);
            if (display == null || !display.detected)
            {
                Write("HDR state could not be detected for " + device);
                return;
            }
            hdr.Checked = display.hdr;
        }
        catch (Exception e) { Write("Display/camera detection: " + e.Message); }
    }
    void LoadReports()
    {
        try
        {
            if (!File.Exists(model.Text))
                return;
            var folder = Path.GetDirectoryName(model.Text)!;
            ManagedCalibration.CorrectionHeatmap(folder, CalibrationModel.Load(model.Text));
            var metadata = Path.Combine(folder, "metadata.json");
            if (File.Exists(metadata))
            {
                var meta = JsonSerializer.Deserialize<CalibrationMetadata>(File.ReadAllText(metadata))!;
                ManagedCalibration.Heatmap(folder, meta, ManagedCalibration.ReadCsv(ManagedCalibration.LatestValidation(folder)));
            }
            void Load(PictureBox box, string name)
            {
                var path = Path.Combine(folder, name);
                if (!File.Exists(path))
                    return;
                using var image = Image.FromFile(path);
                box.Image?.Dispose();
                box.Image = new Bitmap(image);
                FitPlotWidth(box);
            }
            Load(corrections, "corrections-heatmap.png");
            Load(chart, "validation-heatmap.png");
            metrics.Rows.Clear();
            var stats = Path.Combine(folder, "validation-summary.json");
            if (File.Exists(stats))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(stats));
                foreach (var item in new[] { ("Raw", "raw"), ("Model", "predicted_adaptive"), ("Feedback", "calibrated") })
                    if (json.RootElement.TryGetProperty(item.Item2, out var r))
                        metrics.Rows.Add(item.Item1, r.GetProperty("samples").GetInt32(), r.GetProperty("rms").GetDouble().ToString("0.00"), r.GetProperty("max_abs").GetDouble().ToString("0.00"));
            }
        }
        catch (Exception e) { Write("Report preview: " + e.Message); }
    }
    static FlowLayoutPanel Row(params Control[] controls)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(950, 0) };
        p.Controls.AddRange(controls);
        return p;
    }
    Button Browse(TextBox text, string filter)
    {
        var b = new Button { Text = "Browse…", AutoSize = true };
        b.Click += (_, _) => { using var d = new OpenFileDialog { Filter = filter }; if (d.ShowDialog() == DialogResult.OK) text.Text = d.FileName; };
        return b;
    }
    void Write(string s)
    {
        if (IsDisposed)
            return;
        if (InvokeRequired)
        {
            BeginInvoke(() => Write(s));
            return;
        }
        log.AppendText(s + Environment.NewLine);
    }
    async Task HookAction(Func<Task> action) => await Guard(async () => {
        if(busy||toggling)throw new InvalidOperationException("Wait for the current operation to finish.");
        toggling=true;apply.Enabled=disable.Enabled=calibrate.Enabled=refine.Enabled=refineWhite.Enabled=false;
        try{await action();}
        finally{toggling=false;apply.Enabled=disable.Enabled=calibrate.Enabled=refine.Enabled=refineWhite.Enabled=true;}
    });
    async Task Guard(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception e) { Write(e.Message); MessageBox.Show(this, e.Message, "OLED calibration", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
    async Task Calibration(bool fresh, bool whitesOnly = false) => await Guard(async () =>
    {
        if(!fresh&&(!File.Exists(model.Text)||!File.Exists(Path.Combine(Path.GetDirectoryName(model.Text)!,"metadata.json"))))
            throw new InvalidOperationException("No complete calibration is selected. Choose New calibration, or select a saved model with its calibration reports.");
        if (!fresh && File.Exists(model.Text))
        {
            var saved = JsonDocument.Parse(File.ReadAllText(model.Text)).RootElement;
            if (saved.TryGetProperty("monitor_device", out var device) && !string.IsNullOrWhiteSpace(device.GetString()) && device.GetString() != Screen.AllScreens[monitors.SelectedIndex].DeviceName)
                throw new Exception("Refinement must use the display that produced this model. Use New calibration for a different display.");
        }
        if (busy || toggling)
            return;
        Backend.Disable();
        status.Text = "Measuring; filter disabled";
        busy = true;
        calibrate.Enabled = refine.Enabled = refineWhite.Enabled = apply.Enabled = false;
        progress.Value = 0;
        progressStep.Text = "Preparing";
        ManagedCalibration.Progress = (value, phase) => { progress.Value = Math.Max(progress.Value, Math.Clamp(value, 0, 100)); progressStep.Text = phase; };
        var output = fresh ? Path.Combine(dataRoot, "reports", "adaptive", "managed-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")) : Path.GetDirectoryName(model.Text)!;
        calibrationCancellation = new CancellationTokenSource();
        try
        {
            await ManagedCalibration.Run(root, output, fresh, Screen.AllScreens[monitors.SelectedIndex].DeviceName, (camera.SelectedIndex > 0 ? camera.Text : ""), (int)rounds.Value, Write, p => job = p, calibrationCancellation.Token, whitesOnly, (double)peakBrightness.Value, express.Checked);
            model.Text = Path.Combine(output, "runtime-model.json");
            File.WriteAllText(Path.Combine(dataRoot, "build", "managed-settings.json"), JsonSerializer.Serialize(new
            {
                model = model.Text
            }));
            Write("Calibration complete. Model ready to apply.");
            LoadReports();
        }
        catch (OperationCanceledException) { Write("Calibration stopped; previous filter model retained."); }
        finally { job = null; calibrationCancellation.Dispose(); calibrationCancellation = null; busy = false; calibrate.Enabled = refine.Enabled = refineWhite.Enabled = apply.Enabled = true; ManagedCalibration.Progress = null; if (progress.Value < 100) progressStep.Text = "Stopped"; status.Text = "Filter disabled"; }

    });
    void Stop()
    {
        calibrationCancellation?.Cancel();
        try
        {
            job?.CloseMainWindow();
        }
        catch { }
        Write("Requested graceful camera shutdown.");
    }
}
