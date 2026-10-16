using System.Runtime.InteropServices;

namespace KeyboardBridge;

// Optional remote anti-idle probe; never moves the Windows host cursor.
sealed class AntiIdleMover
{
    [StructLayout(LayoutKind.Sequential)]
    struct LastInputInfo { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LastInputInfo info);

    bool positive = true;
    long lastProbeTick;
    public bool Enabled { get; set; }

    public void Tick(bool keyboardRemote, bool mouseRemote, bool connected, Func<int, bool> sendRemoteMovement)
    {
        if (!Enabled || keyboardRemote || mouseRemote || !connected) return;

        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return;

        // Only probe when the user has been inactive locally for at least a minute.
        uint inactiveMs = unchecked((uint)Environment.TickCount - info.dwTime);
        if (inactiveMs < 60_000 || inactiveMs > 0x7FFFFFFF) return;

        long now = Environment.TickCount64;
        if (lastProbeTick != 0 && now - lastProbeTick < 30_000) return;
        // This queues a small HID movement on the Pico, not a Windows SendInput event.
        if (sendRemoteMovement(positive ? 1 : -1))
        {
            positive = !positive;
            lastProbeTick = now;
        }
    }
}
