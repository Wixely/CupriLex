using System.Globalization;

namespace CupriLex.Compiler;

/// <summary>
/// An authored or tweened <c>filter</c>, broken into the numbers it is made of.
///
/// <para>The same move as <see cref="Clip"/>, for the same reason: every value this compiler
/// animates is an <see cref="Amount"/>, and a filter's value is a list of functions. A filter list
/// IS a set of amounts once taken apart, one per function, and the components then merge with
/// everything else on the element exactly as the transform components do.</para>
///
/// <para>The engine has animated <c>filter</c> since CupriFace 0.39.0 (#291), which this
/// repository filed after measuring that it painted statically and never moved. 104 tweens across
/// 16 blocks, and they are nearly all one function: 92 of the corpus's filter values are
/// <c>blur()</c>, 17 are <c>brightness()</c>, and <c>none</c> accounts for 38 more.</para>
///
/// <para><c>none</c> is the identity, and it decomposes to the identity of every function rather
/// than to nothing. That is what makes a blur-in carryable: a tween from <c>none</c> to
/// <c>blur(8px)</c> is a tween from zero blur, and CSS says the same - an absent function in one
/// of two interpolated filter lists is its own identity.</para>
///
/// <para>Anything else - <c>drop-shadow()</c>, which is four lengths and a colour, or a
/// <c>var()</c> - is not reduced. Four corpus values are drop-shadows and they are refused by
/// name with the value quoted.</para>
/// </summary>
internal static class Filter
{
    /// <summary>The functions this reduces to numbers, each with the component name it becomes,
    /// the unit it carries, and what it is worth when the filter does not mention it.</summary>
    private static readonly (string Function, string Component, string Unit, double Identity)[] Known =
    [
        ("blur", "filterBlur", "px", 0),
        ("brightness", "filterBrightness", "", 1),
        ("saturate", "filterSaturate", "", 1),
        ("contrast", "filterContrast", "", 1),
        ("grayscale", "filterGrayscale", "", 0),
        ("opacity", "filterOpacity", "", 1),
    ];

    /// <summary>The components, in the order they are written back out. Fixed, because a filter
    /// list is applied in sequence and blurring a brightened element is not the same picture as
    /// brightening a blurred one.</summary>
    public static readonly string[] Order = [.. Known.Select(k => k.Component)];

    public static bool Is(string component) => Order.Contains(component);

    /// <summary>The unit a component carries, for the property map.</summary>
    public static string Unit(string component) =>
        Known.FirstOrDefault(k => k.Component == component).Unit ?? "";

    /// <summary>What a component is worth on an element nobody has filtered. Every one of them has
    /// an answer, which is why a tween TO a filter never has to be refused for want of a start.</summary>
    public static double? Resting(string component) =>
        Known.Any(k => k.Component == component)
            ? Known.First(k => k.Component == component).Identity
            : null;

    /// <summary>The CSS function a component is written back as.</summary>
    public static string Function(string component) =>
        Known.First(k => k.Component == component).Function;

    /// <summary>Reads <paramref name="css"/> into <paramref name="values"/>. Returns whether it
    /// recognised the whole list - a list with one function it cannot reduce adds nothing at all,
    /// because half a filter is a different picture rather than a smaller truth.</summary>
    public static bool Read(string css, Dictionary<string, Amount> values)
    {
        if (Components(css) is not { } parsed) return false;

        foreach (var (component, amount) in parsed) values[component] = amount;
        return true;
    }

    /// <summary>The components of a filter list, or null for one holding anything this cannot
    /// reduce to numbers.</summary>
    public static Dictionary<string, Amount>? Components(string? css)
    {
        if (css is null) return null;
        var text = css.Trim();
        if (text.Length == 0) return null;

        var found = new Dictionary<string, Amount>();

        // `none` is every function at its identity. Stated rather than left empty: a tween from
        // `none` to `blur(8px)` needs a zero at the other end to travel from, and leaving the
        // components absent would refuse it for want of a start value it has.
        if (text.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var (_, component, unit, identity) in Known)
                found[component] = new Amount(identity, unit);

            return found;
        }

        foreach (var call in Calls(text))
        {
            if (call is null) return null;

            var (name, argument) = call.Value;
            var known = Known.FirstOrDefault(k =>
                k.Function.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (known.Function is null) return null;
            if (Number(argument, known.Unit) is not { } amount) return null;

            found[known.Component] = amount;
        }

        return found.Count > 0 ? found : null;
    }

    /// <summary>
    /// The <c>name(argument)</c> calls in a filter list, in order. A null entry is something that
    /// is not a single-argument call, which abandons the whole list.
    /// </summary>
    /// <remarks>Hand-split rather than regexed because a filter list is separated by whitespace
    /// and an argument may contain whitespace of its own - <c>drop-shadow(0 0 2px black)</c> is
    /// one call with four parts, and reading it as four calls would mistake it for a list this
    /// compiler understands.</remarks>
    private static IEnumerable<(string Name, string Argument)?> Calls(string text)
    {
        var at = 0;

        while (at < text.Length)
        {
            while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
            if (at >= text.Length) break;

            var open = text.IndexOf('(', at);
            if (open < 0) { yield return null; yield break; }

            var name = text[at..open].Trim();
            if (name.Length == 0 || name.Any(c => !char.IsLetter(c) && c != '-'))
            {
                yield return null;
                yield break;
            }

            // Nesting, so a `calc(...)` or an `rgba(...)` inside the argument does not end it
            // early. Either of those is refused by the number reader afterwards, but it has to be
            // read as ONE argument first or the rest of the list parses as nonsense.
            var depth = 1;
            var close = open;

            while (++close < text.Length && depth > 0)
            {
                if (text[close] == '(') depth++;
                else if (text[close] == ')') depth--;
            }

            if (depth > 0) { yield return null; yield break; }

            yield return (name, text[(open + 1)..(close - 1)]);
            at = close;
        }
    }

    /// <summary>One argument, with its unit. <paramref name="fallback"/> is the unit a bare number
    /// takes: px for a blur, nothing for the ratios. A percentage is converted, because
    /// <c>brightness(150%)</c> and <c>brightness(1.5)</c> are the same filter and only one of them
    /// interpolates against a bare number.</summary>
    private static Amount? Number(string text, string fallback)
    {
        text = text.Trim();
        if (text.Length == 0) return null;

        if (text.EndsWith('%'))
        {
            return double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture,
                out var percent)
                // A blur in percent is not a thing CSS allows; a ratio in percent is a hundredth.
                ? fallback == "px" ? null : new Amount(percent / 100, "")
                : null;
        }

        var suffix = text.EndsWith("px", StringComparison.OrdinalIgnoreCase) ? "px"
            : text.EndsWith("rem", StringComparison.OrdinalIgnoreCase) ? "rem"
            : "";

        var digits = suffix.Length > 0 ? text[..^suffix.Length] : text;

        return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture,
            out var parsed)
            ? new Amount(parsed, suffix.Length > 0 ? suffix : fallback)
            : null;
    }
}
