using System.Text.RegularExpressions;
using AngleSharp.Html.Dom;

namespace CupriLex.Compiler;

/// <summary>What kind of thing a URL would fetch, as far as the document says.</summary>
public enum Fetches
{
    /// <summary>A stylesheet. Worth its own kind because fetching one reveals FURTHER requests -
    /// a font service answers with <c>@font-face</c> rules pointing at files on another host - and
    /// a consent decision that did not know that would be uninformed.</summary>
    Stylesheet,

    /// <summary>A font file.</summary>
    Font,

    /// <summary>An image, a video, a poster frame.</summary>
    Media,

    /// <summary>Anything else the document points at.</summary>
    Other,
}

/// <summary>
/// One request a document would make to a machine that is not this one.
/// </summary>
/// <param name="Url">Exactly as it will be requested.</param>
/// <param name="Kind">What it fetches.</param>
/// <param name="Why">Where in the document it comes from, in words a person can act on.</param>
/// <param name="Discovered">False for a request read straight out of the document; true for one
/// found by fetching something already approved. A host that asked a person about the first list
/// has not asked about these.</param>
public sealed record Request(string Url, Fetches Kind, string Why, bool Discovered = false)
{
    /// <summary>The host, for a consent decision made per site rather than per URL.</summary>
    public string Host =>
        Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
}

/// <summary>
/// Every request a document would make off this machine, listed before any of them is made.
///
/// <para><b>This is a runtime capability, not a build step.</b> A host - this compiler's CLI,
/// CupriCut opening an HTML file, anything else - asks what a document wants, shows the list to
/// whoever is running it, and fetches only what comes back approved. Nothing here fetches
/// anything; it reads the document and says what it would need.</para>
///
/// <para>The list is deliberately readable rather than minimal. "A stylesheet from
/// fonts.googleapis.com, for the fonts the composition asks for" is a sentence somebody can say
/// yes or no to; a bare URL is not, and a person clicking through a list they cannot evaluate is
/// consent in form only.</para>
///
/// <para><b>Scripts are not in it.</b> Translation removes them, so a script src is not a request
/// this tool would ever make - and listing one would invite approval for a fetch that cannot
/// happen.</para>
/// </summary>
public static partial class External
{
    public static IReadOnlyList<Request> Of(IHtmlDocument document)
    {
        var found = new List<Request>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? url, Fetches kind, string why)
        {
            if (Absolute(url) is not { } absolute || !seen.Add(absolute)) return;
            found.Add(new Request(absolute, kind, why));
        }

        foreach (var element in document.All)
        {
            switch (element)
            {
                // A script is removed by translation, so its src is not a request this tool makes.
                case IHtmlScriptElement:
                    continue;

                case IHtmlLinkElement link:
                {
                    var rel = (link.Relation ?? string.Empty).ToLowerInvariant();

                    // preconnect and dns-prefetch are hints to a browser, not fetches. Listing
                    // them would ask a person to approve a request that has no content.
                    if (rel is "preconnect" or "dns-prefetch") continue;

                    Add(link.GetAttribute("href"),
                        rel.Contains("stylesheet") ? Fetches.Stylesheet : Fetches.Other,
                        rel.Contains("stylesheet")
                            ? $"a stylesheet the document links ({Describe(link.GetAttribute("href"))})"
                            : $"a <link rel=\"{rel}\"> in the document");
                    continue;
                }

                case IHtmlImageElement image:
                    Add(image.GetAttribute("src"), Fetches.Media, "an <img> in the document");
                    continue;
            }

            foreach (var name in new[] { "src", "poster" })
                Add(element.GetAttribute(name), Fetches.Media,
                    $"a <{element.LocalName}> {name} in the document");

            if (element.GetAttribute("style") is { Length: > 0 } inline)
                foreach (var url in Urls(inline))
                    Add(url, Kind(url), "a url() in a style attribute");
        }

        foreach (var style in document.QuerySelectorAll("style"))
        {
            foreach (Match import in Import().Matches(style.TextContent))
            {
                var url = import.Groups["url"].Value.Trim('"', '\'', ' ');
                Add(url, Fetches.Stylesheet,
                    $"an @import in the document's stylesheet ({Describe(url)})");
            }

            foreach (var url in Urls(style.TextContent))
                Add(url, Kind(url), "a url() in the document's stylesheet");
        }

        return found;
    }

    /// <summary>A short, honest description of what a well-known URL is for. Only where it can be
    /// said with certainty: a guess in a consent prompt is worse than a bare URL, because it reads
    /// as knowledge.</summary>
    private static string Describe(string? url)
    {
        if (url is null) return "purpose unknown";

        if (url.Contains("fonts.googleapis.com", StringComparison.OrdinalIgnoreCase))
        {
            var families = Family().Matches(url)
                .Select(m => m.Groups["name"].Value.Replace('+', ' '))
                .Distinct()
                .ToArray();

            return families.Length > 0
                ? "Google Fonts: " + string.Join(", ", families)
                : "Google Fonts";
        }

        return "purpose unknown";
    }

    private static Fetches Kind(string url) =>
        url.Contains(".woff", StringComparison.OrdinalIgnoreCase)
        || url.Contains(".ttf", StringComparison.OrdinalIgnoreCase)
        || url.Contains(".otf", StringComparison.OrdinalIgnoreCase)
            ? Fetches.Font
            : Fetches.Media;

    private static IEnumerable<string> Urls(string css) =>
        CssUrl().Matches(css).Select(m => m.Groups["url"].Value.Trim('"', '\'', ' '));

    /// <summary>The URL as it will be requested, or null when it names nothing off this machine.
    /// A protocol-relative URL is resolved to https, which is what it would become in any browser
    /// this corpus was written for.</summary>
    private static string? Absolute(string? url)
    {
        var trimmed = url?.Trim();
        if (trimmed is not { Length: > 0 }) return null;

        if (trimmed.StartsWith("//", StringComparison.Ordinal)) trimmed = "https:" + trimmed;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;

        return uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
    }

    [GeneratedRegex(@"url\(\s*(?<url>[^)]+?)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex CssUrl();

    [GeneratedRegex(@"@import\s+(?:url\(\s*)?(?<url>[^;)]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Import();

    [GeneratedRegex(@"family=(?<name>[^:&]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Family();
}
