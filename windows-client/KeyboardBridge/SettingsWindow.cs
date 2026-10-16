using System.Text.Json;
using System.Windows.Forms;

namespace KeyboardBridge;

record UserPreferences(int ToggleKey)
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
        return new UserPreferences(0x7B); // F12
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
    readonly TextBox host = new() { Width = 160 };
    readonly NumericUpDown port = new() { Minimum = 1, Maximum = 65535, Width = 100 };
    readonly TextBox token = new() { Width = 160, UseSystemPasswordChar = true };
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
        Height = 490;
        MinimumSize = new System.Drawing.Size(475, 490);
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 13,
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
        panel.Controls.Add(note, 0, 11);
        panel.SetColumnSpan(note, 2);
        timer.Tick += (_, _) => RefreshStatus();
        timer.Start();
        RefreshStatus();
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
            (remote() ? " — REMOTE" : " — LOCAL");
        uptime.Text = network.ConnectedSince is { } since
            ? (DateTimeOffset.UtcNow - since).ToString(@"dd\.hh\:mm\:ss")
            : "—";
        reports.Text = network.SentReports.ToString("N0");
        reconnects.Text = network.Reconnects.ToString("N0");
        latency.Text = network.RoundTripMs is double ms ? ms.ToString("F1") + " ms" : "Unavailable";
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Stop();
        timer.Dispose();
        base.OnFormClosed(e);
    }
}
