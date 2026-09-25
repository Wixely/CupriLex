namespace CupriLex.Compiler;

/// <summary>Something that could not be carried, named where it was written.</summary>
/// <param name="What">One line, in the author's vocabulary rather than the compiler's.</param>
/// <param name="Line">The line in the block's HTML, where it is known.</param>
/// <remarks>The deliverable, not a log. A translation with an empty refusal list is either perfect
/// or lying, and the corpus says which.</remarks>
public sealed record Refusal(string What, int Line = 0)
{
    public override string ToString() => Line > 0 ? $"line {Line}: {What}" : What;
}

/// <summary>One animatable property at one instant, already in CSS terms.</summary>
/// <param name="Name">A CSS property, or one of the transform components this compiler tracks
/// separately so that several tweens on the same element can be merged into one
/// <c>transform</c>.</param>
internal sealed record Track(string Name, double From, double To, string Unit);

/// <summary>
/// One tween, resolved: when it starts, how long it runs, what it moves, and on what.
/// </summary>
/// <param name="Selector">A CSS selector the engine can match. Every target is reduced to one of
/// these or refused - the engine has no element handles.</param>
/// <param name="Start">Absolute seconds on the composition's clock.</param>
/// <param name="Duration">Seconds. Zero for a <c>set</c>, which is a state change with no travel.</param>
/// <param name="Ease">The GSAP ease name, kept as written so the sampler can say what it did not
/// know rather than silently using linear.</param>
internal sealed record Tween(
    string Selector,
    double Start,
    double Duration,
    string Ease,
    IReadOnlyList<Track> Tracks,
    int Line);

/// <summary>Everything read out of one block's scripts.</summary>
/// <param name="Tweens">Resolved, in the order they were read.</param>
/// <param name="Refusals">What was not. Includes every tween that could not be resolved, by name.</param>
/// <param name="Seconds">The end of the last tween - the composition's own idea of how long it is,
/// which is not always what <c>data-duration</c> claims.</param>
internal sealed record Motion(
    IReadOnlyList<Tween> Tweens, IReadOnlyList<Refusal> Refusals, double Seconds)
{
    public static readonly Motion Nothing = new([], [], 0);
}

/// <summary>
/// The properties this compiler knows how to carry, and what each becomes.
///
/// <para>The list is short because the engine's is short: measured, only <c>width</c>,
/// <c>height</c>, <c>opacity</c> and <c>transform</c> animate at all. Everything else in a tween
/// is refused by name rather than emitted into a declaration that would parse, run, and change
/// nothing - which is the failure this whole repository is arranged around.</para>
/// </summary>
internal static class Properties
{
    /// <summary>GSAP's name, the CSS or transform component it becomes, and the unit it carries.</summary>
    private static readonly Dictionary<string, (string Name, string Unit)> Known = new()
    {
        ["x"] = ("translateX", "px"),
        ["y"] = ("translateY", "px"),
        ["xPercent"] = ("translateX", "%"),
        ["yPercent"] = ("translateY", "%"),
        ["scale"] = ("scale", ""),
        ["scaleX"] = ("scaleX", ""),
        ["scaleY"] = ("scaleY", ""),
        ["rotation"] = ("rotate", "deg"),
        ["rotate"] = ("rotate", "deg"),
        ["rotationZ"] = ("rotate", "deg"),
        ["skewX"] = ("skewX", "deg"),
        ["skewY"] = ("skewY", "deg"),
        ["opacity"] = ("opacity", ""),
        ["autoAlpha"] = ("opacity", ""),     // GSAP also flips visibility; the engine ignores that anyway
        ["width"] = ("width", "px"),
        ["height"] = ("height", "px"),
    };

    /// <summary>The transform components, in the order they are written out. Fixed, because
    /// transform is not commutative and an order that varies between keyframe stops makes an
    /// element take a different path through the same two states.</summary>
    public static readonly string[] TransformOrder =
        ["translateX", "translateY", "scale", "scaleX", "scaleY", "rotate", "skewX", "skewY"];

    public static bool IsTransform(string name) => TransformOrder.Contains(name);

    public static bool TryMap(string gsapName, out string cssName, out string unit)
    {
        if (Known.TryGetValue(gsapName, out var mapped))
        {
            cssName = mapped.Name;
            unit = mapped.Unit;
            return true;
        }

        cssName = gsapName;
        unit = "";
        return false;
    }

    /// <summary>The neutral value of a component, for a stop that has to write the whole transform
    /// even though only part of it is moving.</summary>
    public static double Identity(string component) =>
        component is "scale" or "scaleX" or "scaleY" ? 1 : 0;

    /// <summary>Tween keys that are instructions to GSAP rather than properties to animate. They
    /// are read, not refused: refusing <c>duration</c> as an unsupported property would be
    /// nonsense.</summary>
    public static readonly HashSet<string> Controls =
    [
        "duration", "delay", "ease", "stagger", "repeat", "yoyo", "repeatDelay",
        "immediateRender", "overwrite", "onComplete", "onStart", "onUpdate", "onRepeat",
        "paused", "id", "data", "callbackScope", "lazy", "transformOrigin", "force3D",
    ];
}
