using CupriFace;
using CupriFace.Components;
using CupriFace.Svg;
using SkiaSharp;
using Xunit;

namespace CupriLex.Harness.Tests;

/// <summary>
/// Engine behaviour two rewrite rules used to work around, now asserted the other way up.
///
/// <para>Both were found by dissecting x-post on 2026-10-05: a card centred with
/// <c>top: 50%; left: 50%; transform: translate(-50%, -50%)</c> inside an unsized static root was
/// drawn at the top-right corner of the frame on CupriFace 0.28.1, and the block scored 40% of
/// content with its motion compiled correctly. Two rules worked around it - a percentage translate
/// written in px where the border box was declared, and an unsized root given its declared size -
/// and these tests pinned the conditions they existed for, built to FAIL on the day the engine
/// stopped needing them.</para>
///
/// <para>That day was 0.34.0 (#258, #259). Both rules are deleted and the tests stay, flipped, so
/// a regression in either is a red test here rather than a corpus run away. The matrix carries the
/// same two rows; this is the same question asked where the build will notice.</para>
/// </summary>
public class EngineGapTests
{
    private const int Width = 400, Height = 200;

    private static byte[] Render(string css, string markup = "<div class=\"p\"></div>")
    {
        using var document = CupriDocument.Load(
            $"{markup}<style>html,body{{margin:0;height:100%;background:#fff;}}{css}</style>");
        document.UseComponents(ComponentRegistry.Default());
        document.UseSvg();
        document.Animate(0);
        document.Settle(Width, Height, TimeSpan.FromSeconds(5));
        using var image = document.RenderToImage(Width, Height);
        using var bitmap = SKBitmap.FromImage(image);
        return bitmap.GetPixelSpan().ToArray();
    }

    /// <summary>
    /// A percentage translate moves the element by a fraction of its own box: <c>-50%</c> of a
    /// 200px-wide box is the same picture as <c>-100px</c>. Ignored outright through 0.33.0.
    /// </summary>
    [Fact]
    public void A_percentage_translate_is_a_fraction_of_the_element_s_own_box()
    {
        const string box = ".p{position:absolute;left:0;top:0;width:200px;height:100px;background:#d9642a;";

        var percent = Render(box + "transform:translateX(-50%);}");
        var pixels = Render(box + "transform:translateX(-100px);}");
        var still = Render(box + "}");

        Assert.False(still.AsSpan().SequenceEqual(pixels),
            "the control pair does not differ, so this test cannot see a translate at all");
        Assert.True(percent.AsSpan().SequenceEqual(pixels),
            "a percentage translate no longer resolves against the element's own box. Through "
            + "0.33.0 it was ignored and a rule in the translator wrote it in px; if that is back, "
            + "so is the rule (CupriFace #258).");
    }

    /// <summary>
    /// An absolute child of an unsized static parent is positioned against the viewport, as a
    /// browser does, so <c>top: 50%</c> is halfway down the frame. Through 0.33.0 it was 50% of
    /// the parent's own zero-height box.
    /// </summary>
    [Fact]
    public void Top_50_percent_inside_an_unsized_static_parent_is_halfway_down_the_viewport()
    {
        const string markup = "<div class=\"box\"><div class=\"p\"></div></div>";
        const string box = ".p{position:absolute;left:0;width:200px;height:50px;background:#d9642a;";

        var unsized = Render(".box{}" + box + "top:50%;}", markup);
        var sized = Render(".box{height:200px;}" + box + "top:50%;}", markup);
        var atTop = Render(".box{}" + box + "top:0;}", markup);

        Assert.False(atTop.AsSpan().SequenceEqual(sized),
            "top: 50% in a SIZED parent no longer moves the element, so this test is measuring "
            + "something other than the containing block");
        Assert.True(unsized.AsSpan().SequenceEqual(sized),
            "an absolute child of an unsized static parent is positioned against that parent's "
            + "own box again. Through 0.33.0 that put every centred card at the top of the frame "
            + "and a rule in the translator sized the root; if that is back, so is the rule "
            + "(CupriFace #259).");
    }
}
