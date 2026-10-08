using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;

namespace CupriLex.Compiler;

/// <summary>
/// What the stylesheet already says, for the elements a tween is about to move.
///
/// <para>GSAP reads an element's current value out of the computed style before it animates, so
/// <c>.to(el, { opacity: 1 })</c> on an element the stylesheet authors at <c>opacity: 0</c> is a
/// fade. A compiler that never runs the document assumed the value was 1, produced a keyframe
/// whose every stop said 1, and emitted a still. That was 112 animations across 45 blocks - the
/// largest correctable gap in the compiler, by its own refusal counts.</para>
///
/// <para><b>Declared values, not computed ones.</b> The obvious call is
/// <c>ComputeStyle()</c>, and it is wrong twice over. It throws a
/// <c>NullReferenceException</c> on <c>translate(-50%, -50%)</c>, which is the commonest centring
/// idiom in this corpus. And a computed value is not what a tween starts from: the author wrote
/// <c>scale(0)</c>, the browser hands GSAP <c>scale(0)</c>, and a pixel matrix resolved against a
/// viewport this compiler does not have would be a different number wearing the same name.</para>
///
/// <para><b>It answers nothing rather than guessing.</b> Every path that cannot be resolved -
/// a selector matching no element, two elements the rules disagree about, a transform function
/// this does not parse - returns null, and the caller keeps the behaviour it had before. A wrong
/// start value is an animation that runs from the wrong place, which is worse than one that does
/// not run: the second is visible in the report and the first is not.</para>
/// </summary>
internal sealed partial class Authored
{
    private readonly IDocument _document;
    private readonly IReadOnlyList<ICssStyleRule> _rules;
    private readonly Dictionary<string, IReadOnlyDictionary<string, Amount>?> _cache = new();

    private readonly IReadOnlyList<(string Selector, string Body)> _raw;

    private Authored(IDocument document, IReadOnlyList<ICssStyleRule> rules,
        IReadOnlyList<(string, string)>? raw = null)
    {
        _document = document;
        _rules = rules;
        _raw = raw ?? [];
    }

    /// <summary>Nothing known about anything. The behaviour before this type existed, and what a
    /// document that cannot be parsed falls back to.</summary>
    public static readonly Authored None =
        new(BrowsingContext.New().OpenNewAsync().Result, []);

    public static Authored Of(string html)
    {
        try
        {
            var context = BrowsingContext.New(Configuration.Default.WithCss());
            var document = context.OpenAsync(request => request.Content(html)).Result;

            var rules = new List<ICssStyleRule>();
            foreach (var sheet in document.StyleSheets.OfType<ICssStyleSheet>())
                Collect(sheet.Rules, rules);

            // The SVG presentation properties are not in AngleSharp.Css's property registry, so
            // `stroke-dashoffset: 1000` is dropped at parse time and is absent from the object
            // model AND from the rule's own CssText. The only place it survives is the stylesheet
            // as written, so the rules are collected a second time as text. Images.Fit() reads
            // object-fit the same way and for the same reason.
            //
            // Source order only, no specificity: these are read for one purpose - the start value
            // of a draw-on - and a composition that states the same stroke twice at two
            // specificities is not something this corpus does. The object model still wins where
            // it has an answer.
            var raw = new List<(string, string)>();
            foreach (var style in document.QuerySelectorAll("style"))
                foreach (Match block in Block().Matches(style.TextContent))
                    raw.Add((block.Groups["sel"].Value.Trim(), block.Groups["body"].Value));

            return new Authored(document, rules, raw);
        }
        catch
        {
            // A document this cannot read is a document whose start values are unknown, which is
            // exactly what None means. It is never a reason to fail a translation.
            return None;
        }
    }

