using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Net.Sockets;
using System.Windows.Forms;
using System.Threading;
using System.Drawing;

namespace KeyboardBridge;

record Settings(string Host, int Port, string Token, bool AutoDiscover = false);

static class Program
{
    [STAThread]
    static void Main()
    {
        using var oneInstance = new Mutex(true, @"Local\WifiKeyboardBridge_axw13", out bool firstInstance);
        if (!firstInstance) { MessageBox.Show("Wi-Fi Keyboard Bridge is already running. Check the system tray.", "Already running"); return; }
        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) =>
        {
            DiagnosticLog.Error("Unhandled UI exception: " + e.Exception);
            MessageBox.Show(e.Exception.Message, "Wi-Fi Keyboard Bridge error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            DiagnosticLog.Error("Fatal unhandled exception: " + e.ExceptionObject);
            DiagnosticLog.Flush();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            DiagnosticLog.Warn("Unobserved task exception: " + e.Exception.GetBaseException());
            e.SetObserved();
        };
        DiagnosticLog.Write($"Application starting. Version {Application.ProductVersion}, " +
            $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {Environment.Version}.");
        var cfg = ConfigStore.Load();
        if (cfg is not null)
            DiagnosticLog.Write($"Configuration: host {cfg.Host}, port {cfg.Port}, automatic detection {(cfg.AutoDiscover ? "on" : "off")} ({ConfigStore.Path}).");
        if (cfg is null)
        {
            using var setup = new FirstRunDialog();
            if (setup.ShowDialog() != DialogResult.OK) return;
            cfg = setup.Configuration;
        }
        if (cfg is not null) Application.Run(new BridgeContext(cfg));
    }
}

sealed class BridgeContext : ApplicationContext
{
    readonly NotifyIcon tray;
    readonly AntiIdleMover antiIdle = new();
    readonly System.Windows.Forms.Timer trayTimer = new() { Interval = 1000 };
    readonly Icon disconnectedIcon = TrayStatusIcons.Create(Color.Firebrick);
    readonly Icon localIcon = TrayStatusIcons.Create(Color.SeaGreen);
    readonly Icon remoteIcon = TrayStatusIcons.Create(Color.DeepSkyBlue);
    bool? lastConnectionState;
    readonly KeyboardHook hook;
    readonly MouseHook mouseHook;
    readonly RawMouseInput rawMouse;
    bool mouseRemote;
    int mouseToggleVk;
    bool linkedToggle;
    bool mouseHotkeyLatch;
    bool emergencyHotkeyLatch;
    readonly Transport network;
    readonly HashSet<int> held = new();
    readonly HashSet<int> physical = new();
    readonly HashSet<int> swallowed = new();
    // Keys whose key-down Windows received locally must also have their key-up passed locally.
    readonly HashSet<int> localDown = new();
    bool remote;
    bool exiting;
    bool hotkeyLatch;
    int toggleVk;
    int keyboardMods = 3, mouseMods = 3, emergencyVk = 0x79, emergencyMods = 3;
    string switchSequence = "";
    string recentKeys = "";
    long recentKeyAt;
    SettingsWindow? settingsWindow;
    double mouseSensitivity = 1.0;
    readonly string prefPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WifiKeyboardBridge", "preferences.json");
    readonly SynchronizationContext ui;

    public BridgeContext(Settings cfg)
    {
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        // The transport may report its disconnect after ExitThreadCore disposed the tray icon.
        network = new Transport(cfg, () => ui.Post(_ => { if (!exiting) { SwitchLocal(); ToggleMouseTo(false); } }, null));
        var preferences = UserPreferences.Load(prefPath);
        toggleVk = preferences.ToggleKey == 0x79 ? 0x7B : preferences.ToggleKey;
        mouseToggleVk = preferences.MouseToggleKey == 0x79 || preferences.MouseToggleKey == toggleVk ? 0x7A : preferences.MouseToggleKey;
        if (mouseToggleVk == toggleVk) mouseToggleVk = 0x78;
        keyboardMods = preferences.KeyboardMods;
        mouseMods = preferences.MouseMods;
        emergencyVk = preferences.EmergencyKey;
        emergencyMods = preferences.EmergencyMods;
        switchSequence = preferences.SwitchSequence ?? "";
        linkedToggle = preferences.Linked;
        mouseSensitivity = Math.Clamp(preferences.MouseSensitivity, 0.25, 3.0);
        network.SetMouseTuning(mouseSensitivity, preferences.MouseUpdateMs);
        network.SetAdaptiveMouse(preferences.AdaptiveMouse);
        antiIdle.Enabled = preferences.AntiIdle;
        tray = new NotifyIcon { Icon = disconnectedIcon, Visible = true, Text = "Wi-Fi Bridge: Connecting..." };
        trayTimer.Tick += (_, _) => { UpdateTray(); network.UpdateAntiIdleState(antiIdle.Enabled, remote || mouseRemote); };
        trayTimer.Start();
        UpdateTray();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Switch keyboard", null, (_, _) => Toggle());
        menu.Items.Add("Switch mouse", null, (_, _) => ToggleMouse());
        menu.Items.Add("Emergency LOCAL (Ctrl+Alt+F10)", null, (_, _) => EmergencyLocal("tray menu"));
        menu.Items.Add("Open log folder", null, (_, _) => OpenLogFolder());
        menu.Items.Add("Settings && Connection Statistics...", null, (_, _) => ShowSettings(cfg));
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Toggle();
        hook = new KeyboardHook(HandleKey);
        mouseHook = new MouseHook(HandleMouse);
        rawMouse = new RawMouseInput(() => mouseRemote && network.Connected,
            (dx, dy) => network.SendMouseMovement(dx, dy));
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    static void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e) =>
        DiagnosticLog.Write("Windows power event: " + e.Mode);
    static void OnNetworkAvailabilityChanged(object? sender, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e) =>
        DiagnosticLog.Warn("Windows network availability changed: " + (e.IsAvailable ? "available" : "unavailable"));

