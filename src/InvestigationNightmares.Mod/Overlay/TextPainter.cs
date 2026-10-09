using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using InvestigationNightmares.Images;

namespace InvestigationNightmares.Mod.Overlay;

/// <summary>Renders wrapped text into a BGRA picture with GDI+ (uploaded as a texture by the overlay).</summary>
static class TextPainter
{
    public static Bgra32Image Render(string text, float sizePx, Color color, int maxWidth, bool bold = false, Color? outline = null)
    {
        using var font = new Font("Segoe UI", Math.Max(6, sizePx), bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        SizeF size;
        using (var probe = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(probe))
            size = g.MeasureString(text, font, Math.Max(16, maxWidth));
        int w = Math.Clamp((int)Math.Ceiling(size.Width) + 6, 8, 4096);
        int h = Math.Clamp((int)Math.Ceiling(size.Height) + 6, 8, 4096);

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var rect = new RectangleF(3, 3, w - 6, h - 6);
            if (outline is Color o)
                using (var ob = new SolidBrush(o))
                    foreach (var (dx, dy) in new[] { (-2, 0), (2, 0), (0, -2), (0, 2), (-1, -1), (1, 1), (-1, 1), (1, -1) })
                        g.DrawString(text, font, ob, new RectangleF(rect.X + dx, rect.Y + dy, rect.Width, rect.Height));
            using var brush = new SolidBrush(color);
            g.DrawString(text, font, brush, rect);
        }
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, px, y * w * 4, w * 4);
            return new Bgra32Image(w, h, px);
        }
        finally { bmp.UnlockBits(data); }
    }

    public static Color ParseHex(string hex)
    {
        try { return ColorTranslator.FromHtml(hex); }
        catch { return Color.Gold; }
    }
}
