using System.Globalization;
using System.Text;

namespace CupriLex.Compiler;

/// <summary>A property's value at an instant, after easing has been sampled out of it.</summary>
internal readonly record struct Stop(double Time, double Number, string Unit);

/// <summary>The stylesheet a block's motion becomes, and everything that did not come with it.</summary>
/// <param name="Css">Ready to drop into a <c>&lt;style&gt;</c>.</param>
/// <param name="Seconds">How long the motion runs. Not always the block's declared duration.</param>
/// <param name="Elements">How many selectors ended up with an animation, for the report.</param>
/// <param name="Selectors">Every selector given an animation rule, held apart from
/// <paramref name="Elements"/> so the translator can check them all against the document it is
/// about to emit: a target is resolved from the source alone and never by looking at the document,
/// so nothing upstream of here knows whether one matches anything.</param>
/// <param name="Held">How many of those animations hold one value for their whole length. They are
/// emitted because their end state is load-bearing, and they are not motion.</param>
public sealed record Sheet(
    string Css, double Seconds, int Elements, IReadOnlyList<Refusal> Refusals,
    IReadOnlyList<string> Selectors, int Held);

/// <summary>
/// Tweens into one <c>@keyframes</c> per element.
///
/// <para>One per element because the engine runs exactly one animation per element and a
/// comma-separated list runs <em>neither</em>, silently. So everything that ever touches an
/// element - five tweens on three properties at four different times - has to become a single
/// rule, which means resolving every tween to absolute stops first and writing the element's whole
/// state at each of them.</para>
/// </summary>
internal static class Emit
{
    /// <summary>A gap small enough to read as instant and large enough to survive being rounded
    /// into a percentage. A <c>set</c> is a step, and a step in <c>@keyframes</c> is two stops a
    /// hair apart.</summary>
    private const double Instant = 0.001;

    /// <summary>What a property is worth before anything has touched it. Transform components have
    /// an identity; opacity is 1 because that is what an untouched element has. Width and height
    /// have no such answer - they are whatever the stylesheet says - so a tween that would need to
    /// know is refused rather than guessed at.</summary>
    private static double? Resting(string component) => component switch
    {
        "width" or "height" => null,
        "opacity" => 1,
        // Both are 0 in CSS before anything touches them, and a dash array of 0 is an undashed
        // stroke - so a path that is dashed and wound on by a script starts where it would have
        // started anyway. That is what makes the draw-on idiom carryable without reading the
        // document: the resting state is the solid line.
        "stroke-dashoffset" or "stroke-dasharray" => 0,
        // An element nobody has clipped is clipped to its own box, which is inset(0 0 0 0) and
        // the same picture as `none`. So a tween TO a clip starts from unclipped, which is what
        // a wipe-in means.
        "clipTop" or "clipRight" or "clipBottom" or "clipLeft" => 0,
        // A polygon point has no resting value: an element with no clip has no vertices, and
        // inventing one at the origin would collapse the shape to a corner. A tween that needs
        // to know where a point started is refused, the way a tween of a size is.
        _ when Properties.IsPolygonPoint(component) => null,
        // Every filter function has an identity - no blur, full brightness - so a tween TO a
        // filter never has to be refused for want of a start. That is what makes a blur-in
        // carryable without reading the document.
        _ when Properties.IsFilter(component) => Filter.Resting(component),
        // An unpositioned element paints in document order, which among siblings is what
        // `z-index: 0` means. A tween TO a stacking order therefore starts from the bottom of the
        // stack, which is what bringing a card to the front means.
        "z-index" => 0,
        _ => Properties.Identity(component),
    };

