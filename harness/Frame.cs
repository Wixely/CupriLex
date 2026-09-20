using SkiaSharp;

namespace CupriLex.Harness;

/// <summary>One rendered frame, as straight (non-premultiplied) RGBA, whichever renderer made it.
/// Both sides are decoded by the same Skia so a comparison is never measuring two codecs.</summary>
public sealed record Frame(int Width, int Height, byte[] Rgba)
{
    private static readonly SKImageInfo Info8888 =
        new(0, 0, SKColorType.Rgba8888, SKAlphaType.Unpremul);

    public static Frame Decode(byte[] encoded)
    {
        using var decoded = SKBitmap.Decode(encoded)
            ?? throw new InvalidDataException("not a decodable image");
        return FromBitmap(decoded);
    }

    public static Frame FromImage(SKImage image)
    {
        using var bitmap = SKBitmap.FromImage(image);
        return FromBitmap(bitmap);
    }

    private static Frame FromBitmap(SKBitmap bitmap)
    {
        // Copied into a known layout rather than trusted: a browser PNG decodes to whatever the
        // file says and the engine hands back premultiplied BGRA on some platforms. Comparing
        // those byte for byte would report differences that are purely storage.
        var info = Info8888.WithSize(bitmap.Width, bitmap.Height);
        using var normalised = new SKBitmap(info);

        if (!bitmap.CopyTo(normalised, info.ColorType))
            throw new InvalidOperationException("could not normalise a frame to RGBA8888");

        return new Frame(info.Width, info.Height, normalised.GetPixelSpan().ToArray());
    }

    public SKBitmap ToBitmap()
    {
        var bitmap = new SKBitmap(Info8888.WithSize(Width, Height));
        Rgba.CopyTo(bitmap.GetPixelSpan());
        return bitmap;
    }

    public void Save(string path)
    {
        using var bitmap = ToBitmap();
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        data.SaveTo(file);
    }
}

/// <summary>How close two frames are.</summary>
/// <param name="Similarity">1.0 for identical frames. One minus the mean absolute difference per
/// colour channel, over every pixel. Blunt on purpose: it is a number that can be averaged, and
/// the diff image beside it is what says WHERE.</param>
/// <param name="Differing">The share of pixels visibly different - any channel off by more than
/// <see cref="Threshold"/>. The honest companion to similarity: a frame that is nine tenths flat
/// background scores well on similarity while being wrong everywhere that matters.</param>
public sealed record Comparison(double Similarity, double Differing)
{
    /// <summary>Eight levels out of 255. Above the noise two rasterisers make of the same edge,
    /// below anything a person would call the same colour.</summary>
    public const int Threshold = 8;

    public static readonly Comparison Identical = new(1.0, 0.0);

    /// <summary>Frames of different sizes are not compared. It means one renderer was asked for a
    /// size the other was not, which is a bug in the caller rather than a bad score.</summary>
    public static Comparison Of(Frame a, Frame b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
            throw new ArgumentException(
                $"cannot compare {a.Width}x{a.Height} with {b.Width}x{b.Height}");

        long error = 0;
        long differing = 0;
        var pixels = (long)a.Width * a.Height;

        var left = a.Rgba;
        var right = b.Rgba;

        for (long i = 0; i + 3 < left.LongLength; i += 4)
        {
            // Alpha is ignored. Both renderers are asked for an opaque frame over white, so an
            // alpha channel that disagrees is a storage detail, not a visible difference.
            int dr = Math.Abs(left[i] - right[i]);
            int dg = Math.Abs(left[i + 1] - right[i + 1]);
            int db = Math.Abs(left[i + 2] - right[i + 2]);

            error += dr + dg + db;
            if (dr > Threshold || dg > Threshold || db > Threshold) differing++;
        }

        return new Comparison(
            1.0 - error / (double)(pixels * 3 * 255),
            differing / (double)pixels);
    }
}

/// <summary>The picture that goes with the number: reference, engine, and where they part.</summary>
public static class Diff
{
    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    /// <summary>
    /// Three panels side by side - browser, engine, difference - scaled to <paramref name="panel"/>
    /// pixels wide.
    ///
    /// <para>One file rather than three, because the three are only meaningful together: an engine
    /// frame alone looks plausible, and it is the panel beside it that shows the title never
    /// arrived.</para>
    /// </summary>
    public static void Write(Frame browser, Frame engine, string path, string? caption = null,
        int panel = 640)
    {
        var scale = Math.Min(1.0, panel / (double)browser.Width);
        var w = Math.Max(1, (int)Math.Round(browser.Width * scale));
        var h = Math.Max(1, (int)Math.Round(browser.Height * scale));
        const int gap = 8;
        const int bar = 26;     // room for the captions: three panels of a strange composition are
                                // hard to tell apart, and which one is the engine is the whole point

        using var surface = SKSurface.Create(new SKImageInfo(w * 3 + gap * 2, h + bar));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(0x14, 0x14, 0x18));

        using var browserBitmap = browser.ToBitmap();
        using var engineBitmap = engine.ToBitmap();
        var differenceFrame = Of(browser, engine);
        using var differenceBitmap = differenceFrame.ToBitmap();

        Draw(canvas, browserBitmap, 0, w, h, bar);
        Draw(canvas, engineBitmap, w + gap, w, h, bar);
        Draw(canvas, differenceBitmap, (w + gap) * 2, w, h, bar);

        Label(canvas, "browser" + (caption is null ? "" : "   " + caption), 6);
        Label(canvas, "CupriFace " + Engine.Version, w + gap + 6);
        Label(canvas, "difference", (w + gap) * 2 + 6);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        data.SaveTo(file);
    }

    /// <summary>Magenta on near-black, amplified three times. Amplified because the differences
    /// that matter most are often a few levels of grey over a whole title, and an unamplified diff
    /// of those is a black rectangle.</summary>
    private static Frame Of(Frame a, Frame b)
    {
        var pixels = new byte[a.Rgba.Length];

        for (long i = 0; i + 3 < a.Rgba.LongLength; i += 4)
        {
            var delta = Math.Max(Math.Abs(a.Rgba[i] - b.Rgba[i]),
                Math.Max(Math.Abs(a.Rgba[i + 1] - b.Rgba[i + 1]),
                    Math.Abs(a.Rgba[i + 2] - b.Rgba[i + 2])));

            var lit = (byte)Math.Min(255, delta * 3);
            pixels[i] = lit;
            pixels[i + 1] = (byte)(lit / 4);
            pixels[i + 2] = lit;
            pixels[i + 3] = 255;
        }

        return new Frame(a.Width, a.Height, pixels);
    }

    private static void Draw(SKCanvas canvas, SKBitmap bitmap, int x, int w, int h, int top)
    {
        using var image = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(image, new SKRect(x, top, x + w, top + h), Sampling);
    }

    /// <summary>Best effort. A machine with no usable face still gets the three panels, which is
    /// the part that matters; it is not worth failing a corpus run over a caption.</summary>
    private static void Label(SKCanvas canvas, string text, int x)
    {
        var typeface = SKTypeface.Default;
        if (typeface is null) return;

        using var font = new SKFont(typeface, 13);
        using var paint = new SKPaint { Color = new SKColor(0xC8, 0xC8, 0xD0), IsAntialias = true };
        canvas.DrawText(text, x, 18, SKTextAlign.Left, font, paint);
    }
}
