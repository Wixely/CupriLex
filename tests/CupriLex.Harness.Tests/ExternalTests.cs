using AngleSharp;
using AngleSharp.Html.Parser;
using CupriLex.Compiler;
using Xunit;

namespace CupriLex.Harness.Tests;

/// <summary>
/// What a document would fetch off this machine, and who decides.
///
/// <para>A runtime capability rather than a build step: a host asks what a document wants, shows
/// the list to whoever is running it, and fetches only what comes back approved. These tests hold
/// the two halves that make that trustworthy - the list is complete and honest, and nothing is
/// fetched that was not approved, including the requests that only appear after an approved
/// stylesheet has been read.</para>
/// </summary>
public class ExternalTests
{
    private static readonly HtmlParser Parser = new();

    private static IReadOnlyList<Request> Requests(string html) =>
        External.Of(Parser.ParseDocument(html));

    /// <summary>A fetcher that answers from memory and records what it was asked for. No test here
    /// touches a network: a suite that did would be measuring somebody else's uptime.</summary>
    private sealed class Canned(Dictionary<string, string> answers) : IFetch
    {
        public List<string> Asked { get; } = [];

        public Task<Fetched?> GetAsync(string url, CancellationToken cancel = default)
        {
            Asked.Add(url);

            return Task.FromResult(answers.TryGetValue(url, out var body)
                ? new Fetched(System.Text.Encoding.UTF8.GetBytes(body), "text/css")
                : null);
        }
    }

    // ---- what the list says --------------------------------------------------------------------

    [Fact]
    public void A_linked_stylesheet_is_a_request()
    {
        var found = Requests(
            """
            <html><head><link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Inter">
            </head><body></body></html>
            """);

        var one = Assert.Single(found);
        Assert.Equal(Fetches.Stylesheet, one.Kind);
        Assert.Equal("fonts.googleapis.com", one.Host);
    }

    [Fact]
    public void The_reason_names_the_families_when_it_can_know_them()
    {
        // A person cannot consent to a URL. "Google Fonts: Space Mono, Bebas Neue" is a sentence
        // somebody can say yes or no to.
        var found = Requests(
            """
            <html><head><link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Space+Mono&family=Bebas+Neue">
            </head><body></body></html>
            """);

        Assert.Contains("Space Mono", found[0].Why);
        Assert.Contains("Bebas Neue", found[0].Why);
    }

    [Fact]
    public void An_import_in_the_stylesheet_is_a_request()
    {
        var found = Requests("""
            <html><head><style>@import url("https://fonts.googleapis.com/css2?family=Inter");</style>
            </head><body></body></html>
            """
            );

        Assert.Single(found, r => r.Kind == Fetches.Stylesheet);
    }

    [Fact]
    public void A_preconnect_is_not_a_request()
    {
        // It is a hint to a browser, with no content. Asking a person to approve it would be
        // asking them to approve a fetch that never happens.
        var found = Requests(
            """<html><head><link rel="preconnect" href="https://fonts.gstatic.com"></head><body></body></html>"""
            );

        Assert.Empty(found);
    }

    [Fact]
    public void A_script_src_is_not_a_request()
    {
        // Translation removes scripts, so this is not a fetch this tool would ever make - and
        // listing it would invite approval for something that cannot happen.
        var found = Requests(
            """<html><body><script src="https://cdn.example.com/gsap.js"></script></body></html>"""
            );

        Assert.Empty(found);
    }

    [Fact]
    public void A_relative_reference_is_not_an_external_request()
    {
        var found = Requests(
            """<html><body><img src="assets/logo.png"></body></html>"""
            );

        Assert.Empty(found);
    }

    [Fact]
    public void A_protocol_relative_url_is_listed_as_the_https_it_would_become()
    {
        var found = Requests(
            """<html><body><img src="//example.com/x.png"></body></html>"""
            );

        Assert.Equal("https://example.com/x.png", Assert.Single(found).Url);
    }

    // ---- who decides ----------------------------------------------------------------------------

    [Fact]
    public void Nothing_is_fetched_without_consent()
    {
        var fetch = new Canned([]);
        var document = Parser.ParseDocument(
            """
            <html><head><link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Inter">
            </head><body></body></html>
            """
            );

        var gathered = WebFonts.GatherAsync(document, Consent.None, fetch).Result;

        Assert.Empty(fetch.Asked);
        Assert.Empty(gathered.Faces);
        Assert.Single(gathered.Requested, r => r.Outcome == "not approved");
    }

