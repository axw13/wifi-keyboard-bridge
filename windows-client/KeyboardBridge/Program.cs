using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Net.Sockets;
using System.Windows.Forms;

namespace KeyboardBridge;

record Settings(string Host, int Port, string Token);

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path)) { MessageBox.Show("Copy appsettings.example.json to appsettings.json and configure the NodeMCU."); return; }
        var cfg = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (cfg == null || cfg.Token.Length < 16) { MessageBox.Show("Invalid configuration (token must be 16+ characters)."); return; }
        Application.Run(new BridgeContext(cfg));
    }
}

sealed class BridgeContext : ApplicationContext
{
    readonly NotifyIcon tray;
    readonly KeyboardHook hook;
    readonly MouseHook mouseHook;
    readonly RawMouseInput rawMouse;
    bool mouseRemote;
    int mouseToggleVk;
    bool linkedToggle;
    bool mouseHotkeyLatch;
    readonly Transport network;
    readonly HashSet<int> held = new();
    readonly HashSet<int> physical = new();
    readonly HashSet<int> swallowed = new();
    // Keys whose key-down Windows received locally must also have their key-up passed locally.
    readonly HashSet<int> localDown = new();
    bool remote;
    bool hotkeyLatch;
    int toggleVk;
    SettingsWindow? settingsWindow;
    double mouseSensitivity = 1.0;
    readonly string prefPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WifiKeyboardBridge", "preferences.json");
    readonly SynchronizationContext ui;

