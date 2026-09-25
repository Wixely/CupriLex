using System.Text.Json;
using Acornima.Ast;
using CupriLex.Compiler;

namespace CupriLex.Cli;

/// <summary>Where the corpus is. One copy of this walk, shared with the harness.</summary>
public static class CorpusPath
{
    public static string Blocks()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "corpus", "registry", "blocks");
            if (Directory.Exists(candidate)) return candidate;
        }

        throw new DirectoryNotFoundException(
            "No corpus/registry/blocks above " + AppContext.BaseDirectory
            + ". Run: python tools/fetch-corpus.py");
    }

    public static IEnumerable<string> Files() =>
        Directory.EnumerateFiles(Blocks(), "*.html", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
}

/// <summary>One GSAP call, as written.</summary>
public sealed record Shape(
    string Block, string Verb, string Receiver, string Target, string Position,
    string Values, Where Where);

/// <summary>
/// What the corpus writes, counted from syntax trees.
///
/// <para>Counting calls was already done by <c>tools/survey.py</c> and said the compiler needs
/// four verbs. This asks the question that decides whether those four can be compiled at all: what
/// do their ARGUMENTS look like, and how much of it is straight-line code whose values are there
/// in the source.</para>
/// </summary>
public static class Shapes
{
    /// <summary>The calls worth classifying. Everything else in a block's script is not motion.</summary>
    private static readonly HashSet<string> Verbs =
    [
        "to", "set", "fromTo", "from", "timeline", "add", "addLabel",
        "call", "eventCallback", "time", "seek", "duration", "delayedCall",
        "pause", "play", "clear", "defaults", "killTweensOf", "staggerTo",
    ];

    /// <summary>Verbs that place motion on a clock - the ones the compiler has to resolve.</summary>
    private static readonly HashSet<string> Motion = ["to", "set", "fromTo", "from"];

    public static int Report(string[] args)
    {
        var limit = int.Parse(Option(args, "--limit") ?? "0");
        var files = CorpusPath.Files().ToArray();
        if (limit > 0) files = [.. files.Take(limit)];

        var shapes = new List<Shape>();
        var unparsed = new List<string>();
        var blocks = 0;

        foreach (var file in files)
        {
            blocks++;
            // Directory and stem, always: four directories hold a demo.html, and counting
            // blocks by stem alone collapsed them into one row.
            var name = Path.GetFileName(Path.GetDirectoryName(file)) + "/"
                       + Path.GetFileNameWithoutExtension(file);
            var scripts = ScriptReader.Of(File.ReadAllText(file));

            foreach (var failure in scripts.Failed)
                unparsed.Add($"{name} script {failure.Index}: {failure.Why}");

            foreach (var script in scripts.Parsed)
                Walk(script.Tree, Where.TopLevel, name, shapes);
        }

        Print(blocks, shapes, unparsed);

        if (Option(args, "--json") is { Length: > 0 } path)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new { blocks, shapes, unparsed },
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine();
            Console.WriteLine("wrote " + path);
        }

