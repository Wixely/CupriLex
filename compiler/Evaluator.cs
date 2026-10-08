using System.Globalization;
using Acornima.Ast;

namespace CupriLex.Compiler;

/// <summary>
/// What a value can be known to be without running anything.
///
/// <para>Deliberately tiny. Every shape the corpus's timelines actually need is here, and
/// everything else is <see cref="Unknown"/> - which is not a failure mode but the point: a value
/// this domain cannot represent becomes a refusal with a reason, never a guess.</para>
/// </summary>
internal abstract record Value
{
    public sealed record Number(double Of) : Value;

    public sealed record Text(string Of) : Value;

    /// <summary>One or more elements, as the CSS selector that finds them. The engine has no
    /// element handles, so a target that cannot be reduced to a selector cannot be carried.</summary>
    public sealed record Selector(string Css) : Value;

    public sealed record Bag(IReadOnlyDictionary<string, Value> Of) : Value;

    public sealed record List(IReadOnlyList<Value> Of) : Value;

    /// <summary>A function this compiler can step into: its parameters and its body. Held as a
    /// value because a block may write <c>function build() {}</c> or <c>const build = () =&gt; {}</c>
    /// and mean the same thing.</summary>
    public sealed record Routine(IReadOnlyList<Node> Parameters, Node Body) : Value;

    /// <summary>A GSAP timeline in progress. Held as a value because that is how the source holds
    /// it: <c>const tl = gsap.timeline()</c> is a binding like any other.</summary>
    public sealed record Timeline(Clock Of) : Value;

    /// <summary>
    /// <c>null</c> or <c>undefined</c>, which is a value and not an absence of one.
    ///
    /// <para>The distinction is the whole point. <see cref="Unknown"/> means "this compiler could
    /// not work out what this is", and a test over it leaves both branches unread. Nothing means
    /// "the document says this is null", and a test over it is decidable - falsy, for certain.
    /// The carousel family turns on exactly that: <c>const TXT = DATA.text || null</c> over a
    /// <c>DATA</c> whose <c>text</c> key is written <c>null</c>, then <c>if (TXT)</c> around the
    /// text layer's whole timeline. Reading null as unknown refused 150 tweens across 25 blocks
    /// that the browser does not run either.</para>
    /// </summary>
    /// <param name="IsNull">Which of the two it was written as. Needed only by <c>===</c>, where
    /// <c>null === undefined</c> is FALSE while <c>null == undefined</c> is true - so collapsing
    /// the pair would make strict equality a coin flip exactly where loose equality is certain.</param>
    public sealed record Nothing(bool IsNull = true) : Value;

    /// <summary>Not resolvable, and why. The reason is carried all the way into the report.</summary>
    public sealed record Unknown(string Why) : Value;

    public bool Known => this is not Unknown;

