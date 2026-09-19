namespace CupriLex.Conformance;

/// <summary>One document: what is in the body, and the rules that style it.</summary>
public sealed record Doc(string Markup, string Css);

/// <summary>An animation to run for the third column.</summary>
/// <param name="Css">A <c>@keyframes</c> and the rule that uses it.</param>
/// <param name="Seconds">A time by which it should have visibly moved.</param>
public sealed record Animation(string Css, double Seconds);

/// <summary>
/// One thing to find out: two documents that differ ONLY in the thing under test.
/// </summary>
/// <param name="Property">The CSS property or feature, for the report.</param>
/// <param name="Value">The value probed, recorded beside the answer so a "no" can be
/// re-examined. A property that happens to paint identically for the value chosen would read as
/// unsupported, so the value is part of the answer rather than an implementation detail.</param>
/// <param name="With">The document that uses it.</param>
/// <param name="Without">The control.</param>
/// <param name="Animation">Set when the property is worth keyframing.</param>
/// <remarks>
/// <para>The first version of this type let only the DECLARATION differ, with the markup shared.
/// That made several cases structurally incapable of detecting anything - the two documents were
/// byte-identical - and the probe cheerfully reported that <c>width</c> does not paint. A probe
/// that lies is worse than no probe, so the pair is explicit now and each half is written out.</para>
/// </remarks>
/// <param name="ControlPaintsNothing">True for a probe whose question is "does this appear at
/// all", where the control is deliberately empty. Everything else must have a control that paints
/// something, or it is not measuring what it claims to.</param>
/// <param name="At">When to compare. Almost always 0, but a case about TIMING has to be sampled at
/// a moment where the two documents should disagree - the animation-delay probe compares nothing
/// at all at t=0, because both halves are still sitting on their first keyframe.</param>
public sealed record Case(string Property, string Value, Doc With, Doc Without,
    Animation? Animation = null, bool ControlPaintsNothing = false, double At = 0);

/// <summary>
/// The questions worth asking, chosen from what the corpus actually uses.
///
/// <para>Every percentage below is measured - see docs/CORPUS.md. A property nine blocks use is
/// not worth a rewrite rule; one that 80% use is worth knowing about precisely.</para>
/// </summary>
public static class Cases
{
    private const string Div = "<div class=\"p\"></div>";
    private const string Text = "<div class=\"p\">Handgloves</div>";
    private const string Box = "width:120px;height:80px;background:#d9642a;";
    private const string Ink = "font-size:40px;color:#d9642a;";

    /// <summary>A keyframed move. One animation per element is all the engine runs, so each case
    /// uses exactly one.</summary>
    private static Animation Moves(string property, string from, string to) => new(
        $"@keyframes probe {{ from {{ {property}:{from}; }} to {{ {property}:{to}; }} }}"
        + " .p { animation: probe 2s linear both; }", 2);

    /// <summary>The common shape: same markup, one declaration added to <c>.p</c>.
    ///
    /// <para>Bare declarations are wrapped here. Passing them straight into the stylesheet put
    /// them at top level with no selector, so nothing was styled, both documents rendered blank,
    /// and the probe reported that `opacity` does not paint.</para></summary>
    private static Case Added(string property, string value, string baseCss, string extra,
        string markup = Div, Animation? animation = null, bool controlPaintsNothing = false) =>
        new(property, value,
            new Doc(markup, Rule(baseCss) + Rule(extra)),
            new Doc(markup, Rule(baseCss)), animation, controlPaintsNothing);

    /// <summary>Wrap a bare declaration list in `.p { }`; leave anything that already has a
    /// selector alone.</summary>
    private static string Rule(string css) =>
        css.Contains('{') || css.Trim().Length == 0 ? css : ".p{" + css + "}";

