using Xunit;

namespace CupriLex.Harness.Tests;

/// <summary>
/// What the corpus is, asserted rather than remembered.
///
/// <para>Skipped rather than failed when the corpus is absent: it is deliberately not vendored
/// (<c>python tools/fetch-corpus.py</c>), so a clean checkout has none and a red test would be
/// saying something untrue about the code.</para>
/// </summary>
public class CorpusTests
{
    private static bool Fetched()
    {
        try { return Directory.Exists(Corpus.Root()); }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void RequireCorpus() =>
        Assert.SkipUnless(Fetched(), "no corpus - run: python tools/fetch-corpus.py");

    /// <summary>Seven directories in the corpus hold two blocks each. Keying a block on its
    /// directory - which is what <c>registry-item.json</c> describes - silently scored fourteen
    /// files as seven, and gave the second of each pair the first one's duration.</summary>
    [Fact]
    public void A_directory_holding_two_blocks_yields_two_blocks()
    {
        RequireCorpus();

        var blocks = Corpus.All();
        var directories = blocks.Select(b => b.Directory).Distinct().Count();

        Assert.True(blocks.Count > directories,
            $"{blocks.Count} blocks over {directories} directories: the pairs have gone missing");
        Assert.Equal(blocks.Count, blocks.Select(b => b.Path).Distinct().Count());
    }

    /// <summary>Four directories each hold a <c>demo.html</c>. Named by the file stem alone they
    /// were four blocks called "demo", overwriting one another's images and one another's row in
    /// the report - so the name falls back to <c>directory/stem</c> where the stem repeats.</summary>
    [Fact]
    public void Blocks_that_share_a_file_name_get_distinct_names()
    {
        RequireCorpus();

        var blocks = Corpus.All();
        var duplicates = blocks.GroupBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        Assert.Empty(duplicates);
        Assert.Equal(blocks.Count, blocks.Select(b => b.Slug).Distinct().Count());
    }

    [Fact]
    public void Every_block_has_a_positive_size_and_duration()
    {
        RequireCorpus();

        foreach (var block in Corpus.All())
        {
            Assert.True(block.Width > 0 && block.Height > 0, block.Name + " has no size");
            Assert.True(block.Duration > 0, block.Name + " has no duration");
        }
    }

    /// <summary>The corpus survey's central finding, as a test: every block is animated in
    /// JavaScript and none of it is CSS. If this ever fails, some of the corpus became reachable
    /// without the compiler and <c>docs/CORPUS.md</c> needs re-running.</summary>
    [Fact]
    public void Every_block_is_animated_in_javascript_and_none_of_it_in_css()
    {
        RequireCorpus();

        var withKeyframes = new List<string>();
        var withoutGsap = new List<string>();

        foreach (var block in Corpus.All())
        {
            var html = File.ReadAllText(block.Path);
            if (html.Contains("@keyframes", StringComparison.OrdinalIgnoreCase)) withKeyframes.Add(block.Name);
            if (!html.Contains("gsap", StringComparison.OrdinalIgnoreCase)) withoutGsap.Add(block.Name);
        }

        Assert.Empty(withKeyframes);
        Assert.Empty(withoutGsap);
    }

    [Fact]
    public void An_unknown_block_name_says_what_was_close()
    {
        RequireCorpus();

        var thrown = Assert.Throws<FileNotFoundException>(() => Corpus.Find("bar-chart"));
        Assert.Contains("bar-chart-race", thrown.Message);
    }
}
