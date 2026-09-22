using CupriFace;
using CupriFace.Components;
using CupriFace.Diagnostics;
using CupriFace.Svg;
using CupriFace.Text;
using CupriFace.Woff2;

namespace CupriLex.Harness;

/// <summary>What the engine produced for one block.</summary>
/// <param name="Frames">One frame per requested time, in ascending time order.</param>
/// <param name="Diagnostics">What <c>CupriDoctor</c> said about the document, deduplicated by
/// code. Not a score, but the first place to look when one is bad - a block that renders as a
/// blank rectangle usually said why.</param>
public sealed record Rendered(IReadOnlyList<Frame> Frames, IReadOnlyList<string> Diagnostics);

/// <summary>
/// The engine side of the comparison: CupriFace, rendering the translated document.
///
/// <para>No JavaScript runs here, by design and by the engine having none. Until the compiler
/// exists this is a still image of whatever the markup says before any timeline touches it, which
/// is exactly the baseline Milestone 2 is supposed to establish.</para>
/// </summary>
public static class Engine
{
    /// <summary>The engine version these frames came from. A score without it is a score about
    /// nothing in particular.</summary>
    public static string Version =>
        typeof(CupriDocument).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    public static Rendered Render(Block block, string html, IReadOnlyList<double> times,
        string? fontDirectory)
    {
        using var document = CupriDocument.Load(html);
        document.UseComponents(ComponentRegistry.Default());

        // Inline <svg> draws only for a document that asks for it, and 61 of the 187 blocks have
        // one - logos, icons, a progress ring, the lines of a diagram. Without this call they lay
        // out and stay empty, which is what they did through every measurement before 0.27.0.
        document.UseSvg();

        // Every face in the corpus arrives as .woff2 - 157 of the 187 blocks bring one - and the
        // engine refused them all by name until 0.28.1 shipped the decoder, so the text was
        // measured and drawn in a substitute at different widths.
        //
        // The call installs a PROCESS-WIDE decoder rather than a per-document one, which matters
        // when reasoning about a measurement: once any document in the run has asked for it, every
        // later document decodes too. An A/B of this line has to remove all of them, including the
        // one CupriDoctor is configured with below, or the B side quietly gets the A behaviour.
        document.UseWoff2();

        if (fontDirectory is { Length: > 0 } fonts && Directory.Exists(fonts))
            document.LoadFonts(fonts, recursive: true);

        // Settle BEFORE the frames, and at the size they will be rendered at. Settling re-lays-out
        // from zero, so settling after seeking throws the seek away and renders t=0 - which reads
        // exactly like an animation that did not run. The conformance probe learned this the
        // expensive way; the comment is there too.
        document.Animate(0);
        document.Settle(block.Width, block.Height, TimeSpan.FromSeconds(20));

        var frames = new List<Frame>(times.Count);
        foreach (var t in times.OrderBy(t => t))
        {
            document.Animate(t);
            using var image = document.RenderToImage(block.Width, block.Height);
            frames.Add(Frame.FromImage(image));
        }

        return new Rendered(frames, [.. Diagnose(html, block), .. Fonts(document)]);
    }

    /// <summary>
    /// What the engine made of the document's typefaces.
    ///
    /// <para>Worth its own line in every report. The corpus asks for Inter 104 times, Space Mono
    /// 33, Bebas Neue 28; the faces the harness registers are Noto Sans and Noto Sans Bold. A
    /// block whose families all fell back is being scored in a typeface it never asked for, and
    /// nothing else in the report would have said so.</para>
    /// </summary>
    private static IReadOnlyList<string> Fonts(CupriDocument document)
    {
        var report = document.FontReport;
        var lines = new List<string>();

        var fellBack = report.Resolutions
            .Where(r => r.Source != FontSource.Registered)
            .Select(r => r.Family)
            .Distinct()
            .ToArray();

        if (fellBack.Length > 0)
            lines.Add($"FONT x{fellBack.Length}: asked for {string.Join(", ", fellBack.Take(6))}"
                      + (fellBack.Length > 6 ? ", …" : "")
                      + $" — registered: {string.Join(", ", report.RegisteredFamilies.Take(4))}");

        foreach (var problem in report.Problems.Take(4))
            lines.Add($"FONT '{problem.Family}': {problem.Reason}"
                      + (problem.Sources.Count > 0
                          ? " — " + Path.GetFileName(problem.Sources[0])
                          : ""));

        return lines;
    }

    private static IReadOnlyList<string> Diagnose(string html, Block block)
    {
        try
        {
            // "" and not null for the stylesheet: a null one turned every CSS check off through
            // 0.25.0, and passing it explicitly is a habit worth keeping.
            //
            // configure: the same packages the render uses, or the checker builds a document with
            // none of them and reports markup as undrawable that this harness draws perfectly
            // well. It is the same mistake as checking one document and rendering another.
            return [.. CupriDoctor.Check(html, string.Empty, width: block.Width, height: block.Height,
                    configure: document => { document.UseSvg(); document.UseWoff2(); }).Findings
                .GroupBy(f => f.Code)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key} x{g.Count()}: {g.First().Message}")];
        }
        catch (Exception ex)
        {
            return [$"CupriDoctor threw: {ex.GetType().Name}: {ex.Message}"];
        }
    }

    /// <summary>The faces CupriCut ships, if that repository is beside this one. A document with no
    /// registered face is at the mercy of whatever the machine happens to have installed, which
    /// makes a text-heavy corpus score differently on two machines for no engine reason.</summary>
    public static string? FindFonts()
    {
        // An override, because the faces the engine is given are a variable of the measurement and
        // not a fact about it. The corpus asks for Inter 104 times, Space Mono 33, Bebas Neue 28;
        // the directory found below holds Noto Sans and nothing else, so every block is being
        // scored in a typeface it did not ask for. Pointing this somewhere else is how that gets
        // quantified rather than assumed.
        if (Environment.GetEnvironmentVariable("CUPRILEX_FONTS") is { Length: > 0 } chosen
            && Directory.Exists(chosen))
            return Path.GetFullPath(chosen);

        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(d.FullName, "fonts"),
                         Path.Combine(d.FullName, "..", "CupriCut", "fonts"),
                     })
            {
                if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.ttf").Any())
                    return Path.GetFullPath(candidate);
            }
        }

        return null;
    }
}
