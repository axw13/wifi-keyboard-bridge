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
    readonly Transport network;
    readonly HashSet<int> held = new();
    readonly HashSet<int> physical = new();
    readonly HashSet<int> swallowed = new();
    // Keys whose key-down Windows received locally must also have their key-up passed locally.
    readonly HashSet<int> localDown = new();
    bool remote;
    bool hotkeyLatch;
    readonly SynchronizationContext ui;

    public BridgeContext(Settings cfg)
    {
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        network = new Transport(cfg, () => ui.Post(_ => SwitchLocal(), null));
        tray = new NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Visible = true, Text = "Keyboard Bridge: LOCAL" };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Switch Local / Remote (Ctrl+Alt+F12)", null, (_, _) => Toggle());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Toggle();
        hook = new KeyboardHook(HandleKey);
    }

    bool HandleKey(int vk, bool pressed, bool injected)
    {
        if (injected) return false;
        if (pressed) physical.Add(vk); else physical.Remove(vk);
        bool ctrl = physical.Contains(0xA2) || physical.Contains(0xA3) || physical.Contains(0x11);
        bool alt = physical.Contains(0xA4) || physical.Contains(0xA5) || physical.Contains(0x12);
        if (vk == 0x7B && pressed && ctrl && alt)
        {
            if (!hotkeyLatch) { hotkeyLatch = true; Toggle(); }
            swallowed.Add(vk);
            return true;
        }
        if (vk == 0x7B && !pressed) hotkeyLatch = false;
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
        tray.Text = "Keyboard Bridge: REMOTE";
        SendState();
    }
    void SwitchLocal()
    {
        if (!remote) return;
        remote = false;
        held.Clear();
        network.Send(0, Array.Empty<int>());
        tray.Text = "Keyboard Bridge: LOCAL";
    }
    protected override void ExitThreadCore()
    {
        SwitchLocal();
        hook.Dispose();
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
    string last = "S,0,0,0,0,0,0,0";
    public bool Connected => connected;
    public Transport(Settings cfg, Action disconnected)
    {
        this.cfg = cfg;
        this.disconnected = disconnected;
        _ = Task.Run(Run);
    }
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
                while (!cts.IsCancellationRequested)
                {
                    if (queue.TryTake(out var frame, 250)) await writer.WriteLineAsync(frame);
                    else await writer.WriteLineAsync(Volatile.Read(ref last)); // Key-hold keepalive
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* retry after delay */ }
            finally
            {
                if (connected) { connected = false; disconnected(); }
                Volatile.Write(ref last, "S,0,0,0,0,0,0,0");
                while (queue.TryTake(out _)) { }
            }
            try { await Task.Delay(1000, cts.Token); } catch (OperationCanceledException) { break; }
        }
    }
    public void Dispose() { cts.Cancel(); }
}
