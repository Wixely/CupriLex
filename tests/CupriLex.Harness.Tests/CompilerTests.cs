using CupriLex.Compiler;
using Xunit;

namespace CupriLex.Harness.Tests;

/// <summary>
/// What the GSAP compiler carries, and what it refuses.
///
/// <para>Small documents rather than corpus blocks: a corpus block is evidence that the whole
/// thing works, which is what the comparison harness measures, and these are the individual claims
/// underneath it - where a tween lands, what a position parameter means, which properties survive.</para>
/// </summary>
public class CompilerTests
{
    private static Translated Compile(string script, string markup = """<div class="a"></div>""") =>
        Translator.Of($"<html><body>{markup}<script>{script}</script></body></html>");

    private static string Css(string script, string markup = """<div class="a"></div>""") =>
        Compile(script, markup).Motion.Css;

    // ---- where a tween lands ------------------------------------------------------------------

    [Fact]
    public void A_single_tween_becomes_one_keyframes_and_one_animation()
    {
        var css = Css("""const tl = gsap.timeline(); tl.to(".a", { x: 100, duration: 1 });""");

        Assert.Contains("@keyframes", css);
        Assert.Contains("translateX(100px)", css);
        Assert.Contains(".a { animation:", css);
        Assert.Contains("1s linear both", css);
    }

    /// <summary>A tween with no position parameter appends to the end of the timeline. Getting
    /// this wrong stacks a whole composition on top of itself at t=0 and it still renders.</summary>
    [Fact]
    public void Tweens_without_a_position_run_one_after_another()
    {
        var css = Css("""
            const tl = gsap.timeline();
            tl.to(".a", { x: 100, duration: 1, ease: "none" });
            tl.to(".a", { x: 200, duration: 1, ease: "none" });
            """);

        Assert.Contains("2s linear both", css);       // two seconds in total
        Assert.Contains("50%", css);                  // and the join is halfway
    }

    /// <summary>GSAP's <c>"&lt;"</c>: start where the previous tween started, not where it
    /// ended.</summary>
    [Fact]
    public void The_position_parameter_places_a_tween_at_the_previous_one_s_start()
    {
        var css = Css("""
            const tl = gsap.timeline();
            tl.to(".a", { x: 100, duration: 2, ease: "none" });
            tl.to(".b", { opacity: 0, duration: 2, ease: "none" }, "<");
            """, """<div class="a"></div><div class="b"></div>""");

        // Both run the whole two seconds, so the composition is two seconds long, not four.
        Assert.Contains("2s linear both", css);
        Assert.DoesNotContain("4s linear both", css);
    }

    [Fact]
    public void A_numeric_position_is_absolute_seconds_on_the_timeline()
    {
        var css = Css("""
            const tl = gsap.timeline();
            tl.to(".a", { x: 100, duration: 1, ease: "none" }, 3);
            """);

        Assert.Contains("4s linear both", css);       // starts at 3, runs 1
    }

    [Fact]
    public void A_label_can_be_placed_and_then_tweened_against()
    {
        var css = Css("""
            const tl = gsap.timeline();
            tl.addLabel("beat", 2);
            tl.to(".a", { x: 100, duration: 1, ease: "none" }, "beat");
            """);

        Assert.Contains("3s linear both", css);
    }

    /// <summary>A running total kept in a variable. A third of the corpus writes its timeline this
    /// way, and treating the reassignment as unknowable refused every tween after the first.</summary>
    [Fact]
    public void A_time_kept_in_a_variable_and_added_to_is_followed()
    {
        var css = Css("""
            let t = 0;
            const tl = gsap.timeline();
            tl.to(".a", { x: 10, duration: 0.5, ease: "none" }, t);
            t += 2;
            tl.to(".a", { x: 20, duration: 0.5, ease: "none" }, t);
            """);

        Assert.Contains("2.5s linear both", css);
        Assert.DoesNotContain("not knowable", string.Join(" ",
            Compile("""
                let t = 0;
                const tl = gsap.timeline();
                tl.to(".a", { x: 10, duration: 0.5 }, t);
                t += 2;
                tl.to(".a", { x: 20, duration: 0.5 }, t);
                """).Refusals.Select(r => r.What)));
    }

    /// <summary>Chained calls, which is how most of the corpus is written. Reading only the
    /// outermost call carries the last tween of a chain and loses the rest.</summary>
    [Fact]
    public void A_chain_of_calls_is_followed_to_the_end()
    {
        var css = Css("""
            gsap.timeline()
              .to(".a", { x: 100, duration: 1, ease: "none" })
              .to(".a", { x: 200, duration: 1, ease: "none" })
              .to(".a", { x: 300, duration: 1, ease: "none" });
            """);

        Assert.Contains("3s linear both", css);
        Assert.Contains("translateX(300px)", css);
    }

    // ---- what a tween starts from -------------------------------------------------------------

    /// <summary><c>.from()</c> runs backwards: the values given are where it STARTS, and it ends at
    /// the element's resting state. Reading it as a <c>.to()</c> plays the whole composition
    /// inside out.</summary>
    [Fact]
    public void A_from_tween_starts_at_the_value_written_and_ends_at_rest()
    {
        var css = Css("""gsap.timeline().from(".a", { opacity: 0, duration: 1, ease: "none" });""");

        Assert.Contains("opacity: 0;", css);
        Assert.Contains("opacity: 1;", css);
    }

    [Fact]
    public void A_set_is_a_step_rather_than_a_travel()
    {
        var css = Css("""
            const tl = gsap.timeline();
            tl.to(".a", { x: 100, duration: 1, ease: "none" });
            tl.set(".a", { x: 0 });
            """);

        // The step back to zero happens at the end, and the value before it is still 100.
        Assert.Contains("translateX(100px)", css);
        Assert.Contains("translateX(0px)", css);
    }

    [Fact]
    public void A_to_tween_starts_from_where_the_last_one_left_the_element()
    {
        var css = Css("""
            const tl = gsap.timeline();
            tl.set(".a", { x: 40 });
            tl.to(".a", { x: 90, duration: 1, ease: "none" });
            """);

        Assert.Contains("translateX(40px)", css);
        Assert.Contains("translateX(90px)", css);
    }

    // ---- easing -------------------------------------------------------------------------------

    /// <summary>The engine honours a timing function, but only one per animation - and an element
    /// gets one animation for all of its tweens. So the curve is sampled into stops instead, and
    /// the animation itself runs linear.</summary>
    [Fact]
    public void An_eased_tween_is_sampled_into_stops_rather_than_given_a_timing_function()
    {
        var eased = Css("""gsap.timeline().to(".a", { x: 100, duration: 1, ease: "power2.out" });""");
        var linear = Css("""gsap.timeline().to(".a", { x: 100, duration: 1, ease: "none" });""");

        Assert.True(Stops(eased) > Stops(linear) + 4,
            $"an eased tween produced {Stops(eased)} stops and a linear one {Stops(linear)}");

        Assert.DoesNotContain("cubic-bezier", eased);
        Assert.Contains("linear both", eased);
    }

    /// <summary>A curve the sampler does not know runs linear and is not refused: the motion is
    /// still mostly right, and losing the shape of an acceleration is a smaller lie than losing
    /// the movement.</summary>
    [Fact]
    public void An_unknown_ease_still_carries_its_tween()
    {
        var css = Css("""gsap.timeline().to(".a", { x: 100, duration: 1, ease: "elastic.out(1,0.3)" });""");

        Assert.Contains("translateX(100px)", css);
    }

    /// <summary>
    /// An overshoot written into the ease is used, rather than GSAP's default.
    ///
    /// <para>44 corpus blocks write a parameterised ease and <c>back.out</c> dominates, with
    /// overshoots from 1.04 to 3. Every one of them used to be sampled with 1.70158, which at
    /// either end of that range is a visibly different curve - and the wrongness sits in the
    /// middle of a tween, which is where a frame comparison is least forgiving.</para>
    /// </summary>
    [Fact]
    public void A_back_ease_uses_the_overshoot_it_was_given()
    {
        var gentle = Css("""gsap.timeline().to(".a", { x: 100, duration: 1, ease: "back.out(1.1)" });""");
        var violent = Css("""gsap.timeline().to(".a", { x: 100, duration: 1, ease: "back.out(3)" });""");

        Assert.NotEqual(gentle, violent);

        // A back.out overshoots its target and settles back, so some stop must exceed 100px, and
        // the bigger overshoot must exceed it by more.
        Assert.True(Peak(violent) > Peak(gentle) + 1,
            $"overshoot 3 peaked at {Peak(violent):0.#}px and overshoot 1.1 at {Peak(gentle):0.#}px");
    }

