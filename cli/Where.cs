using Acornima.Ast;

namespace CupriLex.Cli;

/// <summary>Where a call sits, which decides whether its arguments can be known without running
/// anything.</summary>
/// <param name="InLoop">Inside a <c>for</c>, a <c>while</c>, or a <c>forEach</c>-shaped callback.
/// The hard case: the same call runs many times with different values.</param>
/// <param name="InFunction">Inside a function that is not immediately invoked - so whether it runs
/// at all, and with what, depends on the caller.</param>
/// <param name="InCondition">Inside an <c>if</c> or a ternary.</param>
public readonly record struct Where(bool InLoop, bool InFunction, bool InCondition)
{
    public static readonly Where TopLevel = new(false, false, false);

    public Where Looping => this with { InLoop = true };
    public Where InsideFunction => this with { InFunction = true };
    public Where Conditional => this with { InCondition = true };

    /// <summary>Straight-line code at the top level of the document or of an IIFE. The only place
    /// a call's arguments mean exactly what they say.</summary>
    public bool Plain => !InLoop && !InFunction && !InCondition;

    public override string ToString() =>
        Plain ? "straight-line" : string.Join("+",
            new[] { InLoop ? "loop" : null, InFunction ? "function" : null, InCondition ? "conditional" : null }
                .Where(s => s is not null));
}
