using System.Security.Cryptography;
using System.Text;
using AngleSharp;
using AngleSharp.Html.Parser;
using CupriLex.Compiler;

namespace CupriLex.Harness;

/// <summary>
/// The faces a block asks a font service for, fetched with consent and written into the document
/// as <c>data:</c> URIs, so the engine is scored drawing the typeface the browser drew.
///
/// <para>Without this every block that links Google Fonts - 47 of them - is scored in Noto Sans
/// against a browser rendering Inter, DM Sans or Space Mono, and the difference is charged to the
/// engine. Measured on x-post with the card already in the right place: 17.7% of content wrong in
/// the substitute, 14.8% in Inter. Three points of a block's score that were never about
/// rendering.</para>
///
/// <para>The fetch is the packager's own - <see cref="WebFonts.GatherAsync"/>, the same consent,
/// the same request list - so what is scored is what a package carries. The packager writes each
/// face as a file beside the document; here they go inline instead, because the engine is handed
/// a string with no location to resolve a file against, and the matrix says a <c>data:</c> face
/// registers.</para>
///
/// <para>Nothing is fetched unless <c>--download</c> says so, same as packaging. The default
/// measurement stays comparable with every number before it.</para>
/// </summary>
public static class WebFaces
{
    private static readonly HtmlParser Parser = new();

    /// <summary>The document with its web faces inline, and one line per face for the report.
    /// The document unchanged, and no lines, when nothing was approved or nothing came back.</summary>
    public static async Task<(string Html, IReadOnlyList<string> Notes)> EmbedAsync(
        string html, IConsent consent, IFetch fetch, CancellationToken cancel = default)
    {
        var document = Parser.ParseDocument(html);
        var gathered = await WebFonts.GatherAsync(document, consent, fetch, cancel);

        if (gathered.Faces.Count == 0) return (html, []);

        var text = document.ToHtml();
        var notes = new List<string>();

        foreach (var face in gathered.Faces)
        {
            // The packager's rewrite names the file by its key; the bytes stand in for the file.
            text = text.Replace($"url('{face.Key}')",
                "url(data:font/woff2;base64," + Convert.ToBase64String(face.Bytes) + ")",
                StringComparison.Ordinal);
        }

        foreach (var family in gathered.Faces.GroupBy(f => f.Family, StringComparer.OrdinalIgnoreCase))
            notes.Add($"FONT fetched: {family.Key} "
                      + string.Join(", ", family.Select(f => f.Weight.ToString()).Distinct())
                      + $" ({family.Sum(f => f.Bytes.Length) / 1024:n0} KB)");

        return (text, notes);
    }

    /// <summary>What <c>--download</c> allows for a scoring run. No prompt: a measurement does
    /// not stop to ask, and nothing leaves the machine unless the flag says so.</summary>
    public static IConsent Allowed(string? download)
    {
        if (download is null || download.Equals("none", StringComparison.OrdinalIgnoreCase))
            return Consent.None;
        if (download.Equals("all", StringComparison.OrdinalIgnoreCase))
            return Consent.All;

        return Consent.Hosts(download.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>
/// A fetch that remembers. A corpus run asks a font service for the same stylesheet forty times
/// and the same file more often than that; once per run is enough, and the file on disk means a
/// second run on the same machine does not need the network at all.
/// </summary>
public sealed class CachedFetch(IFetch inner, string directory) : IFetch
{
    private readonly Dictionary<string, Fetched?> _memory = new(StringComparer.OrdinalIgnoreCase);

    public async Task<Fetched?> GetAsync(string url, CancellationToken cancel = default)
    {
        if (_memory.TryGetValue(url, out var known)) return known;

        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..32]);

        if (File.Exists(file))
        {
            var bytes = await File.ReadAllBytesAsync(file, cancel);
            var type = File.Exists(file + ".type") ? await File.ReadAllTextAsync(file + ".type", cancel) : null;
            return _memory[url] = new Fetched(bytes, type);
        }

        var fetched = await inner.GetAsync(url, cancel);

        if (fetched is not null)
        {
            await File.WriteAllBytesAsync(file, fetched.Bytes, cancel);
            if (fetched.ContentType is { } contentType)
                await File.WriteAllTextAsync(file + ".type", contentType, cancel);
        }

        return _memory[url] = fetched;
    }
}
