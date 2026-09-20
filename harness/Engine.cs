using CupriFace;
using CupriFace.Components;
using CupriFace.Diagnostics;

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

        return new Rendered(frames, Diagnose(html, block));
    }

    private static IReadOnlyList<string> Diagnose(string html, Block block)
    {
        try
        {
            // "" and not null for the stylesheet: a null one turned every CSS check off through
            // 0.25.0, and passing it explicitly is a habit worth keeping.
            return [.. CupriDoctor.Check(html, string.Empty, width: block.Width, height: block.Height).Findings
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
