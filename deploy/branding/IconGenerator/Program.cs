using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Don.Branding;

/// <summary>
/// Renders the Don mark to a multi-resolution Windows icon.
/// </summary>
/// <remarks>
/// <para>
/// Every path string below is copied verbatim from
/// <c>Resources/Branding/don-mark.svg</c>. WPF's geometry mini-language accepts
/// SVG path syntax, so the two files hold the same drawing rather than two
/// drawings that have to be kept in step by hand.
/// </para>
/// <para>
/// The mark is taller than it is wide and an icon is square, so the drawing is
/// measured and fitted rather than positioned by hand: whatever the geometry
/// turns out to span, it is scaled to fill the frame with a small margin and
/// centred. Editing the paths therefore cannot push the mark off the edge.
/// </para>
/// <para>
/// Two things change with size. Strokes are widened as the icon shrinks, because
/// a 3-unit string at 16 pixels is a third of a pixel and vanishes. And below 32
/// pixels the shadow planes and the knuckle line are dropped: at that size they
/// are not shading, they are dirt.
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>Sizes Windows actually asks for, largest first.</summary>
    private static readonly int[] Sizes = [256, 128, 64, 48, 32, 24, 16];

    /// <summary>Fraction of the frame left empty around the mark.</summary>
    /// <remarks>
    /// The mark sits on a rounded plate, so this is the plate's inner padding.
    /// A mark that touches the corner radius looks like it is falling out of it.
    /// </remarks>
    private const double Margin = 0.13;

    private static readonly Brush White = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA));
    private static readonly Brush Crimson = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly Color Black = Color.FromRgb(0x09, 0x09, 0x0B);

    // A hand gripping the control bar from above: cuff, back of the hand, then
    // three fingers hooking down OVER the bar. The layering is the whole trick —
    // the fingers are drawn after the bar, so they read as in front of it, and a
    // hand in front of a bar is unmistakably a hand holding it.
    private const string Cuff = "M84 14 H124 L128 38 H80 Z";
    private const string BackOfHand = "M62 40 H146 L150 62 L146 80 H66 L58 62 Z";
    private const string Bar = "M26 92 H174 A7 7 0 0 1 174 106 H26 A7 7 0 0 1 26 92 Z";
    private const string FingerLeft = "M68 74 H88 V104 Q88 114 78 114 Q68 114 68 104 Z";
    private const string FingerMiddle = "M94 74 H114 V108 Q114 118 104 118 Q94 118 94 108 Z";
    private const string FingerRight = "M120 74 H140 V102 Q140 112 130 112 Q120 112 120 102 Z";
    private const string Thumb = "M60 66 L46 84 L44 100 L56 106 L70 88 L70 70 Z";
    private const string Strings = "M52 106 L100 162 M100 118 L100 162 M148 106 L100 162";
    private const string Shield = "M68 162 H132 V186 C132 201 119 210 100 217 C81 210 68 201 68 186 Z";

    private const string ShadowCuff = "M108 14 H124 L128 38 H108 Z";
    private const string ShadowBackOfHand = "M124 40 H146 L150 62 L146 80 H124 Z";
    private const string ShadowBar = "M120 92 H174 A7 7 0 0 1 174 106 H120 Z";
    private const string ShadowFingerLeft = "M81 74 H88 V104 Q88 114 78 114 Z";
    private const string ShadowFingerMiddle = "M107 74 H114 V108 Q114 118 104 118 Z";
    private const string ShadowFingerRight = "M133 74 H140 V102 Q140 112 130 112 Z";
    private const string ShadowThumb = "M52 76 L44 100 L56 106 L62 94 Z";
    private const string ShadowShield = "M132 162 V186 C132 201 119 210 100 217 V162 Z";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: IconGenerator <output.ico>");
            return 1;
        }

        var frames = Sizes.Select(Render).ToArray();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
        WriteIcon(args[0], frames);

        Console.WriteLine($"Wrote {args[0]}");

        foreach (var (size, bytes) in Sizes.Zip(frames))
        {
            Console.WriteLine($"  {size,3}px  {bytes.Length,7:N0} bytes");
        }

        return 0;
    }

    /// <summary>
    /// Draws the mark at one size and encodes it as a PNG.
    /// </summary>
    /// <param name="size">Edge length in pixels.</param>
    /// <returns>The encoded frame.</returns>
    private static byte[] Render(int size)
    {
        var drawing = BuildDrawing(size);

        // Measured after drawing, so the fit follows the geometry rather than a
        // hand-maintained guess at its extent.
        var bounds = drawing.Bounds;
        var usable = size * (1 - (2 * Margin));
        var scale = usable / Math.Max(bounds.Width, bounds.Height);

        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            // The mark is white on black by construction, so the icon carries its
            // own field. Drawn on transparency the strings would be invisible
            // against every light background Windows puts an icon on.
            context.DrawRoundedRectangle(
                new SolidColorBrush(Black),
                null,
                new Rect(0, 0, size, size),
                size * 0.22,
                size * 0.22);

            context.PushTransform(new TranslateTransform(
                (size - (bounds.Width * scale)) / 2, (size - (bounds.Height * scale)) / 2));

            context.PushTransform(new ScaleTransform(scale, scale));
            context.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));

            context.DrawDrawing(drawing);

            context.Pop();
            context.Pop();
            context.Pop();
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var buffer = new MemoryStream();
        encoder.Save(buffer);

        return buffer.ToArray();
    }

    /// <summary>
    /// Assembles the mark at its natural coordinates.
    /// </summary>
    /// <param name="size">Target icon size, which decides how much detail survives.</param>
    /// <returns>The drawing, unscaled and unpositioned.</returns>
    private static Drawing BuildDrawing(int size)
    {
        var group = new DrawingGroup();

        // Below this, shading is indistinguishable from noise.
        var shaded = size >= 48;

        // Held at roughly one device pixel however far the mark is scaled down.
        var stringWidth = Math.Max(3.0, 3.0 * (48.0 / size));

        // Behind the bar.
        foreach (var path in new[] { Cuff, BackOfHand })
        {
            group.Children.Add(new GeometryDrawing(White, null, Geometry.Parse(path)));
        }

        if (shaded)
        {
            AddShadow(group, ShadowCuff, 0.28);
            AddShadow(group, ShadowBackOfHand, 0.28);
        }

        group.Children.Add(new GeometryDrawing(White, null, Geometry.Parse(Bar)));

        if (shaded)
        {
            AddShadow(group, ShadowBar, 0.30);
        }

        // In front of it, which is what makes this a grip rather than a stack.
        foreach (var path in new[] { Thumb, FingerLeft, FingerMiddle, FingerRight })
        {
            group.Children.Add(new GeometryDrawing(
                White, new Pen(new SolidColorBrush(Black), 2.4), Geometry.Parse(path)));
        }

        if (shaded)
        {
            AddShadow(group, ShadowThumb, 0.24);
            AddShadow(group, ShadowFingerLeft, 0.32);
            AddShadow(group, ShadowFingerMiddle, 0.32);
            AddShadow(group, ShadowFingerRight, 0.32);
        }

        group.Children.Add(new GeometryDrawing(
            null,
            new Pen(White, stringWidth) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
            Geometry.Parse(Strings)));

        group.Children.Add(new GeometryDrawing(Crimson, null, Geometry.Parse(Shield)));

        if (shaded)
        {
            AddShadow(group, ShadowShield, 0.22);
        }

        return group;
    }

    /// <summary>Lays a shadow plane over the lit form.</summary>
    /// <param name="group">The drawing being assembled.</param>
    /// <param name="path">The plane's geometry.</param>
    /// <param name="opacity">How far the form has turned away.</param>
    private static void AddShadow(DrawingGroup group, string path, double opacity) =>
        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Black) { Opacity = opacity }, null, Geometry.Parse(path)));

    /// <summary>
    /// Writes the frames into an <c>.ico</c> container.
    /// </summary>
    /// <param name="path">Where to write.</param>
    /// <param name="frames">PNG-encoded frames, in the order of <see cref="Sizes"/>.</param>
    /// <remarks>
    /// PNG payloads rather than device-independent bitmaps. Windows has accepted
    /// them since Vista, they carry their own alpha without the AND mask a DIB
    /// needs, and a 256 pixel DIB frame would be a megabyte on its own.
    /// </remarks>
    private static void WriteIcon(string path, IReadOnlyList<byte[]> frames)
    {
        using var file = File.Create(path);
        using var writer = new BinaryWriter(file);

        writer.Write((ushort)0);              // reserved
        writer.Write((ushort)1);              // type: icon
        writer.Write((ushort)frames.Count);

        // Every entry is 16 bytes, and they all precede the image data.
        var offset = 6 + (frames.Count * 16);

        for (var index = 0; index < frames.Count; index++)
        {
            var size = Sizes[index];

            // 256 is written as 0: the field is one byte and 256 does not fit.
            writer.Write((byte)(size == 256 ? 0 : size));
            writer.Write((byte)(size == 256 ? 0 : size));
            writer.Write((byte)0);            // palette size: none
            writer.Write((byte)0);            // reserved
            writer.Write((ushort)1);          // colour planes
            writer.Write((ushort)32);         // bits per pixel
            writer.Write(frames[index].Length);
            writer.Write(offset);

            offset += frames[index].Length;
        }

        foreach (var frame in frames)
        {
            writer.Write(frame);
        }
    }
}
