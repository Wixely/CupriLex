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

        // After the templates are inlined and before anything is emitted. Both halves of that
        // matter: thirteen blocks keep their whole composition, stylesheet included, inside a
        // <template>, and an element that is not in the document yet has no authored value to
        // read.
        var sheet = Emit.Sheet(tweens, readRefusals, Authored.Of(prepared));

        var output = Parser.ParseDocument(prepared);
        var refusals = sheet.Refusals.ToList();

        Descript(output, refusals);

        // Before the URLs are touched, so a src is rewritten once and on the element that will
        // carry it. The engine draws no raw <img> at all - it lays one out and leaves it empty -
        // and 20 of the corpus's packages were shipping exactly that.
        refusals.AddRange(Images.Rewrite(output));
        if (directory is { Length: > 0 }) Rebase(output, directory);
        Style(output, sheet.Css);

        if (inlined > 0)
            refusals.Add(new Refusal(
                $"{inlined} <template> element(s) were inlined: their content is inert in any "
                + "browser until a host clones it in, and the engine has no host"));

        refusals.AddRange(Unmatched(output, sheet));

        return new Translated(output.ToHtml(), sheet, refusals);
    }

    /// <summary>
    /// Animations whose selector matches nothing in the document they are about to be written
    /// into.
    ///
    /// <para>Resolution never consults the document: a target becomes the selector TEXT the author
    /// wrote, which is what makes the compiler static and what makes it impossible for it to be
    /// wrong about which elements it meant. The cost of that is it cannot know whether the
    /// selector finds anything, and a great many of these blocks build their elements in the
    /// JavaScript that has just been thrown away. The rule is then emitted, valid and correct and
    /// matching nothing at all.</para>
    ///
    /// <para>Checking it here is verification rather than resolution, and it is the difference
    /// between a translation that quietly does nothing and one that says which animations landed
    /// on no element. A selector the engine cannot parse counts as unmatched too: it would also
    /// have applied to nothing.</para>
    /// </summary>
    private static IEnumerable<Refusal> Unmatched(IHtmlDocument document, Sheet sheet)
    {
        foreach (var selector in sheet.Selectors)
        {
            // Negative for a selector the document cannot even be queried with: that matches
            // nothing either, and the report should say which of the two happened.
            int matches;
            try { matches = document.QuerySelectorAll(selector).Length; }
            catch (Exception ex) when (ex is DomException or ArgumentException) { matches = -1; }

            if (matches < 0)
                yield return new Refusal(
                    $"an animation on '{selector}', which is not a selector this document can be "
                    + "queried with, so nothing will match it");
            else if (matches == 0)
                yield return new Refusal(
                    $"an animation on '{selector}', which matches no element in the translated "
                    + "document - the element it names is built by the JavaScript that has been "
                    + "removed, so the motion is compiled and lands on nothing");
        }
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
