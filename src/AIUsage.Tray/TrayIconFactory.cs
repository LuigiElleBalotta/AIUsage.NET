using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace AIUsage.Tray;

/// <summary>
/// Generates a placeholder tray icon at runtime. No bundled .ico exists yet — the logo/branding
/// change is deliberately deferred (see PORTING_NOTES.md); this draws a simple neutral glyph so the
/// tray has something to show until a real icon is designed.
/// </summary>
internal static class TrayIconFactory
{
    public static Icon Create()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "aiusage.ico");
        if (File.Exists(iconPath))
        {
            return new Icon(iconPath);
        }

        using var bitmap = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);
        using var brush = new SolidBrush(Color.FromArgb(255, 90, 130, 246));
        g.FillEllipse(brush, 1, 1, 14, 14);
        using var font = new Font("Segoe UI", 7f, System.Drawing.FontStyle.Bold);
        using var textBrush = new SolidBrush(Color.White);
        g.DrawString("AI", font, textBrush, new PointF(0.5f, 2.5f));
        return Icon.FromHandle(bitmap.GetHicon());
    }
}
