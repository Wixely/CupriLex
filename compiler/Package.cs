using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AngleSharp;
using AngleSharp.Html.Parser;

namespace CupriLex.Compiler;

/// <summary>What a composition is, beyond its markup: the things a renderer has to be told.</summary>
/// <param name="Duration">What the block declares. The package may run longer - see
/// <see cref="Package"/> - but never shorter.</param>
public sealed record Composition(
    string Name,
    string Html,
    Sheet Motion,
    IReadOnlyList<Refusal> Refusals,
    int Width,
    int Height,
    double Duration,
    double Fps = 30,
    string? Description = null);

/// <summary>What was written, and what could not be.</summary>
/// <param name="Missing">URLs the document refers to that name nothing on disk. Left in the markup
/// exactly as the author wrote them rather than stripped: a broken reference the report names is
/// fixable, and one that has been quietly removed is not.</param>
public sealed record Packaged(
    string Path, long Bytes, IReadOnlyList<string> Assets, IReadOnlyList<string> Missing);

/// <summary>
/// A translated block as a <c>.cutpkg</c>: one file, assets and fonts as bytes.
///
/// <para>The format is CupriCut's and not this repository's. A package is a zip holding
/// <c>project.json</c> - the same schema a <c>.cut.json</c> parses to - and an <c>assets/</c>
/// folder, with each asset keyed by <b>the name the HTML and CSS refer to it by</b>. CupriCut
/// hands those back as <c>data:</c> URIs when it opens the package, which is why the markup can
/// say <c>url('face.woff2')</c> and have it resolve with no path in it at all.</para>
///
/// <para><b>Nothing depends on this tool at render time.</b> That is the whole point of writing a
/// package rather than a document plus a folder: a project that has been imported is just a
/// project, and CupriLex is not in the loop when it is rendered, or copied, or committed.</para>
///
/// <para><b>Fonts travel as files, not as base64.</b> Decided in PLAN.md and measured first: the
/// document's own <c>@font-face</c> is left exactly as the author wrote it, and the bytes ride in
/// the container. Inlining them into the markup would add a third to their size, duplicate a
/// family into every block that shares it, and edit markup that otherwise survives translation
/// untouched.</para>
/// </summary>
public static class Package
{
    public const string Extension = ".cutpkg";
    public const string Manifest = "project.json";
    public const string AssetFolder = "assets/";

    /// <summary>The report, beside the manifest. Not part of CupriCut's schema and not read by it -
    /// a zip entry nothing looks for is ignored - but a package whose refusals are only in a
    /// console scrollback is a package nobody can audit six months later.</summary>
    public const string Report = "report.md";

    private static readonly HtmlParser Parser = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Packaged Write(Composition composition, string? directory, string path)
    {
        var document = Parser.ParseDocument(composition.Html);
        var collected = Assets.Collect(document, directory);
        var freed = FreeTheWindowSlot(document, composition.Motion.Selectors);

        // Never shorter than the author said, never shorter than the motion. A third of this
        // corpus declares a duration its own timeline does not span, and the two failures are not
        // symmetrical: a package that runs long shows a held last frame, and one that runs short
        // cuts the composition off mid-move. The disagreement is reported rather than resolved
        // silently, because only the author knows which number was the mistake.
        var seconds = Math.Max(composition.Duration, composition.Motion.Seconds);

        var project = new Project
        {
            Name = composition.Name,
            Description = composition.Description,
            Html = document.ToHtml(),
            Render = new Render
            {
                Width = composition.Width,
                Height = composition.Height,
                Fps = composition.Fps,
                Duration = Math.Round(seconds, 3),
            },
            Assets = collected.Entries.ToDictionary(a => a.Key, a => AssetFolder + a.Key,
                StringComparer.Ordinal),
            Meta = new Meta
            {
                Engine = "CupriLex",
                Notes = Notes(composition, collected, seconds, freed),
            },
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        // Written beside and moved, so an interrupted write cannot leave a half-zip wearing the
        // name of a good package.
        var writing = path + ".writing";
        if (File.Exists(writing)) File.Delete(writing);

        using (var file = File.Create(writing))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            Text(zip, Manifest, JsonSerializer.Serialize(project, Json));
            Text(zip, Report, Reporting.Of(composition, collected, seconds, freed));

            foreach (var asset in collected.Entries)
            {
                // Already-compressed bytes are stored rather than deflated: a .woff2 or a .png
                // gains nothing and costs the time twice, once here and once on every open.
                var entry = zip.CreateEntry(AssetFolder + asset.Key,
                    Incompressible(asset.Key) ? CompressionLevel.NoCompression
                                              : CompressionLevel.Optimal);

                using var writer = entry.Open();
                writer.Write(asset.Bytes);
            }
        }

        File.Move(writing, path, overwrite: true);

        return new Packaged(path, new FileInfo(path).Length,
            [.. collected.Entries.Select(a => a.Key)], collected.Missing);
    }

