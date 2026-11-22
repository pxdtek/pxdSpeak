using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace pxdSpeak;

/// <summary>
/// pxd Speak's icon: a speech bubble with sound bars on a cyan-to-green tile (amber while paused). Vector shapes, so it
/// stays crisp at every size; used for the tray, windows, the banner and the exe's .ico.
/// </summary>
public static class BrandIcon
{
    /// <summary>The bubble and bars in a 100×100 box (bubble filled with <paramref name="color"/>, bars cut out).</summary>
    public static Canvas Art(string color, string bars = "#00000000")
    {
        var c = new Canvas { Width = 100, Height = 100 };
        var brush = new SolidColorBrush(Color.Parse(color));
        // Rounded bubble with a tail at the bottom left.
        c.Children.Add(new ShapePath
        {
            Data = Geometry.Parse("M22,10 L78,10 Q92,10 92,24 L92,62 Q92,76 78,76 L44,76 L24,92 L28,76 L22,76 Q8,76 8,62 L8,24 Q8,10 22,10 Z"),
            Fill = brush,
        });
        var barBrush = new SolidColorBrush(Color.Parse(bars == "#00000000" ? "#0B3A3A" : bars));
        void Bar(double x, double top, double bottom) => c.Children.Add(new ShapePath
        {
            Data = Geometry.Parse(FormattableString.Invariant($"M{x},{top} L{x},{bottom}")),
            Stroke = barBrush, StrokeThickness = 8, StrokeLineCap = PenLineCap.Round,
        });
        Bar(28, 38, 48);
        Bar(41, 28, 58);
        Bar(54, 22, 64);
        Bar(67, 32, 54);
        Bar(78, 39, 47);
        return c;
    }

    public static Control Build(double size, bool paused = false)
    {
        var g = new Grid { Width = size, Height = size };
        g.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(size * 0.22),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse(paused ? "#FFC46B" : "#00C8FF"), 0),
                    new GradientStop(Color.Parse(paused ? "#E0892B" : "#19F58C"), 1),
                },
            },
        });
        g.Children.Add(new Viewbox { Margin = new Thickness(size * 0.13), Child = Art("#FFFFFF", paused ? "#7A4A12" : "#0A6B5A") });
        return g;
    }

    public static RenderTargetBitmap Render(int size, bool paused = false)
    {
        var c = Build(size, paused);
        c.Measure(new Size(size, size));
        c.Arrange(new Rect(0, 0, size, size));
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        bitmap.Render(c);
        return bitmap;
    }

    public static WindowIcon Window(int size = 64, bool paused = false) => new(Render(size, paused));

    /// <summary>Writes a multi-size .ico (PNG entries) for the exe, or a 256 px PNG when the path ends in .png.</summary>
    public static void Write(string path)
    {
        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            using var png = File.Create(path);
            Render(256).Save(png);
            return;
        }

        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        var images = sizes.Select(s =>
        {
            using var ms = new MemoryStream();
            Render(s).Save(ms);
            return ms.ToArray();
        }).ToList();
        using var w = new BinaryWriter(File.Create(path));
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)sizes.Length);
        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            var b = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
            w.Write(b);
            w.Write(b);
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(images[i].Length);
            w.Write(offset);
            offset += images[i].Length;
        }

        foreach (var img in images)
            w.Write(img);
    }
}
