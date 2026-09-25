using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

namespace CupriLex.Compiler;

/// <summary>One file the document refers to, and the name it refers to it by.</summary>
public sealed record Asset(string Key, string Source, byte[] Bytes);

/// <summary>What a document asked for, and what was there.</summary>
public sealed record Collected(IReadOnlyList<Asset> Entries, IReadOnlyList<string> Missing);

/// <summary>
/// Every file a translated document refers to, gathered and renamed to a flat key.
///
/// <para>A package keys its assets by <b>the name the HTML and CSS use</b>, so
/// <c>assets/fonts/inter-400.woff2</c> in a block's folder becomes <c>inter-400.woff2</c> in the
/// package and <c>url('inter-400.woff2')</c> in the markup. Flattening is what lets the reference
/// carry no path: a package has one asset namespace, and a consumer that hands the bytes back as a
/// <c>data:</c> URI has nowhere to put a folder anyway.</para>
///
/// <para><b>A reference that names nothing is left alone.</b> Stripping it would turn a fixable
/// broken link into an invisible one, and this corpus has plenty: a block that fetches a font from
/// a CDN, an <c>&lt;img&gt;</c> whose file was never vendored. They are collected by name and
/// reported.</para>
/// </summary>
public static class Assets
{
    private static readonly Regex CssUrl = new(
        @"url\((?<quote>['""]?)(?<url>[^)'""]+)\k<quote>\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Attributes that can name a file. <c>href</c> is included for a stylesheet link and
    /// excluded for an anchor, which is a navigation target and not an asset.</summary>
    private static readonly string[] Attributes = ["src", "poster", "href"];

    /// <summary>
    /// Rewrites <paramref name="document"/> in place and returns what it referred to.
    /// </summary>
    /// <param name="directory">Where the block's own files live. Null means the document has no
    /// folder to resolve against, so every relative reference is reported missing rather than
    /// guessed at.</param>
    /// <param name="carried">Keys the package already holds from somewhere other than disk - a
    /// face fetched from a font service, say. A reference to one of these is neither collected nor
    /// reported missing: it is already answered, and looking for it beside the block would find
    /// nothing and say so, which would be true of the disk and false of the package.</param>
    public static Collected Collect(
        IHtmlDocument document, string? directory, IReadOnlySet<string>? carried = null)
    {
        carried ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var found = new Dictionary<string, Asset>(StringComparer.Ordinal);
        var missing = new List<string>();
        var taken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string? Take(string url)
        {
            if (carried.Contains(url.Trim())) return null;

            if (Resolve(url, directory) is not { } source)
            {
                if (Relative(url) && !missing.Contains(url)) missing.Add(url);
                return null;
            }

            // Two files with the same name from different folders would otherwise silently
            // become one asset, and the second would render as the first.
            var key = Path.GetFileName(source);
            if (taken.TryGetValue(key, out var already) && !PathsMatch(already, source))
            {
                var stem = Path.GetFileNameWithoutExtension(key);
                var extension = Path.GetExtension(key);
                for (var n = 2; taken.ContainsKey(key); n++) key = $"{stem}-{n}{extension}";
            }

            if (!found.ContainsKey(key))
            {
                found[key] = new Asset(key, source, File.ReadAllBytes(source));
                taken[key] = source;
            }

            return key;
        }

        foreach (var element in document.All)
        {
            foreach (var name in Attributes)
            {
                if (element.GetAttribute(name) is not { Length: > 0 } value) continue;
                if (name == "href" && element is IHtmlAnchorElement) continue;
                if (Take(value) is { } key) element.SetAttribute(name, key);
            }

            if (element.GetAttribute("style") is { Length: > 0 } inline)
                element.SetAttribute("style", Rewrite(inline, Take));
        }

        foreach (var style in document.QuerySelectorAll("style"))
            style.TextContent = Rewrite(style.TextContent, Take);

        return new Collected([.. found.Values], missing);
    }

    private static string Rewrite(string css, Func<string, string?> take) => CssUrl.Replace(css,
        match =>
        {
            var key = take(match.Groups["url"].Value);
            if (key is null) return match.Value;

            var quote = match.Groups["quote"].Value;
            return $"url({quote}{key}{quote})";
        });

    /// <summary>The file a reference names, or null if it does not name one on disk.</summary>
    private static string? Resolve(string url, string? directory)
    {
        if (!Relative(url) || directory is not { Length: > 0 }) return null;

        try
        {
            var path = Path.GetFullPath(Path.Combine(directory,
                url.Trim().Replace('/', Path.DirectorySeparatorChar)));

            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;    // a reference that is not a usable path names nothing
        }
    }

    /// <summary>Whether a reference is the kind that could point at a file beside the document. An
    /// absolute URL, a fragment, a root path and a <c>data:</c> URI are all already resolved.</summary>
    private static bool Relative(string url)
    {
        var trimmed = url.Trim();

        return trimmed.Length > 0
               && !trimmed.StartsWith('#')
               && !trimmed.StartsWith('/')
               && !trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
               && !Uri.TryCreate(trimmed, UriKind.Absolute, out _);
    }

    private static bool PathsMatch(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
