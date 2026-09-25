using AngleSharp;
using CupriLex.Compiler;
using Xunit;

namespace CupriLex.Harness.Tests;

/// <summary>
/// <c>&lt;img&gt;</c> becoming <c>&lt;cupri-image&gt;</c>, which two documents in this repository
/// claimed for weeks was already happening.
///
/// <para>The engine draws no raw <c>&lt;img&gt;</c>: it lays one out and leaves it empty, and says
/// so as <c>CF0030</c>. 20 of the corpus's 187 packages were shipping exactly that, with the space
/// taken and nothing in it.</para>
/// </summary>
public class ImageTests
{
    private static string Translate(string body) =>
        Translator.Of($"<html><body>{body}</body></html>").Html;

    private static Translated Compile(string body) =>
        Translator.Of($"<html><body>{body}</body></html>");

    [Fact]
    public void An_img_becomes_a_cupri_image()
    {
        var html = Translate("""<img src="logo.png" alt="A logo">""");

        Assert.Contains("<cupri-image", html);
        Assert.DoesNotContain("<img", html);
        Assert.Contains("src=\"logo.png\"", html);
        Assert.Contains("alt=\"A logo\"", html);
    }

    [Fact]
    public void Everything_that_is_not_about_being_an_image_survives()
    {
        // A stylesheet rule and a compiled animation both find the element by these. Rewriting
        // the tag and losing the class would trade one invisible image for a broken layout.
        var html = Translate(
            """<img id="hero" class="a b" style="opacity: 0" data-var-src="brand" src="x.png">""");

        Assert.Contains("id=\"hero\"", html);
        Assert.Contains("class=\"a b\"", html);
        Assert.Contains("opacity: 0", html);
        Assert.Contains("data-var-src=\"brand\"", html);
    }

    [Fact]
    public void Attributes_that_mean_something_only_to_a_browser_are_dropped()
    {
        var html = Translate(
            """<img src="x.png" crossorigin="anonymous" loading="lazy" decoding="async">""");

        Assert.DoesNotContain("crossorigin", html);
        Assert.DoesNotContain("loading", html);
        Assert.DoesNotContain("decoding", html);
    }

    [Fact]
    public void An_object_fit_in_the_style_attribute_becomes_the_fit()
    {
        // object-fit is a CSS property the engine does not support and the component takes the
        // same four values as an attribute. The difference is an image that fills its box as
        // asked versus one that letterboxes.
        var html = Translate("""<img src="x.png" style="object-fit: cover">""");

        Assert.Contains("fit=\"cover\"", html);
    }

    [Fact]
    public void An_object_fit_in_the_stylesheet_is_found_through_the_selector()
    {
        var html = Translator.Of("""
            <html><head><style>.photo { object-fit: cover; }</style></head>
            <body><img class="photo" src="x.png"></body></html>
            """).Html;

        Assert.Contains("fit=\"cover\"", html);
    }

    [Fact]
    public void A_fit_the_component_does_not_take_is_left_to_the_default()
    {
        // scale-down answered with a guess would be worse than contain, which is what the
        // component does when nothing is said.
        var html = Translate("""<img src="x.png" style="object-fit: scale-down">""");

        Assert.DoesNotContain("fit=", html);
    }

    [Fact]
    public void A_srcset_is_refused_by_name_rather_than_half_honoured()
    {
        var compiled = Compile(
            """<img id="hero" src="x.png" srcset="x@2x.png 2x" sizes="100vw">""");

        Assert.DoesNotContain("srcset", compiled.Html);
        Assert.Contains(compiled.Refusals, r => r.What.Contains("srcset", StringComparison.Ordinal));
    }

    [Fact]
    public void An_img_with_no_src_is_reported()
    {
        var compiled = Compile("""<img alt="nothing">""");

        Assert.Contains(compiled.Refusals, r => r.What.Contains("no src", StringComparison.Ordinal));
    }

    [Fact]
    public void A_document_with_no_images_is_untouched()
    {
        var compiled = Compile("""<div class="a">text</div>""");

        Assert.DoesNotContain("cupri-image", compiled.Html);
        Assert.DoesNotContain(compiled.Refusals, r => r.What.Contains("<img", StringComparison.Ordinal));
    }
}
