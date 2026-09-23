using System.IO.Compression;
using System.Text.Json;
using CupriLex.Compiler;
using Xunit;

namespace CupriLex.Harness.Tests;

/// <summary>
/// What a <c>.cutpkg</c> has to be, pinned.
///
/// <para>The format is CupriCut's, and this repository writes it without referencing CupriCut's
/// types - a compiler that needed a renderer to build would be a worse trade than the drift these
/// tests exist to catch. So the schema is asserted here by name: <c>project.json</c> at the root,
/// an <c>assets/</c> folder, and the field names a <c>CutProject</c> parses.</para>
/// </summary>
public class PackageTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cuprilex-pkg").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Block(string html, params (string Path, byte[] Bytes)[] files)
    {
        var directory = Path.Combine(_root, "block-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "block.html"), html);

        foreach (var (path, bytes) in files)
        {
            var full = Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
        }

        return directory;
    }

    private Packaged Write(string html, string directory, double duration = 5)
    {
        var translated = Translator.Of(html);
        var composition = new Composition("test", translated.Html, translated.Motion,
            translated.Refusals, 1920, 1080, duration);

        return Package.Write(composition, directory, Path.Combine(_root, "out.cutpkg"));
    }

    private static JsonElement Manifest(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        using var entry = zip.GetEntry(Package.Manifest)!.Open();
        using var reader = new StreamReader(entry);
        return JsonDocument.Parse(reader.ReadToEnd()).RootElement.Clone();
    }

    private static string Entry(string path, string name)
    {
        using var zip = ZipFile.OpenRead(path);
        using var entry = zip.GetEntry(name)!.Open();
        using var reader = new StreamReader(entry);
        return reader.ReadToEnd();
    }

    // ---- the shape CupriCut reads -------------------------------------------------------------

    [Fact]
    public void A_package_is_a_zip_with_a_manifest_at_its_root()
    {
        var written = Write("<html><body><div class=\"a\"></div></body></html>", Block(""));

        using var zip = ZipFile.OpenRead(written.Path);
        Assert.NotNull(zip.GetEntry(Package.Manifest));
        Assert.NotNull(zip.GetEntry(Package.Report));
    }

    [Fact]
    public void The_manifest_carries_the_fields_a_project_parses()
    {
        var written = Write("<html><body><div class=\"a\"></div></body></html>", Block(""), 4.5);
        var manifest = Manifest(written.Path);

        // Named one at a time rather than checked as a set: a missing field is a package CupriCut
        // opens and renders wrongly, and the test that would have caught it has to say which.
        Assert.Equal(1, manifest.GetProperty("formatVersion").GetInt32());
        Assert.Equal("test", manifest.GetProperty("name").GetString());
        Assert.Contains("<div", manifest.GetProperty("html").GetString());

        var render = manifest.GetProperty("render");
        Assert.Equal(1920, render.GetProperty("width").GetInt32());
        Assert.Equal(1080, render.GetProperty("height").GetInt32());
        Assert.Equal(4.5, render.GetProperty("duration").GetDouble());
        Assert.Equal(30, render.GetProperty("fps").GetDouble());

        Assert.Equal("CupriLex", manifest.GetProperty("meta").GetProperty("engine").GetString());
    }

    [Fact]
    public void There_is_no_css_field_because_the_motion_is_already_in_the_document()
    {
        // Writing it twice would apply it twice.
        var written = Write("""
            <html><body><div class="a"></div>
            <script>gsap.timeline().to(".a", { x: 100, duration: 1 });</script></body></html>
            """, Block(""));

        var manifest = Manifest(written.Path);

        Assert.False(manifest.TryGetProperty("css", out _));
        Assert.Contains("@keyframes", manifest.GetProperty("html").GetString());
    }

    // ---- assets --------------------------------------------------------------------------------

    [Fact]
    public void An_asset_is_carried_as_bytes_and_keyed_by_the_name_the_document_uses()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var directory = Block("""
            <html><head><style>
              @font-face { font-family: "X"; src: url('assets/fonts/face.woff2'); }
            </style></head><body><div class="a"></div></body></html>
            """, ("assets/fonts/face.woff2", bytes));

        var written = Write("""
            <html><head><style>
              @font-face { font-family: "X"; src: url('assets/fonts/face.woff2'); }
            </style></head><body><div class="a"></div></body></html>
            """, directory);

        Assert.Equal(["face.woff2"], written.Assets);

        var manifest = Manifest(written.Path);
        Assert.Equal("assets/face.woff2",
            manifest.GetProperty("assets").GetProperty("face.woff2").GetString());

        // The reference is flattened to the key: a package has one asset namespace, and a
        // consumer that hands the bytes back as a data: URI has nowhere to put a folder.
        Assert.Contains("url('face.woff2')", manifest.GetProperty("html").GetString());
        Assert.DoesNotContain("assets/fonts/face.woff2", manifest.GetProperty("html").GetString());

        using var zip = ZipFile.OpenRead(written.Path);
        Assert.Equal(bytes.Length, zip.GetEntry("assets/face.woff2")!.Length);
    }

    [Fact]
    public void A_reference_that_names_nothing_is_reported_and_left_alone()
    {
        // Stripping it would turn a fixable broken link into an invisible one.
        var written = Write(
            """<html><body><img src="nowhere.png"><div class="a"></div></body></html>""",
            Block(""));

        Assert.Contains("nowhere.png", written.Missing);
        Assert.Contains("nowhere.png", Manifest(written.Path).GetProperty("html").GetString());
        Assert.Empty(written.Assets);
    }

    [Fact]
    public void An_absolute_url_is_not_an_asset()
    {
        var written = Write("""
            <html><body><img src="https://example.com/x.png"><div class="a"></div></body></html>
            """, Block(""));

        Assert.Empty(written.Assets);
        Assert.Empty(written.Missing);
    }

    // ---- the decisions the package records ------------------------------------------------------

    [Fact]
    public void A_package_runs_the_longer_of_the_declared_duration_and_the_motion()
    {
        // A third of this corpus declares a duration its timeline does not span. Running short
        // cuts the composition off mid-move; running long holds the last frame. Only one of those
        // loses something.
        var written = Write("""
            <html><body><div class="a"></div>
            <script>gsap.timeline().to(".a", { x: 100, duration: 9 });</script></body></html>
            """, Block(""), duration: 2);

        Assert.Equal(9, Manifest(written.Path)
            .GetProperty("render").GetProperty("duration").GetDouble());

        Assert.Contains("declares", Entry(written.Path, Package.Report));
    }

    [Fact]
    public void Timing_attributes_are_removed_because_the_package_states_the_duration()
    {
        // CupriCut reads data-duration as "this element is a timeline window" and then needs its
        // one animation slot. The package has render.duration, so the attribute is redundant here.
        var written = Write("""
            <html><body><div id="root" data-duration="7" data-start="0"><div class="a"></div></div>
            <script>gsap.timeline().to(".a", { x: 100, duration: 1 });</script></body></html>
            """, Block(""));

        var html = Manifest(written.Path).GetProperty("html").GetString()!;

        Assert.DoesNotContain("data-duration", html);
        Assert.DoesNotContain("data-start", html);
        Assert.Contains("id=\"root\"", html);
    }

    // ---- the report ------------------------------------------------------------------------------

    [Fact]
    public void The_report_opens_with_what_was_carried_not_with_what_was_refused()
    {
        var written = Write("""
            <html><body><div class="a"></div>
            <script>gsap.timeline().to(".a", { x: 100, duration: 1 });</script></body></html>
            """, Block(""));

        var report = Entry(written.Path, Package.Report);

        Assert.Contains("What was carried", report);
        Assert.True(report.IndexOf("What was carried", StringComparison.Ordinal)
                    < report.IndexOf("What could not be carried", StringComparison.Ordinal));
    }

    [Fact]
    public void A_refusal_reaches_the_report_inside_the_package()
    {
        // The whole reason the report travels: a refusal in a terminal is gone by the time anyone
        // asks why the rendered composition is missing something.
        var written = Write("""
            <html><body><div class="a"></div>
            <script>gsap.timeline().to(".a", { backgroundColor: "#fff", duration: 1 });</script>
            </body></html>
            """, Block(""));

        Assert.Contains("backgroundColor", Entry(written.Path, Package.Report));
    }

    [Fact]
    public void A_clean_translation_says_so_rather_than_saying_nothing()
    {
        // An empty report means perfect or lying, and it has to say which.
        var written = Write("""
            <html><body><div class="a"></div>
            <script>gsap.timeline().to(".a", { x: 100, duration: 1 });</script></body></html>
            """, Block(""));

        Assert.Contains("Nothing. Every timeline", Entry(written.Path, Package.Report));
    }

    [Fact]
    public void An_interrupted_write_leaves_no_package_wearing_a_good_name()
    {
        var written = Write("<html><body><div class=\"a\"></div></body></html>", Block(""));

        Assert.True(File.Exists(written.Path));
        Assert.False(File.Exists(written.Path + ".writing"));
    }
}