    private static void Text(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(),
            new UTF8Encoding(false));
        writer.Write(content);
    }

    /// <summary>
    /// <c>data-start</c> and <c>data-duration</c> removed from the packaged markup.
    ///
    /// <para><b>Because the package states the duration properly.</b> Those attributes are how a
    /// LOOSE HTML block declares its own length - it has nowhere else to put it - and a
    /// <c>.cutpkg</c> has <c>render.duration</c>, filled in before this runs. Carrying both is
    /// stating one fact twice, and the second copy is the one that causes trouble.</para>
    ///
    /// <para>The trouble is specific. CupriCut reads those attributes as "this element is a
    /// timeline window" and then needs the element's one animation slot for its own use, because
    /// the engine runs one animation per element. An element that is both a window and animated is
    /// an error, CUT003, and 13 of the 187 corpus blocks are one. A class-less timed element trips
    /// it even when the animation is somewhere else entirely, because the check is a text match
    /// over the whole document - so removing the attributes from only the animated elements fixed
    /// the 13 and left the rest still reporting.</para>
    ///
    /// <para>Nothing is lost by removing them. A browser composition puts its motion in a timeline,
    /// which is now <c>@keyframes</c>; it does not use CupriCut's windows, which it has never heard
    /// of. A project that genuinely wants windows is authored as a project, not translated from a
    /// browser by this tool.</para>
    /// </summary>
    private static List<string> FreeTheWindowSlot(
        AngleSharp.Html.Dom.IHtmlDocument document, IReadOnlyList<string> animated)
    {
        _ = animated;   // every timed element is stripped, animated or not - see above

        string[] timing = ["data-start", "data-duration"];
        var freed = new List<string>();

        foreach (var element in document.All.ToArray())
        {
            var had = timing.Where(element.HasAttribute).ToArray();
            if (had.Length == 0) continue;

            foreach (var attribute in had) element.RemoveAttribute(attribute);

            var name = element.Id is { Length: > 0 } id ? "#" + id
                : element.ClassName is { Length: > 0 } cls ? "." + cls.Split(' ')[0]
                : "<" + element.LocalName + ">";

            freed.Add($"{name} ({string.Join(", ", had)})");
        }

        return freed;
    }

    private static List<string> Notes(
        Composition composition, Collected collected, double seconds, IReadOnlyList<string> freed)
    {
        var notes = new List<string>
        {
            $"Translated from a browser composition by CupriLex. {composition.Motion.Elements} "
            + $"element(s) carry an animation; {composition.Refusals.Count} thing(s) could not be "
            + "carried. See " + Report + " in this package for the list.",
        };

        if (composition.Motion.Held > 0)
            notes.Add($"{composition.Motion.Held} animation(s) hold one value for their whole "
                      + "length: their end state is carried and their motion is not.");

        if (Math.Abs(seconds - composition.Duration) > 0.001)
            notes.Add($"The block declares {composition.Duration:0.###}s and its motion spans "
                      + $"{composition.Motion.Seconds:0.###}s. This package runs {seconds:0.###}s, "
                      + "the longer of the two, so nothing is cut off.");

        if (collected.Missing.Count > 0)
            notes.Add($"{collected.Missing.Count} referenced file(s) were not found and are not in "
                      + "this package. Their references are left as they were written.");

        if (freed.Count > 0)
            notes.Add($"data-start/data-duration were removed from {freed.Count} element(s). A "
                      + "loose HTML block has nowhere but an attribute to declare its length; this "
                      + "package has render.duration, and carrying both costs the element its one "
                      + "animation slot.");

        return notes;
    }

    private static bool Incompressible(string name) =>
        Path.GetExtension(name).ToLowerInvariant()
            is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".avif"
            or ".woff" or ".woff2" or ".mp3" or ".m4a" or ".aac" or ".ogg"
            or ".flac" or ".mp4" or ".webm" or ".zip";

    // ---- the manifest, which is CupriCut's schema and not ours --------------------------------

    /// <summary>
    /// <c>project.json</c>, as far as a translated block fills it in.
    ///
    /// <para>Deliberately a copy of the fields this writes and not a reference to CupriCut's own
    /// type. CupriLex does not depend on CupriCut - it produces a file that CupriCut reads - and a
    /// project reference would make the compiler need a renderer to build. The cost is that this
    /// can drift from the schema, so a test pins the property names against a package it writes
    /// and reads back.</para>
    ///
    /// <para><c>css</c> is deliberately absent. The compiled <c>@keyframes</c> are already inside
    /// the document's own <c>&lt;style&gt;</c>, and writing them here as well would apply the
    /// motion twice.</para>
    /// </summary>
    private sealed class Project
    {
        public int FormatVersion { get; init; } = 1;
        public string Name { get; init; } = string.Empty;
        public string? Description { get; init; }
        public string Html { get; init; } = string.Empty;
        public Render Render { get; init; } = new();
        public Dictionary<string, string> Assets { get; init; } = new(StringComparer.Ordinal);
        public Meta Meta { get; init; } = new();
    }

    private sealed class Render
    {
        public int Width { get; init; } = 1280;
        public int Height { get; init; } = 720;
        public double Fps { get; init; } = 30;
        public double Duration { get; init; } = 3;
    }

    private sealed class Meta
    {
        public DateTimeOffset Created { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset Updated { get; init; } = DateTimeOffset.UtcNow;
        public string? Engine { get; init; }
        public List<string> Notes { get; init; } = [];
    }
}
