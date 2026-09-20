using Acornima;
using Acornima.Ast;
using AngleSharp.Html.Parser;

// Acornima's syntax-tree root is also called Program, and so is this project's entry point.
using AstProgram = Acornima.Ast.Program;

namespace CupriLex.Compiler;

/// <summary>One inline script from a document, parsed.</summary>
/// <param name="Index">Which script in the document, counting from zero. In the report, so a
/// refusal can say where.</param>
/// <param name="Line">The line the script opened on, for the same reason.</param>
/// <param name="Source">The JavaScript, as written.</param>
/// <param name="Tree">Its syntax tree.</param>
public sealed record Parsed(int Index, int Line, string Source, AstProgram Tree);

/// <summary>A script that could not be parsed at all.</summary>
public sealed record Unparsed(int Index, int Line, string Why);

/// <summary>The scripts of a document, and the ones that would not parse.</summary>
public sealed record Scripts(IReadOnlyList<Parsed> Parsed, IReadOnlyList<Unparsed> Failed);

/// <summary>
/// Getting from a block's HTML to syntax trees.
///
/// <para>The HTML is parsed by AngleSharp rather than by a regular expression, because a
/// <c>&lt;/script&gt;</c> inside a string literal is legal JavaScript and common in this corpus,
/// and a regular expression stops there. It is also the parser CupriFace itself uses, so both
/// sides of a comparison read the same document the same way.</para>
/// </summary>
public static class ScriptReader
{
    // Source references are off by default, and without them every refusal in the first run
    // reported a line number counted from the start of its own script rather than from the start
    // of the file - which points confidently at the wrong line of markup.
    private static readonly HtmlParser Html = new(new HtmlParserOptions
    {
        IsKeepingSourceReferences = true,
    });

    public static Scripts Of(string html)
    {
        var document = Html.ParseDocument(html);
        var parsed = new List<Parsed>();
        var failed = new List<Unparsed>();
        var index = 0;

        foreach (var element in document.QuerySelectorAll("script"))
        {
            var source = element.TextContent;
            var type = element.GetAttribute("type") ?? "";

            // A script with a src is the library, not the composition. It is never carried and
            // never read: the compiler's whole job is to make it unnecessary.
            if (!string.IsNullOrWhiteSpace(element.GetAttribute("src"))) { index++; continue; }
            if (string.IsNullOrWhiteSpace(source)) { index++; continue; }

            // "application/json", "text/template" and friends are data, not code.
            if (type.Length > 0
                && !type.Contains("javascript", StringComparison.OrdinalIgnoreCase)
                && !type.Equals("module", StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            var line = element.SourceReference?.Position.Line ?? 0;

            try
            {
                var parser = new Parser();
                AstProgram tree = type.Equals("module", StringComparison.OrdinalIgnoreCase)
                    ? parser.ParseModule(source)
                    : parser.ParseScript(source);

                parsed.Add(new Parsed(index, line, source, tree));
            }
            catch (SyntaxErrorException ex)
            {
                failed.Add(new Unparsed(index, line, ex.Message));
            }

            index++;
        }

        return new Scripts(parsed, failed);
    }

    /// <summary>Every node under this one, itself included. Depth first, and iterative rather than
    /// recursive: one corpus block nests deeply enough to be worth not finding out about on a
    /// stack overflow.</summary>
    public static IEnumerable<Node> Descendants(this Node root)
    {
        var pending = new Stack<Node>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var node = pending.Pop();
            yield return node;

            foreach (var child in node.ChildNodes) pending.Push(child);
        }
    }
}
