using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

namespace CupriLex.Compiler;

/// <summary>A typeface a document uses. <paramref name="Weights"/> is every weight it asks that
/// family for, so a fetcher knows which files are worth carrying.</summary>
public sealed record Face(string Family, IReadOnlyList<int> Weights);

/// <summary>What a document asks for and what it brings.</summary>
/// <param name="Missing">Families used with no <c>@font-face</c> to answer them. Under a renderer
/// with a strict font policy these are not a downgrade, they are a refusal: 96 of the corpus's 187
/// packages cannot be built for exactly this.</param>
/// <param name="Provided">Families the document declares a face for. Nothing to do.</param>
public sealed record Typefaces(IReadOnlyList<Face> Missing, IReadOnlyList<string> Provided);

/// <summary>
/// The typefaces a document names but does not carry.
///
/// <para><b>Why this is worth a file.</b> A composition written for a browser gets its fonts from
/// somewhere else: a <c>&lt;link&gt;</c> to a font service, an <c>@import</c>, or nothing at all
/// because the machine happens to have the family installed. None of those survive translation -
/// the engine has no network and no system font list - so the family is named and nothing answers
/// it. The pixel comparison barely notices, because the harness registers its own faces; the
/// renderer refuses outright, because guessing a typeface is exactly what a strict policy exists
/// to prevent.</para>
///
/// <para>Generic families are not missing. <c>sans-serif</c>, <c>monospace</c> and the rest are
/// instructions to pick something, not names of anything, and a fetcher asked to find "monospace"
/// would be inventing an answer.</para>
/// </summary>
public static class Faces
{
    /// <summary>CSS generic families, plus the two system keywords that behave like them. Named
    /// rather than pattern-matched: this list is short, fixed by the specification, and a wrong
    /// entry either hides a real family or sends a fetcher looking for a word.</summary>
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "serif", "sans-serif", "monospace", "cursive", "fantasy", "system-ui",
        "ui-serif", "ui-sans-serif", "ui-monospace", "ui-rounded",
        "inherit", "initial", "unset", "revert", "none", "emoji", "math", "fangsong",
    };

    private static readonly Regex Weight = new(@"font-weight\s*:\s*(\d{3})", RegexOptions.Compiled);

    public static Typefaces Of(string html)
    {
        IDocument document;
        try
        {
            var context = BrowsingContext.New(Configuration.Default.WithCss());
            document = context.OpenAsync(request => request.Content(html)).Result;
        }
        catch
        {
            return new Typefaces([], []);
        }

        var rules = new List<ICssStyleRule>();
        var provided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sheet in document.StyleSheets.OfType<ICssStyleSheet>())
            Collect(sheet.Rules, rules, provided);

        // Weights are gathered per family from the rules that name it, which is coarse and right
        // enough: a fetcher only needs to know that a family is used at 400 and 700 so that it
        // does not carry nine files where two will do.
        var wanted = new Dictionary<string, SortedSet<int>>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in rules)
        {
            var families = Families(rule.Style.GetPropertyValue("font-family"));
            if (families.Count == 0) continue;

            var weight = Weights(rule.Style.GetPropertyValue("font-weight"), rule.CssText);

            foreach (var family in families)
            {
                if (!wanted.TryGetValue(family, out var set)) wanted[family] = set = [];
                foreach (var w in weight) set.Add(w);
            }
        }

        foreach (var element in document.All)
        {
            if (element.GetAttribute("style") is not { Length: > 0 } inline) continue;

            foreach (var family in Families(Declaration(inline, "font-family")))
            {
                if (!wanted.TryGetValue(family, out var set)) wanted[family] = set = [];
                foreach (var w in Weights(Declaration(inline, "font-weight"), inline)) set.Add(w);
            }
        }

        var missing = wanted
            .Where(pair => !provided.Contains(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new Face(pair.Key,
                pair.Value.Count > 0 ? [.. pair.Value] : [400]))
            .ToArray();

        return new Typefaces(missing, [.. provided.OrderBy(f => f, StringComparer.Ordinal)]);
    }

    private static void Collect(
        ICssRuleList rules, List<ICssStyleRule> into, HashSet<string> provided)
    {
        foreach (var rule in rules)
        {
            switch (rule)
            {
                case ICssStyleRule style:
                    into.Add(style);
                    break;

                // A face the document already brings. Its family is answered whatever the src
                // turns out to be: if the file is missing that is a broken reference, which the
                // packager already reports, and not a family to go looking for.
                case ICssFontFaceRule face when Name(face) is { Length: > 0 } family:
                    provided.Add(family);
                    break;
            }

            if (rule is ICssGroupingRule group) Collect(group.Rules, into, provided);
        }
    }

    private static string? Name(ICssFontFaceRule face)
    {
        try { return Unquote(face.Family); }
        catch { return null; }
    }

    /// <summary>The real families in a <c>font-family</c> list, in order, minus the generics.</summary>
    private static List<string> Families(string? value)
    {
        var found = new List<string>();
        if (value is not { Length: > 0 }) return found;

        foreach (var part in value.Split(','))
        {
            var family = Unquote(part);

            if (family.Length == 0 || Generic.Contains(family)) continue;
            if (family.StartsWith("var(", StringComparison.OrdinalIgnoreCase)) continue;
            if (!found.Contains(family, StringComparer.OrdinalIgnoreCase)) found.Add(family);
        }

        return found;
    }

    /// <summary>The weights a rule asks for. The declaration first, then any number in the rule's
    /// own text, because a shorthand or a longhand the reader does not expose still says 700.</summary>
    private static List<int> Weights(string? declared, string? text)
    {
        var found = new List<int>();

        if (declared is { Length: > 0 } && int.TryParse(declared.Trim(), out var one)
            && one is >= 100 and <= 900)
            found.Add(one);

        if (found.Count == 0 && text is { Length: > 0 })
            foreach (Match m in Weight.Matches(text))
                if (int.TryParse(m.Groups[1].Value, out var w) && w is >= 100 and <= 900)
                    found.Add(w);

        if (found.Count == 0 && declared is { Length: > 0 })
        {
            if (declared.Contains("bold", StringComparison.OrdinalIgnoreCase)) found.Add(700);
            else if (declared.Contains("normal", StringComparison.OrdinalIgnoreCase)) found.Add(400);
        }

        return found;
    }

    private static string? Declaration(string style, string property)
    {
        foreach (var part in style.Split(';'))
        {
            var colon = part.IndexOf(':');
            if (colon <= 0) continue;

            if (part[..colon].Trim().Equals(property, StringComparison.OrdinalIgnoreCase))
                return part[(colon + 1)..].Trim();
        }

        return null;
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();

        if (trimmed.Length >= 2
            && ((trimmed[0] == '"' && trimmed[^1] == '"') || (trimmed[0] == '\'' && trimmed[^1] == '\'')))
            trimmed = trimmed[1..^1];

        return trimmed.Trim();
    }
}
