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
    /// <summary>
    /// Where the corpus is.
    ///
    /// <para>A second copy of this walk - the compiler's survey tool has the other one. It used to
    /// be shared, from a type in the compiler, and that type was then published to anyone who
    /// referenced the library. Twelve lines of directory walk is a smaller cost than a corpus path
    /// in somebody else's package.</para>
    /// </summary>
    public static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "corpus", "registry", "blocks");
            if (Directory.Exists(candidate)) return candidate;
        }

        throw new DirectoryNotFoundException(
            "No corpus/registry/blocks above " + AppContext.BaseDirectory
            + ". Run: python tools/fetch-corpus.py");
    }

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

    /// <summary>
    /// Nine blocks, for the run you do between the runs that count.
    ///
    /// <para>A corpus pass is a quarter of an hour and most of it is spent confirming that 93
    /// blocks still paint nothing. This set is chosen so that each block is the FIRST place a
    /// different kind of change would show, which is the only property that makes a subset worth
    /// having: a sample of nine tells you about nine blocks, but nine chosen canaries tell you
    /// which mechanism you broke.</para>
    ///
    /// <para><b>It is not the score.</b> These blocks were picked partly for being unusually
    /// alive, so the mean over them is far above the corpus mean and means nothing on its own.
    /// Quote <c>--all</c>, always, in a commit message or a document. This is for the loop.</para>
    ///
    /// <para>Chosen from the 0.28.1 run in review/baseline.json, and worth revisiting when the
    /// corpus changes shape - if a block here ever joins the 93 that paint nothing, it has stopped
    /// being a canary and needs replacing.</para>
    /// </summary>
    public static readonly IReadOnlyList<(string Block, string Why)> Quick =
    [
        ("slack-notification-ad",
            "the most animated block in the engine: 75% of its pixels move, against a reference "
            + "that moves 100%. Nothing else comes close, so motion breaks here first"),

        ("app-showcase",
            "100% reference movement, 35% engine movement, and the one block whose clock sweep "
            + "shows a genuine unexplained valley at +0.20s. Timing work is judged here"),

        ("notes-reveal",
            "the best-scoring block that both animates and paints text (86%). The typography "
            + "canary: it is one of three blocks the WOFF 2 decoder moved at all"),

        ("heygen-avatar-promo-card",
            "the block the WOFF 2 decoder helped most (+0.9), and it animates. The other half of "
            + "the font signal, on a portrait composition rather than a landscape one"),

        ("mk-clone-wall-transition",
            "paints 91% of its own frame - more than any other block - and still scores 23%. The "
            + "only shape of failure where colour and geometry fidelity can show up at all: this "
            + "one draws the WRONG thing rather than nothing"),

        ("code-snippet-dark-2026",
            "78% content over dense text, no engine motion. The high-water mark: anything that "
            + "breaks text layout or rasterisation drops this before it shows anywhere else. It "
            + "replaced code-snippet-visual-studio-light, which upstream deleted - the test that "
            + "holds this list against the corpus is what noticed, on CI, on its first run"),

        ("world-map",
            "the <svg> canary at 77%. Inline SVG needs an optional package and a UseSvg() call, "
            + "and a translation that quietly stopped making it would look fine everywhere else"),

        ("transitions-mechanical",
            "28 refusals, a quarter of the frame moving in the engine. The compiler-reach canary: "
            + "when the compiler learns to carry something new, the refusal count drops here"),

        ("chatgpt-exchange",
            "0.0% content, 0.0% ink, 43 refusals: the representative of the 93 blocks that paint "
            + "nothing. It keeps the fast set honest, and if a change ever makes a blank block "
            + "paint, this is where it shows"),
    ];

    /// <summary>The <see cref="Quick"/> set, resolved. Throws by name if one has been renamed or
    /// removed, rather than quietly running eight.</summary>
    public static IReadOnlyList<Block> Fast()
    {
        var all = All().ToDictionary(b => b.Name, StringComparer.OrdinalIgnoreCase);
        var missing = Quick.Where(q => !all.ContainsKey(q.Block)).Select(q => q.Block).ToArray();

        if (missing.Length > 0)
            throw new FileNotFoundException(
                "The fast set names blocks this corpus does not have: "
                + string.Join(", ", missing)
                + ". Fix Corpus.Quick rather than the corpus - it is a list of canaries, and a "
                + "missing one means the thing it was watching is no longer watched.");

        return [.. Quick.Select(q => all[q.Block])];
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
