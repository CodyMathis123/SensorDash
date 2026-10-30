using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

internal static class TrayIconFactory
{
    // Custom SensorDash telemetry logo: cyan pulse inside a dark circular dial.
    // Drawn at runtime so the tray icon is independent of working directory,
    // external .ico files, MSI layout, or Windows DPI settings.
    internal static Icon Create()
    {
        using var image = new Bitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.Clear(Color.Transparent);

            using var background = new SolidBrush(Color.FromArgb(17, 34, 49));
            using var outline = new Pen(Color.FromArgb(83, 207, 202), 4.5f)
            {
                Alignment = PenAlignment.Inset
            };
            using var faint = new Pen(Color.FromArgb(55, 108, 125), 1.6f);
            graphics.FillEllipse(background, 3, 3, 58, 58);
            graphics.DrawEllipse(outline, 3.5f, 3.5f, 57, 57);
            graphics.DrawArc(faint, 10.5f, 10.5f, 43, 43, 210, 120);

            using var pulse = new Pen(Color.FromArgb(230, 250, 249), 5.2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            graphics.DrawLines(pulse, new[]
            {
                new PointF(13, 33), new PointF(22, 33), new PointF(28, 20),
                new PointF(35, 45), new PointF(41, 28), new PointF(46, 33),
                new PointF(51, 33)
            });
        }

        // Icon.FromHandle does not own the native HICON. Clone the result and
        // destroy the original to avoid leaking a GDI handle.
        var handle = image.GetHicon();
        try
        {
            using var unmanagedIcon = Icon.FromHandle(handle);
            return (Icon)unmanagedIcon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
