using CupriFace;
using Xunit;

namespace CupriLex.Harness.Tests;

/// <summary>
/// Engine behaviour this repository has to work around, each with the smallest document that
/// shows it.
///
/// <para>These tests assert the CURRENT behaviour, including where that behaviour is a crash. They
/// are meant to fail when CupriFace is fixed, exactly as a conformance matrix entry is meant to
/// change: a failure here is the build saying a workaround can be removed. See
/// docs/CONFORMANCE.md for why that is the shape these take.</para>
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
    /// CupriFace 0.26.1 throws while parsing a <c>border</c> shorthand whose colour is an
    /// <c>rgb()</c> or <c>rgba()</c> written with spaces after the commas - which is how everyone
    /// writes it, and how a browser's own serialisation writes it back.
    ///
    /// <para>Why: <c>StyleResolver.ParseBorderShorthand</c> splits the value on spaces and offers
    /// each token to the colour parser, so <c>rgba(198, 173, 144, 0.32)</c> arrives as the token
    /// <c>rgba(198,</c>. <c>Colors.TryParse</c> then takes the range between the parentheses with
    /// <c>text[(IndexOf('(') + 1)..IndexOf(')')]</c>, and on a token with no closing parenthesis
    /// that is <c>[5..-1]</c> - a length of -6, which is the exact number in the exception.</para>
    ///
    /// <para>Found by the comparison harness, not by reading: two corpus blocks -
    /// <c>beat-freeze-cut</c> and <c>blue-sweater-intro-video</c> - could not be scored at all
    /// because loading them threw. Not something a rewrite rule should paper over; it is a crash
    /// on ordinary CSS and it belongs in CupriFace's issue list.</para>
    /// </summary>
    [Fact]
    public void A_border_shorthand_with_a_spaced_rgba_colour_crashes_the_engine()
    {
        var thrown = Render("border: 1px solid rgba(198, 173, 144, 0.32);");

        Assert.IsType<ArgumentOutOfRangeException>(thrown);
        Assert.Contains("-6", thrown!.Message);
    }

    /// <summary>The same colour with no spaces survives, which is what says the split is the
    /// trigger rather than the colour function.</summary>
    [Fact]
    public void The_same_rgba_without_spaces_is_fine()
    {
        Assert.Null(Render("border: 1px solid rgba(198,173,144,0.32);"));
    }

    /// <summary>And it reaches blocks that never write <c>rgba</c> next to <c>border</c> at all:
    /// <c>beat-freeze-cut</c> writes <c>border: 1px solid var(--bfc-line)</c>, and the custom
    /// property it names holds <c>rgba(255, 255, 255, 0.08)</c>. Substitution happens first, so
    /// the shorthand parser still meets the spaces.</summary>
    [Fact]
    public void A_var_that_resolves_to_a_spaced_rgba_crashes_the_same_way()
    {
        Assert.IsType<ArgumentOutOfRangeException>(Render("border: 1px solid var(--spaced);"));
    }

    [Fact]
    public void A_border_shorthand_with_a_literal_colour_is_fine()
    {
        Assert.Null(Render("border: 1px solid #c6ad90;"));
    }

    [Fact]
    public void A_var_holding_a_plain_colour_is_fine()
    {
        Assert.Null(Render("border: 1px solid var(--plain);"));
    }

    /// <summary>The same colour on the longhand property does not go through the splitting
    /// shorthand, so it is a workaround as well as a diagnosis.</summary>
    [Fact]
    public void The_longhand_border_color_takes_a_spaced_rgba_without_complaint()
    {
        Assert.Null(Render(
            "border-width: 1px; border-style: solid; border-color: rgba(198, 173, 144, 0.32);"));
    }

    /// <summary>A gradient on its own is fine: <c>ParseGradient</c> splits on commas at the top
    /// level only, so the commas inside a colour do not confuse it. Recorded because the first
    /// guess was that it crashed, and it does not.</summary>
    [Fact]
    public void A_gradient_with_a_spaced_rgba_stop_is_fine()
    {
        Assert.Null(Render("background: linear-gradient(90deg, rgba(255, 0, 0, 1) 0%, #000 100%);"));
    }

    /// <summary>
    /// The second caller, found by a corpus run rather than by reading: a background of several
    /// layers where a gradient is followed by a flat colour.
    ///
    /// <para><c>ParseGradient</c> takes everything between the value's FIRST <c>(</c> and its LAST
    /// <c>)</c>, so the two layers are read as one gradient and its parentheses no longer balance.
    /// The pieces that fall out of that include a bare <c>rgba(255</c>.</para>
    /// </summary>
    [Fact]
    public void A_multi_layer_background_ending_in_a_colour_crashes_the_engine()
    {
        Assert.IsType<ArgumentOutOfRangeException>(Render(
            "background: radial-gradient(circle, rgba(255, 255, 255, 0.32), transparent 42%), "
            + "rgba(255, 255, 255, 0.14);"));
    }

    /// <summary>
    /// The third caller, and the one that decided the shape of the rewrite.
    ///
    /// <para><c>ParseFilterOps</c> matches functions with <c>([\w-]+)\(([^)]*)\)</c>, which
    /// stops at the first closing parenthesis - so the argument list it extracts from
    /// <c>drop-shadow(0 0 4px rgba(0,0,0,0.5))</c> ends mid-colour. Taking the spaces out does not
    /// help here, which is why the rewrite converts colours to hex instead.</para>
    /// </summary>
    [Theory]
    [InlineData("filter: drop-shadow(0 0 4px rgba(0, 0, 0, 0.5));")]
    [InlineData("filter: drop-shadow(0 0 4px rgba(0,0,0,0.5));")]
    public void A_drop_shadow_with_an_rgba_crashes_the_engine_spaces_or_not(string declaration) =>
        Assert.IsType<ArgumentOutOfRangeException>(Render(declaration));

    /// <summary>All three survive a hex colour, which is what the rewrite emits.</summary>
    [Theory]
    [InlineData("border: 1px solid #c6ad9051;")]
    [InlineData("background: radial-gradient(circle, #ffffff52, transparent 42%), #ffffff24;")]
    [InlineData("filter: drop-shadow(0 0 4px #00000080);")]
    public void The_same_declarations_in_hex_are_all_fine(string declaration) =>
        Assert.Null(Render(declaration));

    /// <summary>
    /// An angle bracket inside a stylesheet does NOT break rendering, whatever the diagnostic says.
    ///
    /// <para>Written the other way round first. <c>split-flap-board</c> renders as an empty white
    /// frame and scores 0.0% - the worst in the corpus - and the engine's only complaint about it
    /// is <c>CF0010: &lt;selector&gt; is never closed</c>, pointing at a CSS comment that mentions
    /// <c>[data-composition-id="…"] &lt;selector&gt;</c>. That is a tidy story and it is wrong:
    /// the smallest document that reproduces the diagnostic paints perfectly well, so
    /// <c>CF0010</c> is a false positive here and the blank frame has another cause.</para>
    ///
    /// <para>Kept as a test because the wrong answer was convincing enough to have been built on.
    /// Four blocks write a <c>&lt;</c> inside a <c>&lt;style&gt;</c>, and none of them needs a
    /// rewrite for it.</para>
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
