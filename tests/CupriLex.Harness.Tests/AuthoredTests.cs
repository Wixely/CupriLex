using CupriLex.Compiler;
using Xunit;

namespace CupriLex.Harness.Tests;

/// <summary>
/// What the stylesheet already says, and the rule that it must never be guessed.
///
/// <para>A tween starts from wherever the element already is, and for 112 animations across 45
/// corpus blocks that was a value written in the document's own CSS. Reading it wrong is worse
/// than not reading it: an animation from the wrong place looks like motion and lands somewhere
/// nothing asked for, where a flat one at least shows up in the refusals. So every case below that
/// cannot be resolved has to come back null.</para>
/// </summary>
public class AuthoredTests
{
    private static Authored Of(string head, string body) =>
        Authored.Of($"<html><head><style>{head}</style></head><body>{body}</body></html>");

    // ---- the cascade --------------------------------------------------------------------------

    [Fact]
    public void A_class_rule_gives_the_start_value()
    {
        var authored = Of(".a { opacity: 0; }", """<div class="a"></div>""");

        Assert.Equal(new Amount(0, ""), authored.Value(".a", "opacity"));
    }

    [Fact]
    public void Nothing_stated_is_answered_with_nothing()
    {
        var authored = Of(".a { color: red; }", """<div class="a"></div>""");

        Assert.Null(authored.Value(".a", "opacity"));
    }

    [Fact]
    public void A_selector_matching_no_element_is_answered_with_nothing()
    {
        // The commonest shape in this corpus: the element is built by the JavaScript that has
        // just been removed, so the document has nothing to read.
        var authored = Of(".a { opacity: 0; }", "<div class=\"b\"></div>");

        Assert.Null(authored.Value(".a", "opacity"));
    }

    [Fact]
    public void Specificity_beats_source_order()
    {
        // The later rule is the weaker one. Taking the last match would read 1 and emit a tween
        // that starts fully visible, which is the bug this type exists to fix, restated.
        var authored = Of("#hero.a { opacity: 0; } .a { opacity: 1; }",
            """<div id="hero" class="a"></div>""");

        Assert.Equal(new Amount(0, ""), authored.Value("#hero", "opacity"));
    }

    [Fact]
    public void Source_order_breaks_a_tie()
    {
        var authored = Of(".a { opacity: 0; } .b { opacity: 0.5; }",
            """<div class="a b"></div>""");

        Assert.Equal(new Amount(0.5, ""), authored.Value(".a", "opacity"));
    }

    [Fact]
    public void An_inline_style_outranks_every_rule()
    {
        // 187 corpus blocks and the start value is as often in a style attribute as in a rule.
        var authored = Of("#hero { opacity: 1; }",
            """<div id="hero" style="opacity: 0"></div>""");

        Assert.Equal(new Amount(0, ""), authored.Value("#hero", "opacity"));
    }

    [Fact]
    public void A_rule_inside_a_media_query_is_seen()
    {
        // Without descending into grouping rules a block that states its start values inside a
        // media query looks like a block that states none, and the flat animation comes back with
        // nothing in the report to say why.
        var authored = Of("@media (min-width: 1px) { .a { opacity: 0; } }",
            """<div class="a"></div>""");

        Assert.Equal(new Amount(0, ""), authored.Value(".a", "opacity"));
    }

    [Fact]
    public void Two_elements_that_disagree_are_answered_with_nothing()
    {
        // GSAP starts each element from its own value; one @keyframes rule can hold one start.
        // There is no single answer here, so there is no answer.
        var authored = Of(".a { opacity: 0; } #second { opacity: 1; }",
            """<div class="a"></div><div class="a" id="second"></div>""");

        Assert.Null(authored.Value(".a", "opacity"));
    }

    [Fact]
    public void Two_elements_that_agree_are_answered()
    {
        var authored = Of(".a { opacity: 0; }",
            """<div class="a"></div><div class="a"></div>""");

        Assert.Equal(new Amount(0, ""), authored.Value(".a", "opacity"));
    }

    // ---- transforms ---------------------------------------------------------------------------

    [Fact]
    public void A_transform_is_read_as_components()
    {
        var authored = Of(".a { transform: translate(-50%, -50%) scale(0); }",
            """<div class="a"></div>""");

        Assert.Equal(new Amount(-50, "%"), authored.Value(".a", "translateX"));
        Assert.Equal(new Amount(-50, "%"), authored.Value(".a", "translateY"));
        Assert.Equal(new Amount(0, ""), authored.Value(".a", "scale"));
    }

