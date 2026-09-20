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
                dotnet run --project compiler -- translate <block> [--out FILE]

            Needs the corpus: python tools/fetch-corpus.py
            """);
        return 2;
    }
}