    /// <summary>
    /// Rules in source order, including the ones nested inside <c>@media</c> and friends.
    ///
    /// <para>Descending matters: a corpus block that states its start values inside a media query
    /// would otherwise look as though it stated none, and the flat animation would come back with
    /// no sign that anything had been missed.</para>
    /// </summary>
    private static void Collect(ICssRuleList rules, List<ICssStyleRule> into)
    {
        foreach (var rule in rules)
        {
            if (rule is ICssStyleRule style) into.Add(style);
            if (rule is ICssGroupingRule group) Collect(group.Rules, into);
        }
    }

    /// <summary>
    /// The authored value of one component for one selector, or null if it is not knowable.
    /// </summary>
    /// <param name="selector">The tween's target, as the author wrote it.</param>
    /// <param name="component">A name from <see cref="Properties.TransformOrder"/>, or
    /// <c>opacity</c>.</param>
    public Amount? Value(string selector, string component) =>
        Values(selector) is { } values && values.TryGetValue(component, out var amount)
            ? amount
            : null;

    /// <summary>Every component this can resolve for a selector, or null when the selector itself
    /// is not answerable. Cached: a block tweens the same handful of selectors repeatedly, and
    /// each lookup walks every rule in the document.</summary>
    public IReadOnlyDictionary<string, Amount>? Values(string selector)
    {
        if (_cache.TryGetValue(selector, out var cached)) return cached;
        return _cache[selector] = Resolve(selector);
    }

    private IReadOnlyDictionary<string, Amount>? Resolve(string selector)
    {
        IHtmlCollection<IElement> matched;
        try
        {
            matched = _document.QuerySelectorAll(selector);
        }
        catch
        {
            return null;    // a selector the parser will not take is not one to reason about
        }

        if (matched.Length == 0) return null;

        // Several elements, one animation. GSAP would start each from its own value and this
        // compiler has one keyframes rule to say it with, so agreement is required rather than
        // assumed - and where they disagree the honest answer is that there is no single start.
        IReadOnlyDictionary<string, Amount>? agreed = null;

        foreach (var element in matched)
        {
            var here = Declared(element);
            if (agreed is null) { agreed = here; continue; }
            if (!Same(agreed, here)) return null;
        }

        return agreed;
    }

    private static bool Same(IReadOnlyDictionary<string, Amount> a, IReadOnlyDictionary<string, Amount> b) =>
        a.Count == b.Count
        && a.All(pair => b.TryGetValue(pair.Key, out var other)
                         && other.Unit == pair.Value.Unit
                         && Math.Abs(other.Number - pair.Value.Number) < 1e-9);

