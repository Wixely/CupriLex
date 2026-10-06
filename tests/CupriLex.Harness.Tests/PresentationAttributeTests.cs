using CupriLex.Compiler;
using Xunit;

namespace CupriLex.Harness.Tests;

/// <summary>
/// An SVG presentation attribute as the start value of a tween.
///
/// <para>x-post hides its filled heart with <c>&lt;path opacity="0"&gt;</c> and reveals it with
/// <c>.to(heart, { opacity: 1 })</c>. The cascade reader did not read the attribute, so the tween
/// compiled from the assumed 1 to 1: a flat animation holding the heart visible. On an engine
/// that ignored stylesheets inside an svg that was inert; on 0.34.0, which honours them (#262),
/// it was a pink heart from frame zero. Reading the attribute at the bottom of the cascade, which
/// is where the SVG specification puts it, makes the tween the fade the author wrote.</para>
/// </summary>
public class PresentationAttributeTests
{
    private const string Markup = """
        <svg viewBox="0 0 24 24"><path id="heart" d="M0 0h24v24H0z" fill="#f91880" opacity="0"/></svg>
        """;

    [Fact]
    public void An_opacity_attribute_is_the_start_of_a_tween()
    {
        var authored = Authored.Of($"<html><body>{Markup}</body></html>");

        Assert.Equal(new Amount(0, ""), authored.Value("#heart", "opacity"));
    }

    [Fact]
    public void A_stylesheet_rule_outranks_the_attribute()
    {
        var authored = Authored.Of(
            $"<html><head><style>#heart {{ opacity: 0.5; }}</style></head><body>{Markup}</body></html>");

        Assert.Equal(new Amount(0.5, ""), authored.Value("#heart", "opacity"));
    }

    [Fact]
    public void The_hidden_heart_compiles_to_a_fade_and_not_to_a_hold()
    {
        var translated = Translator.Of(
            $"<html><body>{Markup}<script>const tl = gsap.timeline(); "
            + "tl.to(\"#heart\", { opacity: 1, duration: 0.12, ease: \"none\" }, 1.62);</script></body></html>");

        Assert.Contains("opacity: 0;", translated.Motion.Css);
        Assert.Contains("opacity: 1;", translated.Motion.Css);
        Assert.DoesNotContain(translated.Refusals, r => r.What.Contains("held at its end state"));
    }
}
