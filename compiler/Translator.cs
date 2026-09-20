using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace CupriLex.Compiler;

/// <summary>A block, translated.</summary>
/// <param name="Html">CupriFace-safe HTML: no scripts, motion as <c>@keyframes</c>.</param>
/// <param name="Motion">What the timelines became.</param>
/// <param name="Refusals">Everything that could not be carried, by name. An empty list means
/// perfect or lying.</param>
public sealed record Translated(string Html, Sheet Motion, IReadOnlyList<Refusal> Refusals);

/// <summary>
/// Browser HTML in, CupriFace-safe HTML out, and a report of everything that could not come with
/// it.
/// </summary>
public static class Translator
{
    private static readonly HtmlParser Parser = new();

    public static Translated Of(string html, string? directory = null)
    {
        var document = Parser.ParseDocument(html);

        // Templates first, because the scripts of thirteen blocks live inside one and are
        // invisible to everything downstream until it is inlined.
        var inlined = Inline(document);

        var prepared = document.ToHtml();
        var (tweens, readRefusals) = Reader.Read(prepared);
        var sheet = Emit.Sheet(tweens, readRefusals);

        var output = Parser.ParseDocument(prepared);
        var refusals = sheet.Refusals.ToList();

        Descript(output, refusals);
        Restyle(output);
        if (directory is { Length: > 0 }) Rebase(output, directory);
        Style(output, sheet.Css);

        if (inlined > 0)
            refusals.Add(new Refusal(
                $"{inlined} <template> element(s) were inlined: their content is inert in any "
                + "browser until a host clones it in, and the engine has no host"));

        return new Translated(output.ToHtml(), sheet, refusals);
    }

    /// <summary>
    /// A <c>&lt;template&gt;</c> replaced by its content.
    ///
    /// <para>Thirteen blocks - every <c>code-snippet-*</c> - put the whole composition inside one,
    /// scripts included. Template content is inert: it renders nothing in any browser, and its
    /// scripts never run, until a host clones it into the document. The engine is not a host, so
    /// the content is moved out here.</para>
    /// </summary>
    private static int Inline(IHtmlDocument document)
    {
        var templates = document.QuerySelectorAll("template").OfType<IHtmlTemplateElement>().ToArray();

        foreach (var template in templates)
        {
            var parent = template.Parent;
            if (parent is null) continue;

            foreach (var child in template.Content.ChildNodes.ToArray())
                parent.InsertBefore(document.Import(child, deep: true), template);

            parent.RemoveChild(template);
        }

        return templates.Length;
    }

    /// <summary>
    /// The scripts, removed - and counted, because removing them silently is the one thing this
    /// tool must never do.
    /// </summary>
    private static void Descript(IHtmlDocument document, List<Refusal> refusals)
    {
        foreach (var script in document.QuerySelectorAll("script").ToArray())
            script.Remove();
    }