    public static Sheet Sheet(IReadOnlyList<RawTween> tweens, IReadOnlyList<Refusal> carried,
        Authored? authored = null)
    {
        var refusals = carried.ToList();
        authored ??= Authored.None;
        var tracks = Thread(tweens, refusals, authored);
        Carry(tracks, authored);

        if (tracks.Count == 0) return new Sheet("", 0, 0, refusals, [], 0);

        var seconds = tracks.Values
            .SelectMany(byProperty => byProperty.Values)
            .SelectMany(stops => stops)
            .Select(s => s.Time)
            .DefaultIfEmpty(0)
            .Max();

        var css = new StringBuilder();
        var animations = new StringBuilder();
        var animated = new List<string>();   // selectors whose values actually change
        var emitted = new List<string>();    // every selector given an animation rule
        var index = 0;

        foreach (var (selector, properties) in tracks)
        {
            // Measured on 0.37.0: `stroke-dasharray` inside a @keyframes is ignored, and an
            // undashed stroke has nothing for `stroke-dashoffset` to wind, so the whole draw-on
            // renders as a finished line at every instant. The same declaration in an ordinary
            // rule works, and then the animated offset draws the path on exactly as authored
            // (0 -> 3000 -> 6000 ink across a 2s line). So a dash array that never varies - which
            // is every one in this corpus, since the script sets it once from the path's own
            // length - is written beside the animation instead of inside it.
            var dash = Hoist(properties, "stroke-dasharray");

            // A filter function that never leaves its identity contributes nothing but a layer.
            // `filter: none` decomposes to every function at its identity - which it has to, so a
            // tween TO a blur has a zero to travel from - and without this a blur-out wrote
            // `blur(0px) brightness(1) saturate(1) contrast(1) grayscale(0) opacity(1)` at every
            // stop. Correct, and five no-op functions wide.
            foreach (var component in Properties.FilterOrder)
            {
                if (!properties.TryGetValue(component, out var stops)) continue;
                if (Varies(stops)) continue;
                if (Filter.Resting(component) is not { } identity) continue;
                if (stops.Count > 0 && Math.Abs(stops[0].Number - identity) > 1e-9) continue;

                properties.Remove(component);
            }

            var moving = properties.Values.Any(stops => stops.Any(s => s.Time > 0));

            if (properties.Count == 0)
            {
                // The dash array was all there was.
                animations.Append($"{selector} {{ {dash}}}\n");
                continue;
            }

            if (!moving)
            {
                // Nothing here moves: everything was set before the first frame. A static
                // declaration says the same thing without spending the element's one animation.
                animations.Append(Rule(selector, properties, 0, dash));
                continue;
            }

            // Stops at several times and the same value at every one of them: the tween ended
            // where the compiler believed it started. That nearly always means the element's own
            // stylesheet gives it a starting transform or opacity, which GSAP reads from the
            // computed style and a compiler that never runs the document cannot.
            //
            // It is emitted anyway, and that is not the obvious call. It was removed first, on the
            // grounds that a no-op spends the element's one animation and counts as carried motion
            // in every report. The corpus disagreed immediately: flowchart-vertical fell from 97.9%
            // to 0.9%. These elements are authored hidden - opacity: 0 in the stylesheet, revealed
            // by the script - so the flat animation is wrong about the MOTION and right about the
            // END STATE, and with `both` it holds the element visible for the whole composition.
            // Removing it left every one of them invisible.
            //
            // So: emitted, reported, and not counted as motion. All three are needed.
            var still = !properties.Values.Any(Varies);

            if (still)
                refusals.Add(new Refusal(
                    $"'{selector}' is held at its end state rather than animated: every stop has "
                    + "the same value, because the tween ends where the compiler assumed it began. "
                    + "The element's own stylesheet is setting a start that cannot be read without "
                    + "running the document."));

            var name = "cuprilex-" + ++index;
            css.Append(Keyframes(name, properties, seconds));
            animations.Append(
                $"{selector} {{ {dash}animation: {name} {Number(seconds)}s linear both; }}\n");

            emitted.Add(selector);
            if (!still) animated.Add(selector);
        }

        return new Sheet(css.Append(animations).ToString(), seconds, animated.Count,
            refusals, emitted, animated.Count == emitted.Count ? 0 : emitted.Count - animated.Count);
    }