    /// <summary>Every use of <c>steps()</c> in the corpus is <c>steps(1)</c>: hold the start value
    /// for the whole tween, then snap. Sampled as a curve it was a straight slide.</summary>
    [Fact]
    public void A_steps_ease_holds_and_snaps_rather_than_sliding()
    {
        var css = Css("""gsap.timeline().to(".a", { x: 100, duration: 1, ease: "steps(1)" });""");

        // Nothing between the ends: every stop is at one end or the other.
        var values = System.Text.RegularExpressions.Regex.Matches(css, @"translateX\(([\d.]+)px\)")
            .Select(m => double.Parse(m.Groups[1].Value,
                System.Globalization.CultureInfo.InvariantCulture));

        Assert.All(values, v => Assert.True(v < 0.01 || v > 99.99, $"a stop landed at {v}px"));
    }

    private static double Peak(string css) =>
        System.Text.RegularExpressions.Regex.Matches(css, @"translateX\(([\d.]+)px\)")
            .Select(m => double.Parse(m.Groups[1].Value,
                System.Globalization.CultureInfo.InvariantCulture))
            .DefaultIfEmpty(0)
            .Max();

    private static int Stops(string css) => css.Count(c => c == '%');

    // ---- what is refused, and named -----------------------------------------------------------

    [Fact]
    public void A_property_the_engine_cannot_animate_is_refused_by_name()
    {
        var refusals = Compile("""gsap.timeline().to(".a", { backgroundColor: "#fff", duration: 1 });""")
            .Refusals;

        Assert.Contains(refusals, r => r.What.Contains("backgroundColor", StringComparison.Ordinal));
    }

    /// <summary>
    /// A timeline built in a loop over data the document states, which is now written out.
    ///
    /// <para>This test used to assert the opposite - empty CSS and a refusal naming the loop - and
    /// it was right to, for as long as the compiler did not read loops. 1,110 of the corpus's GSAP
    /// calls sit inside one and 622 of them iterate something the source states outright.</para>
    /// </summary>
    [Fact]
    public void Tweens_built_in_a_loop_over_stated_data_are_carried()
    {
        var compiled = Compile("""
            const tl = gsap.timeline();
            [1, 2, 3].forEach(function (n) { tl.to(".a", { x: n * 10, duration: 1 }); });
            """);

        Assert.NotEmpty(compiled.Motion.Css);
        Assert.Contains("translateX(30px)", compiled.Motion.Css);
        Assert.DoesNotContain(compiled.Refusals, r => r.What.Contains("inside a loop", StringComparison.Ordinal));
    }

    [Fact]
    public void A_loop_whose_extent_is_not_knowable_is_still_refused_and_counted()
    {
        // The half that must not change. An extent the compiler cannot resolve leaves the body
        // unread, and the refusal says so rather than carrying some of the motion.
        var compiled = Compile("""
            const tl = gsap.timeline();
            for (let i = 0; i < document.querySelectorAll(".x").length; i++) {
              tl.to(".a", { x: i * 10, duration: 1 });
            }
            """);

        Assert.Empty(compiled.Motion.Css);
        Assert.Contains(compiled.Refusals, r => r.What.Contains("inside a loop", StringComparison.Ordinal));
    }

    [Fact]
    public void A_counted_loop_is_written_out_in_order()
    {
        var css = Css("""
            const tl = gsap.timeline();
            for (let i = 0; i < 3; i++) { tl.to(".a", { x: i * 10, duration: 1 }); }
            """);

        // Three tweens appended one after another: the last ends at 20px, three seconds in.
        Assert.Contains("translateX(20px)", css);
        Assert.Contains("animation:", css);
    }

    [Fact]
    public void A_for_of_over_a_stated_list_is_written_out()
    {
        var css = Css("""
            const STEPS = [5, 15];
            const tl = gsap.timeline();
            for (const s of STEPS) { tl.to(".a", { x: s, duration: 1 }); }
            """);

        Assert.Contains("translateX(15px)", css);
    }

    [Fact]
    public void A_forEach_binds_the_index_and_the_array_as_well_as_the_item()
    {
        // `ROWS.forEach((row, i) => tl.to("#row-" + i, ...))` is the commonest shape in the
        // corpus, and the selector only resolves if the index is bound.
        var css = Css("""
            const ROWS = ["a", "b"];
            const tl = gsap.timeline();
            ROWS.forEach(function (row, i) { tl.to("#r" + i, { x: 10, duration: 1 }); });
            """,
            """<div id="r0"></div><div id="r1"></div>""");

        Assert.Contains("#r0 {", css);
        Assert.Contains("#r1 {", css);
    }

    [Fact]
    public void A_loop_longer_than_the_compiler_will_write_out_is_refused_rather_than_truncated()
    {
        // Half a loop is motion that stops for no reason, which is worse than none.
        var compiled = Compile("""
            const tl = gsap.timeline();
            for (let i = 0; i < 5000; i++) { tl.to(".a", { x: i, duration: 0.01 }); }
            """);

        Assert.Contains(compiled.Refusals, r => r.What.Contains("more than", StringComparison.Ordinal));
    }

    /// <summary>
    /// GSAP does not overwrite by default: both tweens run, and the timeline applies them in order
    /// every tick, so the later-added one is what the frame shows for as long as it lasts. When the
    /// earlier one ends first - 32 of the corpus's 36 overlaps - the later one simply takes over,
    /// and one track of stops says that exactly.
    /// </summary>
    [Fact]
    public void An_overlapping_tween_takes_over_from_where_the_earlier_one_had_reached()
    {
        var compiled = Compile("""
            const tl = gsap.timeline();
            tl.to(".a", { x: 100, duration: 4, ease: "none" }, 0);
            tl.to(".a", { x: 200, duration: 4, ease: "none" }, 1);
            """);

        Assert.DoesNotContain(compiled.Refusals, r => r.What.Contains("overlaps", StringComparison.Ordinal));

        // The first tween reaches 25px at 1s on a five-second timeline, which is 20%, and the
        // second travels from there rather than from 100.
        Assert.Contains("20% { transform: translateX(25px); }", compiled.Motion.Css);
        Assert.Contains("translateX(200px)", compiled.Motion.Css);
    }

    /// <summary>The case one track cannot say. If the earlier tween OUTLASTS the later one, the
    /// picture goes back to it afterwards, and truncating the earlier track throws that tail
    /// away.</summary>
    [Fact]
    public void An_overlapping_tween_the_earlier_one_outlasts_is_still_refused()
    {
        var compiled = Compile("""
            const tl = gsap.timeline();
            tl.to(".a", { x: 100, duration: 8, ease: "none" }, 0);
            tl.to(".a", { x: 200, duration: 1, ease: "none" }, 1);
            """);

        Assert.Contains(compiled.Refusals, r => r.What.Contains("OUTLASTS", StringComparison.Ordinal));
    }

    /// <summary>A .fromTo() states its own start, so it is not given the value the earlier tween had
    /// reached - that would quietly replace a start the author wrote down.</summary>
    [Fact]
    public void An_overlapping_fromTo_keeps_the_start_it_states()
    {
        var css = Css("""
            const tl = gsap.timeline();
            tl.to(".a", { x: 100, duration: 4, ease: "none" }, 0);
            tl.fromTo(".a", { x: 500 }, { x: 200, duration: 4, ease: "none" }, 1);
            """);

        Assert.Contains("translateX(500px)", css);
    }

