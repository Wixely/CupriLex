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

    /// <summary>Two tweens of the same property overlapping cannot both be expressed in one
    /// animation, and one element gets one animation.</summary>
    [Fact]
    public void Overlapping_tweens_of_one_property_refuse_the_second()
    {
        var compiled = Compile("""
            const tl = gsap.timeline();
            tl.to(".a", { x: 100, duration: 4 }, 0);
            tl.to(".a", { x: 200, duration: 4 }, 1);
            """);

        Assert.Contains(compiled.Refusals, r => r.What.Contains("overlaps", StringComparison.Ordinal));
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
            fetch("/x").then(function () { buildTimeline(); });
            """);

        Assert.Contains(compiled.Refusals,
            r => r.What.Contains("`buildTimeline()`", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unfollowed_promise_callback_names_what_it_was_waiting_on()
    {
        var compiled = Compile("""
            const tl = gsap.timeline();
            document.fonts.ready.then(function () { tl.to(".a", { x: 100, duration: 1 }); });
            """);

        Assert.Contains(compiled.Refusals,
            r => r.What.Contains(".then() callback on `document.fonts.ready`", StringComparison.Ordinal));
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
            document.fonts.ready.then(function () {
              document.querySelectorAll(".x").forEach(function (el) {
                tl.to(el, { x: 100, duration: 1 });
              });
            });
            """);

        Assert.Contains(compiled.Refusals,
            r => r.What.Contains(".forEach() over", StringComparison.Ordinal));
        Assert.DoesNotContain(compiled.Refusals,
            r => r.What.Contains(".then() callback", StringComparison.Ordinal));
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
}
