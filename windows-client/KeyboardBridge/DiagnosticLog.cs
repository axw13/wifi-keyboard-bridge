using System.Text;

namespace KeyboardBridge;

// Local rolling diagnostic log; intentionally never writes the shared token.
static class DiagnosticLog
{
    static readonly object sync = new();
    static readonly string directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WifiKeyboardBridge");
    static readonly string path = Path.Combine(directory, "bridge.log");
    public static string LogPath => path;

    public static void Write(string message)
    {
        try
        {
            lock (sync)
            {
                Directory.CreateDirectory(directory);
                // Bound disk usage to around 1 MiB per active log.
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                {
                    string archive = Path.Combine(directory, "bridge.previous.log");
                    File.Move(path, archive, true);
                }
                File.AppendAllText(path, $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { /* Logging must never disable input recovery. */ }
    }
}
