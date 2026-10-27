using System.Text.Json;
using System.Diagnostics;
using System.Windows.Forms;

namespace KeyboardBridge;

record UserPreferences(int ToggleKey, int MouseToggleKey = 0x7A, bool Linked = false, double MouseSensitivity = 1.0, int MouseUpdateMs = 16, bool AdaptiveMouse = false, bool AntiIdle = false, int KeyboardMods = 3, int MouseMods = 3, int EmergencyKey = 0x79, int EmergencyMods = 3, string SwitchSequence = "")
{
    public static UserPreferences Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var prefs = JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(path));
                if (prefs != null && prefs.ToggleKey > 0 && prefs.ToggleKey <= 0xFE)
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
    readonly Label picoDiag = new() { AutoSize = true, MaximumSize = new System.Drawing.Size(405, 0) };
    readonly Label espDiag = new() { AutoSize = true, MaximumSize = new System.Drawing.Size(405, 0) };
    readonly Label linkDiag = new() { AutoSize = true, MaximumSize = new System.Drawing.Size(405, 0) };
    readonly Label firmwareDiag = new() { AutoSize = true, MaximumSize = new System.Drawing.Size(405, 0) };
    readonly Label benchmarkResult = new() { AutoSize = true, MaximumSize = new System.Drawing.Size(395, 0) };
    readonly TextBox host = new() { Width = 160 };
    readonly CheckBox autoDiscovery = new() { Text = "Automatic detect NodeMCU (manual IP fallback)", AutoSize = true };
    readonly NumericUpDown port = new() { Minimum = 1, Maximum = 65535, Width = 100 };
    readonly TextBox token = new() { Width = 160, UseSystemPasswordChar = true };
    readonly Button mouseHotkey = new() { Text = "Record mouse shortcut", AutoSize = true };
    readonly NumericUpDown sensitivity = new() { Minimum = 25, Maximum = 300, Increment = 10, DecimalPlaces = 0, Width = 90, Value = 100 };
    readonly ComboBox updateRate = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    Action<double,int>? saveMouseTuning;
    readonly CheckBox launchAtLogin = new() { Text = "Start with Windows", AutoSize = true };
    readonly Label antiIdleStatus = new() { AutoSize = true, MaximumSize = new System.Drawing.Size(360, 0) };
    Func<string>? getAntiIdleStatus;
    readonly CheckBox antiIdle = new() { Text = "Enable remote PC anti-idle (both inputs LOCAL)", AutoSize = true };
    readonly CheckBox adaptive = new() { Text = "Experimental adaptive mouse pacing", AutoSize = true };
    readonly RttGraph rttGraph = new() { Dock = DockStyle.Fill, Height = 110 };
    readonly CheckBox linked = new() { Text = "Switch keyboard + mouse together", AutoSize = true };
    Func<bool>? isMouseRemote;
    Func<int>? getMouseHotkey;
    Func<bool>? getLinked;
    Action<int,bool>? saveMousePreferences;
    readonly Button hotkey = new() { Text = "Record keyboard shortcut", AutoSize = true };
    readonly Button emergencyHotkey = new() { Text = "Record emergency shortcut", AutoSize = true };
    readonly TextBox sequence = new() { Width = 175, MaxLength = 32 };
    Action<int,int>? keyboardSave;
    Action<int,int>? mouseSave;
    Action<int,int>? emergencySave;
    Action<string>? sequenceSave;
    string? capturing;
    public bool IsRecording => capturing != null;
    public static string ShortcutText(int key, int mods) =>
        ((mods & 1) != 0 ? "Ctrl+" : "") + ((mods & 2) != 0 ? "Alt+" : "") +
        ((mods & 4) != 0 ? "Shift+" : "") + ((mods & 8) != 0 ? "Win+" : "") +
        ((Keys)key).ToString();
    public bool TryRecord(int key, bool down, int mods)
    {
        if (capturing == null) return false;
        if (down && key is not (0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5))
        {
            string target = capturing;
            capturing = null;
            try
            {
                if (key == 0x1B) return true; // Escape cancels
                if (mods == 0) throw new ArgumentException("Choose at least one modifier (Ctrl, Alt, Shift or Win).");
                if (target == "keyboard") keyboardSave?.Invoke(key, mods);
                if (target == "mouse") mouseSave?.Invoke(key, mods);
                if (target == "emergency") emergencySave?.Invoke(key, mods);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Shortcut not saved"); }
            RefreshShortcutLabels?.Invoke();
        }
        return true;
    }
    Action? RefreshShortcutLabels;
    public void SetShortcutRecording(Func<(int key,int mods)> keyboard, Func<(int key,int mods)> mouse,
        Func<(int key,int mods)> emergency, Action<int,int> saveKeyboard, Action<int,int> saveMouse,
        Action<int,int> saveEmergency, string initialSequence, Action<string> saveSequence)
    {
        keyboardSave = saveKeyboard; mouseSave = saveMouse; emergencySave = saveEmergency;
        sequenceSave = saveSequence;
        sequence.Text = initialSequence;
        RefreshShortcutLabels = () => {
            var k = keyboard(); var m = mouse(); var e = emergency();
            hotkey.Text = "Keyboard: " + ShortcutText(k.key,k.mods) + " (record)";
            mouseHotkey.Text = "Mouse: " + ShortcutText(m.key,m.mods) + " (record)";
            emergencyHotkey.Text = "Emergency: " + ShortcutText(e.key,e.mods) + " (record)";
        };
        RefreshShortcutLabels();
    }
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
        var about = AddTab(tabs, "About");
        PopulateAbout(about);
        // Tab pages keep independent row layouts; no more giant vertically scrolling form.
        var panel = connection;

        host.Text = cfg.Host;
        autoDiscovery.Checked = cfg.AutoDiscover;
        port.Value = cfg.Port;
        token.Text = cfg.Token;
        AddRow(panel, 0, "NodeMCU IP / host", host);
        connection.Controls.Add(autoDiscovery, 1, 10);
        AddRow(panel, 1, "TCP port", port);
        AddRow(panel, 2, "Shared token", token);
        AddRow(connection, 3, "Connection / mode", state);
        AddRow(connection, 4, "Session uptime", uptime);
        AddRow(connection, 5, "Bridge RTT", latency);
        AddRow(connection, 6, "Reports sent", reports);
        AddRow(connection, 7, "Disconnections", reconnects);

        AddRow(input, 0, "Keyboard shortcut", hotkey);

        hotkey.Click += (_, _) => { capturing = "keyboard"; hotkey.Text = "Press combination (Esc cancels)..."; };
        mouseHotkey.Click += (_, _) => { capturing = "mouse"; mouseHotkey.Text = "Press combination (Esc cancels)..."; };
        emergencyHotkey.Click += (_, _) => { capturing = "emergency"; emergencyHotkey.Text = "Press combination (Esc cancels)..."; };
        input.Controls.Add(new Label { Text = "Click a shortcut to record; hold modifiers and press a key.", AutoSize = true }, 0, 1);
        input.SetColumnSpan(input.GetControlFromPosition(0,1)!,2);
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
            try { applyConnection(new Settings(newHost, (int)port.Value, token.Text, autoDiscovery.Checked)); }
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
        var saveMouse = new Button { Text = "Save linked mode", AutoSize = true };
        saveMouse.Click += (_, _) => {
            try { saveMousePreferences?.Invoke(getMouseHotkey?.Invoke() ?? 0x7A, linked.Checked); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Shortcut conflict"); }
        };
        input.Controls.Add(saveMouse, 1, 4);
        input.Controls.Add(emergencyHotkey, 1, 5);
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
        diagnostics.Controls.Add(picoDiag, 0, 5);
        diagnostics.SetColumnSpan(picoDiag, 2);
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
                    network.SchedulerDiagnostics() + "\n" + network.EspDiagnostics() + "\n" + network.PicoDiagnostics() + "\n" +
                    network.LinkDiagnostics() + "\n" + "Firmware: " + (network.FirmwareDiag ?? "unavailable") + "\n" +
                    "RTT history (ms): " + string.Join(", ", network.RttHistory().Select(v => v.ToString("F0"))) + "\n\n" +
                    "Recent log (" + DiagnosticLog.LogPath + "):\n" + DiagnosticLog.Tail(400) + "\n");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export failed"); }
        };
        diagnostics.Controls.Add(export, 1, 9);
        var openLogs = new Button { Text = "Open log folder", AutoSize = true };
        openLogs.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(DiagnosticLog.LogDirectory);
                Process.Start(new ProcessStartInfo(DiagnosticLog.LogDirectory) { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Unable to open log folder"); }
        };
        diagnostics.Controls.Add(openLogs, 0, 9);
        diagnostics.Controls.Add(linkDiag, 0, 7);
        diagnostics.SetColumnSpan(linkDiag, 2);
        diagnostics.Controls.Add(firmwareDiag, 0, 8);
        diagnostics.SetColumnSpan(firmwareDiag, 2);
        diagnostics.RowStyles[8].Height = 64;
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
        input.Controls.Add(antiIdle, 1, 6);
        var antiIdleHint = new Label { Text = "NodeMCU moves the remote PC mouse every 15 seconds, even if this Windows PC is OFF. Setting survives NodeMCU restarts. Pauses while either input is REMOTE. Requires updated NodeMCU firmware.", AutoSize = true, MaximumSize = new System.Drawing.Size(340, 0) };
        input.Controls.Add(antiIdleHint, 0, 7);
        input.SetColumnSpan(antiIdleHint, 2);
        input.RowStyles[7].Height = 70;
        AddRow(input, 9, "Switch both: typed sequence", sequence);
        var sequenceButton = new Button { Text = "Save sequence", AutoSize = true };
        sequenceButton.Click += (_, _) => {
            try { sequenceSave?.Invoke(sequence.Text.Trim()); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Invalid sequence"); }
        };
        input.Controls.Add(sequenceButton, 1, 10);
        input.Controls.Add(antiIdleStatus, 0, 8);
        input.SetColumnSpan(antiIdleStatus, 2);
        timer.Tick += (_, _) => antiIdleStatus.Text = getAntiIdleStatus?.Invoke() ?? "";
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
        linked.Checked = linkedMode();
    }

    public void SetAntiIdle(bool enabled, Func<string> status, Action<bool> apply)
    {
        getAntiIdleStatus = status;
        antiIdleStatus.Text = status();
        antiIdle.Checked = enabled;
        antiIdle.CheckedChanged += (_, _) => apply(antiIdle.Checked);
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

    static void PopulateAbout(TableLayoutPanel panel)
    {
        var heading = new Label { Text = "Wi-Fi Keyboard Bridge", Font = new System.Drawing.Font(System.Drawing.SystemFonts.DefaultFont.FontFamily, 15, System.Drawing.FontStyle.Bold), AutoSize = true };
        panel.Controls.Add(heading, 0, 0);
        panel.SetColumnSpan(heading, 2);
        var description = new Label
        {
            Text = "Share keyboard and mouse input from a Windows PC with a second computer over a trusted Wi-Fi network, using a NodeMCU ESP8266 and a Raspberry Pi Pico as a USB HID device.",
            AutoSize = true, MaximumSize = new System.Drawing.Size(470, 0)
        };
        panel.Controls.Add(description, 0, 1);
        panel.SetColumnSpan(description, 2);
        panel.RowStyles[1].Height = 66;
        var hardware = new Label { Text = "Hardware: ESP8266 NodeMCU + Raspberry Pi Pico (RP2040)\nWindows client: .NET 8 / WinForms\nEmergency LOCAL: Ctrl + Alt + F10", AutoSize = true };
        panel.Controls.Add(hardware, 0, 2);
        panel.SetColumnSpan(hardware, 2);
        panel.RowStyles[2].Height = 74;
        AddRow(panel, 4, "Version", new Label { Text = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "Development", AutoSize = true });
        AddRow(panel, 5, "Project author", new Label { Text = "axw13", AutoSize = true });
        AddAboutLink(panel, 6, "GitHub profile", "github.com/axw13", "https://github.com/axw13");
        AddAboutLink(panel, 7, "Source repository", "wifi-keyboard-bridge", "https://github.com/axw13/wifi-keyboard-bridge");
        AddRow(panel, 8, "Diagnostic log", new Label { Text = DiagnosticLog.LogPath, AutoSize = true, MaximumSize = new System.Drawing.Size(315, 0) });
        panel.RowStyles[8].Height = 68;
    }

    static void AddAboutLink(TableLayoutPanel panel, int row, string caption, string display, string url)
    {
        var link = new LinkLabel { Text = display, AutoSize = true };
        link.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Unable to open link"); }
        };
        AddRow(panel, row, caption, link);
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
        // Reflect the authenticated mDNS address, without overwriting edits in progress.
        if (autoDiscovery.Checked && !host.Focused &&
            network.Connected && network.ActiveHost is { } resolvedIp &&
            host.Text != resolvedIp)
            host.Text = resolvedIp;
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
        picoDiag.Text = network.PicoDiagnostics();
        linkDiag.Text = network.LinkDiagnostics();
        firmwareDiag.Text = network.Connected
            ? "Firmware: " + (network.FirmwareDiag?.Replace(",", ", ") ?? "awaiting DIAG reply (update NodeMCU firmware)")
            : "";
        rttGraph.Samples = network.RttHistory();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Stop();
        timer.Dispose();
        base.OnFormClosed(e);
    }
}
