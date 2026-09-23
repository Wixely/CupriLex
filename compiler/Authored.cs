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
public sealed class Authored
{
    private readonly IDocument _document;
    private readonly IReadOnlyList<ICssStyleRule> _rules;
    private readonly Dictionary<string, IReadOnlyDictionary<string, Amount>?> _cache = new();

    private Authored(IDocument document, IReadOnlyList<ICssStyleRule> rules)
    {
        _document = document;
        _rules = rules;
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

            return new Authored(document, rules);
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
        string? opacity = null, transform = null;

        var applicable = _rules
            .Select((rule, order) => (rule, order))
            .Where(r => Matches(element, r.rule))
            .OrderBy(r => r.rule.Selector?.Specificity ?? default)
            .ThenBy(r => r.order);

        foreach (var (rule, _) in applicable)
        {
            if (rule.Style.GetPropertyValue("opacity") is { Length: > 0 } o) opacity = o;
            if (rule.Style.GetPropertyValue("transform") is { Length: > 0 } t) transform = t;
        }

        if (element.GetAttribute("style") is { Length: > 0 } inline)
        {
            if (Inline(inline, "opacity") is { } o) opacity = o;
            if (Inline(inline, "transform") is { } t) transform = t;
        }

        var values = new Dictionary<string, Amount>();

        if (opacity is not null && double.TryParse(opacity.Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var alpha))
            values["opacity"] = new Amount(alpha, "");

        if (transform is not null) Transform.Read(transform, values);

        return values;
    }

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