    bool HandleKey(int vk, bool pressed, bool injected)
    {
        if (injected) return false;
        bool repeatedKeydown = pressed && physical.Contains(vk);
        if (pressed) physical.Add(vk); else physical.Remove(vk);
        bool ctrl = physical.Contains(0xA2) || physical.Contains(0xA3) || physical.Contains(0x11);
        bool alt = physical.Contains(0xA4) || physical.Contains(0xA5) || physical.Contains(0x12);
        int mods = (ctrl ? 1 : 0) | (alt ? 2 : 0) |
            (physical.Contains(0x10) || physical.Contains(0xA0) || physical.Contains(0xA1) ? 4 : 0) |
            (physical.Contains(0x5B) || physical.Contains(0x5C) ? 8 : 0);
        if (settingsWindow?.IsRecording == true) return settingsWindow.TryRecord(vk, pressed, mods);
        if (pressed && ((vk == emergencyVk && mods == emergencyMods) || (vk == 0x79 && mods == 3)))
        {
            if (!emergencyHotkeyLatch) { emergencyHotkeyLatch = true; EmergencyLocal("emergency shortcut"); }
            swallowed.Add(vk);
            return true;
        }
        if (pressed && ((vk == toggleVk && mods == keyboardMods) ||
            (!linkedToggle && vk == mouseToggleVk && mods == mouseMods)))
        {
            if (!hotkeyLatch && !mouseHotkeyLatch)
            {
                if (vk == toggleVk && mods == keyboardMods) { hotkeyLatch = true; Toggle(); if (linkedToggle) ToggleMouseTo(remote); }
                else { mouseHotkeyLatch = true; ToggleMouse(); }
            }
            swallowed.Add(vk);
            return true;
        }
        if ((vk == emergencyVk || vk == 0x79) && !pressed) emergencyHotkeyLatch = false;
        if (vk == toggleVk && !pressed) hotkeyLatch = false;
        if (vk == mouseToggleVk && !pressed) mouseHotkeyLatch = false;
        // Sequence matching observes only new unmodified letter keydowns.
        // It never delays or hides user input: typed characters reach their owner.
        if (pressed && !repeatedKeydown && vk >= 0x41 && vk <= 0x5A && mods == 0)
        {
            long now = Environment.TickCount64;
            recentKeys = now - recentKeyAt > 2000 ? "" : recentKeys;
            recentKeyAt = now;
            recentKeys = (recentKeys + (char)('a' + vk - 0x41));
            if (recentKeys.Length > 32) recentKeys = recentKeys[^32..];
            if (switchSequence.Length >= 4 && recentKeys.EndsWith(switchSequence, StringComparison.Ordinal))
            {
                recentKeys = "";
                // Defer the mode change until this final typed key is handled
                // in its original mode; do not redirect it mid-keydown.
                ui.Post(_ => {
                    if (remote || mouseRemote) EmergencyLocal("switch sequence");
                    else if (network.Connected) { Toggle(); ToggleMouseTo(true); }
                }, null);
            }
        }
        else if (pressed && mods != 0) recentKeys = "";
        // Lock keys must reach Windows as well as the remote HID device: Windows owns
        // the physical keyboard LEDs and its Num Lock state controls keypad VK translation.
        // This is a deliberate exception to local suppression while in REMOTE mode.
        // The host PC's LED is not feedback from the remote PC, so lock states can diverge.
        if (remote && (vk == 0x90 || vk == 0x91 || vk == 0x14))
        {
            if (!network.Connected) { EmergencyLocal("lock key while disconnected"); return false; }
            if (pressed) held.Add(vk); else held.Remove(vk);
            if (!SendState()) EmergencyLocal("send queue full");
            return false;
        }
        // Preserve key-up ownership across LOCAL -> REMOTE transitions.
        // In particular, Ctrl and Alt were pressed locally before the toggle hotkey fired.
        if (!pressed && localDown.Remove(vk)) return false;
        // Consume corresponding key-up for keys whose key-down was forwarded.
        if (!pressed && swallowed.Remove(vk)) { held.Remove(vk); SendState(); return true; }
        if (!remote) {
            if (pressed && !swallowed.Contains(vk)) localDown.Add(vk);
            return false;
        }
        // Auto-repeated key-downs that started in LOCAL must not migrate to REMOTE.
        if (localDown.Contains(vk)) return false;
        if (!network.Connected) { EmergencyLocal("key while disconnected"); return false; }
        int usage = HidMap.Key(vk);
        if (usage == 0) return false; // Don't suppress unsupported keys
        if (pressed) { held.Add(vk); swallowed.Add(vk); }
        else held.Remove(vk);
        if (!SendState()) { DiagnosticLog.Warn("Key frame could not be queued; switching keyboard LOCAL."); SwitchLocal(); return false; }
        return true;
    }

    void EmergencyLocal(string reason)
    {
        if (remote || mouseRemote) DiagnosticLog.Warn("Emergency LOCAL: " + reason);
        SwitchLocal();
        ToggleMouseTo(false);
        // Explicitly release remote HID state even if input-mode flags were already LOCAL.
        network.Send(0, Array.Empty<int>());
        network.SendMouse(0, 0, 0, 0);
    }

