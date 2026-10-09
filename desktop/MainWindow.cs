// SPDX-License-Identifier: GPL-3.0-only
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
    readonly NumericUpDown rounds = new() { Minimum = 1, Maximum = 5, Value = 3, Width = 60 };
    readonly NumericUpDown peakBrightness = new() { Minimum = 250, Maximum = 10000, Value = 1000, Increment = 50, Width = 100 };
    readonly CheckBox hasWhiteSubpixel = new() { Text = "Has white subpixel", AutoSize = true };
    readonly Button express = new() { Text = "Express calibration", AutoSize = true };
    readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.FromArgb(22, 25, 31), ForeColor = Color.Gainsboro };
    readonly Label status = new() { Text = "Checking filter status...", AutoSize = true };
    readonly ProgressBar progress = new() { Width = 500, Height = 18, Minimum = 0, Maximum = 100 };
    readonly Label progressStep = new() { Text = "Ready", AutoSize = true };
    readonly PictureBox corrections = new() { SizeMode = PictureBoxSizeMode.StretchImage };
    readonly DataGridView metrics = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White };
    readonly Button calibrate = new() { Text = "New calibration", AutoSize = true }, refine = new() { Text = "Refine", AutoSize = true }, deleteData = new() { Text = "Delete all data", AutoSize = true }, apply = new() { Text = "Apply", AutoSize = true }, disable = new() { Text = "Disable filter", AutoSize = true }, cancel = new() { Text = "Stop measurement", AutoSize = true };
    readonly CheckBox startWithWindows = new() { Text = "Start with Windows", AutoSize = true };
    readonly ComboBox samplingQuality = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    readonly NumericUpDown samplingCells = new() { Minimum = 256, Maximum = 1000000, Value = 25000, ThousandsSeparator = true, Width = 110 };
    readonly Label samplingGrid = new() { AutoSize = true };
    Process? job; CancellationTokenSource? calibrationCancellation; bool busy, toggling, updatingStartup;
    Task? calibrationTask;
    internal bool AllowExit;
    public event Action<string>? FilterStatusChanged;

    public MainWindow()
    {
        Icon = ApplicationIcon.Monitor;
        Text = "OLED brightness calibration - Beta";
        Width = 1020;
        Height = 800;
        MinimumSize = new Size(800, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);
        DoubleBuffered = true;
        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(16) };
        outer.RowStyles.Add(new(SizeType.AutoSize));
        outer.RowStyles.Add(new(SizeType.Percent, 65));
        outer.RowStyles.Add(new(SizeType.Percent, 35));
        Controls.Add(outer);
        var top = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        outer.Controls.Add(top, 0, 0);
        top.Controls.Add(new Label { Text = "Place the camera as close as possible to the center of the screen. The reference square should fill the camera view.", AutoSize = true, MaximumSize = new Size(940, 0) });
        top.Controls.Add(new Label { Text = "Enable HDR in Windows display settings.", AutoSize = true, MaximumSize = new Size(940, 0) });
        top.Controls.Add(new Label { Text = "To begin, click New calibration. When it finishes, click Apply.", AutoSize = true, MaximumSize = new Size(940, 0), Margin = new Padding(3, 3, 3, 12) });
        foreach (var screen in Screen.AllScreens)
            monitors.Items.Add(screen.DeviceName + "  " + screen.Bounds.Width + " x " + screen.Bounds.Height);
        monitors.SelectedIndex = Math.Min(1, monitors.Items.Count - 1);
        model.Text = Path.Combine(dataRoot, "reports", "adaptive", "runtime-model.json");
        samplingQuality.Items.AddRange(["Quality", "Balanced", "Performance", "Custom"]);
        samplingQuality.SelectedIndex = 1;
        var managedSettings = Path.Combine(dataRoot, "build", "managed-settings.json");
        if (File.Exists(managedSettings))
        {
            try
            {
                var path = JsonDocument.Parse(File.ReadAllText(managedSettings)).RootElement.GetProperty("model").GetString();
                var settings = JsonDocument.Parse(File.ReadAllText(managedSettings)).RootElement;
                if(settings.TryGetProperty("has_white_subpixel",out var white))hasWhiteSubpixel.Checked=white.GetBoolean();
                if (settings.TryGetProperty("sampling_mode", out var mode) && mode.GetInt32() is >= 0 and <= 3)
                    samplingQuality.SelectedIndex = mode.GetInt32();
                if (settings.TryGetProperty("sampling_cells", out var cells))
                    samplingCells.Value = Math.Clamp(cells.GetInt32(), 256, 1000000);
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
        top.Controls.Add(Row(new Label { Text = "Display", Width = 100 }, monitors));
        top.Controls.Add(Row(new Label { Text = "Calibration", Width = 100 }, model, Browse(model, "Calibration model|runtime-model.json|JSON|*.json")));
        top.Controls.Add(Row(new Label { Text = "Camera", Width = 100 }, camera));
        top.Controls.Add(Row(new Label { Text = "Refinement rounds", Width = 145 }, rounds,
            new Label { Text = "Panel peak (nits)", Width = 130 }, peakBrightness, hasWhiteSubpixel));
        top.Controls.Add(Row(new Label { Text = "Sampling", Width = 100 }, samplingQuality, samplingCells, samplingGrid));
        top.Controls.Add(Row(startWithWindows));
        void UpdateSampling()
        {
            samplingCells.Enabled = samplingQuality.SelectedIndex == 3;
            if (!samplingCells.Enabled)
                samplingCells.Value = samplingQuality.SelectedIndex switch
                {
                    0 => 60000,
                    2 => 10000,
                    _ => 25000
                };
            if (monitors.SelectedIndex < 0)
                return;
            var bounds = Screen.AllScreens[monitors.SelectedIndex].Bounds;
            int x = Math.Min(bounds.Width, Math.Max(1, (int)Math.Round(Math.Sqrt((double)samplingCells.Value * bounds.Width / bounds.Height))));
            int y = Math.Min(bounds.Height, Math.Max(1, (int)Math.Round((double)samplingCells.Value / x)));
            samplingGrid.Text = $"{x} x {y} ({x * y:N0} cells)";
        }
        samplingQuality.SelectedIndexChanged += (_, _) => UpdateSampling();
        samplingCells.ValueChanged += (_, _) => UpdateSampling();
        monitors.SelectedIndexChanged += (_, _) => UpdateSampling();
        UpdateSampling();
        var calibrationActions = Row(calibrate, express, refine, cancel, deleteData);
        top.Controls.Add(calibrationActions);
        var filterActions = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Height = 44, Margin = new Padding(3, 6, 3, 6) };
        filterActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        filterActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        apply.AutoSize = disable.AutoSize = false;
        apply.Dock = disable.Dock = DockStyle.Fill;
        apply.BackColor = Color.LightBlue;
        apply.ForeColor = Color.Black;
        apply.UseVisualStyleBackColor = false;
        filterActions.Controls.Add(apply, 0, 0);
        filterActions.Controls.Add(disable, 1, 0);
        top.Controls.Add(filterActions);
        // Fixed preferred width avoids resize feedback between nested panels.
        filterActions.Width = calibrationActions.GetPreferredSize(Size.Empty).Width;
        top.Controls.Add(status);
        top.Controls.Add(progress);
        top.Controls.Add(progressStep);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var summary = new TabPage("Corrections summary");
        summary.Controls.Add(ScrollablePlot(corrections));
        tabs.TabPages.Add(summary);
        var measured = new TabPage("Calibration quality");
        measured.Controls.Add(metrics);
        measured.Controls.Add(new Label { Dock = DockStyle.Bottom, Height = 40, Text = "Relative brightness error (%), based on camera response. Not a luminance measurement." });
        tabs.TabPages.Add(measured);
        foreach (string column in new[] { "Mode", "Samples", "RMS (average error)", "Maximum error" })
            metrics.Columns.Add(column, column);
        outer.Controls.Add(tabs, 0, 1);
        outer.Controls.Add(log, 0, 2);
        calibrate.Click += async (_, _) => await StartCalibration(true);
        express.Click += async (_, _) => await StartCalibration(true, true);
        refine.Click += async (_, _) => await StartCalibration(false);
        deleteData.Click += async (_, _) => await DeleteAllData();
        cancel.Click += (_, _) => Stop();
        apply.Click += async (_, _) => await HookAction(ApplySelectedFilter);
        disable.Click += async (_, _) => await HookAction(() => { Backend.Disable(); Write("Filter disabled; DWM detours removed"); return Task.CompletedTask; });
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing && !AllowExit) { e.Cancel = true; Hide(); } else if (busy) Stop(); };
        try
        {
            startWithWindows.Checked = WindowsStartup.IsEnabled();
        }
        catch (Exception error) { Write("Startup settings: " + error.Message); }
        startWithWindows.CheckedChanged += (_, _) =>
        {
            if (updatingStartup)
                return;
            try
            {
                SaveModelSelection();
                WindowsStartup.SetEnabled(startWithWindows.Checked);
            }
            catch (Exception error)
            {
                updatingStartup = true;
                startWithWindows.Checked = !startWithWindows.Checked;
                updatingStartup = false;
                Write("Startup settings: " + error.Message);
                MessageBox.Show(this, error.Message, "Windows startup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        Write("Ready. Windows manages display color profiles and gamma calibration. The filter applies scene-dependent brightness correction.");
        camera.Items.Add("Automatic");
        camera.SelectedIndex = 0;
        monitors.SelectedIndexChanged += async (_, _) => await RefreshDevices(false);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        FormClosed += (_, _) => { Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged; corrections.Image?.Dispose(); };
        LoadReports();
        model.TextChanged += (_, _) => { if (File.Exists(model.Text)) LoadReports(); };
    }
    static void FitPlotWidth(PictureBox box)
    {
        if (box.Parent == null || box.Image == null)
            return;
        int width = Math.Max(1, box.Parent.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2);
        var size = new Size(width, Math.Max(1, (int)Math.Round(width * (double)box.Image.Height / box.Image.Width)));
        if (box.Size != size) box.Size = size;
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
                ManagedCalibration.CalibrationQualitySummary(folder, meta, ManagedCalibration.ReadCsv(ManagedCalibration.LatestValidation(folder)));
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
            metrics.Rows.Clear();
            var stats = Path.Combine(folder, "validation-summary.json");
            if (File.Exists(stats))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(stats));
                foreach (var item in new[] { ("Raw", "raw"), ("Model", "predicted_adaptive"), ("Feedback", "calibrated") })
                    if (json.RootElement.TryGetProperty("units", out var units) && units.GetString() == "relative_camera_brightness_percent" && json.RootElement.TryGetProperty(item.Item2, out var r))
                    {
                        string Percent(string field) => r.GetProperty(field).ValueKind == JsonValueKind.Number ? r.GetProperty(field).GetDouble().ToString("0.00") + "%" : "?";
                        metrics.Rows.Add(item.Item1, r.GetProperty("samples").GetInt32(), Percent("rms"), Percent("max_abs"));
                    }
            }
        }
        catch (Exception e) { Write("Report preview: " + e.Message); }
    }
    static TableLayoutPanel Row(params Control[] controls)
    {
        var p = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = controls.Length, RowCount = 1 };
        p.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        for (int i = 0; i < controls.Length; i++)
        {
            p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            // Left anchoring centers each control vertically in the shared row.
            controls[i].Anchor = AnchorStyles.Left;
            if (controls[i] is Label label) label.TextAlign = ContentAlignment.MiddleLeft;
            p.Controls.Add(controls[i], i, 0);
        }
        return p;
    }
    Button Browse(TextBox text, string filter)
    {
        var b = new Button { Text = "Browse...", AutoSize = true };
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
    Task HookAction(Func<Task> action) => Guard(() => ExecuteHookAction(action));
    async Task ExecuteHookAction(Func<Task> action)
    {
        if (busy || toggling)
            throw new InvalidOperationException("Wait for the current operation to finish.");
        toggling = true;
        apply.Enabled = disable.Enabled = calibrate.Enabled = express.Enabled = refine.Enabled = deleteData.Enabled = false;
        try
        {
            await action();
        }
        finally { toggling = false; apply.Enabled = disable.Enabled = calibrate.Enabled = express.Enabled = refine.Enabled = deleteData.Enabled = true; RefreshFilterStatus(); }
    }
    async Task Guard(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception e) { Write(e.Message); MessageBox.Show(this, e.Message, "OLED calibration", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
    async Task Calibration(bool fresh, bool expressMode) => await Guard(async () =>
    {
        if (busy || toggling) return;
        if (monitors.SelectedIndex < 0) throw new InvalidOperationException("Select a display first.");
        await DeviceInventory.RequireHdr(root, Screen.AllScreens[monitors.SelectedIndex].DeviceName);
        if (!fresh && (!File.Exists(model.Text) || !File.Exists(Path.Combine(Path.GetDirectoryName(model.Text)!, "metadata.json"))))
            throw new InvalidOperationException("No complete calibration is selected. Choose New calibration, or select a saved model with its calibration reports.");
        if (!fresh && File.Exists(model.Text))
        {
            var saved = JsonDocument.Parse(File.ReadAllText(model.Text)).RootElement;
            if (saved.TryGetProperty("monitor_device", out var device) && !string.IsNullOrWhiteSpace(device.GetString()) && device.GetString() != Screen.AllScreens[monitors.SelectedIndex].DeviceName)
                throw new Exception("Refinement must use the display that produced this model. Use New calibration for a different display.");
        }
        if (busy || toggling)
            return;
        if (!fresh)
            expressMode = JsonSerializer.Deserialize<CalibrationMetadata>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(model.Text)!, "metadata.json")))!.express_mode;
        Backend.Disable();
        status.Text = "Measuring; filter disabled";
        busy = true;
        calibrate.Enabled = express.Enabled = refine.Enabled = deleteData.Enabled = apply.Enabled = false;
        progress.Value = 0;
        progressStep.Text = "Preparing";
        ManagedCalibration.Progress = (value, phase) => { progress.Value = Math.Max(progress.Value, Math.Clamp(value, 0, 100)); progressStep.Text = phase; };
        var output = fresh ? Path.Combine(dataRoot, "reports", "adaptive", "managed-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")) : Path.GetDirectoryName(model.Text)!;
        calibrationCancellation = new CancellationTokenSource();
        try
        {
            await ManagedCalibration.Run(root, output, fresh, Screen.AllScreens[monitors.SelectedIndex].DeviceName, (camera.SelectedIndex > 0 ? camera.Text : ""), (int)rounds.Value, Write, p => job = p, calibrationCancellation.Token, (double)peakBrightness.Value, expressMode, hasWhiteSubpixel.Checked);
            model.Text = Path.Combine(output, "runtime-model.json");
            SaveModelSelection();
            Write("Calibration complete. Model ready to apply.");
            LoadReports();
        }
        catch (OperationCanceledException) { Write("Calibration stopped; previous filter model retained."); }
        finally { job = null; calibrationCancellation.Dispose(); calibrationCancellation = null; busy = false; calibrate.Enabled = express.Enabled = refine.Enabled = deleteData.Enabled = apply.Enabled = true; ManagedCalibration.Progress = null; if (progress.Value < 100) progressStep.Text = "Stopped"; RefreshFilterStatus(); }

    });
    async Task DeleteAllData()
    {
        if (busy || toggling) return;
        if (MessageBox.Show(this, "Disable the filter and delete all application data, cached packages and logs, including their folders? Startup will be disabled and the application will exit. Locked files will be removed at restart. This cannot be undone.",
            "Delete all data", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        bool completed = false;
        await HookAction(async () =>
        {
            Backend.Disable();
            WindowsStartup.SetEnabled(false);
            int pending = await Task.Run(() => ApplicationDataCleanup.DeleteAll(root));
            if (pending > 0)
                MessageBox.Show(this, "Data and logs have been removed. Restart Windows to finish removing locked runtime files and their folders.",
                    "Cleanup pending restart", MessageBoxButtons.OK, MessageBoxIcon.Information);
            completed = true;
        });
        if (completed) { AllowExit = true; Close(); }
    }

    async Task StartCalibration(bool fresh, bool expressMode = false)
    {
        if (busy || toggling)
            return;
        calibrationTask = Calibration(fresh, expressMode);
        try
        {
            await calibrationTask;
        }
        finally { calibrationTask = null; }
    }
    void SaveModelSelection()
    {
        var path = Path.Combine(dataRoot, "build", "managed-settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            model = model.Text,
            has_white_subpixel = hasWhiteSubpixel.Checked,
            sampling_mode = samplingQuality.SelectedIndex,
            sampling_cells = (int)samplingCells.Value
        }));
    }
    async Task ApplySelectedFilter()
    {
        if (monitors.SelectedIndex < 0)
            throw new InvalidOperationException("Select a display first.");
        await Backend.Apply(root, model.Text, Screen.AllScreens[monitors.SelectedIndex], Write, (int)samplingCells.Value);
        SaveModelSelection();
    }
    public async Task InitializeAsync(bool startup)
    {
        await RefreshDevices(true);
        RefreshFilterStatus();
        if (startup && Injector.GetStatus() != FilterState.Enabled)
        {
            await ExecuteHookAction(ApplySelectedFilter);
            RefreshFilterStatus();
        }
    }
    public void RefreshFilterStatus()
    {
        if (busy || toggling || IsDisposed)
            return;
        try
        {
            status.Text = Injector.GetStatus() switch
            {
                FilterState.Enabled => "Filter enabled",
                FilterState.Disabled or FilterState.NotLoaded => "Filter disabled",
                FilterState.Faulted => "Filter stopped after an error",
                _ => "Filter status unavailable (older hook)"
            };
        }
        catch (Exception error) { status.Text = "Filter status unavailable"; Write("Filter status: " + error.Message); }
        FilterStatusChanged?.Invoke(status.Text);
    }
    public async Task PrepareExitAsync(bool disableFilter)
    {
        if (toggling)
            throw new InvalidOperationException("Wait for the filter operation to finish.");
        if (busy)
        {
            Stop();
            if (calibrationTask != null)
                await calibrationTask;
        }
        if (disableFilter)
            Backend.Disable();
    }
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
