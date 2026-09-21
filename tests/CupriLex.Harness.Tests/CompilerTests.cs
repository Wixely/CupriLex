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

    private static int Stops(string css) => css.Count(c => c == '%');

    // ---- what is refused, and named -----------------------------------------------------------

    [Fact]
    public void A_property_the_engine_cannot_animate_is_refused_by_name()
    {
        var refusals = Compile("""gsap.timeline().to(".a", { backgroundColor: "#fff", duration: 1 });""")
            .Refusals;

        Assert.Contains(refusals, r => r.What.Contains("backgroundColor", StringComparison.Ordinal));
    }

    /// <summary>The case the plan said to find early: a timeline built in a loop. It is refused,
    /// and counted, rather than half-carried.</summary>
    [Fact]
    public void Tweens_built_in_a_loop_are_refused_and_counted()
    {
        var compiled = Compile("""
            const tl = gsap.timeline();
            [1, 2, 3].forEach(function (n) { tl.to(".a", { x: n * 10, duration: 1 }); });
            """);

        Assert.Empty(compiled.Motion.Css);
        Assert.Contains(compiled.Refusals, r => r.What.Contains("loop", StringComparison.Ordinal));
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
}
