using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;

// Draw at the native notification-area pixel size rather than rendering a
// decorative 64px image and asking Windows to shrink it down to ~16px.
// At that size, a ring and large padding leave almost no space for numbers.
internal static class TraySensorIcon
{
    public static Icon Create(string fullValue, string hexColor)
    {
        if (!ColorTranslator.TryFromHtmlCompat(hexColor, out var accent))
            accent = Color.FromArgb(98, 213, 206);

        var size = NativeIconSize();
        var displayed = Compact(fullValue, size);
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.None;
            g.TextRenderingHint = size <= 20
                ? TextRenderingHint.SingleBitPerPixelGridFit
                : TextRenderingHint.AntiAliasGridFit;

            // Use all available pixels for the value. The accent is a narrow
            // bottom stripe, not a thick border competing with the digits.
            using var background = new SolidBrush(Color.FromArgb(14, 28, 40));
            using var foreground = new SolidBrush(Color.White);
            using var underline = new SolidBrush(accent);
            g.FillRectangle(background, 0, 0, size, size);
            var stripeHeight = size <= 20 ? 2 : 3;
            g.FillRectangle(underline, 0, size - stripeHeight, size, stripeHeight);

            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            };
            var availableHeight = size - stripeHeight;
            var bestSize = Math.Min(size * .87f, size - 2f);
            // Measure for the *native* raster: a two-digit temperature stays
            // roughly 12px tall at the common 16px taskbar icon resolution.
            using var family = new FontFamily("Arial");
            for (; bestSize >= 6; bestSize -= .5f)
            {
                using var probe = new Font(family, bestSize, FontStyle.Bold, GraphicsUnit.Pixel);
                var measured = g.MeasureString(displayed, probe, 1000, format);
                if (measured.Width <= size - 1 && measured.Height <= availableHeight) break;
            }
            using var font = new Font(family, Math.Max(6, bestSize), FontStyle.Bold, GraphicsUnit.Pixel);
            g.DrawString(displayed, font, foreground, new RectangleF(0, -1, size, availableHeight + 1), format);
        }
        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    // The exact precision is always kept in the Windows hover tooltip.
    // Four or five digits cannot be deciphered at 16px; simplify only the
    // tray graphic (e.g. 24.0 -> 24, 1340 -> 1.3k or 1k at 100% DPI).
    internal static string Compact(string value, int size)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            || !double.IsFinite(number)) return "--";
        if (Math.Abs(number) >= 1000)
        {
            var shortValue = size <= 20
                ? Math.Round(number / 1000, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)
                : (number / 1000).ToString("0.#", CultureInfo.InvariantCulture);
            return shortValue + "k";
        }
        if (value.Length <= 3) return value;
        if (Math.Abs(number) >= 10) return Math.Round(number, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
        // Single-digit readings can accommodate a decimal point.
        if (Math.Abs(number) < 10) return number.ToString("0.#", CultureInfo.InvariantCulture);
        return value;
    }

    private static int NativeIconSize()
    {
        try
        {
            // At 100%, 125%, 150%, 200% Windows typically renders 16, 20,
            // 24, 32 physical pixels respectively.
            var dpi = GetDpiForSystem();
            return Math.Clamp((int)Math.Round(16.0 * dpi / 96), 16, 48);
        }
        catch (EntryPointNotFoundException) { return 16; }
        catch (DllNotFoundException) { return 16; }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
