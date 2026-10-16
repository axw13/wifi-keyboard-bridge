using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace KeyboardBridge;

// Lightweight chart: 256 latest authenticated TCP ping round-trip samples.
sealed class RttGraph : Panel
{
    double[] samples = Array.Empty<double>();
    public double[] Samples { get => samples; set { samples = value ?? Array.Empty<double>(); Invalidate(); } }

    public RttGraph()
    {
        DoubleBuffered = true;
        BackColor = SystemColors.Window;
        BorderStyle = BorderStyle.FixedSingle;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var area = new Rectangle(32, 8, Math.Max(1, ClientSize.Width - 42), Math.Max(1, ClientSize.Height - 30));
        using var axis = new Pen(SystemColors.ControlDark);
        g.DrawLine(axis, area.Left, area.Top, area.Left, area.Bottom);
        g.DrawLine(axis, area.Left, area.Bottom, area.Right, area.Bottom);
        double ceiling = Math.Max(20, samples.Length == 0 ? 20 : Math.Ceiling(samples.Max() / 10) * 10);
        using var font = new Font(Font.FontFamily, 8f);
        g.DrawString($"{ceiling:0} ms", font, SystemBrushes.ControlText, 1, 2);
        g.DrawString("0", font, SystemBrushes.ControlText, 16, area.Bottom - 9);
        if (samples.Length < 2) { g.DrawString("Waiting for RTT samples…", font, SystemBrushes.GrayText, area.Left + 8, area.Top + 8); return; }
        using var stroke = new Pen(Color.SteelBlue, 1.5f);
        var points = samples.Select((v, i) => new PointF(
            area.Left + (float)i * area.Width / (samples.Length - 1),
            area.Bottom - (float)(Math.Min(ceiling, Math.Max(0, v)) / ceiling * area.Height))).ToArray();
        g.DrawLines(stroke, points);
    }
}
