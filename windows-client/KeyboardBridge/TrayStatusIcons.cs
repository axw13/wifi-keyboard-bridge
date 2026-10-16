using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace KeyboardBridge;

// Distinct live status indicators with a stylized Wi-Fi/bridge emblem.
static class TrayStatusIcons
{
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern bool DestroyIcon(IntPtr handle);

    public static Icon Create(Color status)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var background = new SolidBrush(Color.FromArgb(18, 47, 87));
            g.FillEllipse(background, 1, 1, 30, 30);
            using var pen = new Pen(Color.White, 2.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, 7, 7, 18, 17, 215, 110);
            g.DrawArc(pen, 11, 12, 10, 10, 215, 110);
            using var dot = new SolidBrush(Color.White);
            g.FillEllipse(dot, 14, 19, 4, 4);
            using var stateBrush = new SolidBrush(status);
            g.FillEllipse(stateBrush, 21, 21, 10, 10);
        }
        IntPtr handle = bmp.GetHicon();
        try { using var icon = Icon.FromHandle(handle); return (Icon)icon.Clone(); }
        finally { DestroyIcon(handle); }
    }
}