    [Fact]
    public void Approving_a_stylesheet_does_not_approve_what_it_turns_out_to_name()
    {
        // The reason consent is per URL and not per run. A person approved a stylesheet from one
        // host; the files it names live on another, and they have not been shown those.
        const string Sheet = "https://fonts.googleapis.com/css2?family=Inter";
        const string File = "https://fonts.gstatic.com/s/inter/v1/a.woff2";

        var fetch = new Canned(new Dictionary<string, string>
        {
            [Sheet] = "@font-face { font-family: 'Inter'; font-weight: 400; "
                      + $"src: url({File}) format('woff2'); }}",
        });

        var document = Parser.ParseDocument(
            $"""<html><head><link rel="stylesheet" href="{Sheet}"></head><body></body></html>"""
            );

        var gathered = WebFonts.GatherAsync(document, Consent.Urls([Sheet]), fetch).Result;

        Assert.Contains(Sheet, fetch.Asked);
        Assert.DoesNotContain(File, fetch.Asked);
        Assert.Empty(gathered.Faces);
    }

    [Fact]
    public void Allowing_the_host_covers_what_the_stylesheet_names_on_it()
    {
        const string Sheet = "https://fonts.googleapis.com/css2?family=Inter";
        const string File = "https://fonts.gstatic.com/s/inter/v1/a.woff2";

        var fetch = new Canned(new Dictionary<string, string>
        {
            [Sheet] = "@font-face { font-family: 'Inter'; font-weight: 700; "
                      + $"src: url({File}) format('woff2'); }}",
            [File] = "not really a font, but bytes",
        });

        var document = Parser.ParseDocument(
            $"""<html><head><link rel="stylesheet" href="{Sheet}"></head><body></body></html>"""
            );

        var gathered = WebFonts.GatherAsync(
            document, Consent.Hosts("fonts.googleapis.com", "fonts.gstatic.com"), fetch).Result;

        var face = Assert.Single(gathered.Faces);
        Assert.Equal("Inter", face.Family);
        Assert.Equal(700, face.Weight);
        Assert.Equal("inter-700.woff2", face.Key);
    }

    // ---- what it does to the document ------------------------------------------------------------

    [Fact]
    public void A_fetched_face_replaces_the_link_with_a_local_rule()
    {
        const string Sheet = "https://fonts.googleapis.com/css2?family=Inter";
        const string File = "https://fonts.gstatic.com/s/inter/v1/a.woff2";

        var fetch = new Canned(new Dictionary<string, string>
        {
            [Sheet] = "@font-face { font-family: 'Inter'; font-weight: 400; "
                      + $"src: url({File}) format('woff2'); unicode-range: U+0000-00FF; }}",
            [File] = "bytes",
        });

        var document = Parser.ParseDocument(
            $"""<html><head><link rel="stylesheet" href="{Sheet}"></head><body></body></html>"""
            );

        WebFonts.GatherAsync(document, Consent.All, fetch).Wait();
        var html = document.ToHtml();

        // The point of a package: nothing reaches for a network when this renders.
        Assert.DoesNotContain("googleapis", html);
        Assert.Contains("@font-face", html);
        Assert.Contains("url('inter-400.woff2')", html);
        Assert.Contains("unicode-range: U+0000-00FF", html);
    }

    [Fact]
    public void Subsets_of_one_family_keep_the_range_that_tells_them_apart()
    {
        // A service sends one rule per subset. Without the range they arrive as several rules for
        // the same family and weight that nothing can distinguish, a renderer picks one, and the
        // composition loses whichever characters lived in the others.
        const string Sheet = "https://fonts.googleapis.com/css2?family=Inter";

        var fetch = new Canned(new Dictionary<string, string>
        {
            [Sheet] =
                "@font-face { font-family: 'Inter'; font-weight: 400; "
                + "src: url(https://fonts.gstatic.com/a.woff2) format('woff2'); unicode-range: U+0100-024F; }"
                + "@font-face { font-family: 'Inter'; font-weight: 400; "
                + "src: url(https://fonts.gstatic.com/b.woff2) format('woff2'); unicode-range: U+0000-00FF; }",
            ["https://fonts.gstatic.com/a.woff2"] = "a",
            ["https://fonts.gstatic.com/b.woff2"] = "b",
        });

        var document = Parser.ParseDocument(
            $"""<html><head><link rel="stylesheet" href="{Sheet}"></head><body></body></html>"""
            );

        var gathered = WebFonts.GatherAsync(document, Consent.All, fetch).Result;

        Assert.Equal(2, gathered.Faces.Count);
        Assert.Equal(["inter-400.woff2", "inter-400-2.woff2"], gathered.Faces.Select(f => f.Key));
        Assert.Contains("U+0100-024F", document.ToHtml());
        Assert.Contains("U+0000-00FF", document.ToHtml());
    }

    [Fact]
    public void A_request_that_fails_is_recorded_and_costs_nothing_else()
    {
        var fetch = new Canned([]);
        var document = Parser.ParseDocument(
            """
            <html><head><link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Inter">
            </head><body></body></html>
            """);

        var gathered = WebFonts.GatherAsync(document, Consent.All, fetch).Result;

        Assert.Empty(gathered.Faces);
        Assert.Single(gathered.Requested, r => r.Outcome.Contains("failed"));
    }
}
