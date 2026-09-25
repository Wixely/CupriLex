using System.Text.RegularExpressions;
using AngleSharp.Html.Dom;

namespace CupriLex.Compiler;

/// <summary>The class of typeface a stack was asking for, as far as its own generic says.</summary>
public enum Typeface
{
    /// <summary>The stack named no generic, so nothing here knows what class was wanted.</summary>
    Unknown,
    Sans,
    Serif,
    Monospace,
    Cursive,
    Fantasy,
}

/// <summary>
/// A font stack this package cannot answer, described so that a SYSTEM can decide what to do
/// about it.
///
/// <para>Every field is here to be acted on rather than read. A host that wants to substitute
/// needs to know what was asked for (<paramref name="Wanted"/>), what kind of face would do
/// (<paramref name="Class"/>), and how much of the composition is affected
/// (<paramref name="Declarations"/>). A host that wants to ask a person needs the stack exactly as
/// the author wrote it.</para>
///
/// <para>Nothing here is a recommendation. CupriLex will not pick a typeface nobody asked for -
/// the choice belongs to whoever is publishing the composition, and the whole point of flagging it
/// is that they get to make it.</para>
/// </summary>
/// <param name="Stack">The declaration's value, as authored.</param>
/// <param name="Wanted">The real families it named, in the author's order of preference. Empty
/// only when the stack was a bare generic.</param>
/// <param name="Class">What kind of face would satisfy it.</param>
/// <param name="Declarations">How many declarations in this document write this stack.</param>
public sealed record Unresolved(
    string Stack, IReadOnlyList<string> Wanted, Typeface Class, int Declarations);

/// <summary>What a rewrite of the font stacks removed, by family, with how many declarations
/// named it.</summary>
/// <param name="Unresolved">Stacks left naming no face at all. These are the ones a renderer with
/// a strict font policy will refuse, and the ones a host has to decide about.</param>
public sealed record Dropped(
    IReadOnlyDictionary<string, int> Families,
    int Declarations,
    IReadOnlyList<Unresolved> Unresolved);

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
        var unresolved = new Dictionary<string, Unresolved>(StringComparer.Ordinal);
        var touched = 0;

        // What the document itself says a generic means. See Pairings: a stack of
        // `"Space Mono", monospace` is the author telling us which face they meant by monospace,
        // and a bare `monospace` elsewhere in the same document means the same thing.
        var means = Pairings(document, carried);

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

                // A stack that is nothing but a generic names no face at all. Where the
                // document has already said which face it means by that generic, say it here too:
                // a renderer that cannot answer `monospace` can answer `"Space Mono", monospace`,
                // and both are the author's own words.
                var named = kept.Count > 0
                            && kept.All(k => IsGeneric(Unquote(k)))
                            && kept.Select(k => Unquote(k))
                                   .FirstOrDefault(g => means.ContainsKey(g)) is { } generic
                    ? means[generic]
                    : null;

                // Nothing real is left and the document gave no clue what it meant. This is the
                // stack a strict renderer refuses, and the one a host has to decide about.
                if (named is null && kept.All(k => IsGeneric(Unquote(k))))
                {
                    // Collapsed, not reformatted: a stack written across five indented lines in
                    // the source is the same stack, and a host putting it in front of a person
                    // wants the families and their order rather than the CSS layout.
                    var authored = Whitespace().Replace(value, " ").Trim();

                    unresolved[authored] = unresolved.TryGetValue(authored, out var already)
                        ? already with { Declarations = already.Declarations + 1 }
                        : new Unresolved(authored, [.. lost],
                            Class(kept.Select(Unquote).Concat(lost)), 1);
                }

                if (lost.Count == 0 && named is null) return match.Value;

                foreach (var family in lost)
                    dropped[family] = dropped.TryGetValue(family, out var seen) ? seen + 1 : 1;

                touched++;
                if (kept.Count == 0) kept.Add(Default);
                if (named is not null) kept.Insert(0, $"\"{named}\"");

                return match.Groups["lead"].Value + string.Join(", ", kept);
            });
        }

        foreach (var style in document.QuerySelectorAll("style"))
            style.TextContent = Rewrite(style.TextContent);

        foreach (var element in document.All)
            if (element.GetAttribute("style") is { Length: > 0 } inline)
                element.SetAttribute("style", Rewrite(inline));

        return new Dropped(dropped, touched, [.. unresolved.Values]);
    }

    /// <summary>
    /// What kind of face a stack wanted.
    ///
    /// <para>The generic first, because that is the author saying it outright. Failing that, the
    /// names: a stack of <c>Menlo, Monaco, Consolas</c> with no generic at the end is still
    /// unmistakably asking for a monospace, and a host deciding what to substitute should not have
    /// to infer that for itself.</para>
    /// </summary>
    private static Typeface Class(IEnumerable<string> families)
    {
        var all = families.Select(f => f.ToLowerInvariant()).ToArray();

        foreach (var family in all)
        {
            var known = family switch
            {
                "monospace" or "ui-monospace" => Typeface.Monospace,
                "serif" or "ui-serif" => Typeface.Serif,
                "sans-serif" or "ui-sans-serif" or "ui-rounded" or "system-ui" => Typeface.Sans,
                "cursive" => Typeface.Cursive,
                "fantasy" => Typeface.Fantasy,
                _ => Typeface.Unknown,
            };

            if (known is not Typeface.Unknown) return known;
        }

        // No generic. The commonest system monospaces, named: these are exactly the stacks that
        // arrive with no generic and mean one thing.
        string[] monos = ["menlo", "monaco", "consolas", "courier new", "courier", "sf mono",
                          "andale mono", "lucida console", "dejavu sans mono", "liberation mono"];

        if (all.Any(f => monos.Contains(f) || f.Contains("mono"))) return Typeface.Monospace;
        if (all.Any(f => f is "georgia" or "times" or "times new roman" or "garamond"))
            return Typeface.Serif;

        return Typeface.Unknown;
    }

    /// <summary>
    /// What the document's own stacks say a generic means.
    ///
    /// <para>A stack of <c>"Space Mono", monospace</c> is the author naming the face they wanted
    /// and the class it belongs to, in one line. So a bare <c>monospace</c> somewhere else in the
    /// same document means that face too - and saying so is not a substitution, it is reading what
    /// is already written.</para>
    ///
    /// <para>Worth doing because a bare generic is the one thing a package cannot carry. A
    /// renderer with a strict font policy answers <c>sans-serif</c> only if some registered face
    /// happens to serve it, and answers <c>monospace</c> with nothing at all unless a monospace
    /// face was registered - measured, and 53 packages fail on exactly that. Only families the
    /// package CARRIES are used, so this never names a face that is not in the file.</para>
    /// </summary>
    private static Dictionary<string, string> Pairings(
        IHtmlDocument document, IReadOnlySet<string> carried)
    {
        var means = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Learn(string css)
        {
            foreach (Match match in Declaration.Matches(css))
            {
                var families = Split(match.Groups["value"].Value).Select(Unquote).ToArray();
                if (families.Length < 2) continue;

                var first = families[0];
                if (first.Length == 0 || IsGeneric(first) || !carried.Contains(first)) continue;

                foreach (var generic in families.Skip(1).Where(IsGeneric))
                    means.TryAdd(generic, first);
            }
        }

        foreach (var style in document.QuerySelectorAll("style")) Learn(style.TextContent);

        foreach (var element in document.All)
            if (element.GetAttribute("style") is { Length: > 0 } inline) Learn(inline);

        return means;
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

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

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
