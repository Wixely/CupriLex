using Acornima.Ast;

namespace CupriLex.Compiler;

/// <summary>A number with the unit it was written in. <c>x: 100</c> is 100 pixels and
/// <c>xPercent: 100</c> is 100 percent, and the difference has to survive as far as the
/// stylesheet.</summary>
internal readonly record struct Amount(double Number, string Unit);

/// <summary>One tween as read, before its starting values are known. A <c>to</c> begins from
/// wherever the element already is, which is not knowable until every tween is in time order.</summary>
/// <param name="Repeat">How many EXTRA times the tween runs after the first, GSAP's own meaning:
/// <c>repeat: 1</c> plays twice. A finite repeat is the same stops laid down again along one
/// timeline, which the engine's single animation per element can hold; an infinite one cannot and
/// is refused.</param>
/// <param name="Yoyo">Whether every other pass runs backwards. It changes the shape of a repeat
/// rather than the fact of it: with yoyo the value walks back the way it came, without it the
/// value snaps to the start and runs again.</param>
internal sealed record RawTween(
    string Selector,
    string Verb,
    double Start,
    double Duration,
    string Ease,
    IReadOnlyDictionary<string, Amount> To,
    IReadOnlyDictionary<string, Amount>? From,
    int Line,
    int Repeat = 0,
    bool Yoyo = false)
{
    /// <summary>How long the tween occupies, repeats included. What the clock must advance past
    /// and what the overlap check has to compare against: a tween that repeats four times and is
    /// treated as one pass leaves everything after it early, and lets a later tween of the same
    /// property through while this one is still running.</summary>
    public double Span => Duration * (Repeat + 1);
}

