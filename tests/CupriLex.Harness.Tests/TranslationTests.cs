using CupriLex.Compiler;
using Xunit;

namespace CupriLex.Harness.Tests;

/// <summary>
/// What the translator does to a document besides compiling its motion: the rewrites, and the
/// references it has to repair.
/// </summary>
public class TranslationTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "cuprilex-tests-" + Guid.NewGuid().ToString("n")[..8]);

    public TranslationTests()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "assets"));
        File.WriteAllBytes(Path.Combine(_directory, "assets", "logo.png"), [0x89, 0x50, 0x4E, 0x47]);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Translate(string body) =>
        Translator.Of($"<html><body>{body}</body></html>", _directory).Html;

    // ---- references -------------------------------------------------------------------------

    /// <summary>A browser resolves <c>assets/logo.png</c> against the document's location.
    /// <c>CupriDocument.Load</c> is handed a string, which has no location, so the same reference
    /// resolves against the working directory and finds nothing.</summary>
    [Fact]
    public void A_relative_asset_becomes_an_absolute_file_url()
    {
        var html = Translate("""<img src="assets/logo.png">""");

        Assert.Contains("file:///", html);
        Assert.Contains("logo.png", html);
    }

    [Fact]
    public void A_relative_url_inside_css_is_rebased_too()
    {
        var html = Translate("<style>.hero { background: url(assets/logo.png); }</style>");

        Assert.Contains("url(file:///", html);
    }

    /// <summary>Everything already resolvable is left as the author wrote it. A rewrite that is
    /// not needed is output that differs from the source for no reason.</summary>
    [Theory]
    [InlineData("""<img src="data:image/png;base64,iVBORw0KGgo=">""", "data:image/png")]
    [InlineData("""<a href="#section">jump</a>""", "#section")]
    [InlineData("""<img src="/site/absolute.png">""", "/site/absolute.png")]
    [InlineData("""<img src="assets/absent.png">""", "assets/absent.png")]
    public void A_reference_that_needs_no_repair_is_left_alone(string body, string expected) =>
        Assert.Contains(expected, Translate(body));

    // ---- rewrites ----------------------------------------------------------------------------

    /// <summary>An <c>rgba()</c> becomes hex, because the engine crashes on the parenthesised
    /// form in three different parsers and a hex colour has no parentheses to break. Measured,
    /// not assumed - see <c>EngineBugTests</c> and the conformance matrix.</summary>
    [Fact]
    public void An_rgba_colour_becomes_hex()
    {
        var html = Translate("<style>.c { border: 1px solid rgba(198, 173, 144, 0.32); }</style>");

        Assert.Contains("#c6ad9051", html);
        Assert.DoesNotContain("rgba(", html);
    }

    /// <summary>
    /// The alpha is truncated, not rounded, because that is what the engine does to its own
    /// <c>rgba()</c>: <c>(byte)(0.32f * 255f)</c> is 81, and the nearest value is 82.
    ///
    /// <para>Rounding cost six text-heavy blocks two and a half points of corpus score each -
    /// a remarkable amount for one level of alpha, and invisible in anything but a frame
    /// comparison. The conformance matrix holds both halves of the pair.</para>
    /// </summary>
    [Fact]
    public void The_alpha_is_truncated_the_way_the_engine_truncates_it()
    {
        Assert.Contains("#e6edf3a5",
            Translate("<style>.c { color: rgba(230, 237, 243, 0.65); }</style>"));
    }

    [Fact]
    public void An_opaque_rgb_becomes_six_hex_digits_rather_than_eight()
    {
        Assert.Contains("#c6ad90", Translate("<style>.c { color: rgb(198, 173, 144); }</style>"));
    }

    /// <summary>The modern space-separated form is left exactly as written: half-understanding it
    /// would be worse than not touching it.</summary>
    [Fact]
    public void The_slash_form_of_rgb_is_left_alone()
    {
        Assert.Contains("rgb(255 0 0 / 50%)",
            Translate("<style>.c { color: rgb(255 0 0 / 50%); }</style>"));
    }

    /// <summary>115 blocks of 187 use <c>inset: 0</c> and the engine ignores it, so the overlay
    /// that should cover the composition has no size at all. The four longhands do not fix it -
    /// that was measured, and it was the obvious answer - but a percentage size does.</summary>
    [Fact]
    public void Inset_zero_becomes_a_percentage_size_rather_than_the_four_longhands()
    {
        var html = Translate("<style>.overlay { position: absolute; inset: 0; }</style>");

        Assert.Contains("width:100%", html);
        Assert.Contains("height:100%", html);
        Assert.DoesNotContain("inset:0", html.Replace(" ", ""));
    }

    /// <summary>Only the zero case. <c>inset: 12px</c> would need a size of
    /// <c>calc(100% - 24px)</c>, which has not been measured against the engine.</summary>
    [Fact]
    public void A_non_zero_inset_is_left_alone_rather_than_guessed_at()
    {
        var html = Translate("<style>.overlay { position: absolute; inset: 12px; }</style>");

        Assert.Contains("inset: 12px", html);
    }

    /// <summary>The content of a template is inert until a host clones it in, and the engine is
    /// not a host. Thirteen blocks put their whole composition inside one.</summary>
    [Fact]
    public void A_template_is_replaced_by_its_content_and_the_report_says_so()
    {
        var translated = Translator.Of(
            """<html><body><template><div class="inside">text</div></template></body></html>""");

        Assert.Contains("class=\"inside\"", translated.Html);
        Assert.DoesNotContain("<template", translated.Html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(translated.Refusals, r => r.What.Contains("template", StringComparison.Ordinal));
    }

    // ---- the seam the harness uses -----------------------------------------------------------

    /// <summary>A block whose motion cannot be read must say so. An empty refusal list means
    /// perfect or lying, and the corpus says which.</summary>
    [Fact]
    public void A_timeline_the_compiler_cannot_follow_is_refused_by_name()
    {
        var translated = Translator.Of("""
            <html><body><div class="a"></div><script>
              const tl = gsap.timeline();
              for (let i = 0; i < 3; i++) { tl.to(".a", { x: i * 10, duration: 1 }); }
            </script></body></html>
            """);

        Assert.NotEmpty(translated.Refusals);
        Assert.Contains(translated.Refusals, r => r.What.Contains(".to()", StringComparison.Ordinal));
    }

    /// <summary>
    /// The same document written as a helper called once IS followed.
    ///
    /// <para>This test replaced one that used the helper as its example of something unfollowable,
    /// which it was until the compiler learned to step into a straight-line call. 331 of the
    /// corpus's 813 declared functions are called exactly once, so this is not an unusual shape;
    /// it is how a person breaks up a long timeline.</para>
    /// </summary>
    [Fact]
    public void A_helper_called_once_in_straight_line_code_is_followed()
    {
        var translated = Translator.Of("""
            <html><body><div class="a"></div><script>
              const tl = gsap.timeline();
              function build(n) { tl.to(".a", { x: n, duration: 1, ease: "none" }); }
              build(10);
            </script></body></html>
            """);

        Assert.Contains("translateX(10px)", translated.Motion.Css);
        Assert.Empty(translated.Refusals);
    }

    /// <summary>A helper that advances a shared clock is writing to the caller's variable. Binding
    /// it locally instead left the caller at zero and put every later tween at the wrong time,
    /// while every one of them still looked perfectly resolvable.</summary>
    [Fact]
    public void A_helper_that_advances_a_shared_time_advances_the_caller_s_copy()
    {
        var translated = Translator.Of("""
            <html><body><div class="a"></div><script>
              let t = 0;
              const tl = gsap.timeline();
              function beat() { tl.to(".a", { x: 10, duration: 0.5, ease: "none" }, t); t += 2; }
              beat();
              beat();
            </script></body></html>
            """);

        // Two beats: the second starts at 2s and runs half a second.
        Assert.Contains("2.5s linear both", translated.Motion.Css);
    }
}
