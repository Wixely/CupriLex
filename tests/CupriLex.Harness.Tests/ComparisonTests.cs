using Xunit;

namespace CupriLex.Harness.Tests;

/// <summary>
/// What the score means, pinned down - including the part that is unflattering.
///
/// <para>The headline number is a mean absolute difference, and a mean over a frame that is mostly
/// one flat colour is generous to a renderer that got everything interesting wrong. That is a
/// property of the measure rather than a bug in it, and the test below states it in numbers so
/// that nobody quotes a similarity on its own and calls a block nearly done.</para>
/// </summary>
public class ComparisonTests
{
    private static Frame Flat(int w, int h, byte r, byte g, byte b)
    {
        var pixels = new byte[w * h * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = 255;
        }
        return new Frame(w, h, pixels);
    }

    private static Frame WithBlackRectangle(Frame source, int x, int y, int w, int h)
    {
        var pixels = (byte[])source.Rgba.Clone();
        for (var row = y; row < y + h; row++)
        for (var column = x; column < x + w; column++)
        {
            var i = (row * source.Width + column) * 4;
            pixels[i] = pixels[i + 1] = pixels[i + 2] = 0;
        }
        return source with { Rgba = pixels };
    }

    [Fact]
    public void Identical_frames_score_exactly_one()
    {
        var frame = Flat(64, 64, 0xF5, 0xF3, 0xEF);
        var result = Comparison.Of(frame, frame);

        Assert.Equal(1.0, result.Similarity);
        Assert.Equal(0.0, result.Differing);
    }

    [Fact]
    public void Black_against_white_scores_zero()
    {
        var result = Comparison.Of(Flat(16, 16, 255, 255, 255), Flat(16, 16, 0, 0, 0));

        Assert.Equal(0.0, result.Similarity, 6);
        Assert.Equal(1.0, result.Differing);
    }

    /// <summary>The weakness of the headline, written down. A tenth of the frame lost entirely
    /// still scores over 90% similar, which is why nothing in this harness reports similarity
    /// without the differing share beside it.</summary>
    [Fact]
    public void A_frame_missing_a_tenth_of_itself_still_scores_over_ninety_percent_similar()
    {
        var reference = Flat(100, 100, 255, 255, 255);
        var missing = WithBlackRectangle(reference, 0, 0, 100, 10);   // exactly a tenth, gone

        var result = Comparison.Of(reference, missing);

        Assert.Equal(0.90, result.Similarity, 3);
        Assert.Equal(0.10, result.Differing, 6);
    }

    /// <summary>Eight levels of difference is where "the same colour, rasterised twice" stops and
    /// "a different colour" starts. Below it, nothing is counted as differing.</summary>
    [Fact]
    public void A_difference_below_the_threshold_is_not_counted_as_differing()
    {
        var reference = Flat(32, 32, 100, 100, 100);
        var nudged = Flat(32, 32, 108, 100, 100);   // exactly the threshold, not above it

        var result = Comparison.Of(reference, nudged);

        Assert.Equal(0.0, result.Differing);
        Assert.True(result.Similarity < 1.0, "a difference the eye ignores is still error");
    }

    // ---- the two measures added after the first metric ranked the corpus backwards -------------

    /// <summary>
    /// Content is counted over the pixels the REFERENCE paints on, not over the whole frame.
    ///
    /// <para>The same missing rectangle is 10% of a frame and 100% of its content. On a 1080x1920
    /// composition that paints on 6% of its area, the two answers differ by a factor of fifteen,
    /// and the frame-wide one is what made a block missing its entire answer text score 97.8%.</para>
    /// </summary>
    [Fact]
    public void Content_is_measured_over_what_the_reference_actually_paints()
    {
        var reference = WithBlackRectangle(Flat(100, 100, 255, 255, 255), 0, 0, 100, 10);
        var blank = Flat(100, 100, 255, 255, 255);

        var result = Comparison.Of(reference, blank);

        Assert.Equal(0.10, result.Differing, 6);          // a tenth of the frame
        Assert.Equal(1.00, result.ContentDiffering, 6);   // all of the content
    }

    /// <summary>
    /// A reference that paints nothing falls back to the frame-wide share.
    ///
    /// <para>A uniform frame is ALL background by definition, so there is no content to weight by.
    /// The first version returned zero for that - reporting every such frame as perfect content
    /// whatever the candidate drew - and several transition blocks open on exactly a solid fill.
    /// This test is why the fallback exists.</para>
    /// </summary>
    [Fact]
    public void A_reference_that_paints_nothing_falls_back_to_the_frame_wide_share()
    {
        var result = Comparison.Of(Flat(16, 16, 255, 255, 255), Flat(16, 16, 0, 0, 0));

        Assert.Equal(1.0, result.Differing);
        Assert.Equal(1.0, result.ContentDiffering);
    }