/// <summary>A timeline being built: where the next tween lands, and what the labels mean.</summary>
internal sealed class Clock
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
internal sealed class Reader
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

            // A loop whose extent is written in the document is straight-line code with the
            // repetition spelled out, and it is read as exactly that. One whose extent is not
            // knowable falls through to Unreached() as before.
            case ForStatement loop:
                Unroll(loop, scope);
                break;

            case ForOfStatement each:
                UnrollOf(each, scope);
                break;

            // A branch whose test the evaluator can decide is straight-line code with one side
            // written down. These blocks guard whole features behind a literal config flag -
            // `if (CONFIG.animationIn)` over an object that says `animationIn: true` three
            // screens up - and leaving the branch unread refused the motion inside it. A test
            // that is NOT decidable leaves both sides unread, exactly as before, and Unreached()
            // names what was in them.
            case IfStatement branch:
                if (Evaluator.Of(branch.Test, scope).Truth is { } taken
                    && (taken ? branch.Consequent : branch.Alternate) is { } followed)
                    Statement(followed, new Scope(scope));
                break;

            default:
                // Not followed. Whatever motion is inside gets named by Unreached().
                break;
        }
    }

    // ---- loops --------------------------------------------------------------------------------

    /// <summary>
    /// How many iterations this compiler will write out for one loop.
    ///
    /// <para>The corpus's largest resolvable loop runs 62 times; 256 leaves room without letting a
    /// mistake in the bound arithmetic emit a hundred thousand keyframes. Exceeding it is reported
    /// rather than truncated, because half a loop is motion that stops for no reason.</para>
    /// </summary>
    private const int MostIterations = 256;

    /// <summary>How many extra passes of a repeating tween this will write out. Each pass is a
    /// full set of stops, and an eased one is eight of them, so a large repeat is a large rule
    /// for a composition nobody can see repeating that often. The corpus's largest is 12.</summary>
    private const int MostRepeats = 32;

    /// <summary>
    /// <c>for (let i = 0; i &lt; 6; i++)</c>, written out.
    ///
    /// <para>1,110 of the corpus's GSAP calls sit inside a loop and 622 of them iterate something
    /// the document states: a literal array, or a bound that is a number. Refusing those was the
    /// largest gap left in the compiler, and it was never a limit of static analysis - the count
    /// is written in the source.</para>
    ///
    /// <para>This is still static analysis and not execution. The start, the bound and the step
    /// are each resolved by the same evaluator that resolves a duration, and anything it cannot
    /// resolve leaves the loop unread exactly as before. Nothing runs; the repetition is unfolded
    /// and the ordinary walker then reads what is inside.</para>
    /// </summary>
    private void Unroll(ForStatement loop, Scope scope)
    {
        if (loop.Init is not VariableDeclaration { Declarations.Count: 1 } declaration
            || declaration.Declarations[0] is not { Id: Identifier counter } start
            || Evaluator.Of(start.Init, scope).AsNumber is not { } from)
            return;

        if (loop.Test is not NonLogicalBinaryExpression test
            || test.Left is not Identifier tested || tested.Name != counter.Name
            || Evaluator.Of(test.Right, scope).AsNumber is not { } bound)
            return;

        if (Stride(loop.Update, counter.Name, scope) is not { } stride || stride == 0) return;

        var inner = new Scope(scope);
        var iterations = 0;

        for (var value = from; Continues(test.Operator, value, bound); value += stride)
        {
            if (++iterations > MostIterations)
            {
                _refusals.Add(new Refusal(
                    "a loop that runs more than " + MostIterations + " times, which this compiler "
                    + "will not write out - the motion inside it is not carried", Line(loop)));
                return;
            }

            inner.Bind(counter.Name, new Value.Number(value));
            Statement(loop.Body, inner);
        }
    }

    /// <summary><c>for (const row of ROWS)</c> over a list the document states.</summary>
    private void UnrollOf(ForOfStatement each, Scope scope)
    {
        if (Evaluator.Of(each.Right, scope) is not Value.List list) return;
        if (Bound(each.Left) is not { } name) return;

        if (list.Of.Count > MostIterations)
        {
            _refusals.Add(new Refusal(
                "a for-of over " + list.Of.Count + " items, more than the " + MostIterations
                + " this compiler will write out - the motion inside it is not carried", Line(each)));
            return;
        }

        var inner = new Scope(scope);

        foreach (var item in list.Of)
        {
            inner.Bind(name, item);
            Statement(each.Body, inner);
        }
    }

    /// <summary>
    /// <c>ROWS.forEach((row, i) =&gt; ...)</c>, the commonest of all of them: 421 of the corpus's
    /// loop-bodied GSAP calls are written this way.
    ///
    /// <para>The callback is inlined once per element with its parameters bound, which is what
    /// <see cref="Step"/> already does for a function called by name. The third parameter - the
    /// array itself - is bound too, because a body that indexes back into it is ordinary.</para>
    /// </summary>
    private bool Each(CallExpression call, Scope scope)
    {
        if (call.Callee is not MemberExpression { Property: Identifier { Name: "forEach" } } member)
            return false;

        if (call.Arguments.Count == 0 || Routine(call.Arguments[0]) is not Value.Routine body)
            return false;

        if (Evaluator.Of(member.Object, scope) is not Value.List list) return false;

        if (list.Of.Count > MostIterations)
        {
            _refusals.Add(new Refusal(
                "a forEach over " + list.Of.Count + " items, more than the " + MostIterations
                + " this compiler will write out - the motion inside it is not carried", Line(call)));
            return true;
        }

        _handled.Add(call);

        for (var index = 0; index < list.Of.Count; index++)
        {
            var inner = new Scope(scope);

            if (body.Parameters.Count > 0 && body.Parameters[0] is Identifier item)
                inner.Bind(item.Name, list.Of[index]);

            if (body.Parameters.Count > 1 && body.Parameters[1] is Identifier at)
                inner.Bind(at.Name, new Value.Number(index));

            if (body.Parameters.Count > 2 && body.Parameters[2] is Identifier whole)
                inner.Bind(whole.Name, list);

            switch (body.Body)
            {
                case FunctionBody statements: Statements(statements.Body, inner); break;
                case Expression only: Effect(only, inner); break;
            }
        }

        return true;
    }

    /// <summary>The name a <c>for-of</c> binds, whether it declares one or assigns to one.</summary>
    private static string? Bound(Node left) => left switch
    {
        VariableDeclaration { Declarations.Count: 1 } d
            when d.Declarations[0].Id is Identifier id => id.Name,
        Identifier id => id.Name,
        _ => null,
    };

    /// <summary>How far one iteration moves the counter, or null if that is not knowable.</summary>
    private static double? Stride(Node? update, string counter, Scope scope) => update switch
    {
        UpdateExpression { Argument: Identifier a } u when a.Name == counter =>
            u.Operator == Acornima.Operator.Increment ? 1 : -1,

        AssignmentExpression { Left: Identifier b } assign when b.Name == counter =>
            Evaluator.Of(assign.Right, scope).AsNumber is { } by
                ? assign.Operator switch
                {
                    Acornima.Operator.AdditionAssignment => by,
                    Acornima.Operator.SubtractionAssignment => -by,
                    _ => (double?)null,
                }
                : null,

        _ => null,
    };

    private static bool Continues(Acornima.Operator comparison, double value, double bound) =>
        comparison switch
        {
            Acornima.Operator.LessThan => value < bound,
            Acornima.Operator.LessThanOrEqual => value <= bound,
            Acornima.Operator.GreaterThan => value > bound,
            Acornima.Operator.GreaterThanOrEqual => value >= bound,
            _ => false,
        };

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

            // Before the general call, because a forEach IS a call and the general path would
            // read it as a timeline verb it does not know and leave the body unvisited.
            case CallExpression each when Each(each, scope):
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

    /// <summary>
    /// One tween, read and placed.
    ///
    /// <para>The order here is load-bearing and it is not the obvious one. Everything needed to
    /// occupy the right SLICE OF TIME is worked out first - duration, delay, position - and the
    /// clock is advanced before anything that might refuse. A refusal must never move the clock:
    /// a <c>.to()</c> of <c>backgroundColor</c> cannot be carried, but in the browser it still
    /// takes its two seconds, and dropping them puts every un-positioned tween after it two
    /// seconds early. That failure renders perfectly and at the wrong moment, which is the hardest
    /// kind to see.</para>
    ///
    /// <para>Found by sweeping the engine's clock against the browser's on <c>transitions-grid</c>,
    /// where shifting the engine forward 0.75s recovered 5% of content. The first version returned
    /// from five different places before reaching the cursor.</para>
    /// </summary>
    private void Tween(CallExpression call, string verb, Clock? clock, Scope scope)
    {
        var line = Line(call);
        var arguments = call.Arguments;

        if (arguments.Count == 0)
        {
            _refusals.Add(new Refusal($".{verb}() with no arguments", line));
            return;
        }

        var valuesIndex = verb == "fromTo" ? 2 : 1;
        var positionIndex = verb == "fromTo" ? 3 : 2;

        // ---- how long it lasts, before anything can refuse ------------------------------------

        var defaults = clock?.Defaults;
        var values = arguments.Count > valuesIndex
            ? Evaluator.Of(arguments[valuesIndex], scope) as Value.Bag
            : null;

        // A values object that cannot be read takes its duration with it, and without a duration
        // the clock cannot be advanced honestly. Everything after this point on this timeline is
        // then suspect, which is worth saying out loud rather than quietly getting wrong.
        if (values is null)
        {
            _refusals.Add(new Refusal(
                $"a .{verb}() whose values are not known, so its duration is not known either - "
                + "the clock cannot be advanced past it and everything after it on this timeline "
                + "may be early", line));
            return;
        }

        var duration = verb == "set" ? 0 : Setting(values, defaults, "duration") ?? 0.5;
        var delay = Setting(values, defaults, "delay") ?? 0;

        // GSAP's own default, not linear. A tween written without an ease still accelerates, and
        // treating it as linear would put every un-eased element in the wrong place mid-tween.
        var ease = Text(values, defaults, "ease") ?? "power1.out";

        // Read here, before the clock moves, because a repeating tween occupies every pass and
        // anything appended after it would otherwise run early. The REFUSALS wait until the
        // target is resolved, so they can name the selector.
        var repeats = 0;
        var yoyo = false;
        string? repeatRefusal = null;

        if (Setting(values, defaults, "repeat") is { } asked && asked != 0)
        {
            // A finite repeat is carried by writing the stops again, once per pass. An INFINITE
            // one cannot be: `repeat: -1` has no last pass to write, and a rule that stopped
            // after some arbitrary number of them would be a composition that quietly ends.
            // Every repeat in this corpus is finite - 1, 2, 3, 5, 7, 11, 12, and 24 of the 37
            // are `repeat: 1` - so the infinite case is refused on principle, not experience.
            if (asked < 0)
                repeatRefusal = $"repeat: {asked} never ends, and a @keyframes has to";
            else if (asked != Math.Floor(asked) || asked > MostRepeats)
                repeatRefusal = $"repeat: {asked} is more than this writes out";
            else
            {
                repeats = (int)asked;
                yoyo = Setting(values, defaults, "yoyo") is { } flag && flag != 0;

                // A gap between passes needs stops that hold the end value for its length.
                // Nothing in the corpus writes one, so it is named rather than guessed at - and
                // the repeat is still carried, continuous, which is closer than not repeating.
                if (Setting(values, defaults, "repeatDelay") is { } gap && gap != 0)
                    repeatRefusal = $"a repeatDelay of {gap}s, which this writes out as a "
                                    + "continuous repeat with no gap";
            }
        }

        // ---- where it lands --------------------------------------------------------------------

        double start;
        if (clock is null)
        {
            // A bare gsap.to() has no timeline to append to, so it starts at its delay.
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
                    $"a .{verb}() placed at `{Snippet(arguments[positionIndex])}`, which is not "
                    + "knowable, so the clock cannot be advanced past it and everything after it "
                    + "on this timeline may be early", line));
                return;
            }

            start = position.Value + delay;
        }

        // The clock moves HERE, for every tween whose extent is known, carried or not.
        if (clock is not null)
        {
            clock.LastStart = start;
            clock.Cursor = Math.Max(clock.Cursor, start + duration * (repeats + 1));
        }

        // ---- and only now, what it is worth ----------------------------------------------------

        var targetValue = Evaluator.Of(arguments[0], scope);
        var selectors = Targets(targetValue);

        if (selectors.Count == 0)
        {
            // Three different failures, and the report must not call them one thing. A tween of a
            // plain object is GSAP driving numbers for an onUpdate to paint with, and there is no
            // onUpdate here. A LIST that did not resolve is a target this could almost read - it
            // used to be reported as a plain object, which sent the reader looking for a callback
            // that was never there.
            _refusals.Add(new Refusal(targetValue switch
            {
                Value.Bag => $"a .{verb}() of the plain object `{Snippet(arguments[0])}` - it "
                             + "animates numbers for a callback to use, and there is no JavaScript "
                             + "to run the callback",
                Value.List => $"a .{verb}() on the list `{Snippet(arguments[0])}`, where at least "
                              + "one entry could not be reduced to a CSS selector",
                _ => $"a .{verb}() on `{Snippet(arguments[0])}`, which could not be reduced to a "
                     + "CSS selector",
            }, line));
            return;
        }

        // For the messages below, which are about the tween rather than about one of its targets.
        var selector = selectors.Count == 1 ? selectors[0] : string.Join(", ", selectors);

        Value.Bag? fromValues = null;
        if (verb == "fromTo")
        {
            if (Evaluator.Of(arguments[1], scope) is not Value.Bag opening)
            {
                _refusals.Add(new Refusal(
                    $"a .fromTo() on '{selector}' whose start values are not known", line));
                return;
            }
            fromValues = opening;
        }

        if (Setting(values, defaults, "stagger") is { } stagger && stagger != 0)
            _refusals.Add(new Refusal(
                $"a stagger of {stagger}s on '{selector}': it needs one animation per element and "
                + "the engine allows one per element in total", line));

        if (repeatRefusal is not null)
            _refusals.Add(new Refusal($"{repeatRefusal} (on '{selector}')", line));

        var (to, refusedTo) = Amounts(values, selector, verb, line);
        var from = fromValues is null ? null : Amounts(fromValues, selector, verb, line).Amounts;
        _refusals.AddRange(refusedTo);

        if (to.Count == 0 && (from is null || from.Count == 0)) return;

        // One tween per target. The engine gives every element its own animation anyway, so a
        // list is not a special kind of tween - it is the same tween written once and meant
        // several times.
        foreach (var target in selectors)
            _tweens.Add(new RawTween(
                target, verb, (clock?.Offset ?? 0) + start, duration, ease, to, from, line,
                repeats, yoyo));
    }

    /// <summary>
    /// The elements a tween targets, as selectors, or empty when it cannot be reduced to any.
    ///
    /// <para>GSAP takes an array of targets as readily as one, and this corpus leans on it: a
    /// carousel writes <c>tl.set(["#bg-" + n, "#slide-" + n], …)</c> to swap a background and its
    /// slide together. 60 of those across 15 blocks were refused as "plain objects", which was
    /// both a lost tween and a misleading reason.</para>
    ///
    /// <para><b>Every entry must resolve or none does.</b> Carrying the half of a list that
    /// happened to be readable is motion applied to some elements and silently not to others,
    /// which is the failure this compiler exists to avoid. Duplicates are collapsed: the same
    /// element twice is one animation, and emitting it twice would trip the overlap refusal.</para>
    /// </summary>
    private static IReadOnlyList<string> Targets(Value value)
    {
        if (Selector(value) is { } single) return [single];
        if (value is not Value.List list || list.Of.Count == 0) return [];

        var all = new List<string>(list.Of.Count);

        foreach (var item in list.Of)
        {
            if (Selector(item) is not { } css) return [];
            if (!all.Contains(css, StringComparer.Ordinal)) all.Add(css);
        }

        return all;
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

            // clip-path is a SHAPE, not a number, and every other value here is a number with
            // a unit. See Clip: the two shapes this corpus writes are numbers once taken apart,
            // and 55 of the corpus's 57 clip-path values are one of them. The remaining two are
            // circle(), which has no decomposition that interpolates into the others, and are
            // refused with the shape named.
            if (name == "clipPath")
            {
                if (!Clip.Read(value.AsText ?? string.Empty, amounts))
                    refused.Add(new Refusal(
                        $"'clipPath' on '{selector}': inset(), polygon() and none reduce to numbers "
                        + "this can interpolate, and nothing else does, so a tween of "
                        + $"`{value.AsText ?? "a value that is not a string"}` would run and change "
                        + "nothing", line));
                continue;
            }

            if (!Properties.TryMap(name, out _, out var unit))
            {
                // The list grows as the engine learns: width, height, opacity and transform were
                // all of it until 0.35.0 added clip-path, the 3D rotations and the svg stroke
                // properties. Naming the set here rather than saying "unsupported" is what makes
                // a refusal that has gone stale visible - see conformance/support.
                refused.Add(new Refusal(
                    $"'{name}' on '{selector}': the engine animates width, height, opacity, "
                    + "transform and the stroke dash properties, and not this, so a tween of it "
                    + "would run and change nothing", line));
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
                + "does not follow - their motion is not carried, and neither is the TIME they "
                + "occupy, so anything appended after them on the same timeline runs early", line));
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
