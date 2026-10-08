using System.Globalization;

namespace CupriLex.Compiler;

/// <summary>
/// An authored or tweened <c>clip-path</c>, broken into the numbers it is made of.
///
/// <para>Every value this compiler animates is an <see cref="Amount"/>, a number with a unit, and
/// a clip-path's value is a shape. The way through is that the two shapes this corpus writes ARE
/// amounts once taken apart: <c>inset()</c> is four edges and <c>polygon()</c> is 2N points. The
/// components then merge with everything else on the element exactly as the transform components
/// do, and shape-kind compatibility falls out rather than needing a rule of its own - an inset
/// cannot become a polygon, and two polygons need the same point count, because a component with
/// no counterpart at the other end has nothing to travel from and no stop gets written.</para>
///
/// <para>Lifted out of the tween reader so the cascade reader can use it too. A <c>.to()</c> that
/// names only the end shape needs the start one, and for a wipe the start is nearly always in the
/// stylesheet: <c>us-map-hex</c> authors <c>clip-path: inset(0 100% 0 0)</c> on its headline and
/// tweens to <c>inset(0 0% 0 0)</c>. Without reading the authored value that compiles from
/// nothing and the headline is unclipped from the first frame.</para>
///
/// <para>Anything else - <c>circle()</c>, <c>url(#mask)</c>, a <c>var()</c> - is not reduced.
/// Half a shape is not a smaller truth, it is a different picture.</para>
/// </summary>
internal static class Clip
{
    /// <summary>Reads <paramref name="css"/> into <paramref name="values"/>, adding nothing for a
    /// shape it cannot reduce. Returns whether it recognised one, so a caller that owes a refusal
    /// can tell "not a clip I know" from "no clip stated".</summary>
    public static bool Read(string css, Dictionary<string, Amount> values)
    {
        if (Components(css) is not { } parsed) return false;

        foreach (var (component, amount) in parsed) values[component] = amount;
        return true;
    }

    /// <summary>The components of a shape, or null for one this cannot reduce to numbers.</summary>
    public static Dictionary<string, Amount>? Components(string? css)
    {
        var found = new Dictionary<string, Amount>();

        if (Inset(css) is { } edges)
        {
            for (var i = 0; i < Properties.ClipOrder.Length; i++)
                found[Properties.ClipOrder[i]] = edges[i];
            return found;
        }

        if (Polygon(css) is { } points)
        {
            for (var i = 0; i < points.Count; i++)
            {
                found[$"clipP{i}x"] = points[i].X;
                found[$"clipP{i}y"] = points[i].Y;
            }
            return found;
        }

        return null;
    }

    /// <summary>
    /// The points of a <c>polygon()</c>, or null for anything else.
    ///
    /// <para>A polygon is 2N numbers, and two polygons interpolate point by point when they have
    /// the same count - which is how these compositions write a wipe: the same nine-point shape
    /// with its vertices swept from one side to the other. A pair with DIFFERENT counts cannot
    /// interpolate and is caught downstream, where the stops for a point that only one end has
    /// would have nothing to travel from.</para>
    /// </summary>
    public static List<(Amount X, Amount Y)>? Polygon(string? text)
    {
        if (text is null) return null;
        var trimmed = text.Trim();

        if (!trimmed.StartsWith("polygon(", StringComparison.OrdinalIgnoreCase)
            || !trimmed.EndsWith(')'))
            return null;

        var points = new List<(Amount, Amount)>();

        foreach (var pair in trimmed[8..^1].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var axes = pair.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

            // A fill rule - `polygon(evenodd, ...)` - is one token where a point is two, and it
            // is not something this reduces to numbers.
            if (axes.Length != 2) return null;
            if (Number(axes[0], "%") is not { } x) return null;
            if (Number(axes[1], "%") is not { } y) return null;

            points.Add((x, y));
        }

        return points.Count >= 3 ? points : null;
    }

    /// <summary>
    /// The four edges of an <c>inset()</c>, or null for a shape this cannot reduce to numbers.
    ///
    /// <para>CSS's own shorthand: one value is all four edges, two are vertical then horizontal,
    /// three are top, horizontal, bottom, and four are top, right, bottom, left. A trailing
    /// <c>round &lt;radius&gt;</c> is dropped rather than refused - the corners of a clip are not
    /// what a wipe is about, and refusing the whole tween over them would lose the wipe.</para>
    /// </summary>
    public static Amount[]? Inset(string? text)
    {
        if (text is null) return null;
        var trimmed = text.Trim();

        if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
            return [new Amount(0, ""), new Amount(0, ""), new Amount(0, ""), new Amount(0, "")];

        if (!trimmed.StartsWith("inset(", StringComparison.OrdinalIgnoreCase)
            || !trimmed.EndsWith(')'))
            return null;

        var inner = trimmed[6..^1];
        var round = inner.IndexOf("round", StringComparison.OrdinalIgnoreCase);
        if (round >= 0) inner = inner[..round];

        var parts = inner.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 4) return null;

        var edge = new Amount[4];
        for (var i = 0; i < parts.Length; i++)
        {
            if (Number(parts[i], "") is not { } value) return null;
            edge[i] = value;
        }

        return parts.Length switch
        {
            1 => [edge[0], edge[0], edge[0], edge[0]],
            2 => [edge[0], edge[1], edge[0], edge[1]],
            3 => [edge[0], edge[1], edge[2], edge[1]],
            _ => edge,
        };
    }

    /// <summary>One edge or one coordinate, with its unit. <paramref name="fallback"/> is the unit
    /// a bare number takes - percent for a polygon's points, nothing for an inset's edges, where a
    /// unitless zero is the commonest value there is.</summary>
    private static Amount? Number(string text, string fallback)
    {
        text = text.Trim();

        var suffix = text.EndsWith('%') ? "%"
            : text.EndsWith("px", StringComparison.OrdinalIgnoreCase) ? "px"
            : text.EndsWith("rem", StringComparison.OrdinalIgnoreCase) ? "rem"
            : "";

        var digits = suffix.Length > 0 ? text[..^suffix.Length] : text;

        return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture,
            out var parsed)
            ? new Amount(parsed, suffix.Length > 0 ? suffix : fallback)
            : null;
    }
}
