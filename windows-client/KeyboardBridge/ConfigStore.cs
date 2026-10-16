using System.Text.Json;
using System.Windows.Forms;

namespace KeyboardBridge;

static class ConfigStore
{
    static readonly string userConfig = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WifiKeyboardBridge", "appsettings.json");
    static readonly string legacyConfig = System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    // Preserve existing installations, but store new configs in a writable user directory.
    public static string Path => File.Exists(legacyConfig) ? legacyConfig : userConfig;

    public static Settings? Load()
    {
        if (!File.Exists(Path)) return null;
        try
        {
            var cfg = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return IsValid(cfg) ? cfg : null;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Configuration load failed: " + ex.GetType().Name);
            return null;
        }
    }

    public static bool IsValid(Settings? cfg) =>
        cfg is not null && !string.IsNullOrWhiteSpace(cfg.Host) &&
        !cfg.Host.Any(char.IsWhiteSpace) && cfg.Port is >= 1 and <= 65535 &&
        !string.IsNullOrEmpty(cfg.Token) && cfg.Token.Length >= 16;

    public static void Save(Settings cfg)
    {
        if (!IsValid(cfg)) throw new ArgumentException("Invalid connection settings.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
    }
}

sealed class FirstRunDialog : Form
{
    readonly TextBox host = new() { Width = 240 };
    readonly NumericUpDown port = new() { Minimum = 1, Maximum = 65535, Width = 100, Value = 7777 };
    readonly TextBox token = new() { Width = 240, UseSystemPasswordChar = true };
    public Settings? Configuration { get; private set; }

    public FirstRunDialog()
    {
        Text = "Wi-Fi Keyboard Bridge — Connection setup";
        Width = 480; Height = 300; MinimumSize = new System.Drawing.Size(450, 280);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18),
            ColumnCount = 2, RowCount = 6 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(panel);
        var info = new Label { Text = "Configure your NodeMCU connection. Settings are stored locally and can be changed later.",
            AutoSize = true, MaximumSize = new System.Drawing.Size(410, 0) };
        panel.Controls.Add(info, 0, 0); panel.SetColumnSpan(info, 2);
        panel.Controls.Add(new Label { Text = "NodeMCU IP / host", AutoSize = true }, 0, 1);
        panel.Controls.Add(host, 1, 1);
        panel.Controls.Add(new Label { Text = "TCP port", AutoSize = true }, 0, 2);
        panel.Controls.Add(port, 1, 2);
        panel.Controls.Add(new Label { Text = "Shared token", AutoSize = true }, 0, 3);
        panel.Controls.Add(token, 1, 3);
        var save = new Button { Text = "Save and connect", AutoSize = true };
        save.Click += (_, _) =>
        {
            var cfg = new Settings(host.Text.Trim(), (int)port.Value, token.Text);
            if (!ConfigStore.IsValid(cfg))
            {
                MessageBox.Show(this, "Enter a valid host and token (at least 16 characters).",
                    "Invalid configuration", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                ConfigStore.Save(cfg);
                Configuration = cfg;
                DialogResult = DialogResult.OK;
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Unable to save settings"); }
        };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        buttons.Controls.Add(save); buttons.Controls.Add(cancel);
        panel.Controls.Add(buttons, 1, 4);
        AcceptButton = save; CancelButton = cancel;
    }
}
