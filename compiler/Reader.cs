using Acornima.Ast;

namespace CupriLex.Compiler;

/// <summary>A number with the unit it was written in. <c>x: 100</c> is 100 pixels and
/// <c>xPercent: 100</c> is 100 percent, and the difference has to survive as far as the
/// stylesheet.</summary>
public readonly record struct Amount(double Number, string Unit);

/// <summary>One tween as read, before its starting values are known. A <c>to</c> begins from
/// wherever the element already is, which is not knowable until every tween is in time order.</summary>
public sealed record RawTween(
    string Selector,
    string Verb,
    double Start,
    double Duration,
    string Ease,
    IReadOnlyDictionary<string, Amount> To,
    IReadOnlyDictionary<string, Amount>? From,
    int Line);

/// <summary>A timeline being built: where the next tween lands, and what the labels mean.</summary>
public sealed class Clock
{
    public double Offset { get; init; }                 // where this timeline sits on the composition
    public double Cursor { get; set; }                  // end of the last tween: where an un-positioned one goes
    public double LastStart { get; set; }               // for "<"
    public Dictionary<string, double> Labels { get; } = [];
    public IReadOnlyDictionary<string, Value>? Defaults { get; init; }
}

/// <summary>
/// Reading a block's timelines out of its scripts.
///
/// <para>Statements are walked in the order they are written, and only the ones whose effect is
/// knowable from the syntax are followed: bindings, immediately invoked functions, and GSAP calls.
/// Anything else is stepped over, and any motion call it contained is refused BY NAME rather than
/// quietly lost - a tween that vanishes is exactly the failure this tool exists to prevent.</para>
/// </summary>
public sealed class Reader
{
    private static readonly HashSet<string> MotionVerbs = ["to", "set", "fromTo", "from"];

    private readonly List<RawTween> _tweens = [];
    private readonly List<Refusal> _refusals = [];
    private readonly HashSet<Node> _handled = [];
    private readonly HashSet<Node> _inlining = [];
    private int _scriptLine;
    private string _scriptSource = "";

    public static (IReadOnlyList<RawTween> Tweens, IReadOnlyList<Refusal> Refusals) Read(string html)
    {
        var reader = new Reader();
        var scripts = ScriptReader.Of(html);
        var scope = new Scope();

        foreach (var failure in scripts.Failed)
            reader._refusals.Add(new Refusal(
                "a script that would not parse, so none of its motion was read: " + failure.Why,
                failure.Line));

        foreach (var script in scripts.Parsed)
        {
            reader._scriptLine = script.Line;
            reader._scriptSource = script.Source;
            reader.Statements(script.Tree.Body, scope);
            reader.Unreached(script.Tree);
        }

        return (reader._tweens, reader._refusals);
    }

    // ---- statements ---------------------------------------------------------------------------

    private void Statements(IEnumerable<Node> body, Scope scope)
    {
        foreach (var statement in body) Statement(statement, scope);
    }

    private void Statement(Node node, Scope scope)
    {
        switch (node)
        {
            case VariableDeclaration declaration:
                foreach (var declarator in declaration.Declarations) Declare(declarator, scope);
                break;

            case ExpressionStatement { Expression: var expression }:
                Effect(expression, scope);
                break;

            case FunctionDeclaration { Id: Identifier named } declared:
                scope.Bind(named.Name, new Value.Routine([.. declared.Params], declared.Body));
                break;

            // An immediately invoked function runs once, here, in order - so its body is read as
            // if it were written here. Nearly every block in the corpus is one of these.
            case BlockStatement block:
                Statements(block.Body, new Scope(scope));
                break;

            default:
                // Not followed. Whatever motion is inside gets named by Unreached().
                break;
        }
    }

    private void Declare(VariableDeclarator declarator, Scope scope)
    {
        if (declarator.Id is not Identifier name) return;

        // const tl = gsap.timeline({...}) - and equally const tl = gsap.timeline().to(...).to(...),
        // which is one expression that both makes a timeline and fills it.
        if (declarator.Init is CallExpression call && Call(call, scope) is { } clock)
        {
            scope.Bind(name.Name, new Value.Timeline(clock));
            return;
        }

        scope.Bind(name.Name, Routine(declarator.Init) ?? Evaluator.Of(declarator.Init, scope));
    }

    /// <summary>A function written as a value: <c>const build = () =&gt; {}</c>.</summary>
    private static Value? Routine(Node? node) => node switch
    {
        ArrowFunctionExpression arrow => new Value.Routine([.. arrow.Params], arrow.Body),
        FunctionExpression function => new Value.Routine([.. function.Params], function.Body),
        _ => null,
    };

