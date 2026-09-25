using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

namespace CupriLex.Compiler;

/// <summary>
/// <c>&lt;img&gt;</c> rewritten to <c>&lt;cupri-image&gt;</c>, which is the only image the engine
/// draws.
///
/// <para><b>This was documented as done and was not.</b> Two files in this repository said an
/// <c>&lt;img&gt;</c> "becomes <c>&lt;cupri-image&gt;</c>" and no code did it. 20 of the corpus's
/// 187 packages ship a raw <c>&lt;img&gt;</c>, and the engine lays each one out and leaves it
/// empty - <c>CF0030</c>, reported as an error, with the space still taken and nothing in it.</para>
///
/// <para><b>Everything that is not about being an image survives.</b> The class, the id, the style
/// attribute and the data attributes all move across, because a stylesheet rule and a compiled
/// animation both find the element by them: rewriting the tag and losing the class would trade one
/// invisible image for a whole broken layout.</para>
/// </summary>
public static partial class Images
{
    /// <summary>
    /// Attributes that mean something only to a browser's image loader. Dropped rather than
    /// carried: a renderer that ignored them would be no worse off, and one that tried to honour
    /// <c>srcset</c> would be choosing a source this translation never resolved.
    /// </summary>
    private static readonly string[] BrowserOnly =
        ["srcset", "sizes", "crossorigin", "loading", "decoding", "referrerpolicy", "usemap",
         "ismap", "fetchpriority"];

    /// <summary>Rewrites every <c>&lt;img&gt;</c> in place and says what it did.</summary>
    public static IReadOnlyList<Refusal> Rewrite(IHtmlDocument document)
    {
        var refusals = new List<Refusal>();

        foreach (var image in document.QuerySelectorAll("img").ToArray())
        {
            var replacement = document.CreateElement("cupri-image");

            foreach (var attribute in image.Attributes.ToArray())
            {
                if (BrowserOnly.Contains(attribute.Name, StringComparer.OrdinalIgnoreCase)) continue;
                replacement.SetAttribute(attribute.Name, attribute.Value);
            }

            // object-fit is a CSS property the engine does not support, and the component takes
            // the same four values as an attribute. Reading it across is the difference between
            // an image that fills its box as the author asked and one that letterboxes.
            if (Fit(image) is { } fit) replacement.SetAttribute("fit", fit);

            if (image.GetAttribute("src") is not { Length: > 0 })
                refusals.Add(new Refusal(
                    "an <img> with no src, which becomes a <cupri-image> with nothing to draw"));

            if (image.GetAttribute("srcset") is { Length: > 0 })
                refusals.Add(new Refusal(
                    $"a srcset on '{Name(image)}': the engine draws one source, and the src "
                    + "attribute is the one carried"));

            image.Parent?.ReplaceChild(replacement, image);
        }

        return refusals;
    }

    /// <summary>
    /// The <c>fit</c> the component should use, from wherever the author wrote it.
    ///
    /// <para>The inline style first because it is the most specific place it can be, then the
    /// stylesheet by class or id. Only the four values the component takes are carried: a
    /// <c>scale-down</c> answered with a guess would be worse than the default.</para>
    /// </summary>
    private static string? Fit(IElement image)
    {
        var inline = image.GetAttribute("style");

        if (inline is { Length: > 0 } && ObjectFit().Match(inline) is { Success: true } direct)
            return Known(direct.Groups["fit"].Value);

        var document = image.Owner;
        if (document is null) return null;

        foreach (var style in document.QuerySelectorAll("style"))
        {
            foreach (Match rule in Rule().Matches(style.TextContent))
            {
                if (ObjectFit().Match(rule.Groups["body"].Value) is not { Success: true } found)
                    continue;

                var selector = rule.Groups["sel"].Value.Trim();

                try
                {
                    if (image.Matches(selector)) return Known(found.Groups["fit"].Value);
                }
                catch
                {
                    // A selector AngleSharp will not parse matches nothing, here as anywhere.
                }
            }
        }

        return null;
    }

    private static string? Known(string value) => value.Trim().ToLowerInvariant() switch
    {
        "contain" => "contain",
        "cover" => "cover",
        "fill" => "fill",
        "none" => "none",
        _ => null,
    };

    private static string Name(IElement image) =>
        image.Id is { Length: > 0 } id ? "#" + id
        : image.ClassName is { Length: > 0 } cls ? "." + cls.Split(' ')[0]
        : "<img>";

    [GeneratedRegex(@"object-fit\s*:\s*(?<fit>[a-z-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ObjectFit();

    [GeneratedRegex(@"(?<sel>[^{}]+)\{(?<body>[^}]*)\}")]
    private static partial Regex Rule();
}
