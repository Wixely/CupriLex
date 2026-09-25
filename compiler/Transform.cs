using System.Globalization;

namespace CupriLex.Compiler;

/// <summary>
/// An authored <c>transform</c>, broken into the components this compiler animates.
///
/// <para>Written here rather than taken from a CSS library on purpose. What is wanted is the
/// AUTHORED value - <c>scale(0)</c>, <c>translate(-50%, -50%)</c> - in the same component
/// vocabulary that <see cref="Properties.TransformOrder"/> already uses, with its unit intact. A
/// library resolves those to a matrix against a viewport, and a matrix cannot be put back into
/// "the scale component is zero" without assumptions this compiler has no business making.</para>
///
/// <para><b>Unrecognised means unanswered.</b> A function this does not know - <c>matrix</c>,
/// <c>translate3d</c>, <c>perspective</c>, anything with a <c>var()</c> in it - abandons the whole
/// transform rather than returning the half it understood. Half of a transform is not a smaller
/// truth, it is a different element position.</para>
/// </summary>
internal static class Transform
{
    /// <summary>Reads <paramref name="css"/> into <paramref name="values"/>, adding nothing if any
    /// part of it cannot be read.</summary>
    public static void Read(string css, Dictionary<string, Amount> values)
    {
        if (Parse(css) is { } parsed)
            foreach (var (component, amount) in parsed) values[component] = amount;
    }

    /// <summary>The components, or null if the transform holds anything unrecognised.</summary>
    public static Dictionary<string, Amount>? Parse(string css)
    {
        var found = new Dictionary<string, Amount>();
        var text = css.Trim();

        if (text.Length == 0) return null;

        // The identity, spelled two ways. Both mean "no transform", which is a real answer and a
        // different one from "no transform stated": an element whose stylesheet says `none` is at
        // the identity for certain, and the caller may rely on it.
        if (text.Equals("none", StringComparison.OrdinalIgnoreCase)
            || text.Equals("initial", StringComparison.OrdinalIgnoreCase))
            return found;

        var at = 0;
        while (at < text.Length)
        {
            while (at < text.Length && (char.IsWhiteSpace(text[at]) || text[at] == ',')) at++;
            if (at >= text.Length) break;

            var open = text.IndexOf('(', at);
            if (open < 0) return null;

            var close = text.IndexOf(')', open);
            if (close < 0) return null;

            var name = text[at..open].Trim().ToLowerInvariant();
            var arguments = text[(open + 1)..close]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (!Function(name, arguments, found)) return null;

            at = close + 1;
        }

        return found;
    }

    private static bool Function(string name, string[] arguments, Dictionary<string, Amount> into)
    {
        switch (name)
        {
            case "translate" when arguments.Length is 1 or 2:
                // A one-argument translate leaves Y alone, which is the identity and not zero-px:
                // writing translateY(0px) would be the same thing, but only by luck of the unit.
                if (!Set(into, "translateX", arguments[0], "px")) return false;
                return arguments.Length == 1 || Set(into, "translateY", arguments[1], "px");

            case "translatex" when arguments.Length == 1:
                return Set(into, "translateX", arguments[0], "px");

            case "translatey" when arguments.Length == 1:
                return Set(into, "translateY", arguments[0], "px");

            case "scale" when arguments.Length == 1:
                return Set(into, "scale", arguments[0], "");

            // scale(x, y) is two independent components, and the compiler has both. It is NOT
            // `scale` with a second opinion: writing it as one would lose the axis that differs.
            case "scale" when arguments.Length == 2:
                return Set(into, "scaleX", arguments[0], "")
                       && Set(into, "scaleY", arguments[1], "");

            case "scalex" when arguments.Length == 1:
                return Set(into, "scaleX", arguments[0], "");

            case "scaley" when arguments.Length == 1:
                return Set(into, "scaleY", arguments[0], "");

            case "rotate" or "rotatez" when arguments.Length == 1:
                return Set(into, "rotate", arguments[0], "deg");

            case "skewx" when arguments.Length == 1:
                return Set(into, "skewX", arguments[0], "deg");

            case "skewy" when arguments.Length == 1:
                return Set(into, "skewY", arguments[0], "deg");

            case "skew" when arguments.Length is 1 or 2:
                if (!Set(into, "skewX", arguments[0], "deg")) return false;
                return arguments.Length == 1 || Set(into, "skewY", arguments[1], "deg");

            default:
                // matrix, translate3d, rotate3d, perspective, a var() this cannot resolve, or a
                // known function with an argument count that is not one of its forms.
                return false;
        }
    }

    private static bool Set(Dictionary<string, Amount> into, string component, string value, string fallbackUnit)
    {
        if (Number(value, fallbackUnit) is not { } amount) return false;

        into[component] = amount;
        return true;
    }

    /// <summary>A CSS number with its unit, or null. Deliberately strict: a value this cannot read
    /// exactly is one the caller must not act on.</summary>
    private static Amount? Number(string value, string fallbackUnit)
    {
        value = value.Trim();
        if (value.Length == 0) return null;

        var end = 0;
        if (end < value.Length && (value[end] == '+' || value[end] == '-')) end++;
        while (end < value.Length && (char.IsAsciiDigit(value[end]) || value[end] == '.')) end++;

        if (end == 0) return null;

        if (!double.TryParse(value[..end], NumberStyles.Float, CultureInfo.InvariantCulture,
                out var number))
            return null;

        var unit = value[end..].Trim();

        // A number written without a unit takes the one its function implies: px for a length,
        // deg for an angle, nothing at all for a scale. CSS only permits the bare form for zero
        // and for unitless quantities, and both land in the same place here.
        if (unit.Length == 0) return new Amount(number, fallbackUnit);

        return unit.ToLowerInvariant() switch
        {
            "px" => new Amount(number, "px"),
            "%" => new Amount(number, "%"),
            "deg" => new Amount(number, "deg"),
            "rad" => new Amount(number * 180 / Math.PI, "deg"),
            "turn" => new Amount(number * 360, "deg"),
            _ => null,      // em, rem, vw, vh: real lengths this cannot resolve without a layout
        };
    }
}