    // ---- threading ----------------------------------------------------------------------------

    /// <summary>
    /// Every tween in time order, turned into per-property stops.
    ///
    /// <para>In time order because a <c>to</c> starts from wherever the element already is, and
    /// "already is" is only defined once everything before it has been resolved. This is also
    /// where two tweens fighting over one property at the same instant is caught: the merged
    /// animation can only hold one answer, so the second is refused rather than allowed to
    /// overwrite the first silently.</para>
    /// </summary>
    private static Dictionary<string, Dictionary<string, List<Stop>>> Thread(
        IReadOnlyList<RawTween> tweens, List<Refusal> refusals, Authored authored)
    {
        var tracks = new Dictionary<string, Dictionary<string, List<Stop>>>();
        var current = new Dictionary<(string Selector, string Component), Amount>();
        var busyUntil = new Dictionary<(string Selector, string Component), double>();

        foreach (var tween in tweens.OrderBy(t => t.Start))
        {
            foreach (var (gsapName, target) in tween.To)
            {
                if (!Properties.TryMap(gsapName, out var component, out _)) continue;

                var key = (tween.Selector, component);

                // A from-tween ends where the element already was and starts at the value written.
                var written = tween.Verb == "from" ? Resting(component) : target.Number;
                var opening = tween.Verb switch
                {
                    "from" => target,
                    "fromTo" when tween.From?.TryGetValue(gsapName, out var given) == true => given,
                    // Nothing earlier in this timeline touched the property, so the element is
                    // wherever its own stylesheet left it. Asking is the whole point: the
                    // fallback below assumes opacity 1 and the identity transform, and an
                    // element authored `opacity: 0` then produces a tween from 1 to 1.
                    _ => current.TryGetValue(key, out var held)
                        ? held
                        : Start(authored, tween.Selector, component, target) is { } stated
                            ? stated
                            : Resting(component) is { } rest
                                ? new Amount(rest, target.Unit)
                                : default,
                };

                // Only a `.to()`. A `.set()` states the value outright and needs no start to
                // travel from, so refusing one for want of a resting value threw away an
                // assignment that was perfectly knowable - which is how a polygon wipe lost the
                // `.set()` that establishes its first shape, and with it the whole tween that
                // follows, since the `.to()` then had nothing to start from either.
                if (tween.Verb == "to"
                    && !current.ContainsKey(key) && Resting(component) is null
                    && Start(authored, tween.Selector, component, target) is null)
                {
                    refusals.Add(new Refusal(
                        $"'{gsapName}' on '{tween.Selector}': a tween TO a size, from whatever the "
                        + "stylesheet says, and the compiler does not know what that is", tween.Line));
                    continue;
                }

                if (written is null)
                {
                    refusals.Add(new Refusal(
                        $"'{gsapName}' on '{tween.Selector}': a .from() of a size, which would have "
                        + "to end at whatever the stylesheet says", tween.Line));
                    continue;
                }

                var closing = new Amount(written.Value, target.Unit);
                if (tween.Verb is "to" or "set") closing = target;

                if (busyUntil.TryGetValue(key, out var until) && tween.Start < until - Instant)
                {
                    refusals.Add(new Refusal(
                        $"'{gsapName}' on '{tween.Selector}' at {Number(tween.Start)}s overlaps a "
                        + "tween of the same property that is still running, and one element gets "
                        + "one animation", tween.Line));
                    continue;
                }

                var stops = Track(tracks, tween.Selector, component);
                Write(stops, tween, opening, closing);

                current[key] = closing;
                busyUntil[key] = tween.Start + tween.Span;
            }
        }

        return tracks;
    }