    /// <summary>GSAP animating a plain object is a way to drive an onUpdate callback with numbers.
    /// There is no callback here, and saying "could not be reduced to a selector" about it would
    /// send the reader looking for an element that was never involved.</summary>
    [Fact]
    public void A_tween_of_a_plain_object_says_what_it_actually_is()
    {
        var compiled = Compile("""
            const clock = { t: 0 };
            gsap.timeline().to(clock, { t: 1, duration: 2 });
            """);

        Assert.Contains(compiled.Refusals, r => r.What.Contains("plain object", StringComparison.Ordinal));
    }

    [Fact]
    public void A_stagger_is_refused_because_it_needs_one_animation_per_element()
    {
        var compiled = Compile("""
            gsap.timeline().to(".a", { x: 100, duration: 1, stagger: 0.1 });
            """);

        Assert.Contains(compiled.Refusals, r => r.What.Contains("stagger", StringComparison.Ordinal));
    }

    // ---- a refusal must not move the clock -----------------------------------------------------

    /// <summary>
    /// A tween this compiler cannot carry still occupies its place on the timeline.
    ///
    /// <para>The failure it guards against is the nastiest kind: everything downstream renders
    /// perfectly and at the wrong moment. A <c>.to()</c> of <c>backgroundColor</c> is refused
    /// because the engine cannot animate it, and if the refusal also drops the two seconds it
    /// occupied then every un-positioned tween after it starts two seconds early. Nothing in the
    /// output looks wrong; the composition is simply ahead of itself.</para>
    ///
    /// <para>Found by sweeping the engine's clock against the browser's on <c>transitions-grid</c>,
    /// where shifting the engine forward three quarters of a second recovered 5% of content.</para>
    /// </summary>
    [Fact]
    public void A_tween_whose_properties_are_all_refused_still_takes_its_time()
    {
        var css = Css("""
            const tl = gsap.timeline();
            tl.to(".a", { backgroundColor: "#ffffff", duration: 2 });
            tl.to(".a", { x: 100, duration: 1, ease: "none" });
            """);

        Assert.Contains("3s linear both", css);
    }

    /// <summary>The same for a target that cannot be reduced to a selector: unknown WHERE, but the
    /// duration was written down and is not in doubt.</summary>
    [Fact]
    public void A_tween_on_an_unresolvable_target_still_takes_its_time()
    {
        var css = Css("""
            const mystery = window.somethingOpaque();
            const tl = gsap.timeline();
            tl.to(mystery, { x: 50, duration: 2 });
            tl.to(".a", { x: 100, duration: 1, ease: "none" });
            """);

        Assert.Contains("3s linear both", css);
    }

    /// <summary>When the duration itself cannot be read, the clock cannot be advanced and
    /// everything after it is suspect. That is worth its own refusal rather than a silent
    /// guess.</summary>
    [Fact]
    public void A_tween_whose_duration_is_unknown_says_the_timeline_after_it_is_unreliable()
    {
        var compiled = Compile("""
            const tl = gsap.timeline();
            tl.to(".a", window.opaqueVars());
            tl.to(".a", { x: 100, duration: 1 });
            """);

        Assert.Contains(compiled.Refusals,
            r => r.What.Contains("everything after it", StringComparison.Ordinal));
    }

    // ---- carried, and still not visible --------------------------------------------------------

    /// <summary>
    /// An animation whose selector matches nothing in the document is reported.
    ///
    /// <para>Resolution never consults the document, which is what makes it static. The cost is
    /// that a selector naming an element the script was going to build resolves perfectly and then
    /// matches nothing, and the rule is emitted valid and inert. 28 animations across 7 corpus
    /// blocks do exactly this; one of them, <c>chatgpt-exchange</c>, has all twelve of its
    /// animations land on nothing.</para>
    /// </summary>
    [Fact]
    public void An_animation_on_a_selector_that_matches_nothing_is_reported()
    {
        var compiled = Translator.Of("""
            <html><body><div class="present"></div><script>
              gsap.timeline().to(".built-by-the-script", { x: 100, duration: 1 });
            </script></body></html>
            """);

        Assert.Contains(compiled.Refusals,
            r => r.What.Contains("matches no element", StringComparison.Ordinal));
    }

    [Fact]
    public void An_animation_whose_selector_does_match_is_not_reported()
    {
        var compiled = Compile("""gsap.timeline().to(".a", { x: 100, duration: 1 });""");

        Assert.DoesNotContain(compiled.Refusals,
            r => r.What.Contains("matches no element", StringComparison.Ordinal));
    }

    /// <summary>
    /// A tween that ends where the compiler assumed it began is reported, and emitted anyway.
    ///
    /// <para>Both halves are load-bearing and the second one is counter-intuitive. <c>.to(el,
    /// {opacity: 1})</c> on an element the stylesheet authors as <c>opacity: 0</c> is a real fade
    /// in a browser, because GSAP reads the computed style; the compiler assumes the resting value
    /// and produces a keyframes that holds 1 throughout. Deleting those as no-ops was the obvious
    /// cleanup and it took <c>flowchart-vertical</c> from 97.9% to 0.9%: the flat animation is
    /// wrong about the motion and right about the END STATE, and with <c>both</c> it is the only
    /// thing keeping the element visible.</para>
    ///
    /// <para>So it is emitted, reported, and not counted as motion.</para>
    /// </summary>
    [Fact]
    public void A_tween_that_ends_where_it_began_is_reported_but_still_emitted()
    {
        var compiled = Compile("""gsap.timeline().to(".a", { opacity: 1, duration: 1 });""");

        Assert.Contains(compiled.Refusals,
            r => r.What.Contains("held at its end state", StringComparison.Ordinal));

        Assert.Contains("@keyframes", compiled.Motion.Css);
        Assert.Contains("opacity: 1;", compiled.Motion.Css);
        Assert.Equal(1, compiled.Motion.Held);
        Assert.Equal(0, compiled.Motion.Elements);
    }

    [Fact]
    public void A_tween_that_actually_moves_counts_as_motion_and_is_not_reported()
    {
        var compiled = Compile("""gsap.timeline().to(".a", { opacity: 0, duration: 1 });""");

        Assert.Equal(1, compiled.Motion.Elements);
        Assert.Equal(0, compiled.Motion.Held);
        Assert.DoesNotContain(compiled.Refusals,
            r => r.What.Contains("held at its end state", StringComparison.Ordinal));
    }

    // ---- the document ------------------------------------------------------------------------

