using Microsoft.Win32;
using System.Diagnostics;

namespace KeyboardBridge;

static class StartupSettings
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "WifiKeyboardBridge";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string command && command.Length > 0;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("Windows startup registry key unavailable.");
        if (!enabled) { key.DeleteValue(ValueName, throwOnMissingValue: false); return; }
        // A published executable is preferred; dotnet run cannot be registered reliably.
        string path = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine application executable.");
        if (string.Equals(Path.GetFileName(path), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Publish the standalone EXE before enabling startup.");
        key.SetValue(ValueName, "\"" + path + "\"", RegistryValueKind.String);
    }
}