    /// <summary>
    /// The value the stylesheet gives a component, if it gives one this tween can start from.
    ///
    /// <para>The unit check is the part that matters. An authored <c>translate(-50%, -50%)</c> and
    /// a tween of <c>x</c> in pixels are two different quantities, and a keyframe holds one number
    /// and one unit: starting a pixel tween at -50 because the stylesheet said -50% would move the
    /// element to a place nothing asked for. Where they disagree this answers nothing, the
    /// compiler keeps its old assumption, and the refusal it already writes still stands.</para>
    /// </summary>
    private static Amount? Start(Authored authored, string selector, string component, Amount target)
    {
        if (authored.Value(selector, component) is not { } stated) return null;

        // A unitless number - every scale, and a bare 0 - is comparable to anything.
        if (stated.Unit.Length > 0 && target.Unit.Length > 0 && stated.Unit != target.Unit)
            return null;

        return stated;
    }

    /// <summary>
    /// Transform components the stylesheet states and no tween touches, carried into the
    /// animation as constants.
    ///
    /// <para>Without this, an element authored <c>translate(-50%, -50%) scale(0)</c> and tweened
    /// on <c>scale</c> alone is emitted as <c>transform: scale(1)</c> - which replaces the whole
    /// declaration and drops the centring, moving the element by half its own size for the length
    /// of the composition. The animation was right about the property it carried and wrong about
    /// the element's position, which is the harder failure to see.</para>
    ///
    /// <para>Only where something is already animating. An element with no transform track keeps
    /// its stylesheet declaration untouched, and nothing here should reach in and restate it.</para>
    /// </summary>
    private static void Carry(
        Dictionary<string, Dictionary<string, List<Stop>>> tracks, Authored authored)
    {
        foreach (var (selector, properties) in tracks)
        {
            if (!properties.Keys.Any(Properties.IsTransform)) continue;
            if (authored.Values(selector) is not { } stated) continue;

            foreach (var (component, amount) in stated)
            {
                if (!Properties.IsTransform(component)) continue;
                if (properties.ContainsKey(component)) continue;

                properties[component] = [new Stop(0, amount.Number, amount.Unit)];
            }
        }
    }

    /// <summary>Whether a property's stops hold more than one value. A tolerance rather than an
    /// equality test, because the stops between the ends of an eased tween are sampled off a curve
    /// and arrive as fractions.</summary>
    private static bool Varies(List<Stop> stops) =>
        stops.Count > 1 && stops.Max(s => s.Number) - stops.Min(s => s.Number) > 1e-6;

    private static List<Stop> Track(
        Dictionary<string, Dictionary<string, List<Stop>>> tracks, string selector, string component)
    {
        if (!tracks.TryGetValue(selector, out var properties))
            tracks[selector] = properties = [];

        if (!properties.TryGetValue(component, out var stops))
            properties[component] = stops = [];

        return stops;
    }

