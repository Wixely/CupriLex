using CupriLex.Compiler;

namespace CupriLex.Harness;

/// <summary>A block as the engine will be given it, and everything that did not come with it.</summary>
/// <param name="Html">The document to hand to CupriFace.</param>
/// <param name="Refusals">Named, never silent. An empty list means perfect or lying.</param>
/// <remarks>Named <c>Prepared</c> and not <c>Translated</c> because the compiler already has a
/// <c>Translated</c>, and two types with one name either side of a project boundary is a trap for
/// whoever writes the next test.</remarks>
public sealed record Prepared(string Html, IReadOnlyList<string> Refusals);

/// <summary>
/// The harness's side of the seam: a block, through the compiler, ready to render.
///
/// <para>Until Milestone 3 this carried nothing but the markup, which produced the baseline every
/// later milestone has to move. It now calls the GSAP compiler, so the frames it scores are frames
/// with whatever motion the compiler could resolve - and the refusals it reports are the
/// compiler's own, by name.</para>
/// </summary>
public static class Translation
{
    public static Prepared Of(Block block)
    {
        var compiled = Translator.Of(File.ReadAllText(block.Path), block.Directory);

        return new Prepared(
            compiled.Html,
            [.. compiled.Refusals.Select(refusal => refusal.ToString())]);
    }
}
