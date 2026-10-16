using System.Text.Json;
using System.Windows.Forms;

namespace KeyboardBridge;

record UserPreferences(int ToggleKey, int MouseToggleKey = 0x7A, bool Linked = false, double MouseSensitivity = 1.0, int MouseUpdateMs = 16, bool AdaptiveMouse = false)
{
    public static UserPreferences Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var prefs = JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(path));
                if (prefs != null && prefs.ToggleKey >= 0x70 && prefs.ToggleKey <= 0x7B)
                    return prefs;
            }
        }
        catch (Exception) { /* Corrupt settings should not prevent startup. */ }
        return new UserPreferences(0x7B, 0x7A, false); // keyboard F12, mouse F11
    }

    public static void Save(string path, UserPreferences prefs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(prefs, new JsonSerializerOptions { WriteIndented = true }));
    }
}

sealed class SettingsWindow : Form
{
    readonly Transport network;
    readonly Func<bool> remote;
    readonly Func<int> getHotkey;
    readonly Action<int> setHotkey;
    readonly Label state = new() { AutoSize = true };
    readonly Label uptime = new() { AutoSize = true };
    readonly Label reports = new() { AutoSize = true };
    readonly Label reconnects = new() { AutoSize = true };
    readonly Label latency = new() { AutoSize = true };
    readonly Label schedulerDiag = new() { AutoSize = true, MaximumSize = new System.Drawing.Size(405, 0) };
    readonly Label mouseDiag = new() { AutoSize = true, MaximumSize = new System.Drawing.Size(405, 0) };
    readonly Label espDiag = new() { AutoSize = true, MaximumSize = new System.Drawing.Size(405, 0) };
    readonly Label benchmarkResult = new() { AutoSize = true, MaximumSize = new System.Drawing.Size(395, 0) };
    readonly TextBox host = new() { Width = 160 };
    readonly NumericUpDown port = new() { Minimum = 1, Maximum = 65535, Width = 100 };
    readonly TextBox token = new() { Width = 160, UseSystemPasswordChar = true };
    readonly ComboBox mouseHotkey = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 125 };
    readonly NumericUpDown sensitivity = new() { Minimum = 25, Maximum = 300, Increment = 10, DecimalPlaces = 0, Width = 90, Value = 100 };
    readonly ComboBox updateRate = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    Action<double,int>? saveMouseTuning;
    readonly CheckBox launchAtLogin = new() { Text = "Start with Windows", AutoSize = true };
    readonly CheckBox adaptive = new() { Text = "Experimental adaptive mouse pacing", AutoSize = true };
    readonly RttGraph rttGraph = new() { Dock = DockStyle.Fill, Height = 110 };
    readonly CheckBox linked = new() { Text = "Switch keyboard + mouse together", AutoSize = true };
    Func<bool>? isMouseRemote;
    Func<int>? getMouseHotkey;
    Func<bool>? getLinked;
    Action<int,bool>? saveMousePreferences;
    readonly ComboBox hotkey = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 125 };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };

    public SettingsWindow(Settings cfg, Transport network, Func<bool> remote, Func<int> getHotkey, Action<int> setHotkey, Action<Settings> applyConnection)
    {
        this.network = network;
        this.remote = remote;
        this.getHotkey = getHotkey;
        this.setHotkey = setHotkey;
        Text = "Wi-Fi Keyboard Bridge — Settings";
        Width = 580;
        Height = 465;
        MinimumSize = new System.Drawing.Size(540, 420);
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new System.Drawing.Point(14, 7) };
        Controls.Add(tabs);
        var connection = AddTab(tabs, "Connection");
        var input = AddTab(tabs, "Input");
        var performance = AddTab(tabs, "Performance");
        var diagnostics = AddTab(tabs, "Diagnostics");
        // Tab pages keep independent row layouts; no more giant vertically scrolling form.
        var panel = connection;

        host.Text = cfg.Host;
        port.Value = cfg.Port;
        token.Text = cfg.Token;
        AddRow(panel, 0, "NodeMCU IP / host", host);
        AddRow(panel, 1, "TCP port", port);
        AddRow(panel, 2, "Shared token", token);
        AddRow(connection, 3, "Connection / mode", state);
        AddRow(connection, 4, "Session uptime", uptime);
        AddRow(connection, 5, "Bridge RTT", latency);
        AddRow(connection, 6, "Reports sent", reports);
        AddRow(connection, 7, "Disconnections", reconnects);

        for (int n = 1; n <= 12; n++) hotkey.Items.Add("Ctrl + Alt + F" + n);
        hotkey.SelectedIndex = getHotkey() - 0x70;
        AddRow(input, 0, "Keyboard shortcut", hotkey);

        var apply = new Button { Text = "Save shortcut", AutoSize = true };
        apply.Click += (_, _) =>
        {
            try
            {
                if (hotkey.SelectedIndex < 0) return;
                setHotkey(0x70 + hotkey.SelectedIndex);
                MessageBox.Show(this, "Shortcut saved. Release Ctrl and Alt before switching again.",
                    "Keyboard Bridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Save failed"); }
        };
        input.Controls.Add(apply, 1, 1);
        var saveConnection = new Button { Text = "Save connection & restart", AutoSize = true };
        saveConnection.Click += (_, _) =>
        {
            var newHost = host.Text.Trim();
            if (string.IsNullOrWhiteSpace(newHost) || newHost.Any(char.IsWhiteSpace))
            {
                MessageBox.Show(this, "Enter a valid hostname or IP address.", "Invalid address"); return;
            }
            if (token.Text.Length < 16)
            {
                MessageBox.Show(this, "Shared token must be at least 16 characters.", "Invalid token"); return;
            }
            if (MessageBox.Show(this, "Switch to LOCAL, save connection settings and restart Keyboard Bridge?",
                "Apply connection", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try { applyConnection(new Settings(newHost, (int)port.Value, token.Text)); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Unable to save connection settings"); }
        };
        connection.Controls.Add(saveConnection, 1, 8);
        var note = new Label
        {
            Text = "RTT is Windows ↔ NodeMCU ping time, not full key-to-screen latency. New firmware required for RTT.",
            AutoSize = true, MaximumSize = new System.Drawing.Size(390, 0)
        };
        AddRow(input, 2, "Mouse shortcut", mouseHotkey);
        input.Controls.Add(linked, 1, 3);
        var saveMouse = new Button { Text = "Save mouse options", AutoSize = true };
        saveMouse.Click += (_, _) =>
        {
            if (mouseHotkey.SelectedIndex < 0) return;
            if (!linked.Checked && mouseHotkey.SelectedIndex == hotkey.SelectedIndex)
            {
                MessageBox.Show(this, "Use separate F keys, or enable linked switching."); return;
            }
            try { saveMousePreferences?.Invoke(0x70 + mouseHotkey.SelectedIndex, linked.Checked); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Shortcut conflict"); }
        };
        input.Controls.Add(saveMouse, 1, 4);
        var emergencyNote = new Label { Text = "Emergency LOCAL: Ctrl + Alt + F10 (reserved)", AutoSize = true };
        input.Controls.Add(emergencyNote, 0, 5);
        input.SetColumnSpan(emergencyNote, 2);
        var runBenchmark = new Button { Text = "Benchmark (10 seconds)", AutoSize = true };
        runBenchmark.Click += (_, _) =>
        {
            try { network.StartBenchmark(); benchmarkResult.Text = "Collecting samples..."; }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Benchmark unavailable"); }
        };
        diagnostics.Controls.Add(runBenchmark, 1, 0);
        diagnostics.Controls.Add(benchmarkResult, 0, 1);
        diagnostics.SetColumnSpan(benchmarkResult, 2);
        diagnostics.Controls.Add(mouseDiag, 0, 2);
        diagnostics.SetColumnSpan(mouseDiag, 2);
        diagnostics.Controls.Add(schedulerDiag, 0, 3);
        diagnostics.SetColumnSpan(schedulerDiag, 2);
        diagnostics.Controls.Add(espDiag, 0, 4);
        diagnostics.SetColumnSpan(espDiag, 2);
        diagnostics.Controls.Add(note, 0, 10);
        launchAtLogin.Checked = StartupSettings.Enabled;
        launchAtLogin.CheckedChanged += (_, _) =>
        {
            try { StartupSettings.SetEnabled(launchAtLogin.Checked); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Startup option");
                launchAtLogin.Checked = StartupSettings.Enabled;
            }
        };
        connection.Controls.Add(launchAtLogin, 1, 9);
        var export = new Button { Text = "Export diagnostics", AutoSize = true };
        export.Click += (_, _) =>
        {
            using var picker = new SaveFileDialog { Filter = "Text report (*.txt)|*.txt", FileName = "keyboard-bridge-diagnostics.txt" };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                File.WriteAllText(picker.FileName,
                    $"Wi-Fi Keyboard Bridge Diagnostics\nCaptured: {DateTimeOffset.Now:O}\n" +
                    $"Connected: {network.Connected}\nKeyboard remote: {remote()}\nMouse remote: {isMouseRemote?.Invoke()}\n" +
                    $"Mouse interval target: {network.MouseUpdateMs} ms\n" +
                    $"Wi-Fi RTT: {network.RoundTripMs?.ToString("F1") ?? "unavailable"} ms\n" +
                    network.BenchmarkStatus() + "\n" + network.MouseDiagnostics() + "\n" +
                    network.SchedulerDiagnostics() + "\n" + network.EspDiagnostics() + "\n");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export failed"); }
        };
        diagnostics.Controls.Add(export, 1, 9);
        diagnostics.Controls.Add(rttGraph, 0, 6);
        diagnostics.SetColumnSpan(rttGraph, 2);
        diagnostics.RowStyles[6].Height = 120;
        updateRate.Items.AddRange(new object[] { "30 Hz (33 ms)", "60 Hz (16 ms)", "90 Hz (11 ms)", "125 Hz (8 ms)" });
        AddRow(performance, 0, "Mouse sensitivity %", sensitivity);
        AddRow(performance, 1, "Mouse update rate", updateRate);
        var saveTuning = new Button { Text = "Apply mouse tuning", AutoSize = true };
        saveTuning.Click += (_, _) =>
        {
            int[] intervals = { 33, 16, 11, 8 };
            if (updateRate.SelectedIndex >= 0)
                saveMouseTuning?.Invoke((double)sensitivity.Value / 100.0, intervals[updateRate.SelectedIndex]);
        };
        performance.Controls.Add(saveTuning, 1, 2);
        performance.Controls.Add(adaptive, 1, 3);
        var adaptiveHint = new Label { Text = "Default OFF. Never exceeds your chosen speed. Changes apply only after 8-second evaluation windows.", AutoSize = true, MaximumSize = new System.Drawing.Size(330, 0) };
        performance.Controls.Add(adaptiveHint, 0, 4);
        performance.SetColumnSpan(adaptiveHint, 2);
        var effectiveRate = new Label { AutoSize = true };
        performance.Controls.Add(effectiveRate, 1, 5);
        timer.Tick += (_, _) => effectiveRate.Text = $"Effective: {network.MouseUpdateMs} ms (manual {network.ManualMouseInterval} ms)";
        diagnostics.SetColumnSpan(note, 2);
        timer.Tick += (_, _) => RefreshStatus();
        timer.Start();
        RefreshStatus();
    }

    public void SetMouseSettings(Func<bool> mouseRemote, Func<int> mouseKey, Func<bool> linkedMode, Action<int,bool> save)
    {
        isMouseRemote = mouseRemote;
        getMouseHotkey = mouseKey;
        getLinked = linkedMode;
        saveMousePreferences = save;
        for (int n = 1; n <= 12; n++) mouseHotkey.Items.Add("Ctrl + Alt + F" + n);
        mouseHotkey.SelectedIndex = Math.Clamp(mouseKey() - 0x70, 0, 11);
        linked.Checked = linkedMode();
    }

    public void SetAdaptiveMouse(bool enabled, Action<bool> apply)
    {
        adaptive.Checked = enabled;
        adaptive.CheckedChanged += (_, _) => apply(adaptive.Checked);
    }

    public void SetMouseTuning(Func<double> currentSensitivity, Func<int> currentInterval, Action<double,int> apply)
    {
        saveMouseTuning = apply;
        sensitivity.Value = (decimal)Math.Clamp(currentSensitivity() * 100.0, 25, 300);
        int[] rates = { 33, 16, 11, 8 };
        int closest = 0;
        for (int i = 1; i < rates.Length; i++)
            if (Math.Abs(rates[i] - currentInterval()) < Math.Abs(rates[closest] - currentInterval())) closest = i;
        updateRate.SelectedIndex = closest;
    }

    static TableLayoutPanel AddTab(TabControl tabs, string title)
    {
        var page = new TabPage(title) { Padding = new Padding(8) };
        tabs.TabPages.Add(page);
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 2, Padding = new Padding(14), RowCount = 12 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 12; i++) panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        page.Controls.Add(panel);
        return panel;
    }

    static void AddRow(TableLayoutPanel p, int row, string title, Control control)
    {
        // The tab panel predefines row heights.
        p.Controls.Add(new Label { Text = title, AutoSize = true }, 0, row);
        p.Controls.Add(control, 1, row);
    }

    void RefreshStatus()
    {
        state.Text = (network.Connected ? "Connected" : "Disconnected") +
            " — K:" + (remote() ? "R" : "L") + " M:" + (isMouseRemote?.Invoke() == true ? "R" : "L");
        uptime.Text = network.ConnectedSince is { } since
            ? (DateTimeOffset.UtcNow - since).ToString(@"dd\.hh\:mm\:ss")
            : "—";
        reports.Text = network.SentReports.ToString("N0");
        reconnects.Text = network.Reconnects.ToString("N0");
        latency.Text = network.RoundTripMs is double ms ? ms.ToString("F1") + " ms" : "Unavailable";
        benchmarkResult.Text = network.BenchmarkStatus();
        mouseDiag.Text = network.MouseDiagnostics();
        schedulerDiag.Text = network.SchedulerDiagnostics();
        espDiag.Text = network.EspDiagnostics();
        rttGraph.Samples = network.RttHistory();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Stop();
        timer.Dispose();
        base.OnFormClosed(e);
    }
}