    public BridgeContext(Settings cfg)
    {
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        network = new Transport(cfg, () => ui.Post(_ => { SwitchLocal(); ToggleMouseTo(false); }, null));
        var preferences = UserPreferences.Load(prefPath);
        toggleVk = preferences.ToggleKey;
        mouseToggleVk = preferences.MouseToggleKey;
        linkedToggle = preferences.Linked;
        mouseSensitivity = Math.Clamp(preferences.MouseSensitivity, 0.25, 3.0);
        network.SetMouseTuning(mouseSensitivity, preferences.MouseUpdateMs);
        tray = new NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Visible = true, Text = "Keyboard Bridge: LOCAL" };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Switch keyboard", null, (_, _) => Toggle());
        menu.Items.Add("Switch mouse", null, (_, _) => ToggleMouse());
        menu.Items.Add("Settings && Connection Statistics...", null, (_, _) => ShowSettings(cfg));
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Toggle();
        hook = new KeyboardHook(HandleKey);
        mouseHook = new MouseHook(HandleMouse);
        rawMouse = new RawMouseInput(() => mouseRemote && network.Connected,
            (dx, dy) => network.SendMouseMovement(dx, dy));
    }

    bool HandleKey(int vk, bool pressed, bool injected)
    {
        if (injected) return false;
        if (pressed) physical.Add(vk); else physical.Remove(vk);
        bool ctrl = physical.Contains(0xA2) || physical.Contains(0xA3) || physical.Contains(0x11);
        bool alt = physical.Contains(0xA4) || physical.Contains(0xA5) || physical.Contains(0x12);
        if ((vk == toggleVk || (!linkedToggle && vk == mouseToggleVk)) && pressed && ctrl && alt)
        {
            if (!hotkeyLatch && !mouseHotkeyLatch)
            {
                if (vk == toggleVk) { hotkeyLatch = true; Toggle(); if (linkedToggle) ToggleMouseTo(remote); }
                else { mouseHotkeyLatch = true; ToggleMouse(); }
            }
            swallowed.Add(vk);
            return true;
        }
        if (vk == toggleVk && !pressed) hotkeyLatch = false;
        if (vk == mouseToggleVk && !pressed) mouseHotkeyLatch = false;
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
        if (!network.Connected) { SwitchLocal(); return false; }
        int usage = HidMap.Key(vk);
        if (usage == 0) return false; // Don't suppress unsupported keys
        if (pressed) { held.Add(vk); swallowed.Add(vk); }
        else held.Remove(vk);
        if (!SendState()) { SwitchLocal(); return false; }
        return true;
    }

    bool HandleMouse(int message, int x, int y, int wheel)
    {
        if (!mouseRemote) return false;
        if (!network.Connected) { ToggleMouseTo(false); return false; }
        return mouseHook.Forward(message, x, y, wheel, (b, dx, dy, w) => network.SendMouse(b, dx, dy, w));
    }
    void ToggleMouse() => ToggleMouseTo(!mouseRemote);
    void ToggleMouseTo(bool enabled)
    {
        if (enabled && !network.Connected) return;
        mouseRemote = enabled;
        mouseHook.ResetTracking();
        if (!enabled)
        {
            network.DiscardPendingMouseMovement();
            network.SendMouse(0, 0, 0, 0);
        }
        UpdateTray();
    }
    void UpdateTray() => tray.Text = "Bridge: K " + (remote ? "REMOTE" : "LOCAL") + " / M " + (mouseRemote ? "REMOTE" : "LOCAL");
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
        UpdateTray();
        SendState();
    }
    void SwitchLocal()
    {
        if (!remote) return;
        remote = false;
        held.Clear();
        network.Send(0, Array.Empty<int>());
        UpdateTray();
    }
    void ShowSettings(Settings cfg)
    {
        if (settingsWindow != null && !settingsWindow.IsDisposed) { settingsWindow.Activate(); return; }
        settingsWindow = new SettingsWindow(cfg, network, () => remote, () => toggleVk, vk =>
        {
            toggleVk = vk;
            hotkeyLatch = false;
            UserPreferences.Save(prefPath, new UserPreferences(toggleVk, mouseToggleVk, linkedToggle, mouseSensitivity, network.MouseUpdateMs));
        }, newSettings =>
        {
            SwitchLocal();
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                JsonSerializer.Serialize(newSettings, new JsonSerializerOptions { WriteIndented = true }));
            Application.Restart();
        });
        settingsWindow.SetMouseSettings(() => mouseRemote, () => mouseToggleVk, () => linkedToggle, (key, linked) =>
        {
            if (linked && key == toggleVk) key = 0x7A;
            mouseToggleVk = key;
            linkedToggle = linked;
            mouseHotkeyLatch = false;
            UserPreferences.Save(prefPath, new UserPreferences(toggleVk, mouseToggleVk, linkedToggle, mouseSensitivity, network.MouseUpdateMs));
        });
        settingsWindow.SetMouseTuning(() => mouseSensitivity, () => network.MouseUpdateMs, (sensitivity, interval) =>
        {
            mouseSensitivity = sensitivity;
            network.SetMouseTuning(sensitivity, interval);
            UserPreferences.Save(prefPath, new UserPreferences(toggleVk, mouseToggleVk, linkedToggle, sensitivity, interval));
        });
        settingsWindow.Show();
    }
    protected override void ExitThreadCore()
    {
        SwitchLocal();
        ToggleMouseTo(false);
        settingsWindow?.Close();
        hook.Dispose();
        rawMouse.Dispose();
        mouseHook.Dispose();
        network.Dispose();
        tray.Visible = false;
        tray.Dispose();
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
        return vk switch {
            0x0D => 0x28, 0x1B => 0x29, 0x08 => 0x2A, 0x09 => 0x2B, 0x20 => 0x2C,
            0xBD => 0x2D, 0xBB => 0x2E, 0xDB => 0x2F, 0xDD => 0x30, 0xDC => 0x31,
            0xBA => 0x33, 0xDE => 0x34, 0xC0 => 0x35, 0xBC => 0x36, 0xBE => 0x37, 0xBF => 0x38,
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
            try { if (handler((int)k.vk, pressed, (k.flags & 0x10) != 0)) return (IntPtr)1; }
            catch { /* Fail open to local keyboard on hook error */ }
        }
        return CallNextHookEx(handle, code, msg, data);
    }
    public void Dispose() => UnhookWindowsHookEx(handle);
}