    /// <summary>A statement evaluated for what it DOES rather than what it is worth.</summary>
    private void Effect(Node expression, Scope scope)
    {
        switch (expression)
        {
            case CallExpression { Callee: FunctionExpression or ArrowFunctionExpression } iife:
                Invoke(iife, scope);
                break;

            case CallExpression { Callee: Identifier called } invoked
                when scope.Lookup(called.Name) is Value.Routine routine:
                Step(routine, invoked, scope);
                break;

            case CallExpression call:
                Call(call, scope);
                break;

            case AssignmentExpression { Left: Identifier name, Operator: Acornima.Operator.Assignment } assign:
                scope.Set(name.Name, Routine(assign.Right) ?? Evaluator.Of(assign.Right, scope));
                break;

            // t += 0.5 between tweens is how a third of this corpus keeps time, and treating it
            // as "reassigned, therefore unknown" refused ninety tweens over a running total that
            // is perfectly knowable: the left side is known, the right side is known, and nothing
            // between them branches.
            case AssignmentExpression { Left: Identifier name } compound:
                scope.Set(name.Name, Compound(compound, scope.Lookup(name.Name),
                    Evaluator.Of(compound.Right, scope)));
                break;

            case UpdateExpression { Argument: Identifier counted } update:
                scope.Set(counted.Name, scope.Lookup(counted.Name).AsNumber is { } n
                    ? new Value.Number(update.Operator == Acornima.Operator.Increment ? n + 1 : n - 1)
                    : new Value.Unknown("a counter whose value is not known at this point"));
                break;

            case SequenceExpression sequence:
                foreach (var part in sequence.Expressions) Effect(part, scope);
                break;

            default:
                break;
        }
    }

    /// <summary>A compound assignment folded, where both sides are known.</summary>
    private static Value Compound(AssignmentExpression node, Value current, Value operand)
    {
        if (node.Operator == Acornima.Operator.Assignment) return operand;

        if (current.AsNumber is not { } left || operand.AsNumber is not { } right)
            return new Value.Unknown(
                $"a {node.Operator} over a value that is not known at this point");

        return node.Operator switch
        {
            Acornima.Operator.AdditionAssignment => new Value.Number(left + right),
            Acornima.Operator.SubtractionAssignment => new Value.Number(left - right),
            Acornima.Operator.MultiplicationAssignment => new Value.Number(left * right),
            Acornima.Operator.DivisionAssignment => new Value.Number(left / right),
            _ => new Value.Unknown($"a {node.Operator}"),
        };
    }

    /// <summary>
    /// A call to a function declared in the same document, read as if its body were written at the
    /// call.
    ///
    /// <para>331 of the corpus's 813 declared functions are called exactly once - <c>buildIntro()</c>,
    /// <c>outro()</c>, the shape a person reaches for when a timeline gets long. Stepping into the
    /// call with its arguments bound is still static analysis: it is done at the ONE place the call
    /// appears in straight-line code, so nothing is assumed about how often it runs or with what.
    /// Calls from inside a loop or a callback are not reached at all, and are named by
    /// <see cref="Unreached"/>.</para>
    /// </summary>
    private void Step(Value.Routine routine, CallExpression call, Scope scope)
    {
        _handled.Add(call);

        // A function that calls itself, directly or round a ring, would otherwise be followed
        // until the stack ran out.
        if (!_inlining.Add(routine.Body))
        {
            _refusals.Add(new Refusal(
                "a function that calls itself, which this compiler does not unroll", Line(call)));
            return;
        }

        try
        {
            var inner = new Scope(scope);

            for (var i = 0; i < routine.Parameters.Count; i++)
            {
                if (routine.Parameters[i] is not Identifier parameter) continue;
                inner.Bind(parameter.Name, i < call.Arguments.Count
                    ? Evaluator.Of(call.Arguments[i], scope)
                    : new Value.Unknown("an argument that was not passed"));
            }

            switch (routine.Body)
            {
                case FunctionBody body: Statements(body.Body, inner); break;
                case Expression only: Effect(only, inner); break;
            }
        }
        finally
        {
            _inlining.Remove(routine.Body);
        }
    }

    private void Invoke(CallExpression iife, Scope scope)
    {
        _handled.Add(iife);
        var inner = new Scope(scope);

        var body = iife.Callee switch
        {
            FunctionExpression f => f.Body,
            ArrowFunctionExpression { Body: FunctionBody b } => b,
            _ => null,
        };

        if (body is null) return;
        Statements(body.Body, inner);
    }

