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
    readonly ComboBox hotkey = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 125 };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };

    public SettingsWindow(Settings cfg, Transport network, Func<bool> remote, Func<int> getHotkey, Action<int> setHotkey)
    {
        this.network = network;
        this.remote = remote;
        this.getHotkey = getHotkey;
        this.setHotkey = setHotkey;
        Text = "Wi-Fi Keyboard Bridge — Settings";
        Width = 410;
        Height = 370;
        MinimumSize = new System.Drawing.Size(410, 370);
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 9,
            Padding = new Padding(18), AutoSize = false
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(panel);

        AddRow(panel, 0, "Destination", new Label { Text = cfg.Host + ":" + cfg.Port, AutoSize = true });
        AddRow(panel, 1, "Connection", state);
        AddRow(panel, 2, "Active input", new Label { Text = "Shown below", AutoSize = true });
        AddRow(panel, 3, "Session uptime", uptime);
        AddRow(panel, 4, "Reports sent", reports);
        AddRow(panel, 5, "Disconnections", reconnects);

        for (int n = 1; n <= 12; n++) hotkey.Items.Add("Ctrl + Alt + F" + n);
        hotkey.SelectedIndex = getHotkey() - 0x70;
        AddRow(panel, 6, "Switch shortcut", hotkey);

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
        panel.Controls.Add(apply, 1, 7);
        var note = new Label
        {
            Text = "Statistics are client-side. Reports sent is not a latency or delivery measurement.",
            AutoSize = true, MaximumSize = new System.Drawing.Size(335, 0)
        };
        panel.Controls.Add(note, 0, 8);
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
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Stop();
        timer.Dispose();
        base.OnFormClosed(e);
    }
}