    [Fact]
    public void The_scripts_do_not_come_with_it()
    {
        var html = Compile("""gsap.timeline().to(".a", { x: 1, duration: 1 });""").Html;

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A target resolved through a query call, which is how most blocks name their
    /// elements. The selector text is what is kept - the document is never consulted, so the
    /// compiler cannot be wrong about what it matches, only about whether it matches anything.</summary>
    [Fact]
    public void An_element_held_in_a_variable_is_resolved_to_its_selector()
    {
        var css = Css("""
            const hero = document.querySelector(".hero");
            gsap.timeline().to(hero, { x: 100, duration: 1 });
            """, """<div class="hero"></div>""");

        Assert.Contains(".hero { animation:", css);
    }

    [Fact]
    public void An_element_found_by_id_becomes_a_hash_selector()
    {
        var css = Css("""
            const card = document.getElementById("card");
            gsap.timeline().to(card, { opacity: 0, duration: 1 });
            """, """<div id="card"></div>""");

        Assert.Contains("#card { animation:", css);
    }

    // ---- a list of targets --------------------------------------------------------------------
    // GSAP takes an array of targets as readily as one, and this corpus leans on it: 60 tweens
    // across 15 blocks write one. They were all refused, and refused as "plain objects", which is
    // what a tween of `{t: 0}` is - so the report sent the reader looking for an onUpdate that was
    // never there. share-sheet-carousel alone writes 26 and rendered a still.

    private const string Pair = """<div id="a1"></div><div id="b1"></div><div id="a2"></div>""";

    [Fact]
    public void An_array_of_selectors_animates_every_one_of_them()
    {
        var css = Css("""
            const tl = gsap.timeline();
            tl.to(["#a1", "#b1"], { opacity: 0, duration: 1, ease: "none" });
            """, Pair);

        Assert.Contains("#a1 { animation:", css);
        Assert.Contains("#b1 { animation:", css);
    }

    /// <summary>The shape the carousel actually writes: the index is folded, then the strings are
    /// concatenated, then the array is read. All three already worked except the last.</summary>
    [Fact]
    public void An_array_of_concatenated_selectors_resolves_each_entry()
    {
        var css = Css("""
            const tl = gsap.timeline();
            const i = 1;
            tl.to(["#a" + i, "#b" + i], { opacity: 0, duration: 1, ease: "none" });
            """, Pair);

        Assert.Contains("#a1 { animation:", css);
        Assert.Contains("#b1 { animation:", css);
    }

    /// <summary>All of it or none. Half a list is motion applied to some elements and silently
    /// not to others, which is worse than a refusal because nothing says so.</summary>
    [Fact]
    public void A_list_with_one_unreadable_entry_is_refused_whole()
    {
        var compiled = Compile("""
            const tl = gsap.timeline();
            tl.to(["#a1", someElement], { opacity: 0, duration: 1 });
            """, Pair);

        Assert.Empty(compiled.Motion.Css);
        Assert.Contains(compiled.Refusals, r => r.What.Contains("at least one entry"));
    }

    /// <summary>A tween of a plain object is a different failure and must keep saying so: GSAP
    /// driving numbers for an onUpdate to paint with, and there is no onUpdate here.</summary>
    [Fact]
    public void A_plain_object_target_is_still_reported_as_one()
    {
        var compiled = Compile("""
            const clock = { t: 0 };
            gsap.timeline().to(clock, { t: 1, duration: 1 });
            """, Pair);

        Assert.Contains(compiled.Refusals, r => r.What.Contains("plain object"));
    }

    /// <summary>The same element twice is one animation. Emitted twice it would collide with
    /// itself and the second would be refused as an overlap.</summary>
    [Fact]
    public void A_repeated_entry_does_not_collide_with_itself()
    {
        var compiled = Compile("""
            const tl = gsap.timeline();
            tl.to(["#a1", "#a1"], { opacity: 0, duration: 1, ease: "none" });
            """, Pair);

        Assert.Contains("#a1 { animation:", compiled.Motion.Css);
        Assert.DoesNotContain(compiled.Refusals, r => r.What.Contains("overlaps"));
    }

    // ---- a finite repeat ------------------------------------------------------------------------
    // A repeat is not a second animation - the engine allows one per element - it is the same
    // travel written again further along the single timeline. 37 tweens across 14 blocks ask for
    // one and every one of them is finite.

    [Fact]
    public void A_repeat_lays_the_stops_down_again_and_occupies_the_extra_time()
    {
        var css = Css("""
            const tl = gsap.timeline();
            tl.to(".a", { x: 100, duration: 1, ease: "none", repeat: 1 });
            """);

        // Two passes of one second, so the composition is two seconds long, not one.
        Assert.Contains("2s linear both", css);
        // and it snaps back to the start to run again
        Assert.Contains("translateX(0px)", css);
        Assert.Contains("translateX(100px)", css);
    }

    /// <summary>Without yoyo the value snaps back and runs again, so the end value must still be
    /// held a hair before the restart - otherwise the stylesheet interpolates the snap into a
    /// slide and the repeat reads as one long oscillation.</summary>
    [Fact]
    public void A_repeat_without_yoyo_snaps_back_rather_than_sliding_back()
    {
        var css = Css("""
            const tl = gsap.timeline();
            tl.to(".a", { x: 100, duration: 1, ease: "none", repeat: 1 });
            """);

        // The pass boundary sits at 50% of the two seconds: the first pass ends there on 100px,
        // and the restart is a hair LATER so it cannot collide with a stop already written.
        Assert.Contains("50% { transform: translateX(100px); }", css);
        Assert.Contains("50.05% { transform: translateX(0px); }", css);
        Assert.Contains("100% { transform: translateX(100px); }", css);
    }

    [Fact]
    public void A_yoyo_walks_back_the_way_it_came()
    {
        var css = Css("""
            const tl = gsap.timeline();
            tl.to(".a", { x: 100, duration: 1, ease: "none", repeat: 1, yoyo: true });
            """);

        // No snap at the boundary: out to 100px at the midpoint and back to 0 by the end, and
        // nothing restating the opening value a hair after halfway.
        Assert.Contains("50% { transform: translateX(100px); }", css);
        Assert.Contains("100% { transform: translateX(0px); }", css);
        Assert.DoesNotContain("50.05%", css);
    }

    /// <summary>An infinite repeat has no last pass to write. A rule that stopped after some
    /// arbitrary number of them would be a composition that quietly ends.</summary>
    [Fact]
    public void An_infinite_repeat_is_refused_by_name()
    {
        var compiled = Compile("""
            const tl = gsap.timeline();
            tl.to(".a", { x: 100, duration: 1, repeat: -1 });
            """);

        Assert.Contains(compiled.Refusals, r => r.What.Contains("never ends"));
    }

    /// <summary>The clock has to clear every pass, or everything appended after a repeating tween
    /// runs early - which looks like correct motion at the wrong time.</summary>
    [Fact]
    public void A_tween_after_a_repeat_starts_after_every_pass()
    {
        var css = Css("""
            const tl = gsap.timeline();
            tl.to(".a", { x: 100, duration: 1, ease: "none", repeat: 2 });
            tl.to(".b", { opacity: 0, duration: 1, ease: "none" });
            """, """<div class="a"></div><div class="b"></div>""");

        // three passes of the first, then one second more: four seconds in total
        Assert.Contains("4s linear both", css);
    }

    // ---- clip-path, as numbers ------------------------------------------------------------------
    // The engine has animated clip-path since 0.35.0 and a wipe is the commonest transition there
    // is: 89 tweens across 16 blocks. A shape is not an Amount, but the two forms this corpus
    // writes are: inset() is four edges and polygon() is 2N points.

    [Fact]
    public void An_inset_clip_becomes_four_edges_and_wipes()
    {
        var css = Css("""
            const tl = gsap.timeline();
            tl.set(".a", { clipPath: "inset(0 100% 0 0)" }, 0);
            tl.to(".a", { clipPath: "inset(0 0% 0 0)", duration: 1, ease: "none" }, 0);
            """);

        Assert.Contains("clip-path: inset(0 100% 0 0)", css);
        Assert.Contains("clip-path: inset(0 0% 0 0)", css);
    }

    /// <summary>CSS's own shorthand, so one value is all four edges and two are vertical then
    /// horizontal. Getting this wrong clips the wrong edge, which still looks like a wipe.</summary>
    [Theory]
    [InlineData("inset(10%)", "inset(10% 10% 10% 10%)")]
    [InlineData("inset(10% 20%)", "inset(10% 20% 10% 20%)")]
    [InlineData("inset(10% 20% 30%)", "inset(10% 20% 30% 20%)")]
    [InlineData("none", "inset(0 0 0 0)")]
    public void An_inset_expands_the_way_css_does(string written, string expected)
    {
        var css = Css($$"""
            const tl = gsap.timeline();
            tl.set(".a", { clipPath: "{{written}}" }, 0);
            tl.to(".a", { opacity: 0, duration: 1, ease: "none" }, 0);
            """);

        Assert.Contains("clip-path: " + expected, css);
    }

    [Fact]
    public void A_polygon_clip_becomes_its_points_and_keeps_their_order()
    {
        var css = Css("""
            const tl = gsap.timeline();
            tl.fromTo(".a",
                { clipPath: "polygon(0% 0%, 0% 0%, 0% 100%, 0% 100%)" },
                { clipPath: "polygon(0% 0%, 100% 0%, 100% 100%, 0% 100%)", duration: 1, ease: "none" }, 0);
            """);

        Assert.Contains("clip-path: polygon(0% 0%, 0% 0%, 0% 100%, 0% 100%)", css);
        Assert.Contains("clip-path: polygon(0% 0%, 100% 0%, 100% 100%, 0% 100%)", css);
    }

    /// <summary>A .set() states its value outright and needs no start to travel from. Refusing
    /// one for want of a resting value threw away the assignment that establishes a wipe's first
    /// shape, and the .to() after it then had nothing to start from either.</summary>
    [Fact]
    public void A_set_of_a_polygon_stands_without_a_resting_value()
    {
        var compiled = Compile("""
            const tl = gsap.timeline();
            tl.set(".a", { clipPath: "polygon(0% 0%, 0% 0%, 0% 100%, 0% 100%)" }, 0);
            tl.to(".a", { clipPath: "polygon(0% 0%, 100% 0%, 100% 100%, 0% 100%)", duration: 1, ease: "none" }, 0);
            """);

        Assert.Contains("clip-path: polygon(", compiled.Motion.Css);
        Assert.DoesNotContain(compiled.Refusals, r => r.What.Contains("a tween TO a size"));
    }

    /// <summary>A shape that is not numbers stays refused, by name and with the value quoted, so
    /// the report says which shape rather than "clip-path".</summary>
    [Fact]
    public void A_shape_that_is_not_numbers_is_refused_by_name()
    {
        var compiled = Compile("""
            const tl = gsap.timeline();
            tl.to(".a", { clipPath: "url(#mask)", duration: 1 });
            """);

        Assert.Contains(compiled.Refusals, r => r.What.Contains("url(#mask)"));
    }

    // ---- null is a value, not an absence of one --------------------------------------------------
    // Value.Unknown means "this compiler could not work out what this is" and leaves both sides of
    // a branch unread. Nothing means "the document says this is null", which is decidable. Reading
    // the first as the second is what refused 150 tweens the browser does not run either.

    [Fact]
    public void A_branch_over_a_null_config_key_is_decided_and_not_refused()
    {
        var compiled = Compile("""
            const DATA = { text: null };
            const TXT = DATA.text || null;
            const tl = gsap.timeline();
            if (TXT) { tl.to(".a", { x: 100, duration: 1 }); }
            tl.to(".a", { opacity: 0, duration: 1 }, 2);
            """);

        // The guarded tween does not run in a browser either, so neither carrying it nor
        // refusing it is right: it should simply not be mentioned.
        Assert.DoesNotContain("translateX(100px)", compiled.Motion.Css);
        Assert.DoesNotContain(compiled.Refusals, r => r.What.Contains("call(s) inside", StringComparison.Ordinal));
    }

    [Fact]
    public void A_branch_over_a_present_config_key_is_followed()
    {
        var css = Css("""
            const DATA = { text: { words: 3 } };
            const TXT = DATA.text || null;
            const tl = gsap.timeline();
            if (TXT) { tl.to(".a", { x: 100, duration: 1 }); }
            """);

        Assert.Contains("translateX(100px)", css);
    }

    /// <summary>Short-circuiting, so a right side this compiler cannot read costs nothing once the
    /// left side has settled the answer.</summary>
    [Theory]
    [InlineData("0 || 100", 100)]
    [InlineData("5 || 100", 5)]
    [InlineData("5 && 100", 100)]
    [InlineData("0 && 100", 0)]
    [InlineData("null ?? 100", 100)]
    public void The_logical_operators_fold_to_an_operand(string expression, double expected)
    {
        var css = Css($$"""
            const tl = gsap.timeline();
            tl.to(".a", { x: {{expression}}, duration: 1 });
            """);

        Assert.Contains($"translateX({expected.ToString(System.Globalization.CultureInfo.InvariantCulture)}px)", css);
    }

    [Fact]
    public void A_logical_operator_over_something_unknown_stays_unknown()
    {
        // The half that must not change: an unresolvable left side cannot settle the operator, so
        // the whole test is undecidable and the branch stays unread.
        var compiled = Compile("""
            const tl = gsap.timeline();
            if (window.matchMedia("(min-width: 1px)").matches || false) {
              tl.to(".a", { x: 100, duration: 1 });
            }
            """);

        Assert.Empty(compiled.Motion.Css);
        Assert.Contains(compiled.Refusals, r => r.What.Contains("could not decide", StringComparison.Ordinal));
    }

    // ---- a refusal that says which construct ----------------------------------------------------
    // "inside a loop, a callback or a function" was three causes in one sentence and a quantity
    // nobody could act on. The construct is in the AST; naming it is what turns 598 tweens into a
    // list of decisions.

    [Fact]
    public void An_unfollowed_forEach_names_the_list_it_could_not_resolve()
    {
        var compiled = Compile("""
            const tl = gsap.timeline();
            document.querySelectorAll(".x").forEach(function (el) {
              tl.to(el, { x: 100, duration: 1 });
            });
            """);

        Assert.Contains(compiled.Refusals,
            r => r.What.Contains(".forEach() over", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unreached_function_body_names_the_function()
    {
        var compiled = Compile("""
            const tl = gsap.timeline();
            function buildTimeline() { tl.to(".a", { x: 100, duration: 1 }); }
            document.addEventListener("click", function () { buildTimeline(); });
            """);

        Assert.Contains(compiled.Refusals,
            r => r.What.Contains("`buildTimeline()`", StringComparison.Ordinal));
    }

    /// <summary>A <c>.catch()</c> runs only when the promise rejects, and the browser that produced
    /// the reference frames did not reject - so it is named rather than followed. A
    /// <c>.then()</c> IS followed; see the deferred-callback tests below.</summary>
    [Fact]
    public void An_unfollowed_promise_callback_names_what_it_was_waiting_on()
    {
        var compiled = Compile("""
            const tl = gsap.timeline();
            document.fonts.ready.catch(function () { tl.to(".a", { x: 100, duration: 1 }); });
            """);

        Assert.Contains(compiled.Refusals,
            r => r.What.Contains(".catch() callback on `document.fonts.ready`", StringComparison.Ordinal));
    }

    [Fact]
    public void A_gsap_hook_is_named_as_the_hook_the_author_wrote()
    {
        var compiled = Compile("""
            const tl = gsap.timeline();
            tl.to(".a", { opacity: 0, duration: 1, onUpdate: function () { tl.to(".b", { x: 1, duration: 1 }); } });
            """);

        Assert.Contains(compiled.Refusals,
            r => r.What.Contains("`onUpdate` callback", StringComparison.Ordinal));
    }

    /// <summary>Nearest, not outermost. A tween inside a forEach inside a .then() is kept out by
    /// the forEach, and naming the .then() would send somebody to fix the wrong thing.</summary>
    [Fact]
    public void The_construct_named_is_the_nearest_one()
    {
        var compiled = Compile("""
            const tl = gsap.timeline();
            document.fonts.ready.catch(function () {
              document.querySelectorAll(".x").forEach(function (el) {
                tl.to(el, { x: 100, duration: 1 });
              });
            });
            """);

        Assert.Contains(compiled.Refusals,
            r => r.What.Contains(".forEach() over", StringComparison.Ordinal));
        Assert.DoesNotContain(compiled.Refusals,
            r => r.What.Contains(".catch() callback", StringComparison.Ordinal));
    }

    [Fact]
    public void An_undecidable_branch_quotes_the_test()
    {
        var compiled = Compile("""
            const tl = gsap.timeline();
            if (window.innerWidth > 100) { tl.to(".a", { x: 100, duration: 1 }); }
            """);

        Assert.Contains(compiled.Refusals,
            r => r.What.Contains("window.innerWidth > 100", StringComparison.Ordinal));
    }

    // ---- decided against is not the same as not reached ------------------------------------------
    // A test that resolves to false, a counted loop that runs zero times, a forEach over an empty
    // list: the motion inside does not run in a browser either, so the report should be silent
    // about it. Reporting it as refused claimed 150 tweens were lost across the carousel blocks,
    // every one of them a feature the composition had switched off.

    [Fact]
    public void A_false_branch_with_no_else_is_silent_rather_than_refused()
    {
        var compiled = Compile("""
            const tl = gsap.timeline();
            if (null) { tl.to(".a", { x: 100, duration: 1 }); }
            """);

        Assert.Empty(compiled.Motion.Css);
        Assert.Empty(compiled.Refusals);
    }

    [Fact]
    public void A_false_branch_takes_the_else_and_says_nothing_of_the_other_side()
    {
        var compiled = Compile("""
            const CONFIG = { fancy: false };
            const tl = gsap.timeline();
            if (CONFIG.fancy) { tl.to(".a", { x: 100, duration: 1 }); }
            else { tl.to(".a", { x: 5, duration: 1 }); }
            """);

        Assert.Contains("translateX(5px)", compiled.Motion.Css);
        Assert.DoesNotContain("translateX(100px)", compiled.Motion.Css);
        Assert.Empty(compiled.Refusals);
    }

    [Fact]
    public void A_counted_loop_that_runs_zero_times_is_silent()
    {
        var compiled = Compile("""
            const CONFIG = { rows: 0 };
            const tl = gsap.timeline();
            for (let i = 0; i < CONFIG.rows; i++) { tl.to(".a", { x: i * 10, duration: 1 }); }
            """);

        Assert.Empty(compiled.Motion.Css);
        Assert.Empty(compiled.Refusals);
    }

    [Fact]
    public void A_forEach_over_an_empty_list_is_silent()
    {
        var compiled = Compile("""
            const tl = gsap.timeline();
            [].forEach(function (n) { tl.to(".a", { x: n, duration: 1 }); });
            """);

        Assert.Empty(compiled.Motion.Css);
        Assert.Empty(compiled.Refusals);
    }

    /// <summary>The half that must not change. A test this compiler cannot decide leaves BOTH
    /// sides unread and names them, because either might be the one that runs.</summary>
    [Fact]
    public void An_undecidable_branch_still_names_both_sides()
    {
        var compiled = Compile("""
            const tl = gsap.timeline();
            if (window.innerWidth > 100) { tl.to(".a", { x: 100, duration: 1 }); }
            else { tl.to(".a", { x: 5, duration: 1 }); }
            """);

        Assert.Empty(compiled.Motion.Css);
        Assert.Contains(compiled.Refusals, r => r.What.Contains("2 .to() call(s)", StringComparison.Ordinal));
    }

    // ---- a write THROUGH an object ---------------------------------------------------------------
    // This used to do nothing at all, so an object kept the value its literal was written with and
    // a tween reading it compiled to a number the browser had already overwritten. A silently
    // wrong value, which is the one outcome this compiler is arranged to avoid.

    [Fact]
    public void A_value_written_into_an_object_is_the_one_a_tween_reads()
    {
        var css = Css("""
            const DATA = { n: 10 };
            DATA.n = 99;
            const tl = gsap.timeline();
            tl.to(".a", { x: DATA.n, duration: 1, ease: "none" });
            """);

        Assert.Contains("translateX(99px)", css);
        Assert.DoesNotContain("translateX(10px)", css);
    }

    [Fact]
    public void A_write_through_a_nested_object_is_followed_too()
    {
        var css = Css("""
            const CONFIG = { grid: { rows: 2 } };
            CONFIG.grid.rows = 40;
            const tl = gsap.timeline();
            tl.to(".a", { x: CONFIG.grid.rows, duration: 1, ease: "none" });
            """);

        Assert.Contains("translateX(40px)", css);
    }

    [Fact]
    public void A_write_at_a_key_this_cannot_resolve_poisons_the_whole_object()
    {
        // The conservative half. An unknowable key could be any of them, so none of them can be
        // trusted afterwards - including one the literal still appears to state.
        var compiled = Compile("""
            const DATA = { n: 10 };
            DATA[window.which] = 1;
            const tl = gsap.timeline();
            tl.to(".a", { x: DATA.n, duration: 1 });
            """);

        Assert.DoesNotContain("translateX(10px)", compiled.Motion.Css);
        Assert.Contains(compiled.Refusals, r => r.What.Contains("not a plain number", StringComparison.Ordinal));
    }

    [Fact]
    public void A_counter_on_an_object_is_stepped()
    {
        var css = Css("""
            const S = { t: 0 };
            S.t += 2;
            S.t++;
            const tl = gsap.timeline();
            tl.to(".a", { x: 10, duration: 1, ease: "none" }, S.t);
            """);

        // Three seconds in on a four-second timeline: the stop sits at 75%.
        Assert.Contains("75%", css);
    }

    // ---- equality against null -------------------------------------------------------------------
    // How this corpus asks whether an optional feature was configured. Decided only when a side is
    // known to be null or undefined; `<`, `<=`, `>` and `>=` are left alone because they coerce.

    [Theory]
    [InlineData("E.a != null", "{ a: 1 }", true)]
    [InlineData("E.a != null", "{ a: null }", false)]
    [InlineData("E.a == null", "{ a: null }", true)]
    [InlineData("E.a === null", "{ a: null }", true)]
    [InlineData("E.a === null", "{ a: 1 }", false)]
    [InlineData("E.a !== null", "{ a: 1 }", true)]
    public void A_test_against_null_is_decided(string test, string data, bool taken)
    {
        var css = Css($$"""
            const E = {{data}};
            const tl = gsap.timeline();
            if ({{test}}) { tl.to(".a", { x: 7, duration: 1, ease: "none" }); }
            else { tl.to(".a", { x: 3, duration: 1, ease: "none" }); }
            """);

        Assert.Contains(taken ? "translateX(7px)" : "translateX(3px)", css);
    }

    /// <summary>`null == undefined` is true and `null === undefined` is false, which is why the two
    /// cannot be the same value here however alike they look.</summary>
    [Theory]
    [InlineData("null == undefined", true)]
    [InlineData("null === undefined", false)]
    [InlineData("undefined == null", true)]
    [InlineData("0 == null", false)]
    [InlineData("\"\" == null", false)]
    public void The_two_kinds_of_nothing_are_told_apart(string test, bool taken)
    {
        var css = Css($$"""
            const tl = gsap.timeline();
            if ({{test}}) { tl.to(".a", { x: 7, duration: 1, ease: "none" }); }
            else { tl.to(".a", { x: 3, duration: 1, ease: "none" }); }
            """);

        Assert.Contains(taken ? "translateX(7px)" : "translateX(3px)", css);
    }

    [Fact]
    public void A_relational_operator_against_null_is_still_refused()
    {
        // `null >= 0` is TRUE in JavaScript, because relational operators coerce where equality
        // does not. Nothing here models that, so the branch stays unread rather than guessed.
        var compiled = Compile("""
            const tl = gsap.timeline();
            if (null >= 0) { tl.to(".a", { x: 7, duration: 1 }); }
            """);

        Assert.Empty(compiled.Motion.Css);
        Assert.Contains(compiled.Refusals, r => r.What.Contains("could not decide", StringComparison.Ordinal));
    }

    [Fact]
    public void A_test_against_null_over_something_unknown_stays_undecided()
    {
        var compiled = Compile("""
            const tl = gsap.timeline();
            if (window.foo == null) { tl.to(".a", { x: 7, duration: 1 }); }
            """);

        Assert.Empty(compiled.Motion.Css);
        Assert.Contains(compiled.Refusals, r => r.What.Contains("could not decide", StringComparison.Ordinal));
    }

    // ---- a method name is not a tween ------------------------------------------------------------
    // to, set, from and fromTo are four of the most ordinary method names in JavaScript, and nine
    // corpus blocks drive a three.js scene with them. All 37 were counted as motion this compiler
    // had dropped. A report that invents losses is as bad as one that hides them.

    [Fact]
    public void A_three_js_call_that_shares_a_tween_name_is_not_counted_as_motion()
    {
        var compiled = Compile("""
            const tl = gsap.timeline();
            tl.to(".a", { x: 1, duration: 1 });
            function scene() {
              group.rotation.set(0, 1, 0);
              camera.position.set(0, 0, 5);
              Float32Array.from(points);
            }
            """);

        Assert.DoesNotContain(compiled.Refusals, r => r.What.Contains("call(s)", StringComparison.Ordinal));
    }

    /// <summary>The generous half, and the direction to err in: a bare name this walk never
    /// resolved might be a timeline built somewhere it could not follow, so it is still reported.</summary>
    [Fact]
    public void A_bare_receiver_this_never_resolved_is_still_reported()
    {
        var compiled = Compile("""
            const tl = gsap.timeline();
            tl.to(".a", { x: 1, duration: 1 });
            function build() { other.to(".b", { x: 1, duration: 1 }); }
            """);

        Assert.Contains(compiled.Refusals, r => r.What.Contains("`build()`", StringComparison.Ordinal));
    }

    /// <summary>A style write does not change which element a name refers to. Poisoning the
    /// binding for one cost mk-background both its size tweens, because the target stopped
    /// resolving - the regression that found this.</summary>
    [Fact]
    public void A_style_write_leaves_the_element_binding_alone()
    {
        var css = Css("""
            const card = document.querySelector(".a");
            card.style.opacity = "0.5";
            const tl = gsap.timeline();
            tl.to(card, { x: 100, duration: 1, ease: "none" });
            """);

        Assert.Contains("translateX(100px)", css);
    }

    // ---- filter, as numbers ----------------------------------------------------------------------
    // The engine has animated it since CupriFace 0.39.0 (#291), filed from this corpus after
    // measuring that it painted statically and never moved. 104 tweens across 16 blocks, and 92 of
    // the corpus's filter values are a single blur().

    [Fact]
    public void A_blur_tween_becomes_a_filter_keyframe()
    {
        var css = Css("""
            const tl = gsap.timeline();
            tl.fromTo(".a", { filter: "blur(20px)" }, { filter: "blur(0px)", duration: 1, ease: "none" });
            """);

        Assert.Contains("filter: blur(20px)", css);
        Assert.Contains("filter: blur(0px)", css);
    }

    /// <summary>`none` is every function at its identity, which is what gives a tween TO a blur a
    /// zero to travel from. 38 corpus values are `none`.</summary>
    [Fact]
    public void A_tween_to_a_blur_starts_from_no_blur()
    {
        var css = Css("""
            const tl = gsap.timeline();
            tl.to(".a", { filter: "blur(8px)", duration: 1, ease: "none" });
            """);

        Assert.Contains("filter: blur(0px)", css);
        Assert.Contains("filter: blur(8px)", css);
    }

    /// <summary>And the other way: a function that never leaves its identity is dropped, so a
    /// blur-out does not write five no-op functions beside the one that moves.</summary>
    [Fact]
    public void A_filter_function_that_never_moves_is_not_written()
    {
        var css = Css("""
            const tl = gsap.timeline();
            tl.fromTo(".a", { filter: "blur(8px)" }, { filter: "none", duration: 1, ease: "none" });
            """);

        Assert.Contains("filter: blur(8px)", css);
        Assert.DoesNotContain("saturate", css);
        Assert.DoesNotContain("grayscale", css);
    }

    [Fact]
    public void Two_filter_functions_keep_their_order()
    {
        var css = Css("""
            const tl = gsap.timeline();
            tl.fromTo(".a", { filter: "blur(0px) brightness(1)" },
                { filter: "blur(6px) brightness(1.5)", duration: 1, ease: "none" });
            """);

        Assert.Contains("filter: blur(6px) brightness(1.5)", css);
    }

    /// <summary>`brightness(150%)` and `brightness(1.5)` are the same filter, and only one of them
    /// interpolates against a bare number.</summary>
    [Fact]
    public void A_ratio_in_percent_becomes_the_ratio()
    {
        var css = Css("""
            const tl = gsap.timeline();
            tl.to(".a", { filter: "brightness(150%)", duration: 1, ease: "none" });
            """);

        Assert.Contains("filter: brightness(1.5)", css);
    }

    [Fact]
    public void A_filter_function_that_is_not_numbers_is_refused_with_the_value_quoted()
    {
        var compiled = Compile("""
            const tl = gsap.timeline();
            tl.to(".a", { filter: "drop-shadow(0 0 2px black)", duration: 1 });
            """);

        Assert.Contains(compiled.Refusals,
            r => r.What.Contains("drop-shadow(0 0 2px black)", StringComparison.Ordinal));
        Assert.DoesNotContain("filter:", compiled.Motion.Css);
    }

    // ---- z-index ---------------------------------------------------------------------------------
    // Honoured among siblings since CupriFace 0.39.0 (#290), filed from this corpus after measuring
    // that 258 declarations across 71 blocks did nothing at all, silently.

    [Fact]
    public void A_stacking_order_set_outright_is_carried()
    {
        var css = Css("""
            const tl = gsap.timeline();
            tl.set(".a", { zIndex: 2 });
            tl.to(".a", { opacity: 0, duration: 1, ease: "none" });
            """);

        Assert.Contains("z-index: 2", css);
    }

    /// <summary>CSS interpolates an integer by rounding, and 1.3333 is a value no stacking context
    /// has.</summary>
    [Fact]
    public void A_stacking_order_is_written_as_a_whole_number()
    {
        var css = Css("""
            const tl = gsap.timeline();
            tl.to(".a", { zIndex: 3, duration: 1, ease: "none" });
            """);

        Assert.DoesNotContain("z-index: 1.", css);
        Assert.DoesNotContain("z-index: 2.", css);
        Assert.Contains("z-index: 3", css);
    }

    // ---- a promise callback, walked after the synchronous pass -----------------------------------
    // us-map's shape, and a common one: a composition fetches its data and builds its whole
    // timeline inside the callback. The walker bound `buildTimeline` as a routine and never reached
    // the call, so 76 tweens across 18 blocks were reported as being in the body of a function
    // nothing called - true, and not the useful half of the truth.

    [Fact]
    public void A_timeline_built_inside_a_then_callback_is_carried()
    {
        var css = Css("""
            const tl = gsap.timeline();
            fetch("/data.json").then(function (data) {
              tl.to(".a", { x: 100, duration: 1, ease: "none" }, 0);
            });
            """);

        Assert.Contains("translateX(100px)", css);
    }

    [Fact]
    public void A_function_called_from_a_then_callback_is_followed_into()
    {
        var compiled = Compile("""
            const tl = gsap.timeline();
            function build() { tl.to(".a", { x: 100, duration: 1, ease: "none" }, 0); }
            fetch("/x").then((r) => r.json()).then(function (d) { build(); });
            """);

        Assert.Contains("translateX(100px)", compiled.Motion.Css);
        Assert.DoesNotContain(compiled.Refusals, r => r.What.Contains("`build()`", StringComparison.Ordinal));
    }

    /// <summary>Deferred, not walked in place. A callback cannot run until the synchronous script
    /// has finished, so its tweens come after the top-level ones on the clock - and walking it
    /// where it is written would put them wherever the fetch happens to sit in the source.</summary>
    [Fact]
    public void A_deferred_callbacks_tweens_land_after_the_synchronous_ones()
    {
        var css = Css("""
            const tl = gsap.timeline();
            fetch("/x").then(function () { tl.to(".a", { x: 50, duration: 1, ease: "none" }); });
            tl.to(".a", { x: 10, duration: 1, ease: "none" });
            """);

        // The appended tween runs 0-1s and the deferred one 1-2s, so at the halfway stop the
        // element is at 10px rather than on its way to 50.
        Assert.Contains("50% { transform: translateX(10px); }", css);
    }

    /// <summary>The resolved value is not knowable, and a tween that depends on it is refused by
    /// the machinery that already does that - one at a time, naming the binding, while the tweens
    /// that do not depend on it are carried.</summary>
    [Fact]
    public void A_tween_depending_on_the_resolved_value_is_refused_and_the_rest_carried()
    {
        var compiled = Compile("""
            const tl = gsap.timeline();
            fetch("/x").then(function (d) {
              tl.to(".a", { x: 100, duration: 1, ease: "none" }, 0);
              tl.to(".b", { x: d.offset, duration: 1, ease: "none" }, 0);
            });
            """, """<div class="a"></div><div class="b"></div>""");

        Assert.Contains("translateX(100px)", compiled.Motion.Css);
        Assert.Contains(compiled.Refusals, r => r.What.Contains("not a plain number", StringComparison.Ordinal));
    }

    // ---- a write through a list ------------------------------------------------------------------
    // The first write-through poisoned the root when the path went through a list, and
    // `CONFIG.series[1].color = blob2` then cost mk-line-graph every other key of its CONFIG -
    // including the `showValues: true` that decides two branches three hundred lines later.

    [Fact]
    public void A_write_through_a_list_leaves_its_siblings_readable()
    {
        var css = Css("""
            const CONFIG = { series: [{ c: null }, { c: null }], show: true };
            CONFIG.series[1].c = 5;
            const tl = gsap.timeline();
            if (CONFIG.show) { tl.to(".a", { x: 7, duration: 1, ease: "none" }); }
            """);

        Assert.Contains("translateX(7px)", css);
    }

    [Fact]
    public void A_write_through_a_list_takes_effect()
    {
        var css = Css("""
            const CONFIG = { series: [{ c: 1 }, { c: 2 }] };
            CONFIG.series[1].c = 50;
            const tl = gsap.timeline();
            tl.to(".a", { x: CONFIG.series[1].c, duration: 1, ease: "none" });
            """);

        Assert.Contains("translateX(50px)", css);
    }

    // ---- comparison between two numbers ----------------------------------------------------------
    // The same table already existed in Reader.Continues for loop tests only, which is how a
    // counted loop was unrolled while `if (RACE_SECONDS > 0)` over the same arithmetic went unread.

    [Theory]
    [InlineData("4 > 1", true)]
    [InlineData("0 > 1", false)]
    [InlineData("2 >= 2", true)]
    [InlineData("1 < 2", true)]
    [InlineData("2 <= 1", false)]
    [InlineData("3 == 3", true)]
    [InlineData("3 != 3", false)]
    public void A_comparison_between_two_numbers_is_decided(string test, bool taken)
    {
        var css = Css($$"""
            const tl = gsap.timeline();
            if ({{test}}) { tl.to(".a", { x: 7, duration: 1, ease: "none" }); }
            else { tl.to(".a", { x: 3, duration: 1, ease: "none" }); }
            """);

        Assert.Contains(taken ? "translateX(7px)" : "translateX(3px)", css);
    }

    /// <summary>bar-chart-race's shape: a ternary over a comparison feeding a binding that a later
    /// branch tests. Neither link folded before, so the whole race went unread.</summary>
    [Fact]
    public void A_ternary_over_a_comparison_folds_and_so_does_the_branch_that_reads_it()
    {
        var css = Css("""
            const T = 4;
            const RACE = T > 1 ? (T - 1) * 2 : 0;
            const tl = gsap.timeline();
            if (RACE > 0) { tl.to(".a", { x: RACE, duration: 1, ease: "none" }); }
            """);

        Assert.Contains("translateX(6px)", css);
    }

    [Fact]
    public void The_length_of_a_stated_list_is_comparable()
    {
        var css = Css("""
            const steps = [1, 2, 3];
            const tl = gsap.timeline();
            if (steps.length > 2) { tl.to(".a", { x: 7, duration: 1, ease: "none" }); }
            """);

        Assert.Contains("translateX(7px)", css);
    }

    /// <summary>The half that must not change, and the reason relational folding is guarded on both
    /// sides being numbers: `null >= 0` is TRUE in JavaScript because relational operators coerce
    /// where equality does not, and nothing here models that.</summary>
    [Fact]
    public void A_comparison_against_null_is_still_refused()
    {
        var compiled = Compile("""
            const tl = gsap.timeline();
            if (null >= 0) { tl.to(".a", { x: 7, duration: 1 }); }
            """);

        Assert.Empty(compiled.Motion.Css);
        Assert.Contains(compiled.Refusals, r => r.What.Contains("could not decide", StringComparison.Ordinal));
    }

    /// <summary>`.then(build)` as well as `.then(function () { ... })`. Four corpus blocks write the
    /// first form, and code-slice-hero's whole composition is behind one of them.</summary>
    [Fact]
    public void A_named_function_passed_to_then_is_deferred_too()
    {
        var compiled = Compile("""
            const tl = gsap.timeline();
            function build() { tl.to(".a", { x: 100, duration: 1, ease: "none" }, 0); }
            document.fonts.load("700 20px X").then(build);
            """);

        Assert.Contains("translateX(100px)", compiled.Motion.Css);
        Assert.DoesNotContain(compiled.Refusals, r => r.What.Contains("`build()`", StringComparison.Ordinal));
    }

    /// <summary>A timeline DECLARED has always been recognised; one MADE by assignment was not, so
    /// a timeline declared in one scope and built in another was not a timeline at all and every
    /// tween on it fell through to the unreached report. code-slice-hero's whole composition is
    /// behind that one line.</summary>
    [Fact]
    public void A_timeline_made_by_assignment_is_a_timeline()
    {
        var css = Css("""
            let tl;
            function build() {
              tl = gsap.timeline({ paused: true });
              tl.to(".a", { x: 100, duration: 1, ease: "none" }, 0);
            }
            document.fonts.load("x").then(build);
            """);

        Assert.Contains("translateX(100px)", css);
    }

    // ---- an element the stylesheet hides and the timeline reveals --------------------------------
    // CupriFace has painted `visibility: hidden` since 0.41.0 and still does not animate it, so an
    // element hidden by CSS and revealed by the script would stay hidden for the whole render.
    // Three corpus blocks do exactly that and all three FELL when the engine learned the property -
    // ai-chat-reveal by 41 points - because the engine getting it right is what exposed the half
    // this compiler was dropping.

    private static string Page(string css, string script, string markup = """<div id="a"></div>""") =>
        Translator.Of($"<html><head><style>{css}</style></head><body>{markup}"
            + $"<script>{script}</script></body></html>").Motion.Css;

    [Fact]
    public void An_autoAlpha_reveal_writes_the_element_visible()
    {
        var css = Page("#a { visibility: hidden; opacity: 0; }",
            """const tl = gsap.timeline(); tl.set("#a", { autoAlpha: 1 }, 2);""");

        Assert.Contains("visibility: visible", css);
    }

    /// <summary>`autoAlpha` is GSAP's own pairing of the two, so a positive one is a reveal even
    /// though nothing in the bag says "visibility".</summary>
    [Fact]
    public void A_bare_visibility_reveal_is_carried_even_with_no_other_property()
    {
        // Nothing here produces an Amount at all, so there is no tween - and the reveal is the
        // whole point of the call.
        var css = Page("#a { visibility: hidden; }",
            """const tl = gsap.timeline(); tl.set("#a", { visibility: "visible" }, 2);""");

        Assert.Contains("visibility: visible", css);
    }

    [Theory]
    [InlineData("""tl.set("#a", { visibility: "hidden" }, 2);""")]
    [InlineData("""tl.set("#a", { autoAlpha: 0 }, 2);""")]
    public void A_hide_is_not_turned_into_a_reveal(string call)
    {
        // The half that must not change. The engine cannot animate the property either way, so
        // claiming a hide as a reveal would show something the composition had put away.
        var css = Page("#a { }", "const tl = gsap.timeline(); " + call);

        Assert.DoesNotContain("visibility: visible", css);
    }

    /// <summary>The override goes LAST, so it outranks the author's own rule on source order -
    /// which is what beats an id selector without inventing specificity.</summary>
    [Fact]
    public void The_reveal_is_written_after_the_animation_rules()
    {
        var css = Page("#a { visibility: hidden; opacity: 0; }",
            """const tl = gsap.timeline(); tl.to("#a", { autoAlpha: 1, duration: 1 }, 0);""");

        Assert.True(css.IndexOf("visibility: visible", StringComparison.Ordinal)
            > css.IndexOf("animation:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_reveal_says_so_in_the_report()
    {
        var compiled = Translator.Of(
            """<html><head><style>#a { visibility: hidden; opacity: 0; }</style></head>"""
            + """<body><div id="a"></div><script>const tl = gsap.timeline();"""
            + """ tl.set("#a", { autoAlpha: 1 }, 2);</script></body></html>""");

        Assert.Contains(compiled.Refusals,
            r => r.What.Contains("revealed by the timeline", StringComparison.Ordinal));
    }
}