    /// <summary>
    /// The cascade, for one element and the two properties that matter.
    ///
    /// <para>Ordered by specificity and then by source position, which is the cascade as far as
    /// this needs it: everything here is author-origin, and <c>!important</c> is not something a
    /// composition uses to state a start value. The element's own <c>style</c> attribute is
    /// applied last because it outranks every rule.</para>
    ///
    /// <para>A rule written as a list - <c>.a, #b</c> - carries one specificity for the whole
    /// list rather than one per part, so a rule can outrank another by a selector that is not the
    /// one that matched. It costs a start value in a document that states the same property twice
    /// at two specificities through the same list, which no corpus block does, and the wrong
    /// answer it would give is another value the author wrote for the same element.</para>
    /// </summary>
    private Dictionary<string, Amount> Declared(IElement element)
    {
        string? transform = null;

        // The properties read straight through as a number, with the unit they carry. The stroke
        // pair is here for the draw-on idiom: `flowchart` authors `stroke-dasharray: 1000;
        // stroke-dashoffset: 1000` and tweens the offset to 0, so without reading the start the
        // tween compiles from 0 to 0 and the path is drawn from the first frame. Like `opacity`,
        // these are plain numbers in the cascade and in a presentation attribute both.
        string?[] numbers = [null, null, null];
        string[] names = ["opacity", "stroke-dashoffset", "stroke-dasharray"];

        // A presentation attribute on an SVG element - <path opacity="0">, the way every icon in
        // this corpus hides its second state - is a declaration at the bottom of the cascade, and
        // that is where the engine puts it too since 0.34.0 (#262). Before that version the engine
        // read nothing but the attribute, and the compiler read nothing of it: x-post's filled
        // heart compiled to a flat animation holding opacity 1, inert on an engine that ignored
        // stylesheets inside an svg and a pink heart from frame zero on one that honours them.
        // The transform attribute is not read: its grammar is SVG's, not CSS's, and a wrong
        // reading of it is a wrong start position.
        for (var i = 0; i < names.Length; i++)
            if (element.GetAttribute(names[i]) is { Length: > 0 } presented) numbers[i] = presented;

        var applicable = _rules
            .Select((rule, order) => (rule, order))
            .Where(r => Matches(element, r.rule))
            .OrderBy(r => r.rule.Selector?.Specificity ?? default)
            .ThenBy(r => r.order);

        foreach (var (rule, _) in applicable)
        {
            // GetPropertyValue first, then the declaration text. AngleSharp.Css knows the CSS
            // property registry and the SVG presentation properties are not in it, so
            // `stroke-dashoffset` comes back empty from the object model and has to be read off
            // the rule as written - the same hand-parse the `style` attribute already needed.
            for (var i = 0; i < names.Length; i++)
                if (rule.Style.GetPropertyValue(names[i]) is { Length: > 0 } v) numbers[i] = v;

            if (rule.Style.GetPropertyValue("transform") is { Length: > 0 } t) transform = t;
        }

        // Then the stylesheet as written, for the properties the object model threw away.
        foreach (var (selector, body) in _raw)
        {
            if (!MatchesText(element, selector)) continue;

            for (var i = 0; i < names.Length; i++)
                if (numbers[i] is null && Inline(body, names[i]) is { } v) numbers[i] = v;
        }

        if (element.GetAttribute("style") is { Length: > 0 } inline)
        {
            for (var i = 0; i < names.Length; i++)
                if (Inline(inline, names[i]) is { } v) numbers[i] = v;

            if (Inline(inline, "transform") is { } t) transform = t;
        }

        var values = new Dictionary<string, Amount>();

        for (var i = 0; i < names.Length; i++)
            if (Number(numbers[i]) is { } amount) values[names[i]] = amount;

        if (transform is not null) Transform.Read(transform, values);

        return values;
    }

    /// <summary>A declared value that is a bare number, or one in px, and null for anything else.
    /// A dash array of several lengths, a percentage opacity, a <c>var()</c>: all unreadable here,
    /// and all answered with nothing rather than a guess.</summary>
    private static Amount? Number(string? declared)
    {
        if (declared is null) return null;

        var text = declared.Trim();
        var unit = "";

        if (text.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^2].Trim();
            unit = "px";
        }

        return double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? new Amount(value, unit)
            : null;
    }

    /// <summary>Whether an element matches a selector written as text. A selector the parser will
    /// not take matches nothing, here as everywhere else in this file.</summary>
    private static bool MatchesText(IElement element, string selector)
    {
        if (selector.Length == 0 || selector.StartsWith('@')) return false;

        try { return element.Matches(selector); }
        catch { return false; }
    }

    [GeneratedRegex(@"(?<sel>[^{}]+)\{(?<body>[^{}]*)\}")]
    private static partial Regex Block();

    private static bool Matches(IElement element, ICssStyleRule rule)
    {
        try
        {
            return rule.SelectorText is { Length: > 0 } text && element.Matches(text);
        }
        catch
        {
            return false;   // a selector AngleSharp will not match is one that applies to nothing
        }
    }

    /// <summary>One declaration out of a <c>style</c> attribute. Hand-read rather than parsed,
    /// because the attribute is a declaration list and every CSS parser here wants a rule.</summary>
    private static string? Inline(string style, string property)
    {
        foreach (var declaration in style.Split(';'))
        {
            var colon = declaration.IndexOf(':');
            if (colon <= 0) continue;

            if (declaration[..colon].Trim().Equals(property, StringComparison.OrdinalIgnoreCase))
                return declaration[(colon + 1)..].Trim();
        }

        return null;
    }
}