    [Fact]
    public void A_transform_this_cannot_read_abandons_the_whole_declaration()
    {
        // Half a transform is not a smaller truth, it is a different position on the screen. The
        // scale here is perfectly readable and is thrown away with the matrix beside it.
        var authored = Of(".a { transform: scale(0) matrix(1,0,0,1,0,0); }",
            """<div class="a"></div>""");

        Assert.Null(authored.Value(".a", "scale"));
    }

    [Fact]
    public void Transform_none_is_an_answer_and_not_an_absence()
    {
        Assert.NotNull(Transform.Parse("none"));
        Assert.Empty(Transform.Parse("none")!);
    }

    [Theory]
    [InlineData("translateX(40px)", "translateX", 40, "px")]
    [InlineData("translateY(-10%)", "translateY", -10, "%")]
    [InlineData("scale(0.5)", "scale", 0.5, "")]
    [InlineData("scaleY(2)", "scaleY", 2, "")]
    [InlineData("rotate(45deg)", "rotate", 45, "deg")]
    [InlineData("rotate(0.5turn)", "rotate", 180, "deg")]
    [InlineData("skewX(10deg)", "skewX", 10, "deg")]
    public void Transform_functions_become_the_compilers_own_components(
        string css, string component, double number, string unit)
    {
        var parsed = Transform.Parse(css);

        Assert.NotNull(parsed);
        Assert.Equal(new Amount(number, unit), parsed[component]);
    }

    [Theory]
    [InlineData("matrix(1, 0, 0, 1, 0, 0)")]
    [InlineData("translate3d(1px, 2px, 3px)")]
    [InlineData("perspective(500px)")]
    [InlineData("translateX(var(--x))")]
    [InlineData("translateX(2em)")]          // a length that needs a layout to resolve
    [InlineData("scale(1, 2, 3)")]           // a form scale() does not have
    public void A_transform_that_cannot_be_resolved_exactly_is_refused(string css)
    {
        Assert.Null(Transform.Parse(css));
    }

    [Fact]
    public void Scale_with_two_arguments_is_two_components_and_not_one()
    {
        // Folding it into `scale` would silently drop whichever axis differs.
        var parsed = Transform.Parse("scale(2, 3)");

        Assert.NotNull(parsed);
        Assert.Equal(new Amount(2, ""), parsed["scaleX"]);
        Assert.Equal(new Amount(3, ""), parsed["scaleY"]);
        Assert.False(parsed.ContainsKey("scale"));
    }

    // ---- what it changes about the output -----------------------------------------------------

    private static string Css(string script, string markup) =>
        Translator.Of($"<html><body>{markup}<script>{script}</script></body></html>").Motion.Css;

    [Fact]
    public void A_tween_to_full_opacity_from_an_element_authored_hidden_actually_fades()
    {
        var css = Css("""const tl = gsap.timeline(); tl.to(".a", { opacity: 1, duration: 1 });""",
            """<div class="a" style="opacity: 0"></div>""");

        Assert.Contains("opacity: 0;", css);
        Assert.Contains("opacity: 1;", css);
    }

    [Fact]
    public void Without_a_stated_start_the_old_assumption_still_holds()
    {
        // An element that states nothing is assumed to be at full opacity, which is what an
        // untouched element is. The flat animation and its refusal are still the right answer.
        var css = Css("""const tl = gsap.timeline(); tl.to(".a", { opacity: 1, duration: 1 });""",
            """<div class="a"></div>""");

        Assert.DoesNotContain("opacity: 0;", css);
    }

    [Fact]
    public void An_authored_transform_component_no_tween_touches_is_carried_into_every_stop()
    {
        // Without this the emitted `transform` replaces the authored one and the centring is
        // lost: the element animates correctly and sits half its own size away for the whole
        // composition.
        var css = Css("""const tl = gsap.timeline(); tl.to(".a", { scale: 1, duration: 1 });""",
            """<div class="a" style="transform: translate(-50%, -50%) scale(0)"></div>""");

        Assert.Contains("translateX(-50%)", css);
        Assert.Contains("translateY(-50%)", css);
        Assert.Contains("scale(0)", css);
        Assert.Contains("scale(1)", css);
    }

    [Fact]
    public void A_start_in_the_wrong_unit_is_not_used()
    {
        // The stylesheet says -50% and the tween moves x in pixels. One keyframe holds one number
        // and one unit, so starting at -50px because the author wrote -50% would put the element
        // somewhere nothing asked for.
        var css = Css("""const tl = gsap.timeline(); tl.to(".a", { x: 100, duration: 1 });""",
            """<div class="a" style="transform: translateX(-50%)"></div>""");

        Assert.DoesNotContain("translateX(-50px)", css);
    }
}