    bool HandleMouse(int message, int x, int y, int wheel)
    {
        if (!mouseRemote) return false;
        if (!network.Connected) { EmergencyLocal("mouse while disconnected"); return false; }
        return mouseHook.Forward(message, x, y, wheel, (b, dx, dy, w) => network.SendMouse(b, dx, dy, w));
    }
    void ToggleMouse() => ToggleMouseTo(!mouseRemote);
    void ToggleMouseTo(bool enabled)
    {
        if (enabled && !network.Connected) return;
        if (mouseRemote != enabled) DiagnosticLog.Write("Mouse " + (enabled ? "REMOTE" : "LOCAL"));
        mouseRemote = enabled;
        mouseHook.ResetTracking();
        if (!enabled)
        {
            network.DiscardPendingMouseMovement();
            network.SendMouse(0, 0, 0, 0);
        }
        UpdateTray();
    }
    void UpdateTray()
    {
        if (exiting) return;
        bool online = network.Connected;
        // Connection changes are logged by the transport with their cause.
        lastConnectionState = online;
        tray.Icon = !online ? disconnectedIcon : remote || mouseRemote ? remoteIcon : localIcon;
        tray.Text = "Bridge: " + (!online ? "DISCONNECTED" : "CONNECTED") + " | K " + (remote ? "R" : "L") + " M " + (mouseRemote ? "R" : "L");
    }
    bool SendState()
    {
        int modifiers = 0;
        var keys = new List<int>();
        foreach (int vk in held)
        {
            int usage = HidMap.Key(vk);
            if (usage >= 0xE0 && usage <= 0xE7) modifiers |= 1 << (usage - 0xE0);
            else if (usage != 0 && !keys.Contains(usage)) keys.Add(usage);
        }
        keys.Sort();
        return network.Send(modifiers, keys.Take(6).ToArray());
    }
    void Toggle()
    {
        if (remote) { SwitchLocal(); return; }
        if (!network.Connected) { MessageBox.Show("NodeMCU not connected. Check IP, Wi-Fi and token."); return; }
        held.Clear();
        remote = true;
        DiagnosticLog.Write("Keyboard REMOTE");
        UpdateTray();
        SendState();
    }
    void SwitchLocal()
    {
        if (!remote) return;
        remote = false;
        DiagnosticLog.Write("Keyboard LOCAL");
        held.Clear();
        network.Send(0, Array.Empty<int>());
        UpdateTray();
    }
    void ShowSettings(Settings cfg)
    {
        if (settingsWindow != null && !settingsWindow.IsDisposed) { settingsWindow.Activate(); return; }
        settingsWindow = new SettingsWindow(cfg, network, () => remote, () => toggleVk, vk =>
        {
            if (vk == 0x79 || (!linkedToggle && vk == mouseToggleVk))
                throw new ArgumentException("Shortcut conflicts with mouse or reserved emergency Ctrl+Alt+F10.");
            toggleVk = vk;
            hotkeyLatch = false;
            UserPreferences.Save(prefPath, new UserPreferences(toggleVk, mouseToggleVk, linkedToggle, mouseSensitivity, network.ManualMouseInterval, network.AdaptiveMouse, antiIdle.Enabled, keyboardMods, mouseMods, emergencyVk, emergencyMods, switchSequence));
        }, newSettings =>
        {
            EmergencyLocal("connection settings changed");
            DiagnosticLog.Write("Connection settings changed; restarting.");
            ConfigStore.Save(newSettings);
            Application.Restart();
        });
        settingsWindow.SetMouseSettings(() => mouseRemote, () => mouseToggleVk, () => linkedToggle, (key, linked) =>
        {
            if (key == 0x79 || (!linked && key == toggleVk))
                throw new ArgumentException("Mouse shortcut conflicts with keyboard or reserved emergency Ctrl+Alt+F10.");
            mouseToggleVk = key;
            linkedToggle = linked;
            mouseHotkeyLatch = false;
            UserPreferences.Save(prefPath, new UserPreferences(toggleVk, mouseToggleVk, linkedToggle, mouseSensitivity, network.ManualMouseInterval, network.AdaptiveMouse, antiIdle.Enabled, keyboardMods, mouseMods, emergencyVk, emergencyMods, switchSequence));
        });
        settingsWindow.SetMouseTuning(() => mouseSensitivity, () => network.MouseUpdateMs, (sensitivity, interval) =>
        {
            mouseSensitivity = sensitivity;
            network.SetMouseTuning(sensitivity, interval);
            UserPreferences.Save(prefPath, new UserPreferences(toggleVk, mouseToggleVk, linkedToggle, sensitivity, interval, network.AdaptiveMouse, antiIdle.Enabled, keyboardMods, mouseMods, emergencyVk, emergencyMods, switchSequence));
        });
        settingsWindow.SetAdaptiveMouse(network.AdaptiveMouse, enabled =>
        {
            network.SetAdaptiveMouse(enabled);
            UserPreferences.Save(prefPath, new UserPreferences(toggleVk, mouseToggleVk, linkedToggle, mouseSensitivity, network.ManualMouseInterval, enabled, antiIdle.Enabled, keyboardMods, mouseMods, emergencyVk, emergencyMods, switchSequence));
        });
        settingsWindow.SetAntiIdle(antiIdle.Enabled, () => antiIdle.Status(remote, mouseRemote, network.Connected), enabled =>
        {
            antiIdle.Enabled = enabled;
            network.UpdateAntiIdleState(enabled, remote || mouseRemote);
            UserPreferences.Save(prefPath, new UserPreferences(toggleVk, mouseToggleVk, linkedToggle, mouseSensitivity, network.ManualMouseInterval, network.AdaptiveMouse, enabled, keyboardMods, mouseMods, emergencyVk, emergencyMods, switchSequence));
        });
        settingsWindow.SetShortcutRecording(
            () => (toggleVk, keyboardMods), () => (mouseToggleVk, mouseMods), () => (emergencyVk, emergencyMods),
            (key, mods) => {
                ValidateShortcut(key, mods, mouseToggleVk, mouseMods, emergencyVk, emergencyMods);
                toggleVk = key; keyboardMods = mods; SavePrefs();
            },
            (key, mods) => {
                ValidateShortcut(key, mods, toggleVk, keyboardMods, emergencyVk, emergencyMods);
                mouseToggleVk = key; mouseMods = mods; SavePrefs();
            },
            (key, mods) => {
                ValidateShortcut(key, mods, toggleVk, keyboardMods, mouseToggleVk, mouseMods);
                emergencyVk = key; emergencyMods = mods; SavePrefs();
            },
            switchSequence, value => {
                if (value.Length != 0 && (value.Length < 4 || value.Length > 32 || value.Any(c => c < 'a' || c > 'z')))
                    throw new ArgumentException("Use 4–32 lowercase letters, or leave empty to disable.");
                switchSequence = value; recentKeys = ""; SavePrefs();
            });
        settingsWindow.Show();
    }
    static void ValidateShortcut(int key, int mods, int key1, int mods1, int key2, int mods2)
    {
        if (mods == 0 || key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5)
            throw new ArgumentException("A shortcut needs a non-modifier key and at least one modifier.");
        if ((key == 0x79 && mods == 3) || (key == key1 && mods == mods1) || (key == key2 && mods == mods2))
            throw new ArgumentException("This key combination is already assigned.");
    }
    void SavePrefs() => UserPreferences.Save(prefPath,
        new UserPreferences(toggleVk, mouseToggleVk, linkedToggle, mouseSensitivity,
            network.ManualMouseInterval, network.AdaptiveMouse, antiIdle.Enabled,
            keyboardMods, mouseMods, emergencyVk, emergencyMods, switchSequence));

    static void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(DiagnosticLog.LogDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DiagnosticLog.LogDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Unable to open log folder"); }
    }

