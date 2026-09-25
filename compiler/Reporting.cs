using System.Globalization;
using System.Text;

namespace CupriLex.Compiler;

/// <summary>
/// The report, which is a deliverable rather than a log.
///
/// <para>It travels inside the package because that is the only place it is still attached to the
/// thing it describes. A refusal list printed to a terminal is gone by the time anyone asks why a
/// rendered composition is missing its transitions, and the package is what gets copied, handed on
/// and committed.</para>
///
/// <para><b>An empty report means perfect or lying, and this one says which.</b> It opens with
/// what was carried, not with what was refused: a report that lists only problems cannot be read
/// as evidence that anything worked.</para>
/// </summary>
internal static class Reporting
{
    public static string Of(Composition composition, Collected assets, double seconds,
        IReadOnlyList<string> freed, Dropped dropped)
    {
        var report = new StringBuilder();
        var culture = CultureInfo.InvariantCulture;

        report.AppendLine($"# {composition.Name}");
        report.AppendLine();
        report.AppendLine("Translated from a browser composition by CupriLex. This file describes "
                          + "what came across and what did not.");
        report.AppendLine();

        report.AppendLine("## What was carried");
        report.AppendLine();
        report.AppendLine("| | |");
        report.AppendLine("|---|---|");
        report.AppendLine($"| elements animated | {composition.Motion.Elements} |");
        report.AppendLine($"| held at one value, not animated | {composition.Motion.Held} |");
        report.AppendLine($"| assets carried | {assets.Entries.Count} |");
        report.AppendLine($"| size | {composition.Width}x{composition.Height} |");
        report.AppendLine($"| duration | {seconds.ToString("0.###", culture)}s |");
        report.AppendLine();

        if (Math.Abs(seconds - composition.Duration) > 0.001)
        {
            report.AppendLine($"**The block declares "
                              + $"{composition.Duration.ToString("0.###", culture)}s and its motion "
                              + $"spans {composition.Motion.Seconds.ToString("0.###", culture)}s.** "
                              + "This package runs the longer of the two, so nothing is cut off. "
                              + "Only the author knows which number was the mistake.");
            report.AppendLine();
        }

        if (composition.Motion.Held > 0)
        {
            report.AppendLine($"**{composition.Motion.Held} animation(s) hold one value for their "
                              + "whole length.** Their end state is carried and their motion is "
                              + "not, which renders as an element that is in the right place and "
                              + "never moves.");
            report.AppendLine();
        }

        if (assets.Entries.Count > 0)
        {
            report.AppendLine("## Assets in this package");
            report.AppendLine();
            foreach (var asset in assets.Entries.OrderBy(a => a.Key, StringComparer.Ordinal))
                report.AppendLine($"- `{asset.Key}` — {asset.Bytes.Length:n0} bytes");
            report.AppendLine();
        }

        if (assets.Missing.Count > 0)
        {
            report.AppendLine("## Referred to and not found");
            report.AppendLine();
            report.AppendLine("These are named by the document and were not beside it. Their "
                              + "references are left exactly as they were written, because a broken "
                              + "link that is named can be fixed and one that has been quietly "
                              + "removed cannot.");
            report.AppendLine();
            foreach (var url in assets.Missing) report.AppendLine($"- `{url}`");
            report.AppendLine();
        }

        if (freed.Count > 0)
        {
            report.AppendLine("## Timing attributes removed");
            report.AppendLine();
            report.AppendLine("A loose HTML block has nowhere but an attribute to declare its "
                              + "own length. This package has `render.duration`, so the attributes "
                              + "are redundant here - and CupriCut reads them as \"this element is "
                              + "a timeline window\", which costs the element the one animation "
                              + "slot the engine gives it.");
            report.AppendLine();
            foreach (var one in freed) report.AppendLine($"- `{one}`");
            report.AppendLine();
        }

        if (dropped.Unresolved.Count > 0)
        {
            report.AppendLine("## Font stacks this package cannot answer");
            report.AppendLine();
            report.AppendLine("Each of these names no face that travels in this package, so a "
                              + "renderer with a strict font policy will refuse it. **Only a "
                              + "substitution fixes them, and CupriLex will not pick a typeface "
                              + "nobody asked for.** `decisions.json` in this package says the "
                              + "same thing as data, for a tool that can offer a choice.");
            report.AppendLine();
            report.AppendLine("| what the author wrote | wants a | in |");
            report.AppendLine("|---|---|---|");

            foreach (var one in dropped.Unresolved.OrderByDescending(u => u.Declarations))
                report.AppendLine($"| `{one.Stack}` | {one.Class.ToString().ToLowerInvariant()} "
                                  + $"| {one.Declarations} declaration(s) |");

            report.AppendLine();
        }

        if (dropped.Families.Count > 0)
        {
            report.AppendLine("## Font families removed from the stacks");
            report.AppendLine();
            report.AppendLine("A strict font policy refuses a stack that names a face it has not "
                              + "got, **even when a good fallback follows it** - measured, "
                              + "`\"NoSuchFamily\", \"Noto Sans\", sans-serif` fails with Noto "
                              + "Sans registered. These names are answered by nothing in this "
                              + "package, so they were removed from "
                              + dropped.Declarations + " stack(s). A browser used whichever of "
                              + "them the machine happened to have; writing that choice down is "
                              + "what makes the render reproducible.");
            report.AppendLine();

            foreach (var (family, count) in dropped.Families.OrderByDescending(f => f.Value))
                report.AppendLine($"- `{family}` — named in {count} declaration(s)");

            report.AppendLine();
        }

        report.AppendLine("## What could not be carried");
        report.AppendLine();

        if (composition.Refusals.Count == 0)
        {
            report.AppendLine("Nothing. Every timeline in the source was read and every property "
                              + "it animated is one the engine animates.");
            return report.ToString();
        }

        report.AppendLine($"{composition.Refusals.Count} in total. Each one is something the "
                          + "source does that a JavaScript-free renderer cannot, named rather than "
                          + "approximated.");
        report.AppendLine();

        // Grouped, because thirty refusals for one reason and thirty for thirty reasons are very
        // different pieces of news, and a flat list hides which one this is.
        var groups = composition.Refusals
            .GroupBy(r => Shape(r.What))
            .OrderByDescending(g => g.Count());

        foreach (var group in groups)
        {
            report.AppendLine($"### {group.Count()} × {group.Key}");
            report.AppendLine();

            foreach (var refusal in group.Take(12))
                report.AppendLine(refusal.Line > 0
                    ? $"- line {refusal.Line}: {refusal.What}"
                    : $"- {refusal.What}");

            if (group.Count() > 12)
                report.AppendLine($"- …and {group.Count() - 12} more of the same shape");

            report.AppendLine();
        }

        return report.ToString();
    }

    /// <summary>A refusal with its block-specific parts removed, so two of the same kind group.
    /// The quoted selector and the numbers differ per refusal; the sentence around them does
    /// not.</summary>
    private static string Shape(string what)
    {
        var shape = System.Text.RegularExpressions.Regex.Replace(what, "'[^']*'", "'…'");
        shape = System.Text.RegularExpressions.Regex.Replace(shape, "`[^`]*`", "`…`");
        shape = System.Text.RegularExpressions.Regex.Replace(shape, @"\d+(\.\d+)?", "N");

        var stop = shape.IndexOf(" - ", StringComparison.Ordinal);
        if (stop > 20) shape = shape[..stop];

        var comma = shape.IndexOf(", which", StringComparison.Ordinal);
        if (comma > 20) shape = shape[..comma];

        return shape.Length > 90 ? shape[..90] + "…" : shape;
    }
}