    /// <summary>One tween's stops. An eased tween is sampled along its curve, because the merged
    /// animation runs linear and the shape has to live in the stops.</summary>
    private static void Write(List<Stop> stops, RawTween tween, Amount from, Amount to)
    {
        var unit = to.Unit.Length > 0 ? to.Unit : from.Unit;

        if (tween.Duration <= 0)
        {
            // A step. The previous value has to be held until a hair before it, or the stylesheet
            // interpolates all the way from the last stop to here and the step becomes a slide.
            //
            // The awkward case is a set that lands exactly where a tween ended, which is ordinary
            // in this corpus - travel somewhere, then snap back. Appending a hold at
            // `start - Instant` then puts a stop BEFORE the one already written, and the stops are
            // read in order: the tween's whole travel was silently dropped and the element sat
            // still. So the last stop is moved back that hair instead of a new one being added.
            if (stops.Count > 0 && tween.Start > Instant)
            {
                var last = stops[^1];
                if (last.Time >= tween.Start - Instant)
                    stops[^1] = last with { Time = tween.Start - Instant };
                else
                    stops.Add(new Stop(tween.Start - Instant, last.Number, last.Unit));
            }

            stops.Add(new Stop(tween.Start, to.Number, unit));
            return;
        }

        if (stops.Count > 0 && stops[^1].Time < tween.Start - Instant)
            stops.Add(new Stop(tween.Start - Instant, stops[^1].Number, stops[^1].Unit));

        // One set of stops per pass. A repeat is not a second animation - the engine allows only
        // one per element - it is the same travel written again further along the single
        // timeline, which is a shape a @keyframes holds perfectly well.
        for (var pass = 0; pass <= tween.Repeat; pass++)
        {
            var at = tween.Start + pass * tween.Duration;

            // yoyo walks back the way it came, so an odd pass runs end-to-start and needs no
            // jump. Without yoyo the value snaps back to the start: a stop a hair before holds
            // the end it reached, or the stylesheet would interpolate the snap into a slide and
            // the repeat would read as one long oscillation.
            var (opening, closing) = tween.Yoyo && pass % 2 == 1 ? (to, from) : (from, to);

            if (pass == 0)
                stops.Add(new Stop(at, opening.Number, unit));
            else if (!tween.Yoyo)
                // The previous pass already ended on `to` at exactly this instant, so the restart
                // goes a hair LATER rather than a hair earlier. Putting it earlier writes a stop
                // behind one already in the list, and the stops are read in time order: the whole
                // pass was then read as one slow slide instead of a snap and a run.
                stops.Add(new Stop(at + Instant, opening.Number, unit));

            // yoyo needs nothing here: the previous pass ended on the value this one opens with.

            if (!Ease.IsLinear(tween.Ease))
            {
                for (var i = 1; i < Ease.Samples; i++)
                {
                    var p = (double)i / Ease.Samples;
                    var eased = Ease.Of(tween.Ease, p);
                    stops.Add(new Stop(
                        at + tween.Duration * p,
                        opening.Number + (closing.Number - opening.Number) * eased,
                        unit));
                }
            }

            stops.Add(new Stop(at + tween.Duration, closing.Number, unit));
        }
    }

    // ---- writing ------------------------------------------------------------------------------

    private static string Keyframes(
        string name, Dictionary<string, List<Stop>> properties, double seconds)
    {
        var times = properties.Values.SelectMany(stops => stops.Select(s => s.Time))
            .Append(0).Append(seconds)
            .Distinct().OrderBy(t => t).ToArray();

        var css = new StringBuilder($"@keyframes {name} {{\n");

        foreach (var time in times)
        {
            var percent = seconds <= 0 ? 0 : 100 * time / seconds;
            css.Append("  ").Append(Number(percent)).Append("% { ")
                .Append(Declarations(properties, time))
                .Append(" }\n");
        }

        return css.Append("}\n").ToString();
    }

    private static string Rule(
        string selector, Dictionary<string, List<Stop>> properties, double time, string lead = "") =>
        $"{selector} {{ {lead}{Declarations(properties, time)} }}\n";

    /// <summary>
    /// A property lifted out of the keyframes and returned as an ordinary declaration, when it
    /// never varies. Empty when it varies, or is not there at all - a property that really does
    /// change has to stay in the animation whatever the engine makes of it, because writing one
    /// of its values as a constant would be a different composition rather than a worse one.
    /// </summary>
    private static string Hoist(Dictionary<string, List<Stop>> properties, string component)
    {
        if (!properties.TryGetValue(component, out var stops) || stops.Count == 0) return "";
        if (Varies(stops)) return "";

        properties.Remove(component);
        return $"{component}: {Number(stops[0].Number)}{stops[0].Unit}; ";
    }

