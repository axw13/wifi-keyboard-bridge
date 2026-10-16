using System.Runtime.InteropServices;

namespace KeyboardBridge;

// Optional LOCAL-only inactivity prevention. Never called by the remote input path.
sealed class AntiIdleMover
{
    [StructLayout(LayoutKind.Sequential)]
    struct LastInputInfo { public uint cbSize; public uint dwTime; }

    [StructLayout(LayoutKind.Sequential)]
    struct MouseInput
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Input
    {
        public uint type;
        public MouseInput mi;
    }

    [DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint count, [In] Input[] inputs, int size);

    bool positive = true;
    public bool Enabled { get; set; }

    public void Tick(bool keyboardRemote, bool mouseRemote)
    {
        if (!Enabled || keyboardRemote || mouseRemote) return;
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return;
        uint inactiveMs = unchecked((uint)Environment.TickCount - info.dwTime);
        if (inactiveMs < 60_000 || inactiveMs > 0x7FFFFFFF) return;
        // Send one 1-pixel relative movement. Reverse on the next idle event,
        // so the cursor does not drift continually in one direction.
        var inputs = new[] { new Input { type = 0, mi = new MouseInput {
            dx = positive ? 1 : -1, dy = 0, dwFlags = 0x0001 } } };
        if (SendInput(1, inputs, Marshal.SizeOf<Input>()) == 1) positive = !positive;
    }
}
