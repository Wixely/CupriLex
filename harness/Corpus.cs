using System.Text.Json;
using System.Text.RegularExpressions;
using CupriLex.Compiler;

namespace CupriLex.Harness;

/// <summary>One composition to be scored: where it lives, how big it is, how long it runs.</summary>
/// <param name="Name">What to call this block. The HTML file's stem where that is unique, and
/// <c>directory/stem</c> where it is not - four directories each hold a <c>demo.html</c>, so a
/// bare stem would have had four blocks overwriting one another's images and one another's row in
/// the report. Not the directory's name either: seven directories hold two blocks each.</param>
/// <param name="Path">The block's HTML file.</param>
/// <param name="Width">Authored width. Both renderers use it, because a composition laid out at a
/// size it was not written for is a different composition, not a smaller one.</param>
/// <param name="Height">Authored height.</param>
/// <param name="Duration">Declared seconds. What a renderer would use, which is not always what
/// the GSAP timeline actually spans - see <see cref="Reference.TimelineSeconds"/>.</param>
/// <param name="DurationSource">Where the duration came from, so a suspicious score can be traced
/// back to a guess.</param>
public sealed record Block(
    string Name, string Path, int Width, int Height, double Duration, string DurationSource)
{
    public string Directory => System.IO.Path.GetDirectoryName(Path)!;

    /// <summary>The name as a single path segment, for the folder its images go in.</summary>
    public string Slug => Name.Replace('/', '_');
}

/// <summary>The corpus on disk - <c>python tools/fetch-corpus.py</c> puts it there.</summary>
public static class Corpus
{
    // Defaults only, and only when a block declares nothing. Recorded as "assumed" in the report
    // rather than quietly standing in for a measurement.
    private const int AssumedWidth = 1920;
    private const int AssumedHeight = 1080;
    private const double AssumedSeconds = 10;

    private static readonly Regex DataWidth = Attribute("data-width");
    private static readonly Regex DataHeight = Attribute("data-height");
    private static readonly Regex DataDuration = Attribute("data-duration");

    private static Regex Attribute(string name) =>
        new(name + @"\s*=\s*[""']([\d.]+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The compiler's copy, so there is one answer to where the corpus is.</summary>
    public static string Root() => CorpusPath.Blocks();

    public static IReadOnlyList<Block> All()
    {
        var paths = Directory.EnumerateFiles(Root(), "*.html", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var ambiguous = paths.GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return [.. paths.Select(p => Describe(p, ambiguous))];
    }

    /// <summary>By name, with the near misses listed - a typo should not read like an empty corpus.</summary>
    public static Block Find(string name)
    {
        var all = All();
        var hit = all.FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (hit is not null) return hit;

        var near = all.Where(b => b.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            .Select(b => b.Name).Take(8).ToArray();

        throw new FileNotFoundException(
            $"No block named '{name}' in {Root()}."
            + (near.Length > 0 ? " Did you mean: " + string.Join(", ", near) : ""));
    }

    /// <summary>
    /// Size and duration, from the block itself first and its manifest second.
    ///
    /// <para>That order is deliberate. <c>registry-item.json</c> describes the directory, and the
    /// seven directories holding two blocks have one manifest between them - so for the second
    /// block of such a pair the manifest is another block's numbers. The element attributes are
    /// the file's own.</para>
    /// </summary>
    private static Block Describe(string path, IReadOnlySet<string> ambiguousStems)
    {
        var html = File.ReadAllText(path);
        var manifest = Manifest(path);
        var stem = Path.GetFileNameWithoutExtension(path);
        var name = ambiguousStems.Contains(stem)
            ? Path.GetFileName(Path.GetDirectoryName(path)) + "/" + stem
            : stem;

        var (width, wSource) = Number(DataWidth, html, manifest?.Width, AssumedWidth);
        var (height, hSource) = Number(DataHeight, html, manifest?.Height, AssumedHeight);
        var (duration, dSource) = Number(DataDuration, html, manifest?.Duration, AssumedSeconds);

        _ = wSource; _ = hSource;   // the dimensions rarely disagree; the duration is the one that bites
        return new Block(name, path, (int)width, (int)height, duration, dSource);
    }

    private static (double Value, string Source) Number(
        Regex attribute, string html, double? manifest, double assumed)
    {
        if (attribute.Match(html) is { Success: true } m
            && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var declared) && declared > 0)
            return (declared, "element");

        return manifest is > 0 ? (manifest.Value, "manifest") : (assumed, "assumed");
    }

    private sealed record Item(int? Width, int? Height, double? Duration);

    private static Item? Manifest(string blockPath)
    {
        var path = Path.Combine(Path.GetDirectoryName(blockPath)!, "registry-item.json");
        if (!File.Exists(path)) return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            int? w = null, h = null;
            if (root.TryGetProperty("dimensions", out var dims))
            {
                if (dims.TryGetProperty("width", out var wv)) w = wv.GetInt32();
                if (dims.TryGetProperty("height", out var hv)) h = hv.GetInt32();
            }
            double? d = root.TryGetProperty("duration", out var dv) ? dv.GetDouble() : null;
            return new Item(w, h, d);
        }
        catch (JsonException)
        {
            return null;    // a manifest that will not parse is not worth failing a render over
        }
    }
}
