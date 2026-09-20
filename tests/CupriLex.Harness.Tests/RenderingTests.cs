using Xunit;

namespace CupriLex.Harness.Tests;

/// <summary>
/// The two claims every score in this repository rests on, measured rather than assumed.
///
/// <para>One: the browser can be asked for an exact instant, so the reference is a composition at
/// <c>t</c> rather than a composition at whenever-the-screenshot-happened. Two: the engine,
/// today, renders one still frame however long you run it - which is the baseline Milestone 3 has
/// to move.</para>
/// </summary>
public class RenderingTests
{
    private const string Subject = "bar-chart-race";

    private static Block? Available()
    {
        try { return Corpus.Find(Subject); }
        catch (DirectoryNotFoundException) { return null; }
        catch (FileNotFoundException) { return null; }
    }

    [Fact]
    public async Task Seeking_the_timeline_changes_the_pixels()
    {
        var block = Available();
        Assert.SkipWhen(block is null, "no corpus - run: python tools/fetch-corpus.py");
        Assert.SkipWhen(Browser.Find() is null, "no headless browser on this machine");

        await using var browser = await Browser.LaunchAsync(TestContext.Current.CancellationToken);

        Reference reference;
        try
        {
            reference = await browser.RenderAsync(block!, [0, block!.Duration / 2],
                TestContext.Current.CancellationToken);
        }
        catch (BrowserException ex) when (ex.Message.Contains("gsap", StringComparison.Ordinal))
        {
            // Every block in the corpus loads GSAP from a CDN, so this machine is offline rather
            // than wrong. Skipped with the engine's own words, not swallowed.
            Assert.Skip("the reference needs the network: " + ex.Message);
            return;
        }

        Assert.NotEmpty(reference.TimelineIds);

        var moved = Comparison.Of(reference.Frames[0], reference.Frames[1]);
        Assert.True(moved.Differing > 0.001,
            $"seeking {Subject} from 0s to {block!.Duration / 2:0.#}s changed "
            + $"{moved.Differing:P3} of the frame: the seek is not taking effect");
    }

    /// <summary>
    /// 25 blocks write <c>window.__timelines[DATA.id] = tl</c> into an object they expect their
    /// host to have created, and throw when it is not there. The harness creates it before their
    /// scripts run; without that, every <c>carousel-*</c> block in the corpus is unmeasurable.
    /// </summary>
    [Fact]
    public async Task A_block_that_expects_its_host_to_create_the_timeline_registry_is_still_measurable()
    {
        var reference = await ReferenceFor("carousel-circle-1");
        if (reference is null) return;

        Assert.NotEmpty(reference.TimelineIds);
    }

    /// <summary>
    /// 13 blocks - every <c>code-snippet-*</c> - put the whole composition inside a
    /// <c>&lt;template&gt;</c>, scripts included, and render as a blank page in any browser until
    /// a host clones them in. The harness clones them, loading their external scripts first: doing
    /// it in one append let the inline script run before GSAP arrived.
    /// </summary>
    [Fact]
    public async Task A_block_whose_composition_lives_in_a_template_is_still_measurable()
    {
        var reference = await ReferenceFor("code-snippet-dark-2026");
        if (reference is null) return;

        Assert.NotEmpty(reference.TimelineIds);

        var moved = Comparison.Of(reference.Frames[0], reference.Frames[1]);
        Assert.True(moved.Differing > 0.01,
            $"the instantiated template changed {moved.Differing:P2} of the frame: it is not running");
    }

    /// <summary>Two frames of a named block, or null when this machine cannot produce them - no
    /// corpus, no browser, no network. Null rather than an assertion so the caller reads as the
    /// claim it is making.</summary>
    private static async Task<Reference?> ReferenceFor(string name)
    {
        Block? block;
        try { block = Corpus.Find(name); }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
        {
            Assert.Skip("no corpus - run: python tools/fetch-corpus.py");
            return null;
        }

        Assert.SkipWhen(Browser.Find() is null, "no headless browser on this machine");

        await using var browser = await Browser.LaunchAsync(TestContext.Current.CancellationToken);

        try
        {
            return await browser.RenderAsync(block, [0, block.Duration / 2],
                TestContext.Current.CancellationToken);
        }
        catch (BrowserException ex) when (ex.Message.Contains("gsap", StringComparison.Ordinal))
        {
            Assert.Skip("the reference needs the network: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// The engine now moves, on a block the compiler can read.
    ///
    /// <para>This test used to assert the opposite, and it is worth saying why rather than just
    /// replacing it. It was written as <c>Until_the_compiler_exists_the_engine_renders_one_still_frame</c>
    /// and it recorded the Milestone 2 baseline: 150 of 150 scored blocks rendered an identical
    /// frame at every time, because there was nothing to make them move. It was meant to fail on
    /// the day the compiler carried its first timeline. It did, so it now asserts the new truth:
    /// a block whose timeline resolves produces frames that differ.</para>
    ///
    /// <para><c>message-thread-reveal</c> rather than <c>bar-chart-race</c>, because the latter
    /// builds its whole timeline inside a loop over parsed data - the case the plan said to find
    /// early - and the compiler carries none of it.</para>
    /// </summary>
    [Fact]
    public void The_engine_moves_on_a_block_whose_timeline_the_compiler_can_read()
    {
        Block? block;
        try { block = Corpus.Find("message-thread-reveal"); }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
        {
            Assert.Skip("no corpus - run: python tools/fetch-corpus.py");
            return;
        }

        var translated = Translation.Of(block);
        Assert.Contains("@keyframes", translated.Html, StringComparison.Ordinal);

        var rendered = Engine.Render(block, translated.Html,
            [0, block.Duration / 2, block.Duration], Engine.FindFonts());

        var moved = Comparison.Of(rendered.Frames[0], rendered.Frames[^1]);
        Assert.True(moved.Differing > 0.01,
            $"the engine changed {moved.Differing:P2} of the frame between the first sample and the "
            + "last: the compiled keyframes are not running");
    }
}
