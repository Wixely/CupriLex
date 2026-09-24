using AngleSharp;
using AngleSharp.Html.Parser;
using CupriLex.Compiler;
using Xunit;

namespace CupriLex.Harness.Tests;

/// <summary>
/// Font stacks trimmed to what a package can answer.
///
/// <para>Measured on CupriFace 0.28.1, and the measurement is the whole reason this exists: a
/// strict font policy refuses a stack that names a face it has not got, <b>even when a registered
/// family follows it</b>. <c>"NoSuchFamily", "Noto Sans", sans-serif</c> fails with Noto Sans
/// registered; <c>sans-serif</c> alone renders. A stack is answered by its names, not by its
/// order.</para>
/// </summary>
public class FallbackTests
{
    private static readonly HtmlParser Parser = new();

    private static (string Html, Dropped Dropped) Trim(string css, params string[] carried)
    {
        var document = Parser.ParseDocument($"<html><head><style>{css}</style></head><body></body></html>");
        var dropped = Fallbacks.Trim(document, new HashSet<string>(carried, StringComparer.OrdinalIgnoreCase));

        return (document.ToHtml(), dropped);
    }

    [Fact]
    public void A_family_nothing_can_answer_is_removed()
    {
        var (html, dropped) = Trim(".a { font-family: \"Segoe UI\", sans-serif; }");

        Assert.DoesNotContain("Segoe UI", html);
        Assert.Contains("sans-serif", html);
        Assert.Equal(1, dropped.Families["Segoe UI"]);
    }

    [Fact]
    public void A_family_the_package_carries_is_kept()
    {
        var (html, dropped) = Trim(".a { font-family: \"Inter\", sans-serif; }", "Inter");

        Assert.Contains("Inter", html);
        Assert.Empty(dropped.Families);
    }

    [Fact]
    public void A_stack_that_loses_everything_gets_a_generic()
    {
        // The browser fell back to the platform default here. A generic is the only way to write
        // that down without naming a typeface nobody chose.
        var (html, dropped) = Trim(".a { font-family: Menlo, Monaco; }");

        Assert.Contains("sans-serif", html);
        Assert.Equal(2, dropped.Families.Count);
    }

    [Fact]
    public void System_keywords_are_treated_as_generics_and_not_as_families()
    {
        // -apple-system appears in 50 corpus blocks and BlinkMacSystemFont in 47. They name no
        // file anywhere; a fetcher sent after them would be looking for a platform.
        var (html, dropped) = Trim(".a { font-family: -apple-system, BlinkMacSystemFont, sans-serif; }");

        Assert.Contains("-apple-system", html);
        Assert.Empty(dropped.Families);
    }

    [Fact]
    public void A_stack_behind_a_custom_property_is_left_alone()
    {
        // Not resolvable here, and half a rewritten stack is worse than an untouched one.
        const string Css = ".a { font-family: var(--face, \"Segoe UI\"); }";
        var (html, dropped) = Trim(Css);

        Assert.Contains("var(--face", html);
        Assert.Empty(dropped.Families);
    }

    [Fact]
    public void A_quoted_family_with_a_comma_in_its_name_survives_the_split()
    {
        var (html, dropped) = Trim(".a { font-family: \"Ogg, Roman\", sans-serif; }", "Ogg, Roman");

        Assert.Contains("Ogg, Roman", html);
        Assert.Empty(dropped.Families);
    }

    [Fact]
    public void An_inline_style_is_trimmed_too()
    {
        var document = Parser.ParseDocument(
            """<html><body><div style="font-family: Menlo, monospace"></div></body></html>""");

        var dropped = Fallbacks.Trim(document, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.DoesNotContain("Menlo", document.ToHtml());
        Assert.Contains("monospace", document.ToHtml());
        Assert.Equal(1, dropped.Families["Menlo"]);
    }

    [Fact]
    public void The_families_a_document_declares_are_found()
    {
        var document = Parser.ParseDocument("""
            <html><head><style>
              @font-face { font-family: "ClaudeSerif"; src: url('a.woff2'); }
              @font-face { font-family: 'ClaudeSans'; src: url('b.woff2'); }
            </style></head><body></body></html>
            """);

        var carried = Fallbacks.Carried(document);

        Assert.Contains("ClaudeSerif", carried);
        Assert.Contains("ClaudeSans", carried);
    }

    [Fact]
    public void A_declared_family_is_kept_in_every_stack_that_names_it()
    {
        var document = Parser.ParseDocument("""
            <html><head><style>
              @font-face { font-family: "ClaudeSerif"; src: url('a.woff2'); }
              .a { font-family: "ClaudeSerif", Georgia, serif; }
            </style></head><body></body></html>
            """);

        var dropped = Fallbacks.Trim(document, Fallbacks.Carried(document));

        Assert.Contains("ClaudeSerif", document.ToHtml());
        Assert.DoesNotContain("Georgia", document.ToHtml());
        Assert.Equal(1, dropped.Families["Georgia"]);
    }

    [Fact]
    public void Nothing_is_rewritten_when_there_is_nothing_to_drop()
    {
        // A stack that is already answerable must come out byte for byte as it went in: this runs
        // over every packaged document, and a rewrite that touches what it need not is a diff
        // nobody can review.
        const string Css = ".a { font-family: serif; }";
        var (html, dropped) = Trim(Css);

        Assert.Contains(Css, html);
        Assert.Equal(0, dropped.Declarations);
    }
}
