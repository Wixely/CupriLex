namespace CupriLex.Compiler;

/// <summary>
/// What the corpus actually writes, counted from syntax trees rather than guessed.
///
///     dotnet run --project compiler -- shapes
///
/// <para>The corpus survey in <c>tools/survey.py</c> counts GSAP calls with a regular expression,
/// which is the right instrument for "how much of this is there". It cannot answer the question
/// that decides this compiler's scope, which is what the ARGUMENTS look like: a tween whose target
/// is a string literal and whose values are numbers can be resolved by arithmetic, and a tween
/// whose target is a variable assigned in a loop cannot.</para>
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            return args.FirstOrDefault() switch
            {
                "shapes" => Shapes.Report(args),
                "reach" => Reach.Report(args),
                "faces" => FacesReport(args),
                "translate" => Translate(args),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("cuprilex-compiler: " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Every typeface the corpus names and does not carry.
    ///
    /// <para>The measure that matters for the OUTPUT rather than for the score. A family named
    /// with nothing to answer it costs almost nothing in a pixel comparison - the harness
    /// registers its own faces - and costs everything in a renderer with a strict font policy,
    /// which refuses rather than substitutes. 96 of the 187 packages cannot be built for this
    /// reason alone, which is more than every other cause put together.</para>
    /// </summary>
    private static int FacesReport(string[] args)
    {
        var limit = int.TryParse(Option(args, "--limit"), out var n) ? n : int.MaxValue;
        var files = CorpusPath.Files().Take(limit).ToArray();

        var wanted = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var carried = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var blocksMissing = 0;

        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var faces = Faces.Of(Translator.Of(File.ReadAllText(file)).Html);

            if (faces.Missing.Count > 0) blocksMissing++;

            foreach (var face in faces.Missing)
            {
                if (!wanted.TryGetValue(face.Family, out var set)) wanted[face.Family] = set = [];
                set.Add(name);
            }

            foreach (var family in faces.Provided)
            {
                if (!carried.TryGetValue(family, out var set)) carried[family] = set = [];
                set.Add(name);
            }
        }

        Console.WriteLine($"{files.Length} blocks; {blocksMissing} name a family they do not carry");
        Console.WriteLine();
        Console.WriteLine($"{"family",-26} {"blocks",6}  asked for and NOT carried");
        Console.WriteLine(new string('-', 62));

        foreach (var (family, blocks) in wanted.OrderByDescending(p => p.Value.Count))
            Console.WriteLine($"{Clip(family, 26),-26} {blocks.Count,6}  "
                              + string.Join(", ", blocks.Take(3).Order())
                              + (blocks.Count > 3 ? ", …" : ""));

        Console.WriteLine();
        Console.WriteLine($"{carried.Count} family/families ARE carried by the blocks that use them:");
        foreach (var (family, blocks) in carried.OrderByDescending(p => p.Value.Count).Take(8))
            Console.WriteLine($"  {Clip(family, 26),-26} {blocks.Count,6}");

        return 0;
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string Clip(string text, int width) =>
        text.Length <= width ? text : text[..(width - 1)] + "\u2026";

    /// <summary>One block, translated, so the output can be read rather than scored.</summary>
    private static int Translate(string[] args)
    {
        var wanted = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"))
                     ?? throw new ArgumentException("translate needs a block name");

        var file = CorpusPath.Files().FirstOrDefault(f =>
                       Path.GetFileNameWithoutExtension(f)
                           .Equals(wanted, StringComparison.OrdinalIgnoreCase))
                   ?? throw new FileNotFoundException($"no block named '{wanted}'");

        var translated = Translator.Of(File.ReadAllText(file), Path.GetDirectoryName(file));

        Console.WriteLine($"{Path.GetFileName(file)}: {translated.Motion.Elements} element(s) "
                          + $"animated over {translated.Motion.Seconds:0.###}s, "
                          + $"{translated.Refusals.Count} refusal(s)");
        Console.WriteLine();
        Console.WriteLine(translated.Motion.Css.Length > 4000
            ? translated.Motion.Css[..4000] + Environment.NewLine + "/* … */"
            : translated.Motion.Css);

        if (translated.Refusals.Count > 0)
        {
            Console.WriteLine("not carried:");
            foreach (var refusal in translated.Refusals.Take(25)) Console.WriteLine("  - " + refusal);
            if (translated.Refusals.Count > 25)
                Console.WriteLine($"  … and {translated.Refusals.Count - 25} more");
        }

        var index = Array.IndexOf(args, "--out");
        if (index >= 0 && index + 1 < args.Length)
        {
            File.WriteAllText(args[index + 1], translated.Html);
            Console.WriteLine();
            Console.WriteLine("wrote " + args[index + 1]);
        }

        return 0;
    }

    private static int Usage()
    {
        Console.WriteLine("""
            What the corpus writes, and how much of it can be resolved without running it.

                dotnet run --project compiler -- shapes [--limit N] [--json FILE]
                dotnet run --project compiler -- reach  [--limit N]
                dotnet run --project compiler -- faces  [--limit N]
                dotnet run --project compiler -- translate <block> [--out FILE]

            Needs the corpus: python tools/fetch-corpus.py
            """);
        return 2;
    }
}