    // ---- GSAP ---------------------------------------------------------------------------------

    private Clock NewClock(CallExpression call, Scope scope)
    {
        IReadOnlyDictionary<string, Value>? defaults = null;

        if (call.Arguments.Count > 0
            && Evaluator.Of(call.Arguments[0], scope) is Value.Bag options
            && options.Of.TryGetValue("defaults", out var d)
            && d is Value.Bag bag)
            defaults = bag.Of;

        return new Clock { Defaults = defaults };
    }

    /// <summary>
    /// A GSAP call: a tween, a label, a new timeline, or something to refuse.
    /// </summary>
    /// <returns>The timeline the call was made on, so that a chain - <c>tl.to(…).to(…)</c>, which
    /// is how most of this corpus is written - can be followed. Each link is a call whose receiver
    /// is the call before it, and reading only the outermost would have carried the last tween of
    /// every chain and silently dropped the rest.</returns>
    private Clock? Call(CallExpression call, Scope scope)
    {
        if (call.Callee is not MemberExpression { Property: Identifier verb } callee) return null;

        var onGsap = false;
        Clock? clock = null;

        switch (callee.Object)
        {
            case Identifier { Name: "gsap" }:
                onGsap = true;
                break;

            case CallExpression inner:
                clock = Call(inner, scope);
                onGsap = clock is null && IsGsapRoot(inner);
                break;

            default:
                if (Evaluator.Of(callee.Object, scope) is Value.Timeline held) clock = held.Of;
                break;
        }

        if (verb.Name == "timeline" && (onGsap || clock is null))
        {
            _handled.Add(call);
            return NewClock(call, scope);
        }

        // Not a timeline and not gsap: some other object that happens to have a method with the
        // same name. Left alone rather than guessed at.
        if (clock is null && !onGsap) return null;

        _handled.Add(call);

        switch (verb.Name)
        {
            case "to" or "set" or "from" or "fromTo":
                Tween(call, verb.Name, clock, scope);
                break;

            case "addLabel" when clock is not null:
                Label(call, clock, scope);
                break;

            case "add":
                _refusals.Add(new Refusal(
                    "a nested timeline or callback added with .add(), which this compiler does not "
                    + "flatten yet", Line(call)));
                break;

            case "call" or "eventCallback" or "delayedCall":
                _refusals.Add(new Refusal(
                    $".{verb.Name}() is not motion - it runs code at a time, and there is no "
                    + "JavaScript to run it with", Line(call)));
                break;

            default:
                break;      // .pause(), .play(), .duration() and friends: nothing to carry
        }

        return clock;
    }

    private static bool IsGsapRoot(CallExpression call) =>
        call.Callee is MemberExpression { Object: Identifier { Name: "gsap" } };

    private void Label(CallExpression call, Clock clock, Scope scope)
    {
        if (call.Arguments.Count == 0 || Evaluator.Of(call.Arguments[0], scope).AsText is not { } name)
        {
            _refusals.Add(new Refusal("a label whose name is not known", Line(call)));
            return;
        }

        var at = call.Arguments.Count > 1
            ? Place(Evaluator.Of(call.Arguments[1], scope), clock)
            : clock.Cursor;

        if (at is null)
        {
            _refusals.Add(new Refusal($"the label '{name}' is placed somewhere not knowable", Line(call)));
            return;
        }

        clock.Labels[name] = at.Value;
    }

