using System.Drawing.Drawing2D;

namespace MonitorOff;

/// <summary>Draws a small monitor-with-moon glyph so the app needs no icon file.</summary>
internal static class TrayIconFactory
{
    public static Icon Create()
    {
        int size = Math.Max(16, SystemInformation.SmallIconSize.Width) * 2; // draw large; Windows scales down cleanly
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            float u = size / 16f;
            using var frame = new SolidBrush(Color.FromArgb(235, 235, 235));
            using var screen = new SolidBrush(Color.FromArgb(20, 20, 20));
            using var moon = new SolidBrush(Color.FromArgb(120, 170, 255));

            // Bezel and screen
            g.FillRectangle(frame, 1 * u, 2 * u, 14 * u, 9.5f * u);
            g.FillRectangle(screen, 2.2f * u, 3.2f * u, 11.6f * u, 7.1f * u);
            // Stand
            g.FillRectangle(frame, 7 * u, 11.5f * u, 2 * u, 1.8f * u);
            g.FillRectangle(frame, 4.5f * u, 13.3f * u, 7 * u, 1.4f * u);
            // Crescent moon = "off / sleeping"
            g.FillEllipse(moon, 5.6f * u, 4.2f * u, 4.8f * u, 4.8f * u);
            g.FillEllipse(screen, 7.2f * u, 3.6f * u, 4.4f * u, 4.4f * u);
        }

        nint hIcon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            Native.DestroyIcon(hIcon);
        }
    }
}
