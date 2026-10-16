using System.Text.Json;
using System.Windows.Forms;

namespace KeyboardBridge;

record UserPreferences(int ToggleKey, int MouseToggleKey = 0x7A, bool Linked = false, double MouseSensitivity = 1.0, int MouseUpdateMs = 16)
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
        Width = 475;
        Height = 940;
        MinimumSize = new System.Drawing.Size(475, 940);
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 28,
            Padding = new Padding(18), AutoSize = false
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(panel);

        host.Text = cfg.Host;
        port.Value = cfg.Port;
        token.Text = cfg.Token;
        AddRow(panel, 0, "NodeMCU IP / host", host);
        AddRow(panel, 1, "TCP port", port);
        AddRow(panel, 2, "Shared token", token);
        AddRow(panel, 3, "Connection / mode", state);
        AddRow(panel, 4, "Session uptime", uptime);
        AddRow(panel, 5, "Bridge RTT", latency);
        AddRow(panel, 6, "Reports sent", reports);
        AddRow(panel, 7, "Disconnections", reconnects);

        for (int n = 1; n <= 12; n++) hotkey.Items.Add("Ctrl + Alt + F" + n);
        hotkey.SelectedIndex = getHotkey() - 0x70;
        AddRow(panel, 8, "Switch shortcut", hotkey);

        var apply = new Button { Text = "Save shortcut", AutoSize = true };
        apply.Click += (_, _) =>
        {
            try
            {
                setHotkey(0x70 + hotkey.SelectedIndex);
                MessageBox.Show(this, "Shortcut saved. Release Ctrl and Alt before switching again.",
                    "Keyboard Bridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Save failed"); }
        };
        panel.Controls.Add(apply, 1, 9);
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
        panel.Controls.Add(saveConnection, 1, 10);
        var note = new Label
        {
            Text = "RTT is Windows ↔ NodeMCU ping time, not full key-to-screen latency. New firmware required for RTT.",
            AutoSize = true, MaximumSize = new System.Drawing.Size(390, 0)
        };
        AddRow(panel, 11, "Mouse shortcut", mouseHotkey);
        panel.Controls.Add(linked, 1, 12);
        var saveMouse = new Button { Text = "Save mouse options", AutoSize = true };
        saveMouse.Click += (_, _) =>
        {
            if (mouseHotkey.SelectedIndex < 0) return;
            if (!linked.Checked && mouseHotkey.SelectedIndex == hotkey.SelectedIndex)
            {
                MessageBox.Show(this, "Use separate F keys, or enable linked switching."); return;
            }
            saveMousePreferences?.Invoke(0x70 + mouseHotkey.SelectedIndex, linked.Checked);
        };
        panel.Controls.Add(saveMouse, 1, 13);
        var runBenchmark = new Button { Text = "Benchmark (10 seconds)", AutoSize = true };
        runBenchmark.Click += (_, _) =>
        {
            try { network.StartBenchmark(); benchmarkResult.Text = "Collecting samples..."; }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Benchmark unavailable"); }
        };
        panel.Controls.Add(runBenchmark, 1, 17);
        panel.Controls.Add(benchmarkResult, 0, 18);
        panel.SetColumnSpan(benchmarkResult, 2);
        panel.Controls.Add(mouseDiag, 0, 19);
        panel.SetColumnSpan(mouseDiag, 2);
        panel.Controls.Add(schedulerDiag, 0, 20);
        panel.SetColumnSpan(schedulerDiag, 2);
        panel.Controls.Add(espDiag, 0, 21);
        panel.SetColumnSpan(espDiag, 2);
        panel.Controls.Add(note, 0, 22);
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
        panel.Controls.Add(launchAtLogin, 1, 23);
        var export = new Button { Text = "Export diagnostics", AutoSize = true };
        export.Click += (_, _) =>
        {
            using var picker = new SaveFileDialog { Filter = "Text report (*.txt)|*.txt", FileName = "keyboard-bridge-diagnostics.txt" };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                File.WriteAllText(picker.FileName,
                    $"Wi-Fi Keyboard Bridge Diagnostics\\nCaptured: {DateTimeOffset.Now:O}\\n" +
                    $"Connected: {network.Connected}\\nKeyboard remote: {remote()}\\nMouse remote: {isMouseRemote?.Invoke()}\\n" +
                    $"Mouse interval target: {network.MouseUpdateMs} ms\\n" +
                    $"Wi-Fi RTT: {network.RoundTripMs?.ToString("F1") ?? "unavailable"} ms\\n" +
                    network.BenchmarkStatus() + "\\n" + network.MouseDiagnostics() + "\\n" +
                    network.SchedulerDiagnostics() + "\\n" + network.EspDiagnostics() + "\\n");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export failed"); }
        };
        panel.Controls.Add(export, 1, 24);
        updateRate.Items.AddRange(new object[] { "30 Hz (33 ms)", "60 Hz (16 ms)", "90 Hz (11 ms)", "125 Hz (8 ms)" });
        AddRow(panel, 14, "Mouse sensitivity %", sensitivity);
        AddRow(panel, 15, "Mouse update rate", updateRate);
        var saveTuning = new Button { Text = "Apply mouse tuning", AutoSize = true };
        saveTuning.Click += (_, _) =>
        {
            int[] intervals = { 33, 16, 11, 8 };
            if (updateRate.SelectedIndex >= 0)
                saveMouseTuning?.Invoke((double)sensitivity.Value / 100.0, intervals[updateRate.SelectedIndex]);
        };
        panel.Controls.Add(saveTuning, 1, 16);
        panel.SetColumnSpan(note, 2);
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

    static void AddRow(TableLayoutPanel p, int row, string title, Control control)
    {
        p.RowStyles.Add(new RowStyle(SizeType.Absolute, row == 8 ? 55 : 32));
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
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Stop();
        timer.Dispose();
        base.OnFormClosed(e);
    }
}
