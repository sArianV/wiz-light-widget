using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WizLightWidget.Native;

public static class IconFactory
{
    public static IntPtr CreatePowerIcon(bool on, int size = 32)
    {
        using var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        var bg = on ? Color.FromArgb(255, 255, 200, 87) : Color.FromArgb(255, 80, 80, 88);
        using (var brush = new SolidBrush(bg))
            g.FillEllipse(brush, 1, 1, size - 2, size - 2);

        using var pen = new Pen(Color.White, size / 9f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float cx = size / 2f, cy = size / 2f, r = size * 0.26f;
        g.DrawArc(pen, cx - r, cy - r, r * 2, r * 2, -60, 300);
        g.DrawLine(pen, cx, cy - r - size * 0.06f, cx, cy - r * 0.1f);

        return bmp.GetHicon();
    }

    public static IntPtr CreateBrightnessIcon(bool up, int size = 32)
    {
        using var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using (var brush = new SolidBrush(Color.FromArgb(255, 60, 60, 66)))
            g.FillEllipse(brush, 1, 1, size - 2, size - 2);

        using var pen = new Pen(Color.FromArgb(255, 255, 210, 90), size / 8.5f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        float cx = size / 2f;
        float top = size * 0.26f, bottom = size * 0.74f, half = size * 0.15f;

        if (up)
        {
            g.DrawLine(pen, cx, bottom, cx, top);
            g.DrawLine(pen, cx - half, top + half, cx, top);
            g.DrawLine(pen, cx + half, top + half, cx, top);
        }
        else
        {
            g.DrawLine(pen, cx, top, cx, bottom);
            g.DrawLine(pen, cx - half, bottom - half, cx, bottom);
            g.DrawLine(pen, cx + half, bottom - half, cx, bottom);
        }

        return bmp.GetHicon();
    }

    public static IntPtr CreateWhiteTempIcon(bool warm, int size = 32)
    {
        using var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using (var bgBrush = new SolidBrush(Color.FromArgb(255, 60, 60, 66)))
            g.FillEllipse(bgBrush, 1, 1, size - 2, size - 2);

        // Mismo tono que los swatches de blanco cálido/frío del resto de la app.
        var swatch = warm ? Color.FromArgb(255, 255, 217, 166) : Color.FromArgb(255, 234, 244, 255);
        float inset = size * 0.22f;
        using (var swatchBrush = new SolidBrush(swatch))
            g.FillEllipse(swatchBrush, inset, inset, size - inset * 2, size - inset * 2);

        return bmp.GetHicon();
    }

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr handle);
}
