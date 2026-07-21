// One-off tool: renders the AIUsage brand glyph (blue gradient circle badge + "AI" wordmark) at the
// standard Windows icon resolutions and packs them into a single multi-resolution .ico using PNG-
// compressed frames (supported by Windows Vista+ for any icon directory entry size). Not part of the
// shipped solution — run once to (re)generate src/AIUsage.Tray/Resources/aiusage.ico.
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
var pngFrames = new List<(int Size, byte[] Png)>();

foreach (var size in sizes)
{
    using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bitmap);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.Clear(Color.Transparent);

    var inset = Math.Max(0.5f, size * 0.03f);
    var rect = new RectangleF(inset, inset, size - inset * 2, size - inset * 2);

    using var gradient = new LinearGradientBrush(
        new PointF(rect.Left, rect.Top),
        new PointF(rect.Right, rect.Bottom),
        Color.FromArgb(255, 96, 141, 255),   // top-left: light accent blue
        Color.FromArgb(255, 59, 91, 219));   // bottom-right: deeper blue
    g.FillEllipse(gradient, rect);

    // Subtle inner ring for depth at larger sizes.
    if (size >= 32)
    {
        using var ringPen = new Pen(Color.FromArgb(60, 255, 255, 255), Math.Max(1f, size * 0.02f));
        var ringInset = size * 0.06f;
        g.DrawEllipse(ringPen, rect.Left + ringInset, rect.Top + ringInset, rect.Width - ringInset * 2, rect.Height - ringInset * 2);
    }

    using var font = new Font("Segoe UI", size * 0.40f, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
    using var textBrush = new SolidBrush(Color.White);
    using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
    var textRect = new RectangleF(0, 0, size, size);
    // Nudge down slightly to visually center the cap-height of "AI" within the circle.
    textRect.Offset(0, size * 0.02f);
    g.DrawString("AI", font, textBrush, textRect, format);

    using var ms = new MemoryStream();
    bitmap.Save(ms, ImageFormat.Png);
    pngFrames.Add((size, ms.ToArray()));
}

var outputPath = args.Length > 0 ? args[0] : "aiusage.ico";
using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
using (var writer = new BinaryWriter(fs))
{
    // ICONDIR
    writer.Write((ushort)0);      // reserved
    writer.Write((ushort)1);      // type = icon
    writer.Write((ushort)pngFrames.Count);

    var dataOffset = 6 + 16 * pngFrames.Count;
    var offsets = new List<int>();
    foreach (var (size, png) in pngFrames)
    {
        offsets.Add(dataOffset);
        dataOffset += png.Length;
    }

    for (var i = 0; i < pngFrames.Count; i++)
    {
        var (size, png) = pngFrames[i];
        writer.Write((byte)(size >= 256 ? 0 : size)); // width (0 = 256)
        writer.Write((byte)(size >= 256 ? 0 : size)); // height (0 = 256)
        writer.Write((byte)0);   // color palette
        writer.Write((byte)0);   // reserved
        writer.Write((ushort)1); // color planes
        writer.Write((ushort)32);// bits per pixel
        writer.Write((uint)png.Length);
        writer.Write((uint)offsets[i]);
    }

    foreach (var (_, png) in pngFrames)
    {
        writer.Write(png);
    }
}

Console.WriteLine($"Wrote {outputPath} with {pngFrames.Count} frames: {string.Join(", ", sizes)}");