    /// <summary>
    /// How far wrong, among the pixels that are wrong at all.
    ///
    /// <para>The axis the share-of-pixels measures cannot see. Half a frame off by 7% is a
    /// different failure from a fiftieth off by 62% - the first is text rasterised differently,
    /// the second is text that is not there - and by share alone the first looks far worse.</para>
    /// </summary>
    [Fact]
    public void Error_when_wrong_ignores_the_pixels_that_are_right()
    {
        var reference = Flat(100, 100, 255, 255, 255);

        // One row in ten, replaced outright. The other nine are untouched.
        var result = Comparison.Of(reference, WithBlackRectangle(reference, 0, 0, 100, 10));

        Assert.Equal(0.10, result.Differing, 6);
        Assert.Equal(1.0, result.ErrorWhenWrong, 3);   // black against white: the whole scale
        Assert.Equal(0.10, result.Severe, 6);
    }

    /// <summary>A small, uniform shift is wrong everywhere and barely wrong anywhere - the shape
    /// of a different rasteriser rather than missing content, and nothing severe.</summary>
    [Fact]
    public void A_slight_shift_everywhere_reads_as_slight()
    {
        var result = Comparison.Of(Flat(32, 32, 100, 100, 100), Flat(32, 32, 130, 100, 100));

        Assert.Equal(1.0, result.Differing);
        Assert.Equal(30 / 255.0, result.ErrorWhenWrong, 3);
        Assert.Equal(0.0, result.Severe);
    }

    [Fact]
    public void Identical_frames_have_no_error_to_report()
    {
        var frame = Flat(64, 64, 0xF5, 0xF3, 0xEF);
        var result = Comparison.Of(frame, frame);

        Assert.Equal(0.0, result.ErrorWhenWrong);
        Assert.Equal(0.0, result.Severe);
        Assert.Equal(0.0, result.ContentDiffering);
    }

    /// <summary>The background is the frame's most common colour, which on a designed composition
    /// is its ground. Everything else counts as content.</summary>
    [Fact]
    public void The_background_is_the_colour_the_frame_uses_most()
    {
        var frame = WithBlackRectangle(Flat(100, 100, 240, 240, 240), 0, 0, 20, 20);

        var (r, g, b) = frame.Background();

        Assert.InRange(r, 236, 244);
        Assert.InRange(g, 236, 244);
        Assert.InRange(b, 236, 244);
    }

    [Fact]
    public void Frames_of_different_sizes_are_refused_rather_than_scored()
    {
        var thrown = Assert.Throws<ArgumentException>(
            () => Comparison.Of(Flat(10, 10, 0, 0, 0), Flat(20, 10, 0, 0, 0)));

        Assert.Contains("10x10", thrown.Message);
        Assert.Contains("20x10", thrown.Message);
    }

    // ---- ink: how much a renderer painted, asked of one frame alone --------------------------

    [Fact]
    public void A_frame_of_one_colour_has_no_ink_whatever_that_colour_is()
    {
        // Including black. The measure is against the frame's OWN background, so a dark
        // composition that renders as a dark rectangle must read as empty rather than as full -
        // which is what comparing against white would have said, and several corpus blocks are
        // exactly that.
        Assert.Equal(0.0, Flat(64, 64, 255, 255, 255).Ink());
        Assert.Equal(0.0, Flat(64, 64, 0, 0, 0).Ink());
        Assert.Equal(0.0, Flat(64, 64, 34, 34, 34).Ink());
    }

    [Fact]
    public void Ink_is_the_share_of_the_frame_that_is_not_its_background()
    {
        // 20x20 of black on a 100x100 light frame: 400 of 10,000 pixels.
        var frame = WithBlackRectangle(Flat(100, 100, 240, 240, 240), 0, 0, 20, 20);

        Assert.Equal(0.04, frame.Ink(), 3);
    }

    [Fact]
    public void A_blank_engine_frame_is_distinguishable_from_a_wrong_one()
    {
        // The whole reason the measure exists. Both of these score badly against the reference,
        // and they need completely different work: one drew the wrong thing, the other drew
        // nothing. Only ink separates them.
        var reference = WithBlackRectangle(Flat(100, 100, 240, 240, 240), 0, 0, 30, 30);
        var blank = Flat(100, 100, 240, 240, 240);
        var wrong = WithBlackRectangle(Flat(100, 100, 240, 240, 240), 60, 60, 30, 30);

        Assert.True(Comparison.Of(reference, blank).ContentDiffering > 0.9);
        Assert.True(Comparison.Of(reference, wrong).ContentDiffering > 0.9);

        Assert.Equal(0.0, blank.Ink());
        Assert.Equal(0.09, wrong.Ink(), 3);
    }
}
