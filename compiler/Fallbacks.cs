using System.Text.RegularExpressions;
using AngleSharp.Html.Dom;

namespace CupriLex.Compiler;

/// <summary>What a rewrite of the font stacks removed, by family, with how many declarations
/// named it.</summary>
public sealed record Dropped(IReadOnlyDictionary<string, int> Families, int Declarations);

/// <summary>
/// Font stacks trimmed to the faces a package can actually answer.
///
/// <para><b>Why this is necessary and not merely tidy.</b> A renderer with a strict font policy
/// refuses a family it has no face for, and measured on CupriFace 0.28.1 it does so even when the
/// declaration names a perfectly good fallback after it: <c>font-family: "NoSuchFamily", "Noto
/// Sans", sans-serif</c> fails, with Noto Sans registered and a generic at the end. A bare generic
/// on its own resolves. So a stack is answered by its NAMES, not by its order, and one unknown
/// name anywhere in it stops the render.</para>
///
/// <para>That is why 96 of the corpus's 187 packages could not be built. Their stacks name
/// <c>Segoe UI</c>, <c>SF Mono</c>, <c>Menlo</c>, <c>-apple-system</c> - faces that belong to an
/// operating system and will never be in a package, and that the browser only ever used because it
/// happened to be running on the machine that had them.</para>
///
/// <para>Dropping them is what a browser does in effect: it walks the list and uses the first face
/// it has. Writing that down makes the choice explicit and the render reproducible, which is the
/// same argument the strict policy is making. Every name removed is reported.</para>
/// </summary>
public static partial class Fallbacks
{
    /// <summary>A <c>font-family</c> declaration, captured so the value can be rewritten in place.
    /// Deliberately blind to <c>var()</c>: a stack behind a custom property is not resolvable here
    /// and a half-rewritten one is worse than an untouched one.</summary>
    private static readonly Regex Declaration = new(
        @"(?<lead>font-family\s*:\s*)(?<value>[^;}""]*(?:""[^""]*""[^;}""]*)*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>What is written when a stack loses every name it had. The browser fell back to the
    /// platform's default here, and a generic is the only way to say that without naming a
    /// typeface nobody chose.</summary>
    private const string Default = "sans-serif";

    /// <summary>
    /// Rewrites every font stack in <paramref name="document"/> to name only
    /// <paramref name="carried"/> families and generics.
    /// </summary>
    public static Dropped Trim(IHtmlDocument document, IReadOnlySet<string> carried)
    {
        var dropped = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var touched = 0;

        string Rewrite(string css)
        {
            return Declaration.Replace(css, match =>
            {
                var value = match.Groups["value"].Value;

                // A stack that is computed rather than written cannot be reasoned about.
                if (value.Contains("var(", StringComparison.OrdinalIgnoreCase)) return match.Value;

                var kept = new List<string>();
                var lost = new List<string>();

                foreach (var part in Split(value))
                {
                    var family = Unquote(part);
                    if (family.Length == 0) continue;

                    if (IsGeneric(family) || carried.Contains(family)) kept.Add(part.Trim());
                    else lost.Add(family);
                }

                if (lost.Count == 0) return match.Value;

                foreach (var family in lost)
                    dropped[family] = dropped.TryGetValue(family, out var seen) ? seen + 1 : 1;

                touched++;
                if (kept.Count == 0) kept.Add(Default);

                return match.Groups["lead"].Value + string.Join(", ", kept);
            });
        }

        foreach (var style in document.QuerySelectorAll("style"))
            style.TextContent = Rewrite(style.TextContent);

        foreach (var element in document.All)
            if (element.GetAttribute("style") is { Length: > 0 } inline)
                element.SetAttribute("style", Rewrite(inline));

        return new Dropped(dropped, touched);
    }

    /// <summary>The families a document answers itself, from its own <c>@font-face</c> rules.
    /// Read off the text rather than through a CSS object model because this runs on the document
    /// the packager is about to write, after assets have been rewritten under it.</summary>
    public static HashSet<string> Carried(IHtmlDocument document)
    {
        var carried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var style in document.QuerySelectorAll("style"))
            foreach (Match face in FontFace().Matches(style.TextContent))
                if (Unquote(face.Groups["family"].Value) is { Length: > 0 } family)
                    carried.Add(family);

        return carried;
    }

    [GeneratedRegex(
        @"@font-face\s*\{[^}]*?font-family\s*:\s*(?<family>[^;}]+)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FontFace();

    /// <summary>Split a stack on commas that are not inside quotes.</summary>
    private static IEnumerable<string> Split(string value)
    {
        var start = 0;
        var quote = '\0';

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];

            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') { quote = c; continue; }

            if (c == ',')
            {
                yield return value[start..i];
                start = i + 1;
            }
        }

        if (start < value.Length) yield return value[start..];
    }

    /// <summary>Generic families, and the system keywords that behave like one. The keywords are
    /// the reason this list is not simply the CSS specification's: <c>-apple-system</c> and
    /// <c>BlinkMacSystemFont</c> appear in 50 and 47 corpus blocks, they name no file anywhere, and
    /// treating them as families to be found would send a fetcher after a platform.</summary>
    private static bool IsGeneric(string family) => family.ToLowerInvariant() switch
    {
        "serif" or "sans-serif" or "monospace" or "cursive" or "fantasy" or "system-ui"
            or "ui-serif" or "ui-sans-serif" or "ui-monospace" or "ui-rounded"
            or "math" or "emoji" or "fangsong"
            or "-apple-system" or "blinkmacsystemfont" or "-webkit-body"
            or "inherit" or "initial" or "unset" or "revert" => true,
        _ => false,
    };

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();

        if (trimmed.Length >= 2
            && ((trimmed[0] == '"' && trimmed[^1] == '"')
                || (trimmed[0] == '\'' && trimmed[^1] == '\'')))
            trimmed = trimmed[1..^1];

        return trimmed.Trim();
    }
}