    private static string Declarations(Dictionary<string, List<Stop>> properties, double time)
    {
        var parts = new List<string>();
        var transform = new List<string>();

        foreach (var component in Properties.TransformOrder)
        {
            if (!properties.TryGetValue(component, out var stops)) continue;
            var (number, unit) = At(stops, time);
            transform.Add($"{component}({Number(number)}{unit})");
        }

        if (transform.Count > 0) parts.Add("transform: " + string.Join(" ", transform));

        // The points, back into one polygon, in the order the author wrote them. A shape is only
        // a shape in order: sorting by index is what keeps point 3 the third vertex rather than
        // wherever the dictionary happened to put it.
        var polygon = properties.Keys.Where(Properties.IsPolygonPoint).ToList();

        if (polygon.Count > 0)
        {
            var corners = polygon
                .GroupBy(Properties.PointIndex)
                .OrderBy(point => point.Key)
                .Select(point =>
                {
                    string Axis(char axis) =>
                        properties.TryGetValue($"clipP{point.Key}{axis}", out var stops)
                            ? At(stops, time) is var (number, unit) ? Number(number) + unit : "0"
                            : "0";

                    return Axis('x') + " " + Axis('y');
                });

            parts.Add("clip-path: polygon(" + string.Join(", ", corners) + ")");
        }

        // The four edges, back into one declaration. Written whenever any of them is tracked, so
        // a tween of a single edge still produces a complete inset() rather than a fragment.
        else if (Properties.ClipOrder.Any(properties.ContainsKey))
        {
            var edges = Properties.ClipOrder.Select(edge =>
            {
                if (!properties.TryGetValue(edge, out var stops)) return "0";
                var (number, unit) = At(stops, time);
                return Number(number) + unit;
            });

            parts.Add("clip-path: inset(" + string.Join(" ", edges) + ")");
        }

        // The filter functions, back into one declaration, in list order. Only the tracked ones
        // are written, which is right: an absent function in a filter list is its own identity, so
        // a blur-only tween produces `filter: blur(4px)` rather than a list of no-ops.
        var filters = Properties.FilterOrder
            .Where(properties.ContainsKey)
            .Select(component =>
            {
                var (number, unit) = At(properties[component], time);
                return $"{Filter.Function(component)}({Number(number)}{unit})";
            })
            .ToList();

        if (filters.Count > 0) parts.Add("filter: " + string.Join(" ", filters));

        foreach (var (component, stops) in properties)
        {
            if (Properties.IsAssembled(component)) continue;
            var (number, unit) = At(stops, time);

            // z-index is an integer, and CSS interpolates integers by rounding. Writing 1.3333
            // between a 1 and a 2 would be a value no stacking context has.
            if (component == "z-index")
            {
                parts.Add($"z-index: {Math.Round(number, MidpointRounding.AwayFromZero)
                    .ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}");
                continue;
            }

            parts.Add($"{component}: {Number(number)}{unit}");
        }

        return string.Join("; ", parts) + ";";
    }

    /// <summary>A property's value at an instant: the stop if there is one there, the straight line
    /// between the two around it otherwise. Straight, because any curve was already sampled into
    /// stops.</summary>
    /// <remarks>The stops are assumed to be in time order, and they are built in time order. The
    /// one place that stopped being true - a step landing exactly on a tween's end - flattened a
    /// whole tween without any error, which is why <see cref="Write"/> now moves a stop rather
    /// than appending one.</remarks>
    private static (double Number, string Unit) At(List<Stop> stops, double time)
    {
        if (stops.Count == 0) return (0, "");
        if (time <= stops[0].Time) return (stops[0].Number, stops[0].Unit);
        if (time >= stops[^1].Time) return (stops[^1].Number, stops[^1].Unit);

        for (var i = 1; i < stops.Count; i++)
        {
            if (stops[i].Time < time) continue;

            var before = stops[i - 1];
            var after = stops[i];
            var span = after.Time - before.Time;
            var p = span <= 0 ? 1 : (time - before.Time) / span;

            return (before.Number + (after.Number - before.Number) * p, after.Unit);
        }

        return (stops[^1].Number, stops[^1].Unit);
    }

    private static string Number(double value) =>
        Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);
}
