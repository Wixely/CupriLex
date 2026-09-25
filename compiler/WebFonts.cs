using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Html.Dom;

namespace CupriLex.Compiler;

/// <summary>A face that was fetched and will travel in the package.</summary>
/// <param name="Key">The name the rewritten markup refers to it by, and the asset key.</param>
public sealed record Web(
    string Family, int Weight, string Style, string Key, byte[] Bytes, string From,
    string? Range = null);

/// <summary>What a fetch round produced.</summary>
/// <param name="Faces">Files obtained, ready to be carried.</param>
/// <param name="Requested">Every request considered, with what was decided about it. The record a
/// host shows afterwards: approved and fetched, approved and failed, or never approved.</param>
public sealed record Gathered(
    IReadOnlyList<Web> Faces, IReadOnlyList<(Request Request, string Outcome)> Requested);

/// <summary>
/// External web fonts, fetched with consent and written into the document as local faces.
///
/// <para><b>Two stages, and the second is the reason consent is per URL.</b> A font service answers
/// a stylesheet request with <c>@font-face</c> rules pointing at files on another host. Those files
/// are not knowable until the stylesheet has been fetched, so they are presented as DISCOVERED
/// requests and put through the same consent - a person who approved the stylesheet has not
/// thereby approved whatever it turns out to name.</para>
///
/// <para>What comes back replaces the link. The document stops pointing at a service it can no
/// longer reach and declares the face itself, against a file the package carries, so the render is
/// the same on a machine with no network at all. That is the whole point of a package.</para>
/// </summary>
public static partial class WebFonts
{
    /// <summary>
    /// Fetches what <paramref name="consent"/> allows and rewrites <paramref name="document"/> to
    /// use it.
    /// </summary>
    public static async Task<Gathered> GatherAsync(
        IHtmlDocument document, IConsent consent, IFetch fetch, CancellationToken cancel = default)
    {
        var faces = new List<Web>();
        var log = new List<(Request, string)>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var request in External.Of(document))
        {
            if (request.Kind is not Fetches.Stylesheet)
            {
                log.Add((request, consent.Allows(request)
                    ? "allowed, but only stylesheets are fetched for fonts"
                    : "not approved"));
                continue;
            }

            if (!consent.Allows(request)) { log.Add((request, "not approved")); continue; }

            var sheet = await fetch.GetAsync(request.Url, cancel);
            if (sheet is null) { log.Add((request, "approved, but the request failed")); continue; }

            log.Add((request, "approved and fetched"));

            foreach (var face in Parse(sheet.Text))
            {
                // Discovered, so a host that approved a list of URLs rather than a host has the
                // chance to refuse the files that list turned out to name.
                var found = new Request(face.Url, Fetches.Font,
                    $"the {face.Family} {face.Weight} face, named by {request.Host}", Discovered: true);

                if (!consent.Allows(found)) { log.Add((found, "not approved")); continue; }

                var bytes = await fetch.GetAsync(face.Url, cancel);
                if (bytes is null) { log.Add((found, "approved, but the request failed")); continue; }

                var key = Key(face, keys);
                faces.Add(new Web(
                    face.Family, face.Weight, face.Style, key, bytes.Bytes, face.Url, face.Range));
                log.Add((found, $"approved and fetched, {bytes.Bytes.Length:n0} bytes"));
            }
        }

        if (faces.Count > 0) Rewrite(document, faces);

