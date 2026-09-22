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

    private (byte R, byte G, byte B)? _background;

    /// <summary>
    /// The frame's own background: the colour it uses most.
    ///
    /// <para>Needed because "how much of this is wrong" is a different question from "how much of
    /// the CONTENT is wrong", and on a 1080x1920 composition that paints on 6% of its area the two
    /// answers differ by a factor of fifteen. Taking the modal colour is crude and it is right for
    /// what it is used for: every block in this corpus is a designed composition on a filled
    /// ground, so the most common colour IS the ground.</para>
    ///
    /// <para>Quantised to five bits a channel before counting, so the histogram is 32768 slots
    /// rather than sixteen million, and the winner is then averaged over its own bucket. A
    /// gradient background has no single exact colour but it does have a dominant bucket.</para>
    /// </summary>
    public (byte R, byte G, byte B) Background()
    {
        if (_background is { } known) return known;

        var counts = new int[32768];
        for (long i = 0; i + 3 < Rgba.LongLength; i += 4)
            counts[((Rgba[i] >> 3) << 10) | ((Rgba[i + 1] >> 3) << 5) | (Rgba[i + 2] >> 3)]++;

        var top = 0;
        for (var slot = 1; slot < counts.Length; slot++)
            if (counts[slot] > counts[top]) top = slot;

        // The bucket's centre, not its corner: 5 bits back to 8 with the midpoint added.
        _background = ((byte)(((top >> 10) & 31) << 3 | 4),
            (byte)(((top >> 5) & 31) << 3 | 4),
            (byte)((top & 31) << 3 | 4));

        return _background.Value;
    }

    /// <summary>
    /// The share of this frame's pixels that are not its own background colour.
    ///
    /// <para>How much this renderer PAINTED, asked of one frame alone rather than against
    /// anything. Every other measure here is a comparison, and a comparison cannot tell a
    /// translation that draws the wrong thing from one that draws nothing: both score badly and
    /// they need completely different work. A block whose engine frame is 0.2% ink is not a
    /// fidelity problem.</para>
    /// </summary>
    public double Ink()
    {
        var (r, g, b) = Background();
        long ink = 0;
        for (long i = 0; i + 3 < Rgba.LongLength; i += 4)
        {
            if (Math.Max(Math.Abs(Rgba[i] - r),
                    Math.Max(Math.Abs(Rgba[i + 1] - g), Math.Abs(Rgba[i + 2] - b)))
                > Comparison.Threshold) ink++;
        }
        return ink / (double)((long)Width * Height);
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

/// <summary>How close two frames are, in four numbers that fail differently.</summary>
/// <param name="Similarity">One minus the mean absolute difference per colour channel, over every
/// pixel. Averaged across the identical pixels too, so it collapses toward zero and flatters
/// almost everything.</param>
/// <param name="Differing">The share of pixels visibly different - any channel off by more than
/// <see cref="Threshold"/>. WHERE it is wrong.</param>
/// <param name="ContentDiffering">The same, counted only over the pixels the REFERENCE actually
/// paints something on. A composition on an empty background can lose its whole text and still be
/// wrong on 2% of the frame; this is the number that says a third of the content is missing.</param>
/// <param name="ErrorWhenWrong">The mean divergence among the differing pixels alone, as a
/// fraction of full scale. HOW BADLY it is wrong where it is wrong, which is the axis the other
/// three cannot see: half a frame off by 7% is a different failure from 2% off by 62%, and the
/// share-of-pixels measures rank those two backwards.</param>
/// <param name="Severe">The share of the whole frame off by more than half of full scale. Content
/// that is missing rather than merely shifted.</param>
public sealed record Comparison(
    double Similarity,
    double Differing,
    double ContentDiffering,
    double ErrorWhenWrong,
    double Severe)
{
    /// <summary>Eight levels out of 255. Above the noise two rasterisers make of the same edge,
    /// below anything a person would call the same colour.</summary>
    public const int Threshold = 8;

    /// <summary>Half of full scale. Above this a pixel has not shifted, it has been replaced -
    /// text that is missing rather than text that is rasterised differently.</summary>
    public const int Severity = 128;

    public static readonly Comparison Identical = new(1.0, 0.0, 0.0, 0.0, 0.0);

    /// <summary>
    /// Frames of different sizes are not compared. It means one renderer was asked for a size the
    /// other was not, which is a bug in the caller rather than a bad score.
    /// </summary>
    /// <param name="reference">The frame that defines what SHOULD be there, and therefore which
    /// pixels count as content. In this harness that is always the browser.</param>
    /// <param name="candidate">The frame being judged.</param>
    public static Comparison Of(Frame reference, Frame candidate)
    {
        if (reference.Width != candidate.Width || reference.Height != candidate.Height)
            throw new ArgumentException(
                $"cannot compare {reference.Width}x{reference.Height} with "
                + $"{candidate.Width}x{candidate.Height}");

        var background = reference.Background();
        long error = 0, differing = 0, severe = 0, ink = 0, inkDiffering = 0, worstTotal = 0;
        var pixels = (long)reference.Width * reference.Height;

        var left = reference.Rgba;
        var right = candidate.Rgba;

        for (long i = 0; i + 3 < left.LongLength; i += 4)
        {
            // Alpha is ignored. Both renderers are asked for an opaque frame over white, so an
            // alpha channel that disagrees is a storage detail, not a visible difference.
            int dr = Math.Abs(left[i] - right[i]);
            int dg = Math.Abs(left[i + 1] - right[i + 1]);
            int db = Math.Abs(left[i + 2] - right[i + 2]);

            error += dr + dg + db;

            var worst = Math.Max(dr, Math.Max(dg, db));
            var wrong = worst > Threshold;

            if (wrong)
            {
                differing++;
                worstTotal += worst;
                if (worst > Severity) severe++;
            }

            // Content: where the reference paints something other than its own background.
            if (Math.Max(Math.Abs(left[i] - background.R),
                    Math.Max(Math.Abs(left[i + 1] - background.G),
                        Math.Abs(left[i + 2] - background.B))) > Threshold)
            {
                ink++;
                if (wrong) inkDiffering++;
            }
        }

        // A reference that paints nothing - a solid frame, which several transition blocks show at
        // t=0 - has no content to weight by. Falling back to the frame-wide share is the honest
        // answer; returning zero would have scored every such frame as 100% of content correct
        // whatever the engine drew, which is how a measure quietly becomes a lie.
        var content = ink == 0 ? differing / (double)pixels : inkDiffering / (double)ink;

        return new Comparison(
            1.0 - error / (double)(pixels * 3 * 255),
            differing / (double)pixels,
            content,
            differing == 0 ? 0 : worstTotal / (double)differing / 255,
            severe / (double)pixels);
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