    private void Tween(CallExpression call, string verb, Clock? clock, Scope scope)
    {
        var line = Line(call);
        var arguments = call.Arguments;

        if (arguments.Count == 0)
        {
            _refusals.Add(new Refusal($".{verb}() with no arguments", line));
            return;
        }

        var targetValue = Evaluator.Of(arguments[0], scope);

        if (Selector(targetValue) is not { } selector)
        {
            // A tween of a plain object is a different animal from a tween of an element that
            // could not be resolved, and the report should not call them the same thing: GSAP is
            // being used to drive numbers that an onUpdate then paints with, and there is no
            // onUpdate here.
            _refusals.Add(new Refusal(targetValue is Value.Bag or Value.List
                ? $"a .{verb}() of the plain object `{Snippet(arguments[0])}` - it animates numbers "
                  + "for a callback to use, and there is no JavaScript to run the callback"
                : $"a .{verb}() on `{Snippet(arguments[0])}`, which could not be reduced to a CSS "
                  + "selector", line));
            return;
        }

        var valuesIndex = verb == "fromTo" ? 2 : 1;
        var positionIndex = verb == "fromTo" ? 3 : 2;

        if (arguments.Count <= valuesIndex || Evaluator.Of(arguments[valuesIndex], scope) is not Value.Bag values)
        {
            _refusals.Add(new Refusal($"a .{verb}() on '{selector}' whose values are not known", line));
            return;
        }

        Value.Bag? fromValues = null;
        if (verb == "fromTo")
        {
            if (Evaluator.Of(arguments[1], scope) is not Value.Bag opening)
            {
                _refusals.Add(new Refusal($"a .fromTo() on '{selector}' whose start values are not known", line));
                return;
            }
            fromValues = opening;
        }

        var defaults = clock?.Defaults;
        var duration = verb == "set" ? 0 : Setting(values, defaults, "duration") ?? 0.5;
        var delay = Setting(values, defaults, "delay") ?? 0;
        // GSAP's own default, not linear. A tween written without an ease still accelerates, and
        // treating it as linear would put every un-eased element in the wrong place mid-tween.
        var ease = Text(values, defaults, "ease") ?? "power1.out";

        if (Setting(values, defaults, "stagger") is { } stagger && stagger != 0)
            _refusals.Add(new Refusal(
                $"a stagger of {stagger}s on '{selector}': it needs one animation per element and "
                + "the engine allows one per element in total", line));

        if (Setting(values, defaults, "repeat") is { } repeat && repeat != 0)
            _refusals.Add(new Refusal($"repeat: {repeat} on '{selector}'", line));

        // Where it lands. A bare gsap.to() has no timeline to append to, so it starts at its delay.
        double start;
        if (clock is null)
        {
            start = delay;
        }
        else
        {
            var position = arguments.Count > positionIndex
                ? Place(Evaluator.Of(arguments[positionIndex], scope), clock)
                : clock.Cursor;

            if (position is null)
            {
                _refusals.Add(new Refusal(
                    $"a .{verb}() on '{selector}' placed at "
                    + $"`{Snippet(arguments[positionIndex])}`, which is not knowable", line));
                return;
            }

            start = position.Value + delay;
        }

        var (to, refusedTo) = Amounts(values, selector, verb, line);
        var from = fromValues is null ? null : Amounts(fromValues, selector, verb, line).Amounts;
        _refusals.AddRange(refusedTo);

        if (to.Count == 0 && (from is null || from.Count == 0)) return;

        if (clock is not null)
        {
            clock.LastStart = start;
            clock.Cursor = Math.Max(clock.Cursor, start + duration);
        }

        _tweens.Add(new RawTween(
            selector, verb, (clock?.Offset ?? 0) + start, duration, ease, to, from, line));
    }

    /// <summary>GSAP's position parameter, which is five different things wearing one coat.</summary>
    private static double? Place(Value position, Clock clock)
    {
        if (position is Value.Unknown) return null;
        if (position.AsNumber is { } absolute && position is Value.Number) return absolute;

        if (position.AsText is not { } text) return null;
        text = text.Trim();

        if (text.Length == 0) return clock.Cursor;

        // "<" is the start of the previous tween, ">" is the end of it; both take an offset.
        if (text[0] is '<' or '>')
        {
            var basis = text[0] == '<' ? clock.LastStart : clock.Cursor;
            var rest = text[1..].Replace("+=", "+").Replace("-=", "-").Trim();
            if (rest.Length == 0) return basis;
            return double.TryParse(rest, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var shift)
                ? basis + shift
                : null;
        }

        // "+=1.5" and "-=0.25", relative to the end of the timeline so far.
        if (text.StartsWith("+=") || text.StartsWith("-="))
        {
            if (!double.TryParse(text[2..], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var offset)) return null;
            return text[0] == '+' ? clock.Cursor + offset : clock.Cursor - offset;
        }

        // "label", "label+=0.2"
        var split = text.IndexOfAny(['+', '-']);
        var name = (split < 0 ? text : text[..split]).Trim();
        if (!clock.Labels.TryGetValue(name, out var at)) return null;
        if (split < 0) return at;

        var tail = text[split..].Replace("+=", "+").Replace("-=", "-");
        return double.TryParse(tail, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var relative)
            ? at + relative
            : null;
    }

