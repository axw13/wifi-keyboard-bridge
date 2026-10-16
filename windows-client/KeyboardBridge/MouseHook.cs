using System.Runtime.InteropServices;

namespace KeyboardBridge;

// Experimental WH_MOUSE_LL based relative-pointer bridge. Windows desktop pointer
// messages do not offer raw gaming-mouse deltas; high DPI mice may require Raw Input.
sealed class MouseHook : IDisposable
{
    delegate IntPtr HookProc(int code, IntPtr msg, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MouseData
    { public Point Pt; public uint MouseDataValue, Flags, Time; public IntPtr Extra; }
    [DllImport("user32.dll", SetLastError=true)] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr ptr);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr ptr, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);

    readonly HookProc callback;
    readonly IntPtr hook;
    Func<int,int,int,int,bool>? sink;
    int buttons;
    int? lastX, lastY;
    public MouseHook(Func<int,int,int,int,bool> handler)
    {
        this.handler = handler;
        callback = OnMouse;
        hook = SetWindowsHookEx(14, callback, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
    }
    readonly Func<int,int,int,int,bool> handler;
    public void ResetTracking() { lastX = null; lastY = null; buttons = 0; }

    // Returns true if forwarded, so the low-level hook suppresses the local event.
    public bool Forward(int msg, int x, int y, int wheel, Func<int,int,int,int,bool> send)
    {
        int dx = 0, dy = 0;
        if (msg == 0x200) // WM_MOUSEMOVE
        {
            if (lastX.HasValue)
            {
                dx = x - lastX.Value;
                dy = y - lastY!.Value;
            }
            lastX = x;
            lastY = y;
            if (dx == 0 && dy == 0) return true;
        }
        else
        {
            switch(msg)
            {
                case 0x201: buttons |= 1; break;
                case 0x202: buttons &= ~1; break;
                case 0x204: buttons |= 2; break;
                case 0x205: buttons &= ~2; break;
                case 0x207: buttons |= 4; break;
                case 0x208: buttons &= ~4; break;
                case 0x20A: wheel = Math.Sign(wheel); break;
                default: return false;
            }
        }
        // Split larger deltas into legal signed 8-bit HID movement segments.
        do
        {
            int sx = Math.Clamp(dx, -127, 127), sy = Math.Clamp(dy, -127, 127);
            if (!send(buttons, sx, sy, wheel)) return false;
            dx -= sx; dy -= sy; wheel = 0;
        } while (dx != 0 || dy != 0);
        return true;
    }

    IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<MouseData>(lParam);
            if ((data.Flags & 1) == 0)
            {
                int msg = wParam.ToInt32();
                int wheel = msg == 0x20A ? (short)(data.MouseDataValue >> 16) : 0;
                try { if (handler(msg, data.Pt.X, data.Pt.Y, wheel)) return (IntPtr)1; }
                catch { /* Fail open on hook exceptions */ }
            }
        }
        return CallNextHookEx(hook, code, wParam, lParam);
    }
    public void Dispose() => UnhookWindowsHookEx(hook);
}
