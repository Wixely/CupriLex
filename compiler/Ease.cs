namespace CupriLex.Compiler;

/// <summary>
/// GSAP's easing curves, as functions of progress.
///
/// <para>They are evaluated here rather than handed to CSS because of the engine constraint that
/// shapes this whole compiler: one animation per element. Several tweens on one element merge into
/// a single <c>@keyframes</c>, and <c>animation-timing-function</c> applies to the animation, not
/// to a stop - so a per-tween ease cannot be expressed as a timing function. The curve is sampled
/// into stops instead and the animation itself runs linear.</para>
///
/// <para>A curve this does not know is reported and run linear rather than refused. Dropping a
/// tween because its easing was unfamiliar would lose motion that is mostly right; running it
/// linear loses only the shape of the acceleration, and the report says which blocks that
/// happened to.</para>
/// </summary>
internal static class Ease
{
    /// <summary>How many extra stops an eased tween is sampled into. The curve is only visible in
    /// the stops between its ends, and eight is where this started rather than where measurement
    /// put it - the honest way to choose it is against the comparison harness.</summary>
    public const int Samples = 8;

    /// <summary>GSAP's own default overshoot, used when <c>back</c> is written without one.</summary>
    private const double BackOvershoot = 1.70158;

    /// <summary>The curves this knows. Anything else runs linear and is reported.</summary>
    private static readonly HashSet<string> Known =
    [
        "none", "linear", "power0", "power1", "power2", "power3", "power4",
        "quad", "cubic", "quart", "quint", "strong", "sine", "expo", "circ", "back", "steps",
    ];

    public static bool Recognised(string ease) => Known.Contains(Family(ease));

    /// <summary>Whether the curve is a straight line, in which case its tween needs no stops
    /// between its ends at all.</summary>
    public static bool IsLinear(string ease) =>
        Family(ease) is "none" or "linear" or "power0" || !Recognised(ease);

    /// <summary>The number inside the parentheses, where there is one: the overshoot of
    /// <c>back.out(1.4)</c>, the count of <c>steps(4)</c>. 44 blocks of the corpus write one, with
    /// back's overshoot ranging from 1.04 to 3 - and this used to ignore all of them and use
    /// GSAP's default of 1.70158, which is a visibly different curve at either end of that
    /// range.</summary>
    private static double? Parameter(string ease)
    {
        var open = ease.IndexOf('(');
        if (open < 0) return null;

        var close = ease.IndexOf(')', open);
        var inside = close < 0 ? ease[(open + 1)..] : ease[(open + 1)..close];

        // elastic.out(1, 0.4) has two; the first is the one these curves take.
        var first = inside.Split(',')[0].Trim();

        return double.TryParse(first, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>Eased progress, for a linear progress from 0 to 1.</summary>
    public static double Of(string ease, double p)
    {
        p = Math.Clamp(p, 0, 1);

        var family = Family(ease);
        var direction = Direction(ease);
        var parameter = Parameter(ease);

        // A staircase rather than a curve, and not a function of direction at all. Every use in
        // the corpus is steps(1), which holds the start value and snaps at the end.
        if (family == "steps")
        {
            var count = Math.Max(1, (int)Math.Round(parameter ?? 1));
            return Math.Min(1, Math.Floor(p * count) / count);
        }

        var overshoot = parameter ?? BackOvershoot;

        double In(double x) => family switch
        {
            "power1" or "quad" => x * x,
            "power2" or "cubic" => x * x * x,
            "power3" or "quart" => x * x * x * x,
            "power4" or "quint" or "strong" => x * x * x * x * x,
            "sine" => 1 - Math.Cos(x * Math.PI / 2),
            "expo" => x == 0 ? 0 : Math.Pow(2, 10 * x - 10),
            "circ" => 1 - Math.Sqrt(1 - x * x),
            "back" => (overshoot + 1) * x * x * x - overshoot * x * x,
            _ => x,
        };

        return direction switch
        {
            "in" => In(p),
            "inOut" => p < 0.5 ? In(2 * p) / 2 : 1 - In(2 - 2 * p) / 2,
            _ => 1 - In(1 - p),      // "out", and GSAP's default when none is written
        };
    }

    /// <summary>The family, lower-cased and stripped of its arguments: <c>back.out(1.7)</c> is
    /// <c>back</c>.</summary>
    private static string Family(string ease)
    {
        var text = ease.Trim();
        var dot = text.IndexOf('.');
        var open = text.IndexOf('(');
        var end = dot >= 0 && (open < 0 || dot < open) ? dot : open;
        return (end < 0 ? text : text[..end]).ToLowerInvariant();
    }

    private static string Direction(string ease)
    {
        var text = ease.Trim();
        var dot = text.IndexOf('.');
        if (dot < 0) return "out";

        var rest = text[(dot + 1)..];
        var open = rest.IndexOf('(');
        var word = (open < 0 ? rest : rest[..open]).Trim().ToLowerInvariant();

        return word switch
        {
            "in" => "in",
            "inout" => "inOut",
            _ => "out",
        };
    }
}