    /// <summary>The animatable properties of a values object, and a refusal for each one that is
    /// not carried.</summary>
    private static (Dictionary<string, Amount> Amounts, List<Refusal> Refused) Amounts(
        Value.Bag values, string selector, string verb, int line)
    {
        var amounts = new Dictionary<string, Amount>();
        var refused = new List<Refusal>();

        foreach (var (name, value) in values.Of)
        {
            if (Properties.Controls.Contains(name)) continue;

            if (!Properties.TryMap(name, out _, out var unit))
            {
                refused.Add(new Refusal(
                    $"'{name}' on '{selector}': the engine animates only width, height, opacity "
                    + "and transform, so a tween of it would run and change nothing", line));
                continue;
            }

            if (Amount(value, unit) is not { } amount)
            {
                refused.Add(new Refusal(
                    $"'{name}' on '{selector}' in a .{verb}(): its value is not a plain number", line));
                continue;
            }

            amounts[name] = amount;
        }

        return (amounts, refused);
    }

    /// <summary>A tween value, with its unit. <c>100</c>, <c>"100px"</c> and <c>"50%"</c> are all
    /// written in this corpus and mean different things.</summary>
    private static Amount? Amount(Value value, string unit)
    {
        if (value is Value.Number number) return new Amount(number.Of, unit);
        if (value.AsText is not { } text) return null;

        text = text.Trim();
        var suffix = text.EndsWith('%') ? "%" : text.EndsWith("px") ? "px"
            : text.EndsWith("deg") ? "deg" : text.EndsWith("rem") ? "rem" : "";

        var digits = suffix.Length > 0 ? text[..^suffix.Length] : text;

        return double.TryParse(digits, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? new Amount(parsed, suffix.Length > 0 ? suffix : unit)
            : null;
    }

    private static double? Setting(Value.Bag values, IReadOnlyDictionary<string, Value>? defaults, string name) =>
        values.Of.TryGetValue(name, out var own) ? own.AsNumber
        : defaults?.TryGetValue(name, out var fallback) == true ? fallback.AsNumber
        : null;

    private static string? Text(Value.Bag values, IReadOnlyDictionary<string, Value>? defaults, string name) =>
        values.Of.TryGetValue(name, out var own) ? own.AsText
        : defaults?.TryGetValue(name, out var fallback) == true ? fallback.AsText
        : null;

    /// <summary>A target reduced to a selector, or null when it cannot be.</summary>
    private static string? Selector(Value value) => value switch
    {
        Value.Selector s => s.Css,
        Value.Text t when LooksLikeSelector(t.Of) => t.Of,
        _ => null,
    };

    private static bool LooksLikeSelector(string text) =>
        text.Length > 0 && !text.Contains('{') && !text.Contains(';');

    // ---- what was missed ----------------------------------------------------------------------

    /// <summary>
    /// Every motion call the walk above did not reach, named.
    ///
    /// <para>This is the half that makes the rest trustworthy. The statement walker follows only
    /// what it can resolve, so a timeline built inside a loop or a callback is simply not visited -
    /// and without this it would leave no trace at all, which is precisely how somebody ships a
    /// video missing its transitions.</para>
    /// </summary>
    private void Unreached(Node tree)
    {
        var missed = new Dictionary<string, (int Count, int Line)>();

        foreach (var node in tree.Descendants())
        {
            if (node is not CallExpression call || _handled.Contains(call)) continue;
            if (call.Callee is not MemberExpression { Property: Identifier verb }) continue;
            if (!MotionVerbs.Contains(verb.Name)) continue;

            var line = Line(call);
            var key = verb.Name;
            missed[key] = missed.TryGetValue(key, out var seen)
                ? (seen.Count + 1, seen.Line)
                : (1, line);
        }

        foreach (var (verb, (count, line)) in missed)
            _refusals.Add(new Refusal(
                $"{count} .{verb}() call(s) inside a loop, a callback or a function this compiler "
                + "does not follow - their motion is not carried", line));
    }

    /// <summary>The line in the BLOCK, not in the script: the script's own offset plus the lines
    /// before this node. Counted from the source rather than read off the node, because every
    /// refusal in the first run came out on the line the script opened on.</summary>
    private int Line(Node node)
    {
        var offset = Math.Clamp(node.Range.Start, 0, _scriptSource.Length);
        return _scriptLine + _scriptSource.AsSpan(0, offset).Count('\n');
    }

    /// <summary>The expression as the author wrote it. A refusal that quotes the source is worth
    /// several that describe it: "placed at 'startAt + gap'" says immediately which binding to
    /// look at, and "a time that is not knowable" does not.</summary>
    private string Snippet(Node node)
    {
        var (from, to) = (node.Range.Start, node.Range.End);
        if (from < 0 || to > _scriptSource.Length || to <= from) return node.Type.ToString();

        var text = System.Text.RegularExpressions.Regex.Replace(
            _scriptSource[from..to], @"\s+", " ").Trim();
        return text.Length <= 60 ? text : text[..57] + "...";
    }
}
