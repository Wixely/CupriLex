using CupriFace;
using CupriFace.Diagnostics;
using SkiaSharp;

namespace CupriLex.Conformance;

/// <summary>What one property, in one value form, turned out to do.</summary>
/// <param name="Property">The CSS property, as an author writes it.</param>
/// <param name="Value">The value probed. Recorded so a "no" can be re-examined rather than
/// trusted: a property that happens to paint identically for the value chosen would read as
/// unsupported, so the value is part of the answer.</param>
/// <param name="Parses">False when the engine's own reader reported a diagnostic naming it.</param>
/// <param name="Paints">Whether the rendered pixels differ from the control. The important
/// column: a property that parses, runs and paints nothing is the failure that costs an
/// afternoon.</param>
/// <param name="Animates">Whether a <c>@keyframes</c> over it produces different frames at the
/// start and the end. Independent of <see cref="Paints"/> - <c>background-color</c> paints
/// perfectly well and does not animate at all.</param>
/// <param name="Diagnostics">Any codes the engine raised, so "does not parse" says why.</param>
public sealed record Support(
    string Property,
    string Value,
    bool Parses,
    bool Paints,
    bool? Animates,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// What this engine actually supports, measured by rendering.
///
/// <para>Three questions per property, because they fail independently and the difference decides
/// the rewrite: does it parse, does it paint, does it animate. See docs/CONFORMANCE.md.</para>
///
/// <para>Nothing here reads release notes or engine source. Every answer is a pixel comparison
/// between two documents that differ only in the thing under test, which is also why the
/// comparison can be exact rather than perceptual.</para>
/// </summary>
public static class Probe
{
    private const int Width = 320;
    private const int Height = 200;

    /// <summary>A face is registered from here when there is one, so text-shaped probes measure
    /// the engine rather than whatever the machine happens to have installed.</summary>
    public static string? FontDirectory { get; set; }

    public static IReadOnlyList<Support> All() => [.. Cases.All.Select(Run)];

    public static Support Run(Case probe)
    {
        var with = Document(probe.With);
        var without = Document(probe.Without);

        // A guard, not a nicety: an earlier version of Case could only vary the declaration, which
        // made several cases byte-identical and had the probe reporting that `width` does not
        // paint. A case that cannot possibly detect anything is a bug in the case.
        if (with == without)
            throw new InvalidOperationException(
                $"'{probe.Property}' ({probe.Value}): the two documents are identical, so this case "
                + "can never detect anything. Make With and Without actually differ.");

        var findings = Diagnose(with, probe.Property).ToList();

        var (a, failed) = Render(with, probe.At);
        var (b, controlFailed) = Render(without, probe.At);

        if (controlFailed is not null)
            throw new InvalidOperationException(
                $"'{probe.Property}' ({probe.Value}): the CONTROL failed to render ({controlFailed}), "
                + "so no difference could be attributed to the property.");

        // The third guard, and it was reporting the exact opposite of the truth. A document the
        // engine THROWS on used to render as null, null differs from the control's pixels, and
        // the matrix said the property painted. `border: 8px solid rgba(217, 100, 42, 1)` crashes
        // CupriFace 0.26.1 outright and was recorded here as "yes, paints" - a silent wrong answer
        // in the one file whose whole job is to not have any.
        if (failed is not null)
        {
            findings.Add("threw while rendering: " + failed);
            return new Support(probe.Property, probe.Value, findings.Count == 0, false, null, findings);
        }

        // The second guard, and the one that caught the real mistake. Bare declarations were
        // being dropped into the stylesheet with no selector, so nothing was styled, both
        // documents rendered empty, and the probe reported that `opacity` does not paint.
        //
        // The question is about the CONTROL specifically: it is the half that is supposed to paint
        // whatever the property under test does. A handful of probes ask "does this appear at all"
        // and have a deliberately empty control - those say so.
        if (!probe.ControlPaintsNothing && Blank(b))
            throw new InvalidOperationException(
                $"'{probe.Property}' ({probe.Value}): the control renders nothing at all, so a "
                + "difference could not be attributed to the property. Check the CSS has a selector "
                + "and applies, or set ControlPaintsNothing if the blank control is the point.");

        var paints = !SamePixels(a, b);

        bool? animates = null;
        if (probe.Animation is { } animation)
        {
            var moving = Document(probe.Without with { Css = probe.Without.Css + animation.Css });
            var (start, startFailed) = Render(moving, 0);
            var (end, endFailed) = Render(moving, animation.Seconds);
            animates = startFailed is null && endFailed is null && !SamePixels(start, end);
        }

        return new Support(probe.Property, probe.Value, findings.Count == 0, paints, animates, findings);
    }

    // ---- the two things every answer is made of ----------------------------------------------

    /// <summary>The engine's own reader, for the parse column. A property it silently accepts and
    /// ignores produces no finding at all, which is exactly why the paint column exists too.</summary>
    private static IReadOnlyList<string> Diagnose(string html, string property)
    {
        var name = property.Trim('<', '>');

        try
        {
            // "" and not null: a null stylesheet turned every CSS check off through 0.25.0, and
            // passing it explicitly is a habit worth keeping.
            return [.. CupriDoctor.Check(html, string.Empty, width: Width, height: Height).Findings
                .Where(f => f.Message.Contains(name, StringComparison.OrdinalIgnoreCase))
                .Select(f => $"{f.Code}: {f.Message}")];
        }
        catch (Exception ex)
        {
            return [$"threw: {ex.GetType().Name}"];
        }
    }

    /// <summary>The pixels, or the reason there are none. The reason is returned rather than
    /// swallowed: a document the engine throws on is a different answer from one that renders and
    /// shows nothing, and collapsing the two is how this matrix came to record a declaration that
    /// crashes the engine as painting.</summary>
    private static (byte[]? Pixels, string? Failed) Render(string html, double t)
    {
        try
        {
            using var doc = CupriDocument.Load(html, null);
            doc.UseComponents(CupriFace.Components.ComponentRegistry.Default());

            if (FontDirectory is { Length: > 0 } dir && Directory.Exists(dir))
                doc.LoadFonts(dir, recursive: true);

            // Settle BEFORE the frame you want. Settling re-lays-out from zero, so animating first
            // and settling after throws the frame away and renders t=0 - which reads exactly like
            // "the animation did not run".
            doc.Animate(0);
            doc.Settle(Width, Height, TimeSpan.FromSeconds(10));
            doc.Animate(t);

            using var image = doc.RenderToImage(Width, Height);
            using var bitmap = SKBitmap.FromImage(image);
            return (bitmap.GetPixelSpan().ToArray(), null);
        }
        catch (Exception ex)
        {
            // A document the engine refuses outright is a real answer, and the strongest kind of
            // no. It is returned rather than swallowed so the matrix can say which exception.
            return (null, $"{ex.GetType().Name}: {ex.Message.Split('\n')[0].Trim()}");
        }
    }

    /// <summary>Whether a frame is nothing but the page background - which means the document
    /// painted nothing, not that the property did nothing.</summary>
    private static bool Blank(byte[]? pixels)
    {
        if (pixels is null) return true;

        // The background is #101014 opaque; any pixel differing from it is something drawn.
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            if (pixels[i] != 0x14 || pixels[i + 1] != 0x10 || pixels[i + 2] != 0x10) return false;
        }
        return true;
    }

    private static bool SamePixels(byte[]? a, byte[]? b) =>
        a is null || b is null ? a is null && b is null : a.AsSpan().SequenceEqual(b);

    private static string Document(Doc doc) => $$"""
        {{doc.Markup}}
        <style>
          body, html { font-family: "Noto Sans"; background: #101014; margin: 0; }
          {{doc.Css}}
        </style>
        """;
}