    protected override void ExitThreadCore()
    {
        EmergencyLocal("application exit");
        exiting = true;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        trayTimer.Stop();
        trayTimer.Dispose();
        DiagnosticLog.Write("Application exiting.");
        settingsWindow?.Close();
        hook.Dispose();
        rawMouse.Dispose();
        mouseHook.Dispose();
        network.Dispose();
        tray.Visible = false;
        tray.Dispose();
        disconnectedIcon.Dispose();
        localIcon.Dispose();
        remoteIcon.Dispose();
        DiagnosticLog.Flush();
        base.ExitThreadCore();
    }
}

static class HidMap
{
    public static int Key(int vk)
    {
        if (vk >= 0x41 && vk <= 0x5A) return vk - 0x41 + 4;
        if (vk >= 0x31 && vk <= 0x39) return vk - 0x31 + 30;
        if (vk == 0x30) return 39;
        if (vk >= 0x70 && vk <= 0x7B) return vk - 0x70 + 0x3A;
        // Dedicated keypad virtual keys do not share the top-row number usages.
        if (vk >= 0x61 && vk <= 0x69) return vk - 0x61 + 0x1E; // Keypad 1-9 as number-row digits
        if (vk == 0x60) return 0x27; // Keypad 0 as number-row 0
        return vk switch {
            0x0D => 0x28, 0x1B => 0x29, 0x08 => 0x2A, 0x09 => 0x2B, 0x20 => 0x2C,
            0xBD => 0x2D, 0xBB => 0x2E, 0xDB => 0x2F, 0xDD => 0x30, 0xDC => 0x31,
            0xBA => 0x33, 0xDE => 0x34, 0xC0 => 0x35, 0xBC => 0x36, 0xBE => 0x37, 0xBF => 0x38,
            0x6A => 0x55, 0x6B => 0x57, 0x6C => 0x85, // Keypad *, +, separator
            0x6D => 0x56, 0x6E => 0x37, 0x6F => 0x54, // Keypad -, decimal, /
            0x14 => 0x39, 0x2C => 0x46, 0x91 => 0x47, 0x13 => 0x48, 0x2D => 0x49, 0x24 => 0x4A,
            0x21 => 0x4B, 0x2E => 0x4C, 0x23 => 0x4D, 0x22 => 0x4E, 0x27 => 0x4F,
            0x25 => 0x50, 0x28 => 0x51, 0x26 => 0x52, 0x90 => 0x53,
            0xA2 or 0x11 => 0xE0, 0xA0 or 0x10 => 0xE1, 0xA4 or 0x12 => 0xE2,
            0x5B => 0xE3, 0xA3 => 0xE4, 0xA1 => 0xE5, 0xA5 => 0xE6, 0x5C => 0xE7,
            _ => 0
        };
    }
}

sealed class KeyboardHook : IDisposable
{
    readonly Func<int,bool,bool,bool> handler;
    readonly HookProc proc;
    readonly IntPtr handle;
    delegate IntPtr HookProc(int code, IntPtr msg, IntPtr data);
    [StructLayout(LayoutKind.Sequential)]
    struct KbdData { public uint vk, scan, flags, time; public IntPtr extra; }
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc cb, IntPtr h, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr msg, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    public KeyboardHook(Func<int,bool,bool,bool> handler)
    {
        this.handler = handler;
        proc = Callback;
        handle = SetWindowsHookEx(13, proc, GetModuleHandle(null), 0);
        if (handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
    }
    IntPtr Callback(int code, IntPtr msg, IntPtr data)
    {
        if (code >= 0 && (msg.ToInt32() is 0x100 or 0x101 or 0x104 or 0x105))
        {
            var k = Marshal.PtrToStructure<KbdData>(data);
            bool pressed = msg.ToInt32() is 0x100 or 0x104;
            // Num Lock translation happens on Windows BEFORE the low-level hook.
            // Detect physical non-extended keypad scan codes, even when Windows reports arrows.
            int vk = (int)k.vk;
            if ((k.flags & 0x01) == 0 && Control.IsKeyLocked(Keys.NumLock))
            {
                vk = (int)k.scan switch
                {
                    0x52 => 0x60, 0x4F => 0x61, 0x50 => 0x62, 0x51 => 0x63,
                    0x4B => 0x64, 0x4C => 0x65, 0x4D => 0x66,
                    0x47 => 0x67, 0x48 => 0x68, 0x49 => 0x69,
                    0x53 => 0x6E,
                    _ => vk
                };
            }
            try { if (handler(vk, pressed, (k.flags & 0x10) != 0)) return (IntPtr)1; }
            catch { /* Fail open to local keyboard on hook error */ }
        }
        return CallNextHookEx(handle, code, msg, data);
    }
    public void Dispose() => UnhookWindowsHookEx(handle);
}

sealed class Transport : IDisposable
{
    readonly Settings cfg;
    string fallbackHost;
    string? activeHost;
    public string? ActiveHost => Volatile.Read(ref activeHost);
    public string FallbackHost => Volatile.Read(ref fallbackHost);
    readonly Action disconnected;
    readonly BlockingCollection<string> queue = new(256);
    readonly CancellationTokenSource cts = new();
    readonly SemaphoreSlim wake = new(0);
    volatile bool connected;
    long sentReports;
    readonly object motionLock = new();
    int pendingX, pendingY, pendingWheel, mouseButtons;
    long lastMouseFlush;
    long mouseReports;
    long mouseIntervalCount;
    long mouseIntervalTotal;
    long mouseIntervalMax;
    long mouseLastSentTick;
    readonly object timingLock = new();
    readonly Queue<double> schedulerIntervals = new();
    readonly Queue<double> schedulerLateness = new();
    readonly Queue<double> activeReportIntervals = new();
    readonly Queue<double> rttHistory = new();
    readonly Queue<double> picoHistory = new();
    long picoLastAckTick;
    int picoProbeId;
    int picoTimeouts;
    public string PicoDiagnostics()
    {
        lock (timingLock)
        {
            if (!Connected) return "Pico UART: disconnected";
            if (picoHistory.Count == 0) return $"Pico UART: awaiting ACK (or return wire/firmware); timeouts {Volatile.Read(ref picoTimeouts)}";
            return $"NodeMCU ↔ Pico UART probe avg/P95/max: {TimingSummary(picoHistory)} | timeouts {Volatile.Read(ref picoTimeouts)}";
        }
    }
    public double[] RttHistory() { lock (timingLock) return rttHistory.ToArray(); }
    bool adaptiveMouse;
    int manualMouseInterval = 16;
    long lastAdaptiveAdjustment;
    public bool AdaptiveMouse => Volatile.Read(ref adaptiveMouse);
    public int ManualMouseInterval => Volatile.Read(ref manualMouseInterval);
    public void SetAdaptiveMouse(bool enabled)
    {
        Volatile.Write(ref adaptiveMouse, enabled);
        Volatile.Write(ref mouseIntervalMs, Volatile.Read(ref manualMouseInterval));
        Interlocked.Exchange(ref lastAdaptiveAdjustment, Environment.TickCount64);
    }
    void UpdateAdaptiveMouse()
    {
        if (!AdaptiveMouse || !Connected || BenchmarkRunning) return;
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref lastAdaptiveAdjustment) < 8000) return;
        int target = ManualMouseInterval;
        // Conservative: only back off from the chosen target when movement backlog
        // persists. Never choose a rate faster than the user selected.
        int backlog;
        lock (motionLock) backlog = Math.Max(Math.Abs(pendingX), Math.Abs(pendingY));
        int current = MouseUpdateMs;
        // Large backlog calls for maintaining responsiveness, not slowing the sender.
        // Back off only when scheduler lateness has remained elevated, then recover slowly.
        double p95Lateness;
        lock (timingLock)
        {
            var values = schedulerLateness.OrderBy(v => v).ToArray();
            p95Lateness = values.Length >= 32 ? values[(int)Math.Ceiling(values.Length * .95) - 1] : 0;
        }
        int next = backlog < 32 && p95Lateness >= 12 ? Math.Min(33, current + 2) :
            backlog >= 32 || p95Lateness <= 4 ? Math.Max(target, current - 1) : current;
        if (next != current) Volatile.Write(ref mouseIntervalMs, next);
        Interlocked.Exchange(ref lastAdaptiveAdjustment, now);
    }
    const int TimingWindow = 256;
    static void AddTiming(Queue<double> samples, double value)
    {
        if (samples.Count == TimingWindow) samples.Dequeue();
        samples.Enqueue(value);
    }
    static string TimingSummary(Queue<double> samples)
    {
        if (samples.Count == 0) return "—";
        var sorted = samples.OrderBy(v => v).ToArray();
        return $"{sorted.Average():F1} / {sorted[(int)Math.Ceiling(sorted.Length * .95) - 1]:F1} / {sorted[^1]:F1} ms";
    }
    public string SchedulerDiagnostics()
    {
        lock (timingLock)
            return $"Scheduler interval avg/P95/max: {TimingSummary(schedulerIntervals)} | lateness avg/P95/max: {TimingSummary(schedulerLateness)}";
    }
    void RecordSchedulerTick(long now, int targetMs)
    {
        if (lastMouseFlush == 0) return;
        var interval = now - lastMouseFlush;
        if (interval > 100) return; // Exclude suspension or long worker pauses from active pacing window
        lock (timingLock)
        {
            AddTiming(schedulerIntervals, interval);
            AddTiming(schedulerLateness, Math.Max(0, interval - targetMs));
        }
    }
    int espRssi = int.MinValue;
    long espHeap = -1, espFrames = -1, espBytes = -1;
    public string MouseDiagnostics()
    {
        long n = Interlocked.Read(ref mouseIntervalCount);
        double avg = n > 0 ? Interlocked.Read(ref mouseIntervalTotal) / (double)n : 0;
        long max = Interlocked.Read(ref mouseIntervalMax);
        int x, y;
        lock (motionLock) { x = pendingX; y = pendingY; }
        string intervals;
        lock (timingLock) intervals = TimingSummary(activeReportIntervals);
        return $"Mouse reports: {Interlocked.Read(ref mouseReports)} | active report gap avg/P95/max: {intervals} | pending X:{x} Y:{y}";
    }
    public string EspDiagnostics()
    {
        int rssi = Volatile.Read(ref espRssi);
        if (!Connected) return "NodeMCU: disconnected";
        if (rssi == int.MinValue) return "NodeMCU: telemetry unavailable (update firmware)";
        return $"NodeMCU: RSSI {rssi} dBm | free heap {Interlocked.Read(ref espHeap) / 1024.0:F1} KiB | UART frames {Interlocked.Read(ref espFrames)} | bytes {Interlocked.Read(ref espBytes)}";
    }
    int mouseIntervalMs = 16;
    double mouseSensitivity = 1.0;
    double fractionalX, fractionalY;
    public int MouseUpdateMs => Volatile.Read(ref mouseIntervalMs);
    public void SetMouseTuning(double sensitivity, int intervalMs)
    {
        lock (motionLock)
        {
            mouseSensitivity = Math.Clamp(sensitivity, 0.25, 3.0);
            Volatile.Write(ref manualMouseInterval, Math.Clamp(intervalMs, 8, 33));
            Volatile.Write(ref mouseIntervalMs, Math.Clamp(intervalMs, 8, 33));
            fractionalX = fractionalY = 0;
        }
    }
    long reconnects;
    long latencyTenths = -1;
    long pingSentTimestamp;
    readonly object benchmarkLock = new();
    readonly List<double> benchmarkSamples = new();
    long benchmarkEndTick;
    int benchmarkAttempts;
    int benchmarkTimeouts;
    public bool BenchmarkRunning => Environment.TickCount64 < Interlocked.Read(ref benchmarkEndTick);
    public void StartBenchmark()
    {
        if (!Connected) throw new InvalidOperationException("NodeMCU is disconnected.");
        lock (benchmarkLock)
        {
            benchmarkSamples.Clear();
            benchmarkAttempts = 0;
            benchmarkTimeouts = 0;
            Interlocked.Exchange(ref benchmarkEndTick, Environment.TickCount64 + 10000);
        }
    }
    public string BenchmarkStatus()
    {
        lock (benchmarkLock)
        {
            if (benchmarkSamples.Count == 0)
                return BenchmarkRunning ? "Collecting samples..." : "No samples. Run benchmark while connected.";
            var sorted = benchmarkSamples.OrderBy(x => x).ToArray();
            var avg = sorted.Average();
            var median = sorted[sorted.Length / 2];
            var p95 = sorted[(int)Math.Ceiling(sorted.Length * .95) - 1];
            return $"{(BenchmarkRunning ? "Running" : "Finished")}: {sorted.Length} samples, avg {avg:F1} ms, median {median:F1} ms, p95 {p95:F1} ms, max {sorted[^1]:F1} ms; unanswered: {benchmarkTimeouts}";
        }
    }
    public double? RoundTripMs => Interlocked.Read(ref latencyTenths) is var value && value >= 0 ? value / 10.0 : null;
    DateTimeOffset? connectedSince;
    public long SentReports => Interlocked.Read(ref sentReports);
    public long Reconnects => Interlocked.Read(ref reconnects);
    public DateTimeOffset? ConnectedSince => connectedSince;
    string last = "S,0,0,0,0,0,0,0";
    public bool Connected => connected;
    public Transport(Settings cfg, Action disconnected)
    {
        this.cfg = cfg;
        fallbackHost = cfg.Host;
        this.disconnected = disconnected;
        _ = Task.Run(Run);
    }
    // Distinct HID frame: bypass motion coalescing and mouse sensitivity. 
    // Enqueued on the transport worker; never moves the local Windows cursor.
    bool? idleEnabledSent;
    bool? idlePausedSent;
    public void UpdateAntiIdleState(bool enabled, bool paused)
    {
        if (!Connected) { idleEnabledSent = null; idlePausedSent = null; return; }
        if (idleEnabledSent != enabled && Enqueue(enabled ? "IDLE,ON" : "IDLE,OFF"))
            idleEnabledSent = enabled;
        if (idlePausedSent != paused && Enqueue(paused ? "IDLE,PAUSE" : "IDLE,RESUME"))
            idlePausedSent = paused;
    }