sealed class Transport : IDisposable
{
    readonly Settings cfg;
    readonly Action disconnected;
    readonly BlockingCollection<string> queue = new(256);
    readonly CancellationTokenSource cts = new();
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
        this.disconnected = disconnected;
        _ = Task.Run(Run);
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
            if (!queue.TryAdd(packet)) return false;
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
            long now = Environment.TickCount64;
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
        return queue.TryAdd(frame);
    }
    async Task Run()
    {
        while (!cts.IsCancellationRequested)
        {
            try
            {
                using var socket = new TcpClient { NoDelay = true };
                await socket.ConnectAsync(cfg.Host, cfg.Port, cts.Token);
                var stream = socket.GetStream();
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                await writer.WriteLineAsync("AUTH " + cfg.Token);
                if (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3), cts.Token) != "OK")
                    throw new IOException("Authentication failed");
                connected = true;
                Volatile.Write(ref espRssi, int.MinValue);
                connectedSince = DateTimeOffset.UtcNow;
                Interlocked.Exchange(ref latencyTenths, -1);
                var readReplies = Task.Run(async () =>
                {
                    while (!cts.IsCancellationRequested)
                    {
                        var response = await reader.ReadLineAsync(cts.Token);
                        if (response == null) throw new IOException("Connection closed");
                        if (response.StartsWith("STAT,"))
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
                        else if (response == "PONG")
                        {
                            var t = Interlocked.Exchange(ref pingSentTimestamp, 0);
                            if (t != 0)
                            {
                                var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                                Interlocked.Exchange(ref latencyTenths, (long)Math.Round(ms * 10));
                                if (BenchmarkRunning) lock (benchmarkLock) benchmarkSamples.Add(ms);
                            }
                        }
                    }
                }, cts.Token);
                var lastPing = DateTimeOffset.MinValue;
                lastMouseFlush = Environment.TickCount64;
                lock (timingLock) { schedulerIntervals.Clear(); schedulerLateness.Clear(); activeReportIntervals.Clear(); }
                long lastKeyboardKeepalive = Environment.TickCount64;
                long lastTelemetryRequest = Environment.TickCount64;
                while (!cts.IsCancellationRequested)
                {
                    if (readReplies.IsFaulted || readReplies.IsCompleted) throw new IOException("Ping response reader stopped");
                    if (queue.TryTake(out var frame))
                    {
                        await writer.WriteLineAsync(frame);
                        Interlocked.Increment(ref sentReports);
                    }
                    // Avoid flooding the UART with keyboard keepalives while processing mouse movement.
                    int targetMs = MouseUpdateMs;
                    long mouseTick = Environment.TickCount64;
                    if (mouseTick - lastMouseFlush >= targetMs)
                    {
                        RecordSchedulerTick(mouseTick, targetMs);
                        await FlushMouseMovementAsync(writer);
                        lastMouseFlush = mouseTick;
                    }
                    if (Environment.TickCount64 - lastKeyboardKeepalive >= 250)
                    {
                        await writer.WriteLineAsync(Volatile.Read(ref last)); // Key-hold keepalive
                        lastKeyboardKeepalive = Environment.TickCount64;
                    }
                    if (DateTimeOffset.UtcNow - lastPing > (BenchmarkRunning ? TimeSpan.FromMilliseconds(150) : TimeSpan.FromSeconds(2)))
                    {
                        lastPing = DateTimeOffset.UtcNow;
                        if (Interlocked.Read(ref pingSentTimestamp) != 0)
                        {
                            Interlocked.Exchange(ref latencyTenths, -1); // timed-out / unsupported firmware
                            if (BenchmarkRunning) lock (benchmarkLock) benchmarkTimeouts++;
                        }
                        if (BenchmarkRunning) lock (benchmarkLock) benchmarkAttempts++;
                        Interlocked.Exchange(ref pingSentTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());
                        await writer.WriteLineAsync("PING");
                    }
                    if (Environment.TickCount64 - lastTelemetryRequest >= 2000)
                    {
                        await writer.WriteLineAsync("STAT");
                        lastTelemetryRequest = Environment.TickCount64;
                    }
                    // Avoid blocking the transport thread in 5ms queue waits.
                    // Short async delays produce steadier scheduling at 60 Hz.
                    await Task.Delay(1, cts.Token);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* retry after delay */ }
            finally
            {
                if (connected) { connected = false; connectedSince = null; Interlocked.Increment(ref reconnects); disconnected(); }
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
    public void Dispose() { cts.Cancel(); }
}