        return 0;
    }

    // ---- the walk -----------------------------------------------------------------------------

    private static void Walk(Node node, Where where, string block, List<Shape> found)
    {
        if (node is CallExpression call && Describe(call, block, where) is { } shape)
            found.Add(shape);

        foreach (var child in node.ChildNodes) Walk(child, Context(node, child, where), block, found);
    }

    /// <summary>How the context changes on the way into a child.</summary>
    private static Where Context(Node parent, Node child, Where where) => parent switch
    {
        ForStatement or ForInStatement or ForOfStatement or WhileStatement or DoWhileStatement
            => where.Looping,

        IfStatement or ConditionalExpression or SwitchCase => where.Conditional,

        // An immediately invoked function is not a function for this purpose: its body runs once,
        // in order, exactly where it is written. Nearly every block in the corpus wraps everything
        // in one, and counting those bodies as "inside a function" would have reported that almost
        // nothing in the corpus is straight-line code.
        CallExpression { Callee: FunctionExpression or ArrowFunctionExpression } => where,

        // A callback handed to forEach/map/filter IS a loop, whatever it looks like.
        CallExpression iterating when child is FunctionExpression or ArrowFunctionExpression
            => Iterates(iterating) ? where.Looping : where.InsideFunction,

        FunctionDeclaration or FunctionExpression or ArrowFunctionExpression => where.InsideFunction,

        _ => where,
    };

    private static bool Iterates(CallExpression call) =>
        call.Callee is MemberExpression { Property: Identifier name }
        && name.Name is "forEach" or "map" or "filter" or "flatMap" or "reduce" or "some" or "every";

    // ---- classification -----------------------------------------------------------------------

    private static Shape? Describe(CallExpression call, string block, Where where)
    {
        if (call.Callee is not MemberExpression { Property: Identifier verb } callee) return null;
        if (!Verbs.Contains(verb.Name)) return null;

        var arguments = call.Arguments;
        var motion = Motion.Contains(verb.Name);

        // fromTo takes two variable objects; its position is the fourth argument.
        var valuesIndex = verb.Name == "fromTo" ? 2 : 1;
        var positionIndex = verb.Name == "fromTo" ? 3 : 2;

        return new Shape(
            block,
            verb.Name,
            Root(callee.Object),
            motion && arguments.Count > 0 ? Form(arguments[0]) : "-",
            motion && arguments.Count > positionIndex ? Position(arguments[positionIndex]) : "none",
            motion && arguments.Count > valuesIndex ? Values(arguments[valuesIndex]) : "-",
            where);
    }

    /// <summary>What the call is being made ON. <c>gsap</c> itself, a timeline held in a variable,
    /// or something else again.</summary>
    private static string Root(Node node) => node switch
    {
        Identifier id => id.Name == "gsap" ? "gsap" : "variable",
        MemberExpression member => Root(member.Object) == "gsap" ? "gsap" : "member",
        CallExpression => "call",
        ThisExpression => "this",
        _ => node.Type.ToString(),
    };

    /// <summary>The shape of a tween's target.</summary>
    private static string Form(Node node) => node switch
    {
        StringLiteral => "selector string",
        Identifier => "variable",
        MemberExpression => "member",
        ArrayExpression => "array",
        TemplateLiteral => "template string",
        CallExpression { Callee: MemberExpression { Property: Identifier name } } =>
            name.Name is "querySelector" or "querySelectorAll" or "getElementById"
                ? "query call"
                : name.Name is "toArray"
                    ? "gsap.utils.toArray"
                    : "call",
        CallExpression => "call",
        _ => node.Type.ToString(),
    };

    /// <summary>The shape of a position parameter - the thing that decides WHEN.</summary>
    private static string Position(Node node) => node switch
    {
        NumericLiteral => "number",
        StringLiteral s when s.Value == "<" => "\"<\"",
        StringLiteral s when s.Value == ">" => "\">\"",
        StringLiteral s when s.Value.StartsWith("<") || s.Value.StartsWith(">") => "relative to last",
        StringLiteral s when s.Value.Contains("+=") || s.Value.Contains("-=") => "label or relative",
        StringLiteral => "label",
        Identifier or MemberExpression => "variable",
        BinaryExpression => "arithmetic",
        TemplateLiteral => "template string",
        CallExpression => "call",
        _ => node.Type.ToString(),
    };

    /// <summary>Whether the values object can be read straight off the page. "literal" means every
    /// value is a number or a string; anything else has to be computed, and computing it is what
    /// this compiler will not do.</summary>
    private static string Values(Node node)
    {
        if (node is not ObjectExpression obj) return node.Type.ToString();
        if (obj.Properties.Count == 0) return "empty";

        var literal = true;
        foreach (var property in obj.Properties)
        {
            if (property is not Property { Value: var value }) { literal = false; continue; }
            if (value is not (NumericLiteral or StringLiteral or BooleanLiteral or UnaryExpression
                { Argument: NumericLiteral })) literal = false;
        }

        return literal ? "literal" : "computed";
    }

    // ---- the report ---------------------------------------------------------------------------

    private static void Print(int blocks, List<Shape> shapes, List<string> unparsed)
    {
        Console.WriteLine($"{blocks} blocks, {shapes.Count} GSAP calls read from syntax trees");
        if (unparsed.Count > 0)
            Console.WriteLine($"{unparsed.Count} script(s) would not parse: "
                              + string.Join("; ", unparsed.Take(3)));
        Console.WriteLine();

        var motion = shapes.Where(s => Motion.Contains(s.Verb)).ToArray();

        Table("verb", shapes.GroupBy(s => s.Verb), shapes.Count);
        Table("called on", shapes.GroupBy(s => s.Receiver), shapes.Count);
        Table("tween target", motion.GroupBy(s => s.Target), motion.Length);
        Table("position parameter", motion.GroupBy(s => s.Position), motion.Length);
        Table("values object", motion.GroupBy(s => s.Values), motion.Length);
        Table("written where", motion.GroupBy(s => s.Where.ToString()), motion.Length);

        // The number the compiler's scope turns on.
        var plain = motion.Count(s => s.Where.Plain);
        var resolvable = motion.Count(s =>
            s.Where.Plain && s.Values == "literal"
            && s.Target is "selector string" or "variable" or "member");

        Console.WriteLine();
        Console.WriteLine($"{plain} of {motion.Length} motion calls are straight-line code "
                          + $"({Percent(plain, motion.Length)})");
        Console.WriteLine($"{resolvable} of {motion.Length} are straight-line AND have literal "
                          + $"values AND a target worth resolving ({Percent(resolvable, motion.Length)})");

        var byBlock = motion.GroupBy(s => s.Block).ToArray();
        var whole = byBlock.Count(g => g.All(s =>
            s.Where.Plain && s.Values == "literal"
            && s.Target is "selector string" or "variable" or "member"));

        Console.WriteLine($"{whole} of {byBlock.Length} blocks have NOTHING outside that subset");
    }

    private static void Table(string title, IEnumerable<IGrouping<string, Shape>> groups, int total)
    {
        Console.WriteLine(title);
        foreach (var group in groups.OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {group.Count(),6}  {Percent(group.Count(), total),5}  {group.Key}");
        Console.WriteLine();
    }

    private static string Percent(int part, int whole) =>
        whole == 0 ? "-" : (100.0 * part / whole).ToString("0") + "%";

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