    public double? AsNumber => this switch
    {
        Number n => n.Of,
        Text t when double.TryParse(t.Of, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    public string? AsText => this switch
    {
        Text t => t.Of,
        Number n => n.Of.ToString("R", CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>
    /// JavaScript truthiness, or null when it is not knowable.
    ///
    /// <para>Separate from <see cref="AsNumber"/>, which is what the ternary used and which calls
    /// a string and an object unknowable. Both are truthy in JavaScript, and a block that writes
    /// <c>if (CONFIG.mode)</c> or <c>if (CONFIG.grid)</c> means exactly that. Null is the only
    /// answer that must never be guessed at: an unknown test means neither branch is read, and
    /// the calls inside are named in the report as unreached.</para>
    /// </summary>
    public bool? Truth => this switch
    {
        Number n => n.Of != 0,
        Text t => t.Of.Length > 0,
        Selector or Bag or List or Routine or Timeline => true,
        Nothing => false,
        _ => null,
    };
}

/// <summary>Names in scope, and what they were bound to. A name assigned more than once is bound
/// to <see cref="Value.Unknown"/>: this compiler does not track flow, and a variable that changes
/// is a variable whose value at any particular call is not knowable from the syntax.</summary>
internal sealed class Scope(Scope? parent = null)
{
    private readonly Dictionary<string, Value> _names = [];

    public Scope? Parent { get; } = parent;

    /// <summary>A declaration: the name belongs to THIS scope from here on.</summary>
    public void Bind(string name, Value value) => _names[name] = value;

    /// <summary>
    /// An assignment: the name keeps whichever scope already owns it.
    ///
    /// <para>Not the same as <see cref="Bind"/>, and the difference is load-bearing once functions
    /// are stepped into. A helper that advances a shared <c>t</c> by half a second is writing to
    /// the caller's <c>t</c>; binding it locally instead leaves the caller's copy at zero, and
    /// every tween after the call lands at the wrong time while looking perfectly resolvable.</para>
    /// </summary>
    public void Set(string name, Value value)
    {
        for (var scope = this; scope is not null; scope = scope.Parent)
        {
            if (!scope._names.ContainsKey(name)) continue;
            scope._names[name] = value;
            return;
        }

        _names[name] = value;
    }

    public void Reassign(string name, string why)
    {
        for (var scope = this; scope is not null; scope = scope.Parent)
        {
            if (!scope._names.ContainsKey(name)) continue;
            scope._names[name] = new Value.Unknown(why);
            return;
        }

        _names[name] = new Value.Unknown(why);
    }

    public Value Lookup(string name)
    {
        for (var scope = this; scope is not null; scope = scope.Parent)
            if (scope._names.TryGetValue(name) is { } found) return found;

        return new Value.Unknown($"'{name}' is not something this compiler can resolve");
    }
}

file static class DictionaryExtensions
{
    public static Value? TryGetValue(this Dictionary<string, Value> map, string key) =>
        map.TryGetValue(key, out var value) ? value : null;
}

/// <summary>
/// Reading a value out of an expression, by arithmetic and lookup only.
///
/// <para>This is where the "static analysis, never execution" line is drawn, so it is worth being
/// exact about which side of it this sits on. Nothing from the document is executed: there is no
/// interpreter loop, no statements, no assignment, no control flow, no host objects. What happens
/// here is constant folding over a whitelist - the same thing a compiler does to
/// <c>2 + 3</c> - plus a lookup table of DOM queries whose only outcome is a CSS selector string.
/// Anything outside that whitelist evaluates to <see cref="Value.Unknown"/> and is refused.</para>
/// </summary>
internal static class Evaluator
{
    public static Value Of(Node? node, Scope scope) => node switch
    {
        null => new Value.Unknown("nothing"),

        NumericLiteral n => new Value.Number(n.Value),
        StringLiteral s => new Value.Text(s.Value),
        BooleanLiteral b => new Value.Number(b.Value ? 1 : 0),
        NullLiteral => new Value.Nothing(IsNull: true),

        // `undefined` is an identifier in JavaScript, not a literal, and nothing rebinds it in a
        // composition. A block that writes `CONFIG.src || undefined` means the same falsy thing
        // as one that writes null.
        Identifier { Name: "undefined" } => new Value.Nothing(IsNull: false),

        Identifier id => scope.Lookup(id.Name),

        UnaryExpression u => Unary(u, scope),

        // Before BinaryExpression, which it derives from: `&&` is a BinaryExpression as far as
        // the type hierarchy is concerned and arithmetic folding is the wrong answer for it.
        LogicalExpression l => Logical(l, scope),
        BinaryExpression b => Binary(b, scope),
        ConditionalExpression c => Conditional(c, scope),

        ArrayExpression a => Array(a, scope),
        ObjectExpression o => Object(o, scope),
        TemplateLiteral t => Template(t, scope),

        MemberExpression m => Member(m, scope),
        CallExpression call => Call(call, scope),

        _ => new Value.Unknown($"a {node.Type} is outside what can be resolved without running it"),
    };

    /// <summary>The single expression a helper returns, or null when its body does anything more
    /// than return one. A body with branches or statements is a function to be followed, not a
    /// value to be computed, and that is the statement walker's job.</summary>
    private static Node? Returned(Node body) => body switch
    {
        BlockStatement { Body: [ReturnStatement { Argument: { } only }] } => only,
        BlockStatement => null,
        _ => body,      // an expression-bodied arrow: `const clamp = (v) => Math.min(1, v)`
    };

    private static Value Unary(UnaryExpression node, Scope scope)
    {
        var inner = Of(node.Argument, scope);
        // `!` is here because the corpus guards whole features with it - `if (!CONFIG.src)` picks
        // the stand-in media, `if (!CONFIG.grid.enabled)` hides the grid - and a negation this
        // could not fold left the branch unread and its motion refused.
        if (node.Operator == Acornima.Operator.LogicalNot)
            return inner.Truth is { } truth
                ? new Value.Number(truth ? 0 : 1)
                : new Value.Unknown("a ! of something not known");

        return (node.Operator, inner.AsNumber) switch
        {
            (Acornima.Operator.UnaryNegation, { } n) => new Value.Number(-n),
            (Acornima.Operator.UnaryPlus, { } n) => new Value.Number(n),
            _ => new Value.Unknown($"unary {node.Operator}"),
        };
    }

    /// <summary>
    /// <c>&amp;&amp;</c>, <c>||</c> and <c>??</c>, which yield an OPERAND rather than a boolean.
    ///
    /// <para>That is what makes them worth folding: <c>DATA.text || null</c> is how this corpus
    /// writes an optional feature, and the branch that guards it is decidable the moment the
    /// operator is. Short-circuiting is honoured, so a right side this compiler cannot read costs
    /// nothing when the left side already settles the answer - <c>CONFIG.grid &amp;&amp;
    /// buildGrid()</c> resolves when the grid is off.</para>
    /// </summary>
    private static Value Logical(LogicalExpression node, Scope scope)
    {
        var left = Of(node.Left, scope);

        if (node.Operator == Acornima.Operator.NullishCoalescing)
            return left is Value.Nothing ? Of(node.Right, scope)
                : left.Known ? left
                : new Value.Unknown("a ?? over a left side that is not known");

        if (left.Truth is not { } decided)
            return new Value.Unknown($"a {node.Operator} over a left side that is not known");

        var shortCircuits = node.Operator == Acornima.Operator.LogicalAnd ? !decided : decided;

        return shortCircuits ? left : Of(node.Right, scope);
    }

    private static Value Binary(BinaryExpression node, Scope scope)
    {
        var left = Of(node.Left, scope);
        var right = Of(node.Right, scope);

        // String concatenation first: "translate(" + x + "px)" is a string, not arithmetic.
        if (node.Operator == Acornima.Operator.Addition
            && (left is Value.Text || right is Value.Text)
            && left.AsText is { } a && right.AsText is { } b)
            return new Value.Text(a + b);

        // Equality against null, which is how this corpus asks whether an optional feature was
        // configured: `if (END.imagesTo != null)`. Decided ONLY when a side is Nothing - and
        // deliberately not extended to `<`, `<=`, `>`, `>=`, which coerce (`null >= 0` is TRUE,
        // and the arithmetic guard below refuses those for the wrong reason with the right
        // outcome).
        if (Equality(node.Operator) is { } strict && (left is Value.Nothing || right is Value.Nothing))
        {
            if (Same(left, right, strict) is not { } same)
                return new Value.Unknown($"{node.Operator} over a value that is not known");

            var wanted = node.Operator is Acornima.Operator.Equality
                or Acornima.Operator.StrictEquality;

            return new Value.Number(same == wanted ? 1 : 0);
        }

        if (left.AsNumber is not { } x || right.AsNumber is not { } y)
            return new Value.Unknown($"{node.Operator} over values that are not both known");

        return node.Operator switch
        {
            Acornima.Operator.Addition => new Value.Number(x + y),
            Acornima.Operator.Subtraction => new Value.Number(x - y),
            Acornima.Operator.Multiplication => new Value.Number(x * y),
            Acornima.Operator.Division => new Value.Number(x / y),
            Acornima.Operator.Remainder => new Value.Number(x % y),
            Acornima.Operator.Exponentiation => new Value.Number(Math.Pow(x, y)),

            // Comparison between two values that are both NUMBERS, which is where the coercion
            // that makes relational operators treacherous does not arise - a side that is null,
            // undefined or an object fails the AsNumber guard above and never reaches here, so
            // `null >= 0` stays refused rather than being answered wrongly. The same table already
            // existed in Reader.Continues for loop tests only, which is how a counted loop was
            // unrolled while `if (RACE_SECONDS > 0)` over the same arithmetic went unread, and
            // `T > 1 ? (T - 1) * PERIOD : 0` resolved to nothing.
            Acornima.Operator.LessThan => new Value.Number(x < y ? 1 : 0),
            Acornima.Operator.LessThanOrEqual => new Value.Number(x <= y ? 1 : 0),
            Acornima.Operator.GreaterThan => new Value.Number(x > y ? 1 : 0),
            Acornima.Operator.GreaterThanOrEqual => new Value.Number(x >= y ? 1 : 0),

            // Equality between two numbers. The null cases are decided above, before the guard.
            Acornima.Operator.Equality or Acornima.Operator.StrictEquality
                => new Value.Number(x == y ? 1 : 0),
            Acornima.Operator.Inequality or Acornima.Operator.StrictInequality
                => new Value.Number(x != y ? 1 : 0),

            _ => new Value.Unknown($"operator {node.Operator}"),
        };
    }

    /// <summary>Whether an operator is one of the four equalities, and whether it is the strict
    /// kind. Null for everything else.</summary>
    private static bool? Equality(Acornima.Operator op) => op switch
    {
        Acornima.Operator.Equality or Acornima.Operator.Inequality => false,
        Acornima.Operator.StrictEquality or Acornima.Operator.StrictInequality => true,
        _ => null,
    };

    /// <summary>
    /// Whether two values are equal, for a comparison where at least one side is null or
    /// undefined. Null when the answer is not knowable.
    ///
    /// <para>ECMA's abstract equality says null and undefined equal each other and nothing else,
    /// which makes <c>x == null</c> the idiomatic "is this unset" test and makes it decidable the
    /// moment either side is known to be one of them. Strict equality does not coerce, so it
    /// additionally needs to know WHICH of the two - hence the discriminator on
    /// <see cref="Value.Nothing"/>.</para>
    ///
    /// <para>The one case that stays unknowable: a side this compiler could not resolve. It might
    /// be null and it might not, and nothing about the comparison narrows it.</para>
    /// </summary>
    private static bool? Same(Value left, Value right, bool strict) => (left, right) switch
    {
        (Value.Nothing a, Value.Nothing b) => !strict || a.IsNull == b.IsNull,

        // An unresolvable value could itself be null, so neither answer is available.
        (Value.Unknown, _) or (_, Value.Unknown) => null,

        // One side is null or undefined and the other is a value that is neither, which under both
        // equalities is false.
        _ => false,
    };

    private static Value Conditional(ConditionalExpression node, Scope scope)
    {
        var test = Of(node.Test, scope);
        if (test.AsNumber is not { } decided)
            return new Value.Unknown("a ternary whose test is not known");

        return Of(decided != 0 ? node.Consequent : node.Alternate, scope);
    }

    private static Value Array(ArrayExpression node, Scope scope)
    {
        var items = new List<Value>(node.Elements.Count);
        foreach (var element in node.Elements) items.Add(Of(element, scope));
        return new Value.List(items);
    }

    private static Value Object(ObjectExpression node, Scope scope)
    {
        var map = new Dictionary<string, Value>();

        foreach (var property in node.Properties)
        {
            if (property is not Property { Computed: false } p) return new Value.Unknown("a spread or computed key");
            if (Key(p.Key) is not { } name) return new Value.Unknown("a key that is not a plain name");
            map[name] = Of(p.Value, scope);
        }

        return new Value.Bag(map);
    }

    private static Value Template(TemplateLiteral node, Scope scope)
    {
        var text = new System.Text.StringBuilder();

        for (var i = 0; i < node.Quasis.Count; i++)
        {
            text.Append(node.Quasis[i].Value.Cooked);
            if (i >= node.Expressions.Count) continue;

            if (Of(node.Expressions[i], scope).AsText is not { } piece)
                return new Value.Unknown("a template string with a value that is not known");

            text.Append(piece);
        }

        return new Value.Text(text.ToString());
    }

    private static Value Member(MemberExpression node, Scope scope)
    {
        var target = Of(node.Object, scope);

        var key = node.Computed
            ? Of(node.Property, scope).AsText
            : Key(node.Property);

        if (key is null) return new Value.Unknown("a property name that is not known");

        return (target, key) switch
        {
            (Value.Bag bag, _) when bag.Of.TryGetValue(key, out var found) => found,
            (Value.List list, "length") => new Value.Number(list.Of.Count),
            (Value.List list, _) when int.TryParse(key, out var index)
                => index >= 0 && index < list.Of.Count
                    ? list.Of[index]
                    : new Value.Unknown($"index {index} is past the end"),
            _ => new Value.Unknown($"'.{key}' on something this compiler does not model"),
        };
    }

    /// <summary>
    /// The only calls that resolve: DOM lookups, which yield a selector, and a handful of
    /// arithmetic helpers.
    ///
    /// <para>A DOM lookup is resolved to the SELECTOR TEXT, never to an element. That is what
    /// makes this static: the compiler never looks at the document to answer it, so it cannot
    /// matter what the document contains, and a selector that matches nothing produces a rule that
    /// matches nothing rather than a wrong guess.</para>
    /// </summary>
    /// <summary>How deep a helper may call another before this gives up. A guard, not a budget:
    /// nothing in this corpus nests more than twice, and a helper that calls itself would
    /// otherwise not return.</summary>
    private const int MostNesting = 8;

    [ThreadStatic] private static int _nesting;

    private static Value Call(CallExpression node, Scope scope)
    {
        // A helper that only computes. `function clamp(v, lo, hi) { return Math.min(hi,
        // Math.max(lo, v)); }` is arithmetic with a name, and this corpus keeps its timings in
        // one: `var IN = clamp(DUR * 0.08, 0.3, 0.8)`. Refusing it made every tween placed at IN
        // unknowable, and the clock could not be advanced past them.
        //
        // This is not the statement walker's rule about stepping into a body once. That rule is
        // about following a function for what it does to a timeline, where reading it twice would
        // emit the motion twice. A function evaluated for its RETURN VALUE has no such hazard, so
        // the number of call sites does not matter.
        if (node.Callee is Identifier called
            && scope.Lookup(called.Name) is Value.Routine routine
            && Returned(routine.Body) is { } expression)
        {
            if (_nesting >= MostNesting)
                return new Value.Unknown($"a helper nested more than {MostNesting} deep");

            var inner = new Scope(scope);
            for (var i = 0; i < routine.Parameters.Count; i++)
                if (routine.Parameters[i] is Identifier parameter)
                    inner.Bind(parameter.Name, i < node.Arguments.Count
                        ? Of(node.Arguments[i], scope)
                        : new Value.Unknown("a parameter with no argument"));

            _nesting++;
            try { return Of(expression, inner); }
            finally { _nesting--; }
        }

        if (node.Callee is not MemberExpression { Property: Identifier name } callee)
            return new Value.Unknown("a call this compiler cannot resolve");

        var arguments = node.Arguments.Select(a => Of(a, scope)).ToArray();
        var first = arguments.Length > 0 ? arguments[0] : new Value.Unknown("no argument");

        switch (name.Name)
        {
            case "querySelector" or "querySelectorAll":
            {
                if (first.AsText is not { } selector)
                    return new Value.Unknown("a query whose selector is not known");

                // Scoped to whatever it was called on, when that is itself a known selector.
                var root = Of(callee.Object, scope);
                return root is Value.Selector { Css: var outer }
                    ? new Value.Selector(outer + " " + selector)
                    : new Value.Selector(selector);
            }

            case "getElementById":
                return first.AsText is { } id
                    ? new Value.Selector("#" + id)
                    : new Value.Unknown("getElementById with an unknown id");

            case "toArray":
                return first is Value.Selector or Value.Text && first.AsText is { } many
                    ? new Value.Selector(many)
                    : first;

            case "abs" or "min" or "max" or "round" or "floor" or "ceil" or "sqrt" or "pow":
            {
                var numbers = arguments.Select(a => a.AsNumber).ToArray();
                if (numbers.Any(n => n is null)) return new Value.Unknown($"Math.{name.Name} of an unknown value");

                var values = numbers.Select(n => n!.Value).ToArray();
                return new Value.Number(name.Name switch
                {
                    "abs" => Math.Abs(values[0]),
                    "min" => values.Min(),
                    "max" => values.Max(),
                    "round" => Math.Round(values[0], MidpointRounding.AwayFromZero),
                    "floor" => Math.Floor(values[0]),
                    "ceil" => Math.Ceiling(values[0]),
                    "sqrt" => Math.Sqrt(values[0]),
                    _ => Math.Pow(values[0], values[1]),
                });
            }

            default:
                return new Value.Unknown($"a call to {name.Name}()");
        }
    }

    public static string? Key(Node node) => node switch
    {
        Identifier id => id.Name,
        StringLiteral s => s.Value,
        NumericLiteral n => n.Value.ToString("R", CultureInfo.InvariantCulture),
        _ => null,
    };
}