        return new Gathered(faces, log);
    }

    /// <summary>
    /// Replaces the external references with local <c>@font-face</c> rules.
    ///
    /// <para>The link and the <c>@import</c> go, because after this the document does not need
    /// them and leaving them in would have a renderer reach for a network it may not have. The
    /// preconnect hints go with them: they point at a service nothing asks for any more.</para>
    /// </summary>
    private static void Rewrite(IHtmlDocument document, IReadOnlyList<Web> faces)
    {
        foreach (var link in document.QuerySelectorAll("link").ToArray())
        {
            var href = link.GetAttribute("href") ?? string.Empty;
            if (href.Contains("fonts.googleapis.com", StringComparison.OrdinalIgnoreCase)
                || href.Contains("fonts.gstatic.com", StringComparison.OrdinalIgnoreCase))
                link.Remove();
        }

        foreach (var style in document.QuerySelectorAll("style"))
            style.TextContent = Import().Replace(style.TextContent, string.Empty);

        var css = new StringBuilder();
        css.AppendLine();
        css.AppendLine("/* Fetched by CupriLex and carried in this package. The document asked a");
        css.AppendLine("   font service for these; they are local now, so the render needs no network. */");

        foreach (var face in faces)
        {
            css.AppendLine($"@font-face {{ font-family: \"{face.Family}\"; "
                           + $"font-style: {face.Style}; font-weight: {face.Weight}; "
                           + $"src: url('{face.Key}') format('woff2');"
                           + (face.Range is { Length: > 0 } range
                               ? $" unicode-range: {range};"
                               : string.Empty)
                           + " }");
        }

        var head = document.Head ?? document.Body;
        if (head is null) return;

        var element = document.CreateElement("style");
        element.TextContent = css.ToString();

        // First in the head, so a rule the document already wrote for the same family still wins.
        head.InsertBefore(element, head.FirstChild);
    }

    /// <summary>A filename for the face, unique within the package.</summary>
    private static string Key(Parsed face, HashSet<string> taken)
    {
        var stem = Slug(face.Family) + "-" + face.Weight
                   + (face.Style.Equals("italic", StringComparison.OrdinalIgnoreCase) ? "-italic" : "");

        var key = stem + ".woff2";
        for (var n = 2; !taken.Add(key); n++) key = $"{stem}-{n}.woff2";

        return key;
    }

    private static string Slug(string family)
    {
        var slug = new StringBuilder();

        foreach (var c in family.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) slug.Append(c);
            else if (slug.Length > 0 && slug[^1] != '-') slug.Append('-');
        }

        return slug.ToString().Trim('-') is { Length: > 0 } clean ? clean : "face";
    }

    private sealed record Parsed(
        string Family, int Weight, string Style, string Url, string? Range);

    /// <summary>
    /// The <c>@font-face</c> rules in a fetched stylesheet.
    ///
    /// <para>Only <c>woff2</c> sources are taken. A font service sends several formats and the
    /// engine reads this one; carrying a TTF as well would double the package to no end.</para>
    ///
    /// <para><b>The <c>unicode-range</c> comes with them.</b> A service sends one rule per subset -
    /// latin, latin-ext, vietnamese - and without the range they arrive as several rules for the
    /// same family and weight that nothing can tell apart, so a renderer picks one and a
    /// composition loses whichever characters lived in the others. The range is what makes them
    /// distinguishable, and it is written back out.</para>
    /// </summary>
    private static List<Parsed> Parse(string css)
    {
        var found = new List<Parsed>();

        foreach (Match rule in Block().Matches(css))
        {
            var body = rule.Groups["body"].Value;

            var family = One(body, "font-family")?.Trim().Trim('"', '\'');
            if (family is not { Length: > 0 }) continue;

            var style = One(body, "font-style")?.Trim() ?? "normal";
            var weight = Weight(One(body, "font-weight"));

            var url = Woff2().Match(body) is { Success: true } m
                ? m.Groups["url"].Value.Trim('"', '\'', ' ')
                : null;

            if (url is not { Length: > 0 }) continue;

            // A service sends one rule per subset, all naming the same file for a given weight.
            if (found.Any(f => f.Url == url)) continue;

            found.Add(new Parsed(family, weight, style, url, One(body, "unicode-range")?.Trim()));
        }

        return found;
    }

    /// <summary>A weight range - <c>font-weight: 100 900</c> from a variable font - is recorded by
    /// its first number, because a keyframe of a package needs one number and the file covers the
    /// range anyway.</summary>
    private static int Weight(string? declared)
    {
        if (declared is not { Length: > 0 }) return 400;

        var first = declared.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        if (int.TryParse(first, out var weight) && weight is >= 1 and <= 1000) return weight;

        return first?.Equals("bold", StringComparison.OrdinalIgnoreCase) == true ? 700 : 400;
    }

    private static string? One(string body, string property)
    {
        var match = Regex.Match(body, property + @"\s*:\s*(?<v>[^;]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["v"].Value : null;
    }

    [GeneratedRegex(@"@font-face\s*\{(?<body>[^}]*)\}", RegexOptions.IgnoreCase)]
    private static partial Regex Block();

    [GeneratedRegex(@"url\(\s*(?<url>[^)]*?\.woff2[^)]*?)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex Woff2();

    [GeneratedRegex(@"@import\s+(?:url\()?[^;]*fonts\.(?:googleapis|gstatic)\.com[^;]*;",
        RegexOptions.IgnoreCase)]
    private static partial Regex Import();
}
