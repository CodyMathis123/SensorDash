using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

// Small numerical badge, allocated only when the displayed reading changes.
// Each tray widget has its own NotifyIcon, enabling Windows native overflow
// and taskbar placement preferences.
internal static class TraySensorIcon
{
    public static Icon Create(string value, string hexColor)
    {
        if (!ColorTranslator.TryFromHtmlCompat(hexColor, out var accent))
            accent = Color.FromArgb(98, 213, 206);
        using var bitmap = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Color.FromArgb(17, 34, 49));
            using var border = new Pen(accent, 3.2f);
            using var fg = new SolidBrush(Color.FromArgb(242, 255, 255));
            g.FillRoundedRectangleCompat(bg, new RectangleF(2, 4, 60, 56), 11);
            g.DrawRoundedRectangleCompat(border, new RectangleF(3.5f, 5.5f, 57, 53), 10);
            var fontSize = value.Length >= 5 ? 18f : value.Length >= 4 ? 23f : 30f;
            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment=StringAlignment.Center, LineAlignment=StringAlignment.Center };
            g.DrawString(value, font, fg, new RectangleF(4, 5, 56, 54), format);
        }
        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    // Keep our own helper rather than depend on a Windows Forms extension.
    private static GraphicsPath Round(RectangleF rect, float radius)
    {
        var d=radius*2;
        var path = new GraphicsPath();
        path.AddArc(rect.Left,rect.Top,d,d,180,90);
        path.AddArc(rect.Right-d,rect.Top,d,d,270,90);
        path.AddArc(rect.Right-d,rect.Bottom-d,d,d,0,90);
        path.AddArc(rect.Left,rect.Bottom-d,d,d,90,90);
        path.CloseFigure();
        return path;
    }
    private static void FillRoundedRectangleCompat(this Graphics g, Brush brush, RectangleF r, float radius)
    {
        using var path=Round(r,radius);
        g.FillPath(brush,path);
    }
    private static void DrawRoundedRectangleCompat(this Graphics g, Pen pen, RectangleF r, float radius)
    {
        using var path=Round(r,radius);
        g.DrawPath(pen,path);
    }
}
internal static class ColorTranslator
{
    public static bool TryFromHtmlCompat(string? hex, out Color result)
    {
        result = Color.FromArgb(98,213,206);
        if (string.IsNullOrEmpty(hex)||hex.Length!=7||hex[0]!='#')return false;
        if (!int.TryParse(hex[1..], System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,out var raw))return false;
        result=Color.FromArgb((raw>>16)&255,(raw>>8)&255,raw&255);
        return true;
    }
}
