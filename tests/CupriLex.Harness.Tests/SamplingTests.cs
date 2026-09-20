using Xunit;

namespace CupriLex.Harness.Tests;

/// <summary>Where the harness looks, along a composition's clock.</summary>
public class SamplingTests
{
    /// <summary>Both ends included, because both ends are where the disagreement is: <c>t=0</c>
    /// catches an opening state the engine never applied, and the end catches a composition that
    /// finished somewhere the engine never went.</summary>
    [Fact]
    public void Samples_include_the_first_and_last_instant()
    {
        var times = Program.Times(12, 5);

        Assert.Equal([0, 3, 6, 9, 12], times);
    }

    [Fact]
    public void A_single_sample_lands_in_the_middle_rather_than_on_an_empty_opening_frame()
    {
        Assert.Equal([5.0], Program.Times(10, 1));
    }

    [Fact]
    public void Asking_for_no_samples_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Program.Times(10, 0));
    }
}