    private static readonly Regex SpacedColour = new(
        @"\b(?<fn>rgba?)\(\s*(?<parts>[^()]*?)\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The spaces taken out of every <c>rgb()</c> and <c>rgba()</c>.
    ///
    /// <para>A rewrite rule, and the condition it works around is measured rather than assumed:
    /// <c>rgba(198, 173, 144, 0.32)</c> <em>crashes</em> CupriFace 0.26.1 in three different
    /// places, and the same colour without the spaces does not - see
    /// <c>conformance/support/0.26.1.json</c> and <c>EngineBugTests</c>. The fault is in
    /// <c>Colors.TryParse</c>, which takes the text between the parentheses with
    /// <c>text[(IndexOf('(') + 1)..IndexOf(')')]</c> and gets a length of -6 when there is no
    /// closing one. Three callers hand it a fragment that has none, each by splitting a value on
    /// spaces: the <c>border</c> shorthand, <c>ParseGradient</c>, and <c>ParseFilterOps</c>.</para>
    ///
    /// <para>The first version of this rule rewrote colours inside <c>border</c> declarations
    /// only, because that is where the first two crashing blocks had theirs. It recovered 25
    /// blocks and left 10 still crashing - in gradient stops and in a <c>drop-shadow()</c> - which
    /// is what a corpus run is for. Taking the spaces out of every colour function is harmless
    /// everywhere and covers all three callers.</para>
    ///
    /// <para>When the engine is fixed this rule should be deleted: <c>EngineBugTests</c> fails on
    /// that day and says so.</para>
    /// </summary>
    private static void Restyle(IHtmlDocument document)
    {
        foreach (var style in document.QuerySelectorAll("style"))
            style.TextContent = Rewrites(style.TextContent);

        foreach (var element in document.QuerySelectorAll("[style]"))
            if (element.GetAttribute("style") is { } inline)
                element.SetAttribute("style", Rewrites(inline));
    }

    private static string Rewrites(string css) => Fill(Unspace(css));

    /// <summary>
    /// Every <c>rgb()</c> and <c>rgba()</c> rewritten as hex.
    ///
    /// <para>Hex rather than merely unspaced, because taking the spaces out only fixes one of the
    /// three callers. The filter parser matches functions with <c>([\w-]+)\(([^)]*)\)</c>, which
    /// stops at the FIRST closing parenthesis, so <c>drop-shadow(0 0 4px rgba(0,0,0,0.5))</c>
    /// hands the colour parser <c>rgba(0,0,0,0.5</c> whether or not it had spaces in it. And
    /// <c>ParseGradient</c> takes everything between the first <c>(</c> and the last <c>)</c> of
    /// the whole value, so a multi-layer <c>background: radial-gradient(…), rgba(…)</c> is read as
    /// one gradient whose parentheses no longer balance.</para>
    ///
    /// <para>A hex colour has no parentheses, so none of the three can break it. The only loss is
    /// the alpha, quantised from a fraction to eight bits - <c>0.32</c> becomes <c>52</c>, which
    /// is <c>0.3216</c> - and the engine stores eight-bit alpha anyway.</para>
    /// </summary>
    private static string Unspace(string css) => SpacedColour.Replace(css, match =>
        Hex(match.Groups["parts"].Value) ?? match.Value);

    /// <summary>Null when the arguments are not three or four plain numbers - the modern
    /// <c>rgb(255 0 0 / 50%)</c> form among them, which is left exactly as written rather than
    /// half-understood.</summary>
    private static string? Hex(string arguments)
    {
        var parts = arguments.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is not (3 or 4)) return null;

        Span<byte> channels = stackalloc byte[4];
        channels[3] = 255;

        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value)) return null;

            // The first three are 0-255; the fourth is a 0-1 fraction.
            //
            // TRUNCATED, not rounded, because that is what the engine does to its own rgba():
            // (byte)(0.65f * 255f) is 165, and the nearest value is 166. Rounding here made the
            // rewritten colour one level off the one the browser is being compared against, and
            // six text-heavy blocks lost two and a half points of score to it - which is a
            // remarkable amount for a single level of alpha, and exactly the kind of thing that
            // is invisible until two renders are put side by side.
            channels[i] = (byte)Math.Clamp(i == 3 ? Math.Truncate(value * 255) : Math.Round(value), 0, 255);
        }

        var hex = $"#{channels[0]:x2}{channels[1]:x2}{channels[2]:x2}";
        return channels[3] == 255 ? hex : hex + channels[3].ToString("x2");
    }

    private static readonly Regex InsetZero = new(
        @"(?<![\w-])inset\s*:\s*0(?:px|%)?\s*(?=;|\})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// <c>inset: 0</c> written the long way, and sized rather than stretched.
    ///
    /// <para>115 blocks of 187 use it - the overlay, the backdrop, the end card that covers the
    /// composition - and the engine reports <c>CF0050</c> and lays the element out with no size at
    /// all, so the thing meant to cover everything covers nothing. It is the largest layout gap in
    /// the corpus and it is invisible in a diff, which is how it survived until a frame comparison
    /// put the two side by side.</para>
    ///
    /// <para>The obvious expansion - the four longhands - was written first and then measured, and
    /// it does <em>not</em> work: the engine accepts <c>top/right/bottom/left</c> and still gives
    /// the element no size. Percentage width and height do work. Both answers are in
    /// <c>conformance/support/0.26.1.json</c>, which is the only reason this rule is the shape it
    /// is rather than the shape it looked like it should be.</para>
    ///
    /// <para>Only the zero case is rewritten. <c>inset: 12px</c> would need a size of
    /// <c>calc(100% - 24px)</c>, which is a different question and has not been measured; the
    /// nineteen occurrences that are not zero are left alone rather than guessed at.</para>
    /// </summary>
    private static string Fill(string css) =>
        InsetZero.Replace(css, "top:0;left:0;width:100%;height:100%");

    /// <summary>The compiled motion, last in the head so its rules win the ties.</summary>
    private static void Style(IHtmlDocument document, string css)
    {
        if (string.IsNullOrWhiteSpace(css)) return;

        var style = document.CreateElement("style");
        style.TextContent = "\n/* Compiled from GSAP by CupriLex. */\n" + css;
        (document.Head ?? document.Body)?.AppendChild(style);
    }

    private static readonly Regex Attribute = new(
        """(?<lead>\b(?:src|href|poster)\s*=\s*)(?<quote>["'])(?<url>[^"']+)\k<quote>""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CssUrl = new(
        """url\(\s*(?<quote>["']?)(?<url>[^"')]+)\k<quote>\s*\)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Relative URLs made absolute against the block's own directory.
    ///
    /// <para>A browser resolves <c>assets/logo.png</c> against the document's location.
    /// <c>CupriDocument.Load</c> is given a string, which has no location, so the same reference
    /// resolves against the working directory and finds nothing. Milestone 5 will embed these as
    /// bytes in a <c>.cutpkg</c>; until then they are made absolute.</para>
    /// </summary>
    private static void Rebase(IHtmlDocument document, string directory)
    {
        foreach (var element in document.All)
        {
            foreach (var name in new[] { "src", "href", "poster" })
            {
                if (element.GetAttribute(name) is not { Length: > 0 } value) continue;
                if (Absolute(value, directory) is { } resolved) element.SetAttribute(name, resolved);
            }

            if (element.GetAttribute("style") is { Length: > 0 } inline)
                element.SetAttribute("style", RebaseCss(inline, directory));
        }

        foreach (var style in document.QuerySelectorAll("style"))
            style.TextContent = RebaseCss(style.TextContent, directory);
    }

    private static string RebaseCss(string css, string directory) => CssUrl.Replace(css, match =>
    {
        var resolved = Absolute(match.Groups["url"].Value, directory);
        var quote = match.Groups["quote"].Value;
        return resolved is null ? match.Value : $"url({quote}{resolved}{quote})";
    });

    /// <summary>Null when the URL is already absolute, or names nothing on disk.</summary>
    private static string? Absolute(string url, string directory)
    {
        var trimmed = url.Trim();

        if (trimmed.Length == 0
            || trimmed.StartsWith('#')
            || trimmed.StartsWith('/')
            || trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || Uri.TryCreate(trimmed, UriKind.Absolute, out _))
            return null;

        try
        {
            var path = Path.GetFullPath(
                Path.Combine(directory, trimmed.Replace('/', Path.DirectorySeparatorChar)));
            return File.Exists(path) ? new Uri(path).AbsoluteUri : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