    public static readonly IReadOnlyList<Case> All =
    [
        // ---- the four believed to animate, as regression --------------------------------------
        new("width", "120px -> 400px", new Doc(Div, ".p{width:400px;height:80px;background:#d9642a;}"),
            new Doc(Div, ".p{" + Box + "}"), Moves("width", "20px", "300px")),
        new("height", "80px -> 160px", new Doc(Div, ".p{width:120px;height:160px;background:#d9642a;}"),
            new Doc(Div, ".p{" + Box + "}"), Moves("height", "20px", "160px")),
        Added("opacity", "0.25", Box, "opacity:0.25;", animation: Moves("opacity", "1", "0")),
        Added("transform", "translateX(80px)", Box, "transform:translateX(80px);",
            animation: Moves("transform", "translateX(0)", "translateX(120px)")),

        // ---- the ones that paint but do NOT animate: the dangerous half -----------------------
        Added("left", "80px", Box + "position:absolute;", "left:80px;",
            animation: Moves("left", "0px", "120px")),
        Added("margin-left", "80px", Box, "margin-left:80px;",
            animation: Moves("margin-left", "0px", "120px")),
        new("background-color", "#d9642a -> #3f6fd8",
            new Doc(Div, ".p{width:120px;height:80px;background-color:#3f6fd8;}"),
            new Doc(Div, ".p{width:120px;height:80px;background-color:#d9642a;}"),
            Moves("background-color", "#d9642a", "#3f6fd8")),
        new("color", "#d9642a -> #3f6fd8", new Doc(Text, ".p{font-size:40px;color:#3f6fd8;}"),
            new Doc(Text, ".p{" + Ink + "}"), Moves("color", "#d9642a", "#3f6fd8")),

        // ---- the corpus unknowns: each is a rewrite rule that may not be needed ---------------
        Added("clip-path", "inset(0 40% 0 0)", Box, "clip-path:inset(0 40% 0 0);"),          // 21%
        Added("filter", "blur(6px)", Box, "filter:blur(6px);"),                               // 19%
        Added("filter", "brightness(0.4)", Box, "filter:brightness(0.4);"),
        Added("backdrop-filter", "blur(6px)",                                                 // 6%
            ".under{position:absolute;width:220px;height:120px;"
            + "background:linear-gradient(90deg,#d9642a,#3f6fd8);}"
            + ".p{position:absolute;left:40px;width:120px;height:80px;}",
            ".p{backdrop-filter:blur(6px);}",
            markup: "<div class=\"under\"></div>" + Div),
        Added("mix-blend-mode", "difference",                                                 // 5%
            ".under{position:absolute;width:220px;height:120px;background:#8899aa;}"
            + ".p{position:absolute;width:120px;height:80px;background:#d9642a;}",
            ".p{mix-blend-mode:difference;}",
            markup: "<div class=\"under\"></div>" + Div),
        new("display", "grid",                                                                // 14%
            new Doc("<div class=\"p\"><i></i><i></i></div>",
                "i{width:40px;height:40px;background:#d9642a;display:block;}"
                + ".p{display:grid;grid-template-columns:1fr 1fr;width:200px;height:80px;}"),
            new Doc("<div class=\"p\"><i></i><i></i></div>",
                "i{width:40px;height:40px;background:#d9642a;display:block;}"
                + ".p{display:block;width:200px;height:80px;}")),
        Added("transform", "rotateY(50deg)", Box, "transform:rotateY(50deg);"),               // 28% use 3D
        Added("transform", "translate3d(60px,20px,0)", Box, "transform:translate3d(60px,20px,0);"),
        Added("perspective", "400px", Box + "transform:rotateX(45deg);", "perspective:400px;"),
        Added("box-shadow", "0 10px 30px #000", Box, "box-shadow:0 10px 30px #000;"),
        Added("text-shadow", "0 4px 8px #000", Ink, "text-shadow:0 4px 8px #000;", markup: Text),
        new("gap", "0 -> 24px",
            new Doc("<div class=\"p\"><i></i><i></i></div>",
                "i{width:40px;height:40px;background:#d9642a;}.p{display:flex;gap:24px;width:200px;}"),
            new Doc("<div class=\"p\"><i></i><i></i></div>",
                "i{width:40px;height:40px;background:#d9642a;}.p{display:flex;gap:0;width:200px;}")),
        new("align-self", "center",                                                           // flex
            new Doc("<div class=\"row\"><div class=\"p\"></div></div>",
                ".row{display:flex;height:140px;width:200px;}" + ".p{align-self:center;" + Box + "}"),
            new Doc("<div class=\"row\"><div class=\"p\"></div></div>",
                ".row{display:flex;height:140px;width:200px;}" + ".p{" + Box + "}")),

        // ---- svg: at 32% of blocks, the single biggest unknown --------------------------------
        new("<svg>", "inline rect",
            new Doc("<div class=\"p\"><svg width=\"100\" height=\"60\" xmlns=\"http://www.w3.org/2000/svg\">"
                    + "<rect width=\"100\" height=\"60\" fill=\"#d9642a\"/></svg></div>",
                ".p{width:120px;height:80px;}"),
            new Doc("<div class=\"p\"></div>", ".p{width:120px;height:80px;}"),
            ControlPaintsNothing: true),

        // ---- known limits, kept so a release that FIXES one is noticed ------------------------
        Added("letter-spacing", "12px", Ink, "letter-spacing:12px;", markup: Text),           // 80%
        Added("line-height", "72px", "font-size:24px;color:#d9642a;", "line-height:72px;",    // 59%
            markup: Text),
        new("background", "repeating-linear-gradient",                                        // 2%
            new Doc(Div, ".p{width:120px;height:80px;"
                         + "background:repeating-linear-gradient(90deg,#d9642a 0 12px,#101014 12px 24px);}"),
            new Doc(Div, ".p{width:120px;height:80px;}"), ControlPaintsNothing: true),
        new("background", "linear-gradient",
            new Doc(Div, ".p{width:120px;height:80px;background:linear-gradient(90deg,#d9642a,#3f6fd8);}"),
            new Doc(Div, ".p{width:120px;height:80px;}"), ControlPaintsNothing: true),
        Added("border-left", "8px solid", "width:120px;height:80px;", "border-left:8px solid #d9642a;",
            controlPaintsNothing: true),

        // ---- the trap that would silently break every staggered import ------------------------
        // Both documents animate; the delayed one should still be at its START value at t=0.5.
        // If calc() is honoured they differ there; if it is treated as zero they do not.
        new("animation-delay", "calc(var(--d) + 0.5s)",
            new Doc(Div, ":root{--d:1s;}@keyframes probe{from{width:20px;}to{width:300px;}}"
                         + ".p{height:80px;background:#d9642a;animation:probe 1s linear calc(var(--d) + 0.5s) both;}"),
            new Doc(Div, ":root{--d:1s;}@keyframes probe{from{width:20px;}to{width:300px;}}"
                         + ".p{height:80px;background:#d9642a;animation:probe 1s linear 1.5s both;}"),
            // At 0.5s the literal delay is still holding at 20px. If calc() were honoured the
            // other would be too; if it collapses to zero it is halfway across. Comparing at t=0
            // would show both on their first keyframe and prove nothing.
            At: 0.5),
    ];
}
