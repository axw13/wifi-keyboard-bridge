using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KeyboardBridge;

// Raw Input reports physical relative motion independently of the Windows cursor position.
// WH_MOUSE_LL continues to suppress local cursor messages while remote mouse mode is active.
sealed class RawMouseInput : NativeWindow, IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct RawDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RawHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterRawInputDevices(
        [In] RawDevice[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint GetRawInputData(
        IntPtr raw, uint command, [Out] byte[]? data,
        ref uint size, uint headerSize);

    readonly Func<bool> enabled;
    readonly Action<int,int> move;

    public RawMouseInput(Func<bool> enabled, Action<int,int> move)
    {
        this.enabled = enabled;
        this.move = move;
        CreateHandle(new CreateParams { Caption = "Keyboard Bridge Raw Mouse Input" });
        var devices = new[] { new RawDevice {
            UsagePage = 1, Usage = 2, Flags = 0x100, Target = Handle
        } };
        if (!RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RawDevice>()))
        {
            int error = Marshal.GetLastWin32Error();
            DestroyHandle();
            throw new System.ComponentModel.Win32Exception(error);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x00FF) // WM_INPUT
        {
            uint headerSize = (uint)Marshal.SizeOf<RawHeader>();
            uint size = 0;
            GetRawInputData(m.LParam, 0x10000003, null, ref size, headerSize);
            if (size >= headerSize + 20 && size < 4096)
            {
                var buffer = new byte[size];
                if (GetRawInputData(m.LParam, 0x10000003, buffer, ref size, headerSize) == size
                    && BitConverter.ToUInt32(buffer, 0) == 0 && enabled())
                {
                    int offset = (int)headerSize;
                    ushort flags = BitConverter.ToUInt16(buffer, offset);
                    if ((flags & 1) == 0) // MOUSE_MOVE_RELATIVE only
                    {
                        int dx = BitConverter.ToInt32(buffer, offset + 12);
                        int dy = BitConverter.ToInt32(buffer, offset + 16);
                        if (dx != 0 || dy != 0) move(dx, dy);
                    }
                }
            }
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        // Unregister foreground/background raw mouse sink.
        var remove = new[] { new RawDevice {
            UsagePage = 1, Usage = 2, Flags = 0x1, Target = IntPtr.Zero
        } };
        RegisterRawInputDevices(remove, 1, (uint)Marshal.SizeOf<RawDevice>());
        DestroyHandle();
    }
}
