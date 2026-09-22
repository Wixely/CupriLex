using System.Text.Json;
using System.Text.Json.Serialization;
using CupriFace;

namespace CupriLex.Conformance;

/// <summary>
/// Ask the engine what it supports, and write it down so the next version can be diffed against it.
///
///     dotnet run --project conformance -- --out conformance/support
///     dotnet run --project conformance -- --compare conformance/support/0.26.1.json conformance/support/0.27.0.json
///
/// See docs/CONFORMANCE.md for why this is a program rather than a document. Short version: it was
/// a document, and one engine release made three of them wrong at once.
/// </summary>
public static class Program
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static int Main(string[] args)
    {
        try
        {
            var compare = Argument(args, "--compare");
            return compare is null ? Measure(Argument(args, "--out")) : Compare(compare, args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("cuprilex-conformance: " + ex.Message);
            return 1;
        }
    }

    // ---- measure -----------------------------------------------------------------------------

    private static int Measure(string? outDirectory)
    {
        var version = typeof(CupriDocument).Assembly.GetName().Version?.ToString(3) ?? "unknown";

        // Text-shaped cases need a registered face or they are at the mercy of the machine.
        Probe.FontDirectory = FindFonts();
        Probe.FontFiles = Cases.Woff2Path is { } woff2 ? [woff2] : [];

        Console.WriteLine($"CupriFace {version} on {Environment.OSVersion.VersionString}");
        Console.WriteLine($"fonts: {Probe.FontDirectory ?? "(none found - text cases may be unreliable)"}");
        Console.WriteLine($"woff2: {(Probe.FontFiles.Count > 0 ? Probe.FontFiles[0] : "(none found - the @font-face case will read NO for the wrong reason)")}");
        Console.WriteLine();

        var results = Probe.All();

        Console.WriteLine($"{"property",-20} {"value",-30} parse paint animate");
        Console.WriteLine(new string('-', 78));
        foreach (var r in results)
        {
            Console.WriteLine($"{r.Property,-20} {Clip(r.Value, 30),-30} "
                              + $"{Mark(r.Parses),-5} {Mark(r.Paints),-5} {Mark(r.Animates),-5}");
        }

        var matrix = new Matrix(version, Environment.OSVersion.Platform.ToString(),
            Environment.Version.ToString(), DateTimeOffset.UtcNow, results);

        Console.WriteLine();
        Console.WriteLine($"{results.Count(r => r.Paints)}/{results.Count} paint, "
                          + $"{results.Count(r => r.Animates == true)}/{results.Count(r => r.Animates is not null)} animate");

        if (outDirectory is { Length: > 0 })
        {
            Directory.CreateDirectory(outDirectory);
            var path = Path.Combine(outDirectory, version + ".json");
            File.WriteAllText(path, JsonSerializer.Serialize(matrix, Json));
            Console.WriteLine("wrote " + path);
        }

        return 0;
    }

    // ---- compare -----------------------------------------------------------------------------

    /// <summary>The whole point of committing the matrix: two versions, and what moved between
    /// them. A property that gained support means a rewrite rule can be REMOVED, which is output
    /// closer to what the author wrote.</summary>
    private static int Compare(string first, string[] args)
    {
        var second = args.SkipWhile(a => a != "--compare").Skip(2).FirstOrDefault()
            ?? throw new ArgumentException("--compare takes two files.");

        var a = Read(first);
        var b = Read(second);

        Console.WriteLine($"{a.Engine}  ->  {b.Engine}");
        Console.WriteLine();

        var changes = 0;
        foreach (var after in b.Support)
        {
            var before = a.Support.FirstOrDefault(
                s => s.Property == after.Property && s.Value == after.Value);

            if (before is null)
            {
                Console.WriteLine($"  NEW CASE  {after.Property} ({after.Value}): "
                                  + $"parse {Mark(after.Parses)} paint {Mark(after.Paints)} animate {Mark(after.Animates)}");
                changes++;
                continue;
            }

            foreach (var (what, was, now) in new[]
                     {
                         ("parses", (bool?)before.Parses, (bool?)after.Parses),
                         ("paints", before.Paints, after.Paints),
                         ("animates", before.Animates, after.Animates),
                     })
            {
                if (was == now) continue;
                Console.WriteLine($"  {after.Property} ({after.Value}) {what}: {Mark(was)} -> {Mark(now)}");
                changes++;
            }
        }

        foreach (var gone in a.Support.Where(s => !b.Support.Any(
                     t => t.Property == s.Property && t.Value == s.Value)))
        {
            Console.WriteLine($"  DROPPED   {gone.Property} ({gone.Value})");
            changes++;
        }

        Console.WriteLine();
        Console.WriteLine(changes == 0
            ? "nothing moved."
            : $"{changes} change(s). Each one is a rewrite rule to add, remove, or reconsider.");
        return 0;
    }

    // ---- plumbing ----------------------------------------------------------------------------

    private sealed record Matrix(
        string Engine, string Platform, string Runtime, DateTimeOffset Measured,
        IReadOnlyList<Support> Support);

    private static Matrix Read(string path) =>
        JsonSerializer.Deserialize<Matrix>(File.ReadAllText(path), Json)
        ?? throw new InvalidOperationException($"'{path}' is not a support matrix.");

    private static string? Argument(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string Mark(bool? b) => b is null ? "-" : b.Value ? "yes" : "NO";

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    /// <summary>The fonts CupriCut ships, if that repository is beside this one. A text case with
    /// no registered face measures the machine rather than the engine.</summary>
    private static string? FindFonts()
    {
        var here = new DirectoryInfo(AppContext.BaseDirectory);
        for (var d = here; d is not null; d = d.Parent)
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(d.FullName, "fonts"),
                         Path.Combine(d.FullName, "..", "CupriCut", "fonts"),
                     })
            {
                if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.ttf").Any())
                    return Path.GetFullPath(candidate);
            }
        }
        return null;
    }

}