    public bool SendMouseMovement(int dx, int dy)
    {
        if (!Connected) return false;
        lock (motionLock)
        {
            // Preserve fractional movement at low sensitivity rather than truncating each event.
            double sx = dx * mouseSensitivity + fractionalX;
            double sy = dy * mouseSensitivity + fractionalY;
            int ix = (int)Math.Truncate(sx), iy = (int)Math.Truncate(sy);
            fractionalX = sx - ix;
            fractionalY = sy - iy;
            pendingX = Math.Clamp(pendingX + ix, -1024, 1024);
            pendingY = Math.Clamp(pendingY + iy, -1024, 1024);
        }
        return true;
    }
    public bool SendMouse(int buttons, int dx, int dy, int wheel)
    {
        if (!Connected) return false;
        lock (motionLock)
        {
            if (dx != 0 || dy != 0)
            {
                // Bound pending movement so it cannot produce minutes of stale input.
                pendingX = Math.Clamp(pendingX + dx, -254, 254);
                pendingY = Math.Clamp(pendingY + dy, -254, 254);
                return true;
            }
            mouseButtons = buttons;
            // Button presses/releases and wheel remain immediate and reliable.
            if (wheel != 0) pendingWheel = Math.Clamp(pendingWheel + wheel, -127, 127);
            var packet = $"M,{buttons},0,0,{Math.Clamp(pendingWheel,-127,127)}";
            if (!Enqueue(packet)) return false;
            pendingWheel = 0;
            return true;
        }
    }
    // Emit at most one HID movement report per interval. Bursting several
    // reports at once makes pointer motion appear to jump even when RTT is low.
    async Task FlushMouseMovementAsync(StreamWriter writer)
    {
        int dx, dy, buttons;
        lock (motionLock)
        {
            dx = Math.Clamp(pendingX, -127, 127);
            dy = Math.Clamp(pendingY, -127, 127);
            buttons = mouseButtons;
            pendingX -= dx;
            pendingY -= dy;
        }
        if (dx != 0 || dy != 0)
        {
            await writer.WriteLineAsync($"M,{buttons},{dx},{dy},0");
            Interlocked.Increment(ref mouseReports);
            long now = NowMs();
            long previous = Interlocked.Exchange(ref mouseLastSentTick, now);
            if (previous != 0 && now - previous <= 100)
            {
                // Gaps above 100 ms are idle pauses, not movement pacing jitter.
                long interval = now - previous;
                lock (timingLock) AddTiming(activeReportIntervals, interval);
                Interlocked.Increment(ref mouseIntervalCount);
                Interlocked.Add(ref mouseIntervalTotal, interval);
                long seen;
                while (interval > (seen = Interlocked.Read(ref mouseIntervalMax)) &&
                    Interlocked.CompareExchange(ref mouseIntervalMax, interval, seen) != seen) { }
            }
        }
    }
    public void DiscardPendingMouseMovement()
    {
        lock (motionLock)
        {
            pendingX = pendingY = pendingWheel = mouseButtons = 0;
            fractionalX = fractionalY = 0;
        }
    }
    void ClearMouseMotion() => DiscardPendingMouseMovement();
    public bool Send(int mods, IReadOnlyList<int> keys)
    {
        var six = new int[6];
        for (int i = 0; i < Math.Min(6, keys.Count); i++) six[i] = keys[i];
        string frame = "S," + mods + "," + string.Join(",", six);
        Volatile.Write(ref last, frame);
        return Enqueue(frame);
    }
    // Stopwatch-based milliseconds. Environment.TickCount64 only advances in ~15.6 ms
    // steps on Windows, which quantised mouse pacing to 16/32 ms regardless of the setting.
    static long NowMs() => System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency;
    static double ElapsedMs(long stopwatchTimestamp) =>
        (System.Diagnostics.Stopwatch.GetTimestamp() - stopwatchTimestamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    const int PingIntervalMs = 500;
    const int StallThresholdMs = 250;  // PONG slower than this counts as a link stall
    // Unacknowledged bytes to the NodeMCU above this mean the PC -> NodeMCU direction is stuck
    // (normally only a frame or two is in flight). Mouse motion is held only then.
    const int ForwardBacklogBytes = 200;
    // No PONG for this long: drop the socket and reconnect. Replies can be lost for several
    // seconds while input still reaches the NodeMCU, so this only catches a NodeMCU that is gone.
    const int DeadLinkMs = 10000;

    // Connects within timeoutMs, or returns null so the caller can fall back to mDNS.
    async Task<TcpClient?> TryConnectAsync(string host, int port, int timeoutMs)
    {
        var socket = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        timeout.CancelAfter(timeoutMs);
        try { await socket.ConnectAsync(host, port, timeout.Token); return socket; }
        catch (Exception) when (!cts.IsCancellationRequested) { socket.Dispose(); return null; }
        catch { socket.Dispose(); throw; }
    }

    async Task Run()
    {
        while (!cts.IsCancellationRequested)
        {
            bool timerRaised = false;
            string reason = "cancelled";
            try
            {
                string target = Volatile.Read(ref fallbackHost);
                string source = "saved host";
                var connectWatch = System.Diagnostics.Stopwatch.StartNew();
                // Try the last authenticated address first; mDNS costs 1.5-3 s per reconnect.
                TcpClient? attempt = cfg.AutoDiscover ? await TryConnectAsync(target, cfg.Port, 1000) : null;
                if (attempt is null && cfg.AutoDiscover)
                {
                    string? discovered = await MdnsDiscovery.ResolveAsync(cts.Token);
                    if (discovered != null)
                    {
                        target = discovered;
                        source = "mDNS";
                        DiagnosticLog.Write("mDNS resolved NodeMCU at " + discovered);
                    }
                    else DiagnosticLog.Warn("Saved host and mDNS both unavailable; retrying " + target);
                }
                if (attempt is null)
                {
                    attempt = new TcpClient { NoDelay = true };
                    try { await attempt.ConnectAsync(target, cfg.Port, cts.Token); }
                    catch { attempt.Dispose(); throw; }
                }
                using var socket = attempt;
                long connectMs = connectWatch.ElapsedMilliseconds;
                var stream = socket.GetStream();
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                await writer.WriteLineAsync("AUTH " + cfg.Token);
                string? authReply;
                try { authReply = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3), cts.Token); }
                catch (TimeoutException) { throw new IOException("Authentication timed out (no reply within 3 s)"); }
                if (authReply == "ERR") throw new IOException("Authentication rejected by NodeMCU (token mismatch?)");
                if (authReply != "OK") throw new IOException("Authentication failed (reply: " + (authReply ?? "connection closed") + ")");
                DiagnosticLog.Write($"Connected to {target}:{cfg.Port} via {source}; TCP connect {connectMs} ms, authenticated {connectWatch.ElapsedMilliseconds} ms.");
                // Only trust and store a discovered address after shared-token authentication.
                Volatile.Write(ref activeHost, target);
                if (cfg.AutoDiscover && target != Volatile.Read(ref fallbackHost))
                {
                    Volatile.Write(ref fallbackHost, target);
                    try { ConfigStore.Save(cfg with { Host = target }); }
                    catch (Exception ex) { DiagnosticLog.Write("Unable to save discovered NodeMCU IP: " + ex.GetType().Name); }
                }
                timerRaised = TimerResolution.Begin();
                connected = true;
                pongSeen = false;
                Volatile.Write(ref espRssi, int.MinValue);
                connectedSince = DateTimeOffset.UtcNow;
                Interlocked.Exchange(ref latencyTenths, -1);
                ResetLinkSummary();
                var readReplies = Task.Run(async () =>
                {
                    while (!cts.IsCancellationRequested)
                    {
                        var response = await reader.ReadLineAsync(cts.Token);
                        if (response == null) throw new IOException("Connection closed by NodeMCU");
                        if (response.StartsWith("PICO,"))
                        {
                            var parts = response.Split(',');
                            if (parts.Length == 3 && int.TryParse(parts[1], out int ackId) &&
                                long.TryParse(parts[2], out long microseconds) &&
                                ackId == Volatile.Read(ref picoProbeId) && microseconds >= 0 && microseconds <= 500000)
                            {
                                Interlocked.Exchange(ref picoLastAckTick, Environment.TickCount64);
                                lock (timingLock) AddTiming(picoHistory, microseconds / 1000.0);
                            }
                        }
                        else if (response.StartsWith("STAT,"))
                        {
                            var fields = response.Split(',');
                            if (fields.Length == 5 && int.TryParse(fields[1], out int rssi) &&
                                long.TryParse(fields[2], out long heap) &&
                                long.TryParse(fields[3], out long frames) && long.TryParse(fields[4], out long bytes))
                            {
                                Volatile.Write(ref espRssi, rssi);
                                Interlocked.Exchange(ref espHeap, heap);
                                Interlocked.Exchange(ref espFrames, frames);
                                Interlocked.Exchange(ref espBytes, bytes);
                            }
                        }
                        else if (response.StartsWith("GAP,"))
                        {
                            if (long.TryParse(response.AsSpan(4), out long gap)) ResolveStall(gap);
                        }
                        else if (response.StartsWith("DIAG,"))
                        {
                            Volatile.Write(ref firmwareDiag, response[5..]);
                            DiagnosticLog.Write("Firmware: " + response[5..]);
                        }
                        else if (response == "PONG")
                        {
                            var t = Interlocked.Exchange(ref pingSentTimestamp, 0);
                            if (t != 0)
                            {
                                pongSeen = true;
                                var ms = ElapsedMs(t);
                                Interlocked.Exchange(ref latencyTenths, (long)Math.Round(ms * 10));
                                lock (timingLock) AddTiming(rttHistory, ms);
                                if (BenchmarkRunning) lock (benchmarkLock) benchmarkSamples.Add(ms);
                                RecordPong(ms);
                            }
                        }
                    }
                }, cts.Token);
                long lastPing = 0;
                lastMouseFlush = NowMs();
                lock (timingLock) { schedulerIntervals.Clear(); schedulerLateness.Clear(); activeReportIntervals.Clear(); rttHistory.Clear(); }
                long lastKeyboardKeepalive = Environment.TickCount64;
                long lastTelemetryRequest = Environment.TickCount64;
                long lastPicoProbe = Environment.TickCount64;
                long lastSummary = Environment.TickCount64;
                bool benchmarkTimeoutCounted = false;
                Interlocked.Exchange(ref picoLastAckTick, lastPicoProbe);
                lock (timingLock) picoHistory.Clear();
                await writer.WriteLineAsync("DIAG");
                await writer.WriteLineAsync("GAP?");  // starts the NodeMCU's gap window for this session
                while (!cts.IsCancellationRequested)
                {
                    if (readReplies.IsFaulted || readReplies.IsCompleted)
                        throw readReplies.Exception?.GetBaseException() ?? new IOException("Reply reader stopped");
                    // Send every queued key/button frame now; one frame per pass made typing queue up.
                    while (queue.TryTake(out var frame))
                    {
                        await writer.WriteLineAsync(frame);
                        Interlocked.Increment(ref sentReports);
                    }
                    long sentAt = Interlocked.Read(ref pingSentTimestamp);
                    double outstanding = sentAt != 0 ? ElapsedMs(sentAt) : 0;
                    if (pongSeen && outstanding > DeadLinkMs)
                        throw new IOException($"No PONG for {outstanding:F0} ms; link presumed dead");
                    if (!pongSeen && outstanding > 3000)
                    {
                        // Firmware without PING support: keep running without RTT and stall detection.
                        Interlocked.Exchange(ref pingSentTimestamp, 0);
                        Interlocked.Exchange(ref latencyTenths, -1);
                        sentAt = 0;
                    }
                    if (BenchmarkRunning && outstanding > 1000 && !benchmarkTimeoutCounted)
                    {
                        lock (benchmarkLock) benchmarkTimeouts++;
                        benchmarkTimeoutCounted = true;
                    }
                    int targetMs = MouseUpdateMs;
                    long mouseTick = NowMs();
                    if (mouseTick - lastMouseFlush >= targetMs)
                    {
                        RecordSchedulerTick(mouseTick, targetMs);
                        // A late PONG alone may be a stall on the return path only, so motion is held
                        // just while Windows reports our own frames unacknowledged. Held motion keeps
                        // accumulating (bounded) and goes out as one report when the link recovers,
                        // instead of a burst of stale reports.
                        bool forwardBlocked = UpdateTcpStats(socket.Client, sentAt != 0);
                        if (!forwardBlocked) await FlushMouseMovementAsync(writer);
                        else Interlocked.Increment(ref mouseHeldSlots);
                        lastMouseFlush = mouseTick;
                    }
                    // Keepalives are never suppressed: they keep the NodeMCU watchdog from
                    // releasing held keys during a stall on the return path.
                    if (Environment.TickCount64 - lastKeyboardKeepalive >= 250)
                    {
                        await writer.WriteLineAsync(Volatile.Read(ref last)); // Key-hold keepalive
                        lastKeyboardKeepalive = Environment.TickCount64;
                    }
                    if (sentAt == 0 && NowMs() - lastPing >= (BenchmarkRunning ? 150 : PingIntervalMs))
                    {
                        lastPing = NowMs();
                        benchmarkTimeoutCounted = false;
                        if (BenchmarkRunning) lock (benchmarkLock) benchmarkAttempts++;
                        lock (linkLock) { retransAtPing = tcpRetrans; timeoutsAtPing = tcpTimeouts; forwardBlockedDuringPing = false; }
                        Interlocked.Exchange(ref pingSentTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());
                        await writer.WriteLineAsync("PING");
                    }
                    if (gapQueryNeeded)
                    {
                        gapQueryNeeded = false;
                        await writer.WriteLineAsync("GAP?");
                    }
                    long stallAt;
                    lock (linkLock) stallAt = pendingStallAt;
                    if (stallAt != 0 && Environment.TickCount64 - stallAt > 3000) ResolveStall(null);
                    if (Environment.TickCount64 - lastTelemetryRequest >= 2000)
                    {
                        await writer.WriteLineAsync("STAT");
                        lastTelemetryRequest = Environment.TickCount64;
                    }
                    if (Environment.TickCount64 - lastPicoProbe >= 3000)
                    {
                        // Single outstanding probe: avoid overloading UART if the return wire is absent.
                        if (Interlocked.Read(ref picoLastAckTick) < lastPicoProbe)
                            Interlocked.Increment(ref picoTimeouts);
                        int id = (Volatile.Read(ref picoProbeId) + 1) & 65535;
                        Volatile.Write(ref picoProbeId, id);
                        await writer.WriteLineAsync($"PICO?,{id}");
                        lastPicoProbe = Environment.TickCount64;
                    }
                    if (Environment.TickCount64 - lastSummary >= 60000)
                    {
                        LogLinkSummary("periodic");
                        await writer.WriteLineAsync("DIAG");
                        lastSummary = Environment.TickCount64;
                    }
                    UpdateAdaptiveMouse();
                    // Sleep until the next mouse slot; queued key/button frames wake the loop at once.
                    int waitMs = (int)Math.Clamp(targetMs - (NowMs() - lastMouseFlush), 1, targetMs);
                    await wake.WaitAsync(waitMs, cts.Token);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { reason = ex.GetType().Name + ": " + ex.Message; }
            finally
            {
                if (timerRaised) TimerResolution.End();
                Volatile.Write(ref activeHost, null);
                if (connected)
                {
                    var duration = connectedSince is { } since ? DateTimeOffset.UtcNow - since : TimeSpan.Zero;
                    connected = false; connectedSince = null; Interlocked.Increment(ref reconnects);
                    DiagnosticLog.Warn($"Connection ended after {duration:hh\\:mm\\:ss}: {reason}");
                    LogLinkSummary("at disconnect");
                    disconnected();
                }
                else if (reason != "cancelled") DiagnosticLog.Warn("Connect attempt failed: " + reason);
                ClearMouseMotion();
                Interlocked.Exchange(ref mouseLastSentTick, 0);
                Volatile.Write(ref espRssi, int.MinValue);
                Interlocked.Exchange(ref latencyTenths, -1);
                Interlocked.Exchange(ref pingSentTimestamp, 0);
                Volatile.Write(ref last, "S,0,0,0,0,0,0,0");
                while (queue.TryTake(out _)) { }
            }
            try { await Task.Delay(1000, cts.Token); } catch (OperationCanceledException) { break; }
        }
    }

    // Link health since the last periodic summary; read by the UI and written to the log.
    readonly object linkLock = new();
    volatile bool pongSeen;
    string? firmwareDiag;
    int summaryPongs, summaryStalls;
    double summaryRttSum, summaryRttMax, summaryLongestStall;
    long totalStalls, droppedFrames;
    double longestStall;
    long lastStallLogTick;
    int suppressedStallLogs;
    public string? FirmwareDiag => Volatile.Read(ref firmwareDiag);

    void ResetLinkSummary()
    {
        lock (linkLock)
        {
            summaryPongs = summaryStalls = 0; summaryRttSum = summaryRttMax = summaryLongestStall = 0;
            tcpBaselineSet = false; tcpRetrans = tcpTimeouts = retransAtPing = timeoutsAtPing = 0;
            forwardBlockedDuringPing = false;
            pendingStallMs = 0; pendingStallTcpHint = false; pendingStallAt = 0;
            gapQueryNeeded = false;
        }
    }

    void RecordPong(double ms)
    {
        lock (linkLock)
        {
            summaryPongs++;
            summaryRttSum += ms;
            summaryRttMax = Math.Max(summaryRttMax, ms);
            if (ms < StallThresholdMs) return;
            summaryStalls++;
            totalStalls++;
            summaryLongestStall = Math.Max(summaryLongestStall, ms);
            longestStall = Math.Max(longestStall, ms);
            // A late PONG cannot say which direction stalled, and Windows also retransmits when
            // only the NodeMCU's ACKs are lost. Ask the NodeMCU whether our data kept arriving.
            pendingStallMs = Math.Max(pendingStallMs, ms);
            pendingStallTcpHint |= forwardBlockedDuringPing || tcpRetrans != retransAtPing || tcpTimeouts != timeoutsAtPing;
            if (pendingStallAt == 0) pendingStallAt = Environment.TickCount64;
            gapQueryNeeded = true;
        }
    }

    // gapMs: longest silence the NodeMCU saw from us since the previous GAP? (null: no reply,
    // firmware before 2.3). Input frames arrive at least every 250 ms, so 500+ ms means our
    // data stopped; otherwise input kept arriving and only the NodeMCU's replies were late.
    void ResolveStall(long? gapMs)
    {
        lock (linkLock)
        {
            if (pendingStallMs == 0) return;
            double ms = pendingStallMs;
            bool forward = gapMs is { } g ? g >= 500 : pendingStallTcpHint;
            if (forward) forwardStalls++; else returnStalls++;
            string direction = gapMs is { } gap
                ? forward ? $"PC->NodeMCU stalled (NodeMCU received nothing for {gap} ms)"
                          : $"NodeMCU->PC replies delayed; input kept arriving (longest gap {gap} ms)"
                : !tcpStatsAvailable ? "direction unknown"
                : forward ? "likely PC->NodeMCU (TCP retransmits; update NodeMCU firmware to confirm)"
                          : "likely NodeMCU->PC (no TCP retransmits; update NodeMCU firmware to confirm)";
            pendingStallMs = 0;
            pendingStallTcpHint = false;
            pendingStallAt = 0;
            if (ms < 500) return;
            // Bound log volume on a bad link: one stall line per 5 s plus a suppressed count.
            long now = Environment.TickCount64;
            if (now - lastStallLogTick < 5000) { suppressedStallLogs++; return; }
            string more = suppressedStallLogs > 0 ? $" ({suppressedStallLogs} more since last report)" : "";
            lastStallLogTick = now;
            suppressedStallLogs = 0;
            int rssi = Volatile.Read(ref espRssi);
            DiagnosticLog.Warn($"Link stall: PONG after {ms:F0} ms, {direction}, RSSI {(rssi == int.MinValue ? "?" : rssi.ToString())} dBm{more}");
        }
    }

    double pendingStallMs;
    bool pendingStallTcpHint;
    long pendingStallAt;
    volatile bool gapQueryNeeded;

    void LogLinkSummary(string when)
    {
        string rtt;
        lock (linkLock)
        {
            rtt = summaryPongs > 0
                ? $"RTT avg/max {summaryRttSum / summaryPongs:F1}/{summaryRttMax:F0} ms over {summaryPongs} pings, stalls>{StallThresholdMs}ms {summaryStalls} (longest {summaryLongestStall:F0} ms)"
                : "no PONG replies";
            summaryPongs = summaryStalls = 0;
            summaryRttSum = summaryRttMax = summaryLongestStall = 0;
        }
        DiagnosticLog.Write($"Link summary ({when}): {rtt} | {LinkDiagnostics()} | {EspDiagnostics()} | {SchedulerDiagnostics()} | " +
            $"{MouseDiagnostics()} | {PicoDiagnostics()} | dropped frames {Interlocked.Read(ref droppedFrames)} | disconnections {Reconnects}");
    }

    public string LinkDiagnostics()
    {
        if (!Connected) return "Link: disconnected";
        lock (linkLock)
            return $"Link: stalls >{StallThresholdMs} ms this session {totalStalls} (longest {longestStall:F0} ms; " +
                $"{forwardStalls} PC→NodeMCU, {returnStalls} NodeMCU→PC) | mouse held {Interlocked.Read(ref mouseHeldSlots) * MouseUpdateMs / 1000.0:F1} s | " +
                (tcpStatsAvailable ? $"TCP retransmits {tcpRetrans - sessionRetransBase} B, RTO {tcpTimeouts - sessionTimeoutBase} | " : "TCP stats unavailable | ") +
                $"dropped frames {Interlocked.Read(ref droppedFrames)} | timer {(TimerResolution.Active ? "1 ms" : "default")}";
    }

    readonly byte[] tcpInfoBuffer = new byte[128];
    bool tcpStatsAvailable;
    uint tcpRetrans, tcpTimeouts, retransAtPing, timeoutsAtPing, sessionRetransBase, sessionTimeoutBase;
    bool forwardBlockedDuringPing;
    bool tcpBaselineSet;
    long forwardStalls, returnStalls, mouseHeldSlots;

    // Returns true while frames sent to the NodeMCU are piling up unacknowledged.
    bool UpdateTcpStats(Socket socket, bool pingOutstanding)
    {
        var stats = TcpStats.Read(socket, tcpInfoBuffer);
        lock (linkLock)
        {
            tcpStatsAvailable = stats is not null;
            if (stats is not { } s) return false;
            if (!tcpBaselineSet)
            {
                tcpBaselineSet = true;
                sessionRetransBase = s.BytesRetrans;
                sessionTimeoutBase = s.TimeoutEpisodes;
            }
            tcpRetrans = s.BytesRetrans;
            tcpTimeouts = s.TimeoutEpisodes;
            bool blocked = s.BytesInFlight > ForwardBacklogBytes;
            if (blocked && pingOutstanding) forwardBlockedDuringPing = true;
            return blocked;
        }
    }

    bool Enqueue(string frame)
    {
        if (!queue.TryAdd(frame))
        {
            if (Interlocked.Increment(ref droppedFrames) % 50 == 1)
                DiagnosticLog.Warn($"Send queue full; dropped frame {frame} (total {Interlocked.Read(ref droppedFrames)})");
            return false;
        }
        // Wake the transport loop now instead of at its next mouse slot.
        if (wake.CurrentCount == 0) wake.Release();
        return true;
    }

    public void Dispose() { cts.Cancel(); }
}
