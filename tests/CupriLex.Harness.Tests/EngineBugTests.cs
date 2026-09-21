using CupriFace;
using CupriFace.Svg;
using Xunit;

namespace CupriLex.Harness.Tests;

/// <summary>
/// Engine behaviour this repository used to work around, now asserted the other way up.
///
/// <para>Every test here was originally written as its own opposite. They pinned the conditions
/// that two rewrite rules existed to dodge - a colour that crashed the whole document, an overlay
/// with no size - and they were built to FAIL on the day the engine stopped needing them, because
/// a rewrite that is no longer needed produces output that differs from what the author wrote for
/// no reason.</para>
///
/// <para>That day was CupriFace 0.26.2 and 0.27.0. Both rules have been deleted. The tests stay,
/// flipped, because the conformance matrix only covers the shapes it has cases for, and two of
/// these crashes - a multi-layer background and a <c>drop-shadow</c> - are not among them. A
/// regression in either would otherwise stay silent until a corpus run.</para>
/// </summary>
public class EngineBugTests
{
    private static string Document(string declaration) => $$"""
        <div class="probe">text</div>
        <style>
          :root { --plain: #c6ad90; --spaced: rgba(255, 255, 255, 0.08); }
          .probe { width: 100px; height: 40px; {{declaration}} }
        </style>
        """;

    private static Exception? Render(string declaration)
    {
        try
        {
            using var document = CupriDocument.Load(Document(declaration));
            document.UseSvg();
            document.Settle(200, 100, TimeSpan.FromSeconds(5));
            using var image = document.RenderToImage(200, 100);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>
    /// A colour written the way every CSS formatter writes it.
    ///
    /// <para>Through 0.26.1 this threw <c>ArgumentOutOfRangeException</c> with a substring length
    /// of -6 and took the whole document with it, so <b>35 of 187 corpus blocks could not be
    /// loaded at all</b>. Three separate parsers reached the same fault by splitting a value
    /// badly: the <c>border</c> shorthand, <c>ParseGradient</c> and <c>ParseFilterOps</c>. Fixed in
    /// 0.26.2 (#196), in the shorthand and in <c>Colors.TryParse</c> itself.</para>
    /// </summary>
    [Theory]
    [InlineData("border: 1px solid rgba(198, 173, 144, 0.32);")]
    [InlineData("border: 1px solid rgba(198,173,144,0.32);")]
    [InlineData("border: 1px solid var(--spaced);")]
    [InlineData("border: 1px solid #c6ad9051;")]
    [InlineData("border-width: 1px; border-style: solid; border-color: rgba(198, 173, 144, 0.32);")]
    public void A_border_colour_in_any_spelling_builds_the_document(string declaration) =>
        Assert.Null(Render(declaration));

    /// <summary>The second of the three callers: <c>ParseGradient</c> took everything between the
    /// value's first <c>(</c> and its last <c>)</c>, so two layers were read as one gradient whose
    /// parentheses no longer balanced.</summary>
    [Fact]
    public void A_multi_layer_background_ending_in_a_colour_builds_the_document()
    {
        Assert.Null(Render(
            "background: radial-gradient(circle, rgba(255, 255, 255, 0.32), transparent 42%), "
            + "rgba(255, 255, 255, 0.14);"));
    }

    /// <summary>The third, and the one that decided the shape of the rewrite while it existed: the
    /// filter parser's own regular expression stopped at the first <c>)</c>, so the colour lost its
    /// tail with or without spaces. Unspacing did not help, which is why the rule had to convert
    /// colours to hex rather than merely tidy them.</summary>
    [Theory]
    [InlineData("filter: drop-shadow(0 0 4px rgba(0, 0, 0, 0.5));")]
    [InlineData("filter: drop-shadow(0 0 4px rgba(0,0,0,0.5));")]
    [InlineData("filter: drop-shadow(0 0 4px #00000080);")]
    public void A_drop_shadow_with_a_colour_builds_the_document(string declaration) =>
        Assert.Null(Render(declaration));

    /// <summary>
    /// An angle bracket inside a stylesheet does not break rendering, whatever the diagnostic says.
    ///
    /// <para>Kept because the wrong answer was convincing. <c>split-flap-board</c> rendered as an
    /// empty white frame and the engine's only complaint was <c>CF0010: &lt;selector&gt; is never
    /// closed</c>, pointing at a CSS comment that mentions one. The smallest document reproducing
    /// that diagnostic paints perfectly well, so the diagnostic was a false lead and the blank
    /// frame had another cause.</para>
    /// </summary>
    [Fact]
    public void An_angle_bracket_in_a_style_comment_is_harmless_despite_the_diagnostic()
    {
        const string html = """
            <div class="probe"></div>
            <style>
              /* written as `.board <selector>` at render */
              .probe { width: 100px; height: 40px; background: #d9642a; }
            </style>
            """;

        using var document = CupriDocument.Load(html);
        document.Settle(200, 100, TimeSpan.FromSeconds(5));
        using var image = document.RenderToImage(200, 100);
        using var bitmap = SkiaSharp.SKBitmap.FromImage(image);

        var pixels = bitmap.GetPixelSpan();
        var painted = false;
        for (var i = 0; i + 3 < pixels.Length; i += 4)
            if (pixels[i] != 0xFF || pixels[i + 1] != 0xFF || pixels[i + 2] != 0xFF) painted = true;

        Assert.True(painted,
            "an angle bracket in a style comment now empties the document, which it did not before");
    }
}
