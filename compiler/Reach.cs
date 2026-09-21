using System.Text.RegularExpressions;

namespace CupriLex.Compiler;

/// <summary>
/// How much of the corpus the compiler carries, and what it refuses.
///
/// <para>Not a score. Frames decide the score - that is the harness's job - and a block whose
/// every tween was read can still render wrongly. This answers the cheaper question underneath
/// it: how much motion reaches the stylesheet at all, and what is standing in the way of the
/// rest.</para>
/// </summary>
public static class Reach
{
    public static int Report(string[] args)
    {
        var limit = int.Parse(Option(args, "--limit") ?? "0");
        var files = CorpusPath.Files().ToArray();
        if (limit > 0) files = [.. files.Take(limit)];

        var rows = new List<(string Block, int Animated, double Seconds, int Refusals, int Calls, int Unmatched, int Held)>();
        var reasons = new Dictionary<string, int>();
        var examples = new Dictionary<string, List<string>>();

        foreach (var file in files)
        {
            var name = Path.GetFileName(Path.GetDirectoryName(file)) + "/"
                       + Path.GetFileNameWithoutExtension(file);
            var html = File.ReadAllText(file);

            Translated translated;
            try
            {
                translated = Translator.Of(html, Path.GetDirectoryName(file));
            }
            catch (Exception ex)
            {
                rows.Add((name, 0, 0, 0, 0, 0, 0));
                Count(reasons, "the compiler threw: " + ex.GetType().Name);
                continue;
            }

            // Counted separately because it is the one refusal that says the compiler did its job
            // and the result still cannot show: a correct animation on a selector that finds
            // nothing, because the element was built by the script that has been removed.
            var landsOnNothing = translated.Refusals.Count(
                r => r.What.Contains("matches no element", StringComparison.Ordinal));

            rows.Add((name, translated.Motion.Elements, translated.Motion.Seconds,
                translated.Refusals.Count, Calls(html), landsOnNothing, translated.Motion.Held));

            foreach (var refusal in translated.Refusals)
            {
                var kind = Kind(refusal.What);
                Count(reasons, kind);

                // Two examples per cause, quoted from the source. A count says how big a problem
                // is; an example says what to do about it.
                if (!examples.TryGetValue(kind, out var seen)) examples[kind] = seen = [];
                if (seen.Count < 2) seen.Add($"{name}: {refusal}");
            }
        }

        Print(rows, reasons, examples);

        if (Option(args, "--json") is { Length: > 0 } path)
        {
            System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(
                rows.Select(r => new { r.Block, r.Animated, r.Seconds, r.Refusals, r.Calls, r.Unmatched, r.Held }),
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine();
            Console.WriteLine("wrote " + path);
        }

        return 0;
    }

    /// <summary>A rough count of motion calls in the source, so "carried 12 selectors" can be read
    /// beside "there were 40 tweens".</summary>
    private static int Calls(string html) =>
        Regex.Matches(html, @"\.\s*(?:to|set|from|fromTo)\s*\(").Count;

    /// <summary>Refusal text with the block-specific parts taken out, so the same cause groups.</summary>
    private static string Kind(string what)
    {
        var text = Regex.Replace(what, @"'[^']*'", "'…'");
        text = Regex.Replace(text, @"\b\d+(\.\d+)?\b", "N");
        return text.Length <= 110 ? text : text[..110];
    }

    private static void Count(Dictionary<string, int> into, string key) =>
        into[key] = into.TryGetValue(key, out var seen) ? seen + 1 : 1;

    private static void Print(
        List<(string Block, int Animated, double Seconds, int Refusals, int Calls, int Unmatched, int Held)> rows,
        Dictionary<string, int> reasons,
        Dictionary<string, List<string>> examples)
    {
        var withMotion = rows.Where(r => r.Animated > 0).ToArray();

        Console.WriteLine($"{rows.Count} blocks");
        Console.WriteLine($"{withMotion.Length} carry at least one animated element "
                          + $"({Percent(withMotion.Length, rows.Count)})");

        if (withMotion.Length > 0)
        {
            Console.WriteLine($"{withMotion.Sum(r => r.Animated)} elements animated in total, "
                              + $"median {Median([.. withMotion.Select(r => (double)r.Animated)]):0} per block");
            Console.WriteLine($"{rows.Sum(r => r.Refusals)} refusals, "
                              + $"{rows.Sum(r => r.Calls)} motion calls in the source");
        }

        // Two lines of their own, because both are cases where the compiler did its job and the
        // frame still cannot show it. Every other refusal says something was not carried; these
        // two read as a complete translation and render as a still, which is the harder failure to
        // notice and the reason the line above them used to be overstated by a fifth.
        var held = rows.Sum(r => r.Held);
        if (held > 0)
            Console.WriteLine($"{held} more are held at their end state rather than animated: the "
                              + "tween ends where the compiler assumed it began");

        var blind = rows.Where(r => r.Unmatched > 0).ToArray();
        if (blind.Length > 0)
            Console.WriteLine($"{blind.Sum(r => r.Unmatched)} animation(s) across {blind.Length} "
                              + "block(s) land on a selector that matches nothing");

        Console.WriteLine();
        Console.WriteLine("what is refused, by cause");
        foreach (var (reason, count) in reasons.OrderByDescending(r => r.Value).Take(18))
        {
            Console.WriteLine($"  {count,5}  {reason}");
            if (!examples.TryGetValue(reason, out var shown)) continue;
            foreach (var example in shown) Console.WriteLine($"         {Clip(example, 130)}");
        }

        Console.WriteLine();
        Console.WriteLine("blocks carrying the most");
        foreach (var row in rows.OrderByDescending(r => r.Animated).Take(12))
            Console.WriteLine($"  {row.Block,-46} {row.Animated,3} elements  {row.Seconds,6:0.##}s  "
                              + $"{row.Refusals,3} refused");
    }

    private static string Clip(string text, int n) =>
        text.Length <= n ? text : text[..(n - 1)] + "…";

    private static double Median(double[] values)
    {
        if (values.Length == 0) return 0;
        Array.Sort(values);
        return values.Length % 2 == 1
            ? values[values.Length / 2]
            : (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2;
    }

    private static string Percent(int part, int whole) =>
        whole == 0 ? "-" : (100.0 * part / whole).ToString("0") + "%";

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
