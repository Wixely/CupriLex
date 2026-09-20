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

    [Fact]
    public void Frames_of_different_sizes_are_refused_rather_than_scored()
    {
        var thrown = Assert.Throws<ArgumentException>(
            () => Comparison.Of(Flat(10, 10, 0, 0, 0), Flat(20, 10, 0, 0, 0)));

        Assert.Contains("10x10", thrown.Message);
        Assert.Contains("20x10", thrown.Message);
    }
}
