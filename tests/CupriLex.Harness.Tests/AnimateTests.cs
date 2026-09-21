using CupriFace;
using CupriFace.Components;
using CupriLex.Compiler;
using SkiaSharp;
using Xunit;

namespace CupriLex.Harness.Tests;

/// <summary>
/// Whether seeking one document repeatedly gives the same frames as loading it fresh at each time.
///
/// <para>The harness renders a block's five sample times through ONE <c>CupriDocument</c>, settled
/// once and then advanced with <c>Animate(t)</c>. The conformance probe loads a fresh document per
/// time. Both are reasonable and they had never been compared, which matters because 47 blocks
/// carry compiled motion and still report an identical frame at every sample - more blocks than
/// the 38 that move.</para>
/// </summary>
public class AnimateTests
{
    private const string Subject = "chatgpt-exchange";

    /// <summary>The harness's way: settle once, then seek.</summary>
    private static List<Frame> Reused(Block block, string html, IReadOnlyList<double> times)
    {
        using var document = Load(html);
        document.Animate(0);
        document.Settle(block.Width, block.Height, TimeSpan.FromSeconds(20));

        var frames = new List<Frame>();
        foreach (var t in times)
        {
            document.Animate(t);
            using var image = document.RenderToImage(block.Width, block.Height);
            frames.Add(Frame.FromImage(image));
        }

        return frames;
    }

    /// <summary>The conformance probe's way: a document per instant.</summary>
    private static List<Frame> Fresh(Block block, string html, IReadOnlyList<double> times)
    {
        var frames = new List<Frame>();
        foreach (var t in times)
        {
            using var document = Load(html);
            document.Animate(0);
            document.Settle(block.Width, block.Height, TimeSpan.FromSeconds(20));
            document.Animate(t);
            using var image = document.RenderToImage(block.Width, block.Height);
            frames.Add(Frame.FromImage(image));
        }

        return frames;
    }

    private static CupriDocument Load(string html)
    {
        var document = CupriDocument.Load(html);
        document.UseComponents(ComponentRegistry.Default());

        if (Engine.FindFonts() is { Length: > 0 } fonts && Directory.Exists(fonts))
            document.LoadFonts(fonts, recursive: true);

        return document;
    }

    private static double Movement(IReadOnlyList<Frame> frames) =>
        frames.Skip(1).Average(f => Comparison.Of(frames[0], f).Differing);

    /// <summary>
    /// The two ways agree, on a block whose motion the compiler carries.
    ///
    /// <para>If they ever disagree, every corpus number is suspect: the harness would be reporting
    /// the engine as still on blocks whose animation runs perfectly well when loaded differently,
    /// and the whole score would be measuring the harness.</para>
    /// </summary>
    [Fact]
    public void Seeking_one_document_gives_the_same_frames_as_loading_it_fresh()
    {
        Block block;
        try { block = Corpus.Find(Subject); }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
        {
            Assert.Skip("no corpus - run: python tools/fetch-corpus.py");
            return;
        }

        var html = Translation.Of(block).Html;
        double[] times = [0, block.Duration / 4, block.Duration / 2, block.Duration];

        var reused = Reused(block, html, times);
        var fresh = Fresh(block, html, times);

        for (var i = 0; i < times.Length; i++)
        {
            var same = Comparison.Of(reused[i], fresh[i]);
            Assert.True(same.Differing < 0.0001,
                $"at t={times[i]:0.###}s the reused document and a fresh one differ over "
                + $"{same.Differing:P3} of the frame. Movement across the samples: reused "
                + $"{Movement(reused):P3}, fresh {Movement(fresh):P3}.");
        }
    }
}
