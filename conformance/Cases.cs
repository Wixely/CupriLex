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
        // On the PARENT, because that is the element it applies to: a child's rotation is projected
        // through its parent's perspective, and the property on the rotated element itself does
        // nothing in a browser either. The first version wrote it there and could only ever read
        // the parse column.
        new("perspective", "400px on the parent of a rotateX(45deg) child",
            new Doc("<div class=\"box\"><div class=\"p\"></div></div>",
                ".box{perspective:400px;width:120px;height:80px;}.p{" + Box + "transform:rotateX(45deg);}"),
            new Doc("<div class=\"box\"><div class=\"p\"></div></div>",
                ".box{width:120px;height:80px;}.p{" + Box + "transform:rotateX(45deg);}")),
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

        // The shorthand splits its value on spaces and hands each token to the colour parser, so
        // "rgba(198," arrives with an opening parenthesis and no closing one and the parser
        // indexes past the end. In 0.26.1 this THROWS rather than failing to paint, which the
        // probe records as "does not paint" - the matrix cannot say "crashed", but it can say the
        // day it stops. 36 blocks of the corpus carry one, and the two that were found first
        // could not be loaded at all. See docs/TRANSLATION.md.
        Added("border", "1px solid rgba(r, g, b, a)", "width:120px;height:80px;",
            "border:8px solid rgba(217, 100, 42, 1);", controlPaintsNothing: true),
        Added("border", "1px solid rgba(r,g,b,a)", "width:120px;height:80px;",
            "border:8px solid rgba(217,100,42,1);", controlPaintsNothing: true),

        // 115 blocks of 187 write `inset: 0` to make a child fill its parent - an overlay, a
        // backdrop, an end card that covers the composition. The engine reports CF0050 and lays
        // the element out with no size at all, so the thing that was supposed to cover everything
        // covers nothing. The pair below is the evidence for the rewrite: the shorthand against
        // the four longhands it expands to, same document otherwise.
        new("inset", "0 (shorthand)",
            new Doc("<div class=\"box\"><div class=\"p\"></div></div>",
                ".box{position:relative;width:160px;height:100px;}"
                + ".p{position:absolute;inset:0;background:#d9642a;}"),
            new Doc("<div class=\"box\"><div class=\"p\"></div></div>",
                ".box{position:relative;width:160px;height:100px;}"
                + ".p{position:absolute;background:#d9642a;}"),
            ControlPaintsNothing: true),

        new("inset", "0 (four longhands)",
            new Doc("<div class=\"box\"><div class=\"p\"></div></div>",
                ".box{position:relative;width:160px;height:100px;}"
                + ".p{position:absolute;top:0;right:0;bottom:0;left:0;background:#d9642a;}"),
            new Doc("<div class=\"box\"><div class=\"p\"></div></div>",
                ".box{position:relative;width:160px;height:100px;}"
                + ".p{position:absolute;background:#d9642a;}"),
            ControlPaintsNothing: true),

        new("inset", "0 (percent size)",
            new Doc("<div class=\"box\"><div class=\"p\"></div></div>",
                ".box{position:relative;width:160px;height:100px;}"
                + ".p{position:absolute;top:0;left:0;width:100%;height:100%;background:#d9642a;}"),
            new Doc("<div class=\"box\"><div class=\"p\"></div></div>",
                ".box{position:relative;width:160px;height:100px;}"
                + ".p{position:absolute;background:#d9642a;}"),
            ControlPaintsNothing: true),

        // ---- selectors, because a compiled animation is only as good as what it matches -----
        // The compiler resolves every target to a selector: a string the author wrote, or the one
        // a document.querySelector() was given. If the engine matches a narrower set of selectors
        // than a browser, a correct @keyframes lands on nothing at all - and nothing at all is
        // exactly what a still frame looks like.
        new("selector", "descendant (.box .p)",
            new Doc("<div class=\"box\"><div class=\"p\"></div></div>",
                ".p{width:120px;height:80px;}.box .p{background:#d9642a;}"),
            new Doc("<div class=\"box\"><div class=\"p\"></div></div>",
                ".p{width:120px;height:80px;}"),
            ControlPaintsNothing: true),

        new("selector", "id (#p)",
            new Doc("<div id=\"p\"></div>", "#p{width:120px;height:80px;background:#d9642a;}"),
            new Doc("<div id=\"p\"></div>", "#p{width:120px;height:80px;}"),
            ControlPaintsNothing: true),

        new("selector", "compound (.p.q)",
            new Doc("<div class=\"p q\"></div>",
                ".p{width:120px;height:80px;}.p.q{background:#d9642a;}"),
            new Doc("<div class=\"p q\"></div>", ".p{width:120px;height:80px;}"),
            ControlPaintsNothing: true),

        new("selector", "child (.box > .p)",
            new Doc("<div class=\"box\"><div class=\"p\"></div></div>",
                ".p{width:120px;height:80px;}.box > .p{background:#d9642a;}"),
            new Doc("<div class=\"box\"><div class=\"p\"></div></div>",
                ".p{width:120px;height:80px;}"),
            ControlPaintsNothing: true),

        // An animation reached through a descendant selector: the combination the compiler
        // actually emits, rather than the two halves separately.
        new("selector", "animation via descendant",
            new Doc("<div class=\"box\"><div class=\"p\"></div></div>",
                ".p{width:120px;height:80px;background:#d9642a;}"
                + "@keyframes probe{from{opacity:1;}to{opacity:0;}}"
                + ".box .p{animation:probe 2s linear both;}"),
            new Doc("<div class=\"box\"><div class=\"p\"></div></div>",
                ".p{width:120px;height:80px;background:#d9642a;}"),
            new Animation("@keyframes probe2{from{opacity:1;}to{opacity:0;}}"
                          + ".box .p{animation:probe2 2s linear both;}", 2),
            At: 2),

        // ---- the shape the compiler actually emits ------------------------------------------
        // An animated property has to beat the element's own static declaration, or a compiled
        // @keyframes lands on an element whose stylesheet already said opacity:0 and nothing
        // moves. Sampled at t=0, where the animation says 1 and the rule says 0.2.
        new("animation", "beats a static declaration",
            new Doc(Div, ".p{width:120px;height:80px;background:#d9642a;opacity:0.2;}"
                         + "@keyframes probe{0%{opacity:1;}100%{opacity:0.5;}}"
                         + ".p{animation:probe 2s linear both;}"),
            new Doc(Div, ".p{width:120px;height:80px;background:#d9642a;opacity:0.2;}")),

        // The compiler samples eased tweens into stops, and those stops land on percentages like
        // 28.4746%. If the engine wants round numbers, every compiled animation is wrong in a way
        // that no diagnostic would mention.
        new("@keyframes", "fractional percentages",
            new Doc(Div, ".p{width:120px;height:80px;background:#d9642a;}"
                         + "@keyframes probe{0%{opacity:1;}28.4746%{opacity:0.5;}100%{opacity:0;}}"
                         + ".p{animation:probe 2s linear both;}"),
            new Doc(Div, ".p{width:120px;height:80px;background:#d9642a;}"),
            At: 0.5693),

        // Many stops, which is what sampling a curve produces.
        new("@keyframes", "many stops",
            new Doc(Div, ".p{width:120px;height:80px;background:#d9642a;}"
                         + "@keyframes probe{0%{transform:translateX(0px);}"
                         + "12.5%{transform:translateX(10px);}25%{transform:translateX(30px);}"
                         + "37.5%{transform:translateX(60px);}50%{transform:translateX(100px);}"
                         + "62.5%{transform:translateX(130px);}75%{transform:translateX(150px);}"
                         + "87.5%{transform:translateX(160px);}100%{transform:translateX(165px);}}"
                         + ".p{animation:probe 2s linear both;}"),
            new Doc(Div, ".p{width:120px;height:80px;background:#d9642a;}"),
            At: 1),

        // The compiler rewrites colours to hex to get past a crash elsewhere. That is only safe
        // if the engine paints the two forms identically - the pair below asks it directly, with
        // an alpha that does not divide evenly into 255.
        new("rgba vs hex", "text colour, alpha 0.65",
            new Doc(Text, ".p{font-size:40px;color:rgba(230,237,243,0.65);}"),
            new Doc(Text, ".p{font-size:40px;color:#e6edf3a6;}")),

        // The same colour with the alpha TRUNCATED rather than rounded - 0.65 * 255 is 165.75,
        // and the engine's own cast makes that 165. These two should be indistinguishable, and a
        // "yes" here means the rewrite is one level off on every colour it touches.
        new("rgba vs hex", "truncated alpha, should match",
            new Doc(Text, ".p{font-size:40px;color:rgba(230,237,243,0.65);}"),
            new Doc(Text, ".p{font-size:40px;color:#e6edf3a5;}")),

        new("rgba vs hex", "background, alpha 0.65",
            new Doc(Div, ".p{width:120px;height:80px;background:rgba(230,237,243,0.65);}"),
            new Doc(Div, ".p{width:120px;height:80px;background:#e6edf3a6;}")),

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

        // ---- the format every font pipeline emits ---------------------------------------------
        // Asked through a REGISTERED FILE rather than an @font-face url, because the question is
        // whether the engine can decode WOFF 2 at all and a url would also be asking about path
        // resolution. Probe.FontFiles registers inter-latin-400-normal.woff2 for every case; this
        // one is the only case that asks for that family by name.
        //
        // The control names a family that is deliberately NOT registered, and that choice is the
        // whole case. Naming a face the harness already loads does not work: an unresolved family
        // does not fall back to it, so the two halves differ whether or not the .woff2 decoded and
        // the case reads 'yes' on an engine that cannot read WOFF 2 at all. Against an absent
        // control both halves land on the SAME fallback when the decode fails, and differ only
        // when 'Inter' really registered.
        new("@font-face", "WOFF 2, registered face",
            new Doc(Text, ".p{font-family:'Inter';font-size:40px;color:#d9642a;}"),
            new Doc(Text, ".p{font-family:'NoSuchFaceExists';font-size:40px;color:#d9642a;}")),

        // ---- one unsupported function in a transform LIST ---------------------------------------
        // The compiler writes a transform as a fixed list of components, so a single element's
        // animation can carry `translateX(40px) ... skewX(0deg)` in one declaration. skewX is
        // reported unsupported on 9 corpus blocks. Whether that costs the skew or costs the WHOLE
        // declaration is the difference between a cosmetic gap and every transform in those blocks
        // silently not applying, and nothing here had ever asked.
        //
        // The control has no transform at all, so a "yes" means the supported components still
        // moved the element and a "NO" means one unknown function threw the list away.
        Added("transform", "translateX + an unsupported skewX", Box,
            "transform:translateX(80px) skewX(20deg);"),

        Added("transform", "skewX(20deg) alone", Box, "transform:skewX(20deg);"),

        // ---- the other half of the font question: how the bytes arrive -------------------------
        // An @font-face src the document carries itself. docs/CORPUS.md says a data: URI works and
        // proposes it as the rewrite for the 44 blocks that fetch a face over the network, which
        // makes it load-bearing enough to measure rather than believe.
        new("@font-face", "src: data: URI",
            new Doc(Text, FaceRule("DataFace", DataUri) + ".p{font-family:'DataFace';"
                          + "font-size:40px;color:#d9642a;}"),
            new Doc(Text, ".p{font-family:'NoSuchFaceExists';font-size:40px;color:#d9642a;}")),

        // ---- what dissecting one block found ---------------------------------------------------
        // x-post scored 40% of content with its motion compiled correctly, because the engine drew
        // the card at the top-right corner instead of the centre. Every case below is one of the
        // behaviours that put it there, measured on 0.28.1 and absent from this matrix until then.
        // None of them is a property the corpus survey could have flagged: they are the ordinary
        // CSS around the properties, which is why the matrix had no question about any of them.
        // See docs/HARNESS.md, "What dissecting one block found".

        // A percentage translate. translate(-50%, -50%) is the commonest centring idiom in the
        // corpus - 31 blocks author one - and the compiler emits GSAP's xPercent/yPercent as one.
        // The px form a few lines up paints and animates; this one is accepted and does nothing,
        // in a static declaration and in @keyframes alike.
        Added("transform", "translateX(-50%)", Box, "transform:translateX(-50%);",
            animation: Moves("transform", "translateX(0)", "translateX(-50%)")),

        // top: 50% against a static parent with no height of its own. A browser positions an
        // absolute element against its nearest POSITIONED ancestor, or the initial containing
        // block when there is none, so the card lands halfway down the frame. Three corpus blocks
        // wrap their composition in exactly this: an unsized, static #root. The pair isolates the
        // containing block - the sized parent is the control that shows top:50% itself works.
        new("top", "50% in an unsized static parent",
            new Doc("<div class=\"box\"><div class=\"p\"></div></div>",
                "html,body{height:100%;}.box{}.p{position:absolute;top:50%;" + Box + "}"),
            new Doc("<div class=\"box\"><div class=\"p\"></div></div>",
                "html,body{height:100%;}.box{}.p{position:absolute;top:0;" + Box + "}")),

        new("top", "50% in a sized static parent",
            new Doc("<div class=\"box\"><div class=\"p\"></div></div>",
                ".box{height:200px;}.p{position:absolute;top:50%;" + Box + "}"),
            new Doc("<div class=\"box\"><div class=\"p\"></div></div>",
                ".box{height:200px;}.p{position:absolute;top:0;" + Box + "}")),

        // A margin on an absolutely positioned element. spotify-card centres its card with
        // top/left: 50% and a negative margin of half its size, which is the other classic
        // centring idiom. The margin-left case above, on a static block, is the control.
        Added("margin-left", "80px, on position:absolute", Box + "position:absolute;top:0;left:0;",
            "margin-left:80px;"),

        // Pseudo-elements. 60 of 165 blocks use one - a glow, an underline, a highlight bar, a
        // scrim. A solid one with explicit size paints nothing at all, so it is not a matter of
        // which properties a pseudo-element supports: it is never generated.
        new("::after", "content:'' with a size and a background",
            new Doc(Div, ".p{position:relative;width:120px;height:80px;}"
                         + ".p::after{content:\"\";position:absolute;left:0;top:0;width:120px;"
                         + "height:80px;background:#d9642a;}"),
            new Doc(Div, ".p{position:relative;width:120px;height:80px;}"),
            ControlPaintsNothing: true),

        new("::before", "content:'' with a size and a background",
            new Doc(Div, ".p{position:relative;width:120px;height:80px;}"
                         + ".p::before{content:\"\";position:absolute;left:0;top:0;width:120px;"
                         + "height:80px;background:#d9642a;}"),
            new Doc(Div, ".p{position:relative;width:120px;height:80px;}"),
            ControlPaintsNothing: true),

        // An element INSIDE an <svg>, and the stylesheet. 16 blocks tween one - a path that fades
        // in, a circle that scales. The rule below hides the rect in a browser; the animation
        // fades it out. The <svg> root takes both (the next case), and a presentation attribute
        // on the child is read (the one after), so the gap is specifically stylesheet-to-child.
        new("<svg> child", "stylesheet opacity:0 on a rect",
            new Doc(SvgRect, ".r{opacity:0;}"),
            new Doc(SvgRect, ""),
            new Animation("@keyframes probe{from{opacity:1;}to{opacity:0;}}"
                          + ".r{animation:probe 2s linear both;}", 2)),

        new("<svg>", "stylesheet opacity:0 on the root",
            new Doc(SvgRect, "svg{opacity:0;}"),
            new Doc(SvgRect, ""),
            new Animation("@keyframes probe{from{opacity:1;}to{opacity:0;}}"
                          + "svg{animation:probe 2s linear both;}", 2)),

        new("<svg> child", "opacity=\"0\" attribute on a rect",
            new Doc(SvgRect.Replace("class=\"r\"", "class=\"r\" opacity=\"0\""), ""),
            new Doc(SvgRect, "")),

        // A variable font's weight axis. Google Fonts answers a modern browser with ONE file for
        // every weight a document asks for, and that single file is what the packager carries -
        // 47 blocks link the service. The face below is that file: Inter, latin subset, wght
        // 100-900. The two halves differ only in font-weight, so a 'yes' means bold is bold and a
        // 'NO' means every weight in a fetched face is drawn the same.
        //
        // Reads NO for the wrong reason if the file is missing: both halves then fall back to the
        // same face. The run prints whether it was found.
        new("@font-face", "variable wght axis, 700 vs 400",
            new Doc(Text, FaceRule("VarFace", VariableDataUri, "100 900")
                          + ".p{font-family:'VarFace';font-size:40px;color:#d9642a;font-weight:700;}"),
            new Doc(Text, FaceRule("VarFace", VariableDataUri, "100 900")
                          + ".p{font-family:'VarFace';font-size:40px;color:#d9642a;font-weight:400;}")),

        // ---- what the 0.34.0 corpus run ranked next -------------------------------------------
        // After the six above were fixed, the engine's own diagnostics over the corpus and the
        // blocks that paint MORE than the browser does ranked these. Each is here so the next
        // upstream report quotes a row rather than a count of CF0050 lines.

        // 20 blocks: every LIVE / BREAKING / kicker in the corpus is lowercase in the source and
        // uppercased by CSS. Reported as unsupported; this asks whether it is also unpainted.
        Added("text-transform", "uppercase", Ink, "text-transform:uppercase;", markup: Text),

        // 23 blocks. A gradient told to cover half its box, against the same gradient filling it.
        // Longhands, because the first draft wrote the shorthand with `no-repeat` and the CONTROL
        // painted nothing - which is the case after this one.
        Added("background-size", "50% 100% on a gradient",
            "width:200px;height:80px;background-image:linear-gradient(90deg,#d9642a,#3f6fd8);"
            + "background-repeat:no-repeat;",
            "background-size:50% 100%;"),

        // The shorthand with a repeat keyword after the image, which is how a background that
        // should not tile is usually written. Found by accident: the control above, written this
        // way, rendered nothing at all.
        Added("background", "gradient + no-repeat, shorthand", "width:200px;height:80px;",
            "background:linear-gradient(90deg,#d9642a,#3f6fd8) no-repeat;", controlPaintsNothing: true),

        // <text> inside an svg: the "r/" in a subreddit badge, axis labels, a logo's wordmark.
        // CF0071 says one laid out 0x0 in two corpus blocks.
        new("<svg> text", "<text> inside an inline svg",
            new Doc("<div class=\"p\"><svg width=\"200\" height=\"60\" viewBox=\"0 0 200 60\" "
                    + "xmlns=\"http://www.w3.org/2000/svg\"><text x=\"10\" y=\"45\" font-size=\"40\" "
                    + "fill=\"#d9642a\">Handgloves</text></svg></div>", ".p{width:200px;height:60px;}"),
            new Doc("<div class=\"p\"></div>", ".p{width:200px;height:60px;}"),
            ControlPaintsNothing: true),

        // A gradient fill by reference, which is how every svg glow and ring in the corpus is
        // coloured. The control fills the same rect with a flat colour, so a 'NO' here is not
        // "nothing painted" - it is the gradient read as that colour or as nothing.
        new("<svg> fill", "url(#gradient) on a rect",
            new Doc("<div class=\"p\"><svg width=\"200\" height=\"60\" xmlns=\"http://www.w3.org/2000/svg\">"
                    + "<defs><linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"#d9642a\"/>"
                    + "<stop offset=\"1\" stop-color=\"#3f6fd8\"/></linearGradient></defs>"
                    + "<rect width=\"200\" height=\"60\" fill=\"url(#g)\"/></svg></div>",
                ".p{width:200px;height:60px;}"),
            new Doc("<div class=\"p\"><svg width=\"200\" height=\"60\" xmlns=\"http://www.w3.org/2000/svg\">"
                    + "<rect width=\"200\" height=\"60\" fill=\"#d9642a\"/></svg></div>",
                ".p{width:200px;height:60px;}")),

        // A dashed stroke drawn on: the "path draws itself" idiom, animated through
        // stroke-dashoffset in 6 corpus blocks. The control shows the whole stroke.
        new("<svg> stroke", "stroke-dashoffset hides half a line",
            new Doc("<div class=\"p\"><svg width=\"200\" height=\"60\" xmlns=\"http://www.w3.org/2000/svg\">"
                    + "<line x1=\"0\" y1=\"30\" x2=\"200\" y2=\"30\" stroke=\"#d9642a\" stroke-width=\"20\" "
                    + "stroke-dasharray=\"200\" stroke-dashoffset=\"100\"/></svg></div>",
                ".p{width:200px;height:60px;}"),
            new Doc("<div class=\"p\"><svg width=\"200\" height=\"60\" xmlns=\"http://www.w3.org/2000/svg\">"
                    + "<line x1=\"0\" y1=\"30\" x2=\"200\" y2=\"30\" stroke=\"#d9642a\" stroke-width=\"20\" "
                    + "stroke-dasharray=\"200\" stroke-dashoffset=\"0\"/></svg></div>",
                ".p{width:200px;height:60px;}")),

        // overflow: hidden on a parent whose child is transformed out of it. A wall of clones that
        // slides, a ticker that scrolls: in each the parent is a window and the child leaves it.
        // The control has no overflow rule and shows the child where it went.
        new("overflow", "hidden clips a translated child",
            new Doc("<div class=\"box\"><div class=\"p\"></div></div>",
                ".box{position:relative;width:120px;height:80px;overflow:hidden;}"
                + ".p{position:absolute;left:0;top:0;" + Box + "transform:translateX(100px);}"),
            new Doc("<div class=\"box\"><div class=\"p\"></div></div>",
                ".box{position:relative;width:120px;height:80px;}"
                + ".p{position:absolute;left:0;top:0;" + Box + "transform:translateX(100px);}")),

        new("overflow", "hidden clips a scaled child",
            new Doc("<div class=\"box\"><div class=\"p\"></div></div>",
                ".box{position:relative;width:120px;height:80px;overflow:hidden;}"
                + ".p{position:absolute;left:0;top:0;" + Box + "transform:scale(3);}"),
            new Doc("<div class=\"box\"><div class=\"p\"></div></div>",
                ".box{position:relative;width:120px;height:80px;}"
                + ".p{position:absolute;left:0;top:0;" + Box + "transform:scale(3);}")),

        // clip-path in the forms the corpus writes: inset() is above and reads NO; these are the
        // other two. 29 blocks use one - every wipe, iris and mask reveal.
        Added("clip-path", "circle(30%)", Box, "clip-path:circle(30%);"),
        Added("clip-path", "polygon(...)", Box, "clip-path:polygon(0 0,100% 0,50% 100%);"),

        // A flip card: the back face is rotated away and hidden. Without 3D transforms the two
        // faces paint on top of each other, and backface-visibility decides which one shows.
        Added("backface-visibility", "hidden, face rotated 180deg", Box + "transform:rotateY(180deg);",
            "backface-visibility:hidden;"),

        // ---- what the compiler could carry next, if the engine animates it ---------------------
        // 0.35.0 paints all four. Whether each ANIMATES decides whether the compiler's refusal of
        // the tween is still honest: clipPath is 89 refused tweens across 16 blocks, filter 166
        // across 16, strokeDashoffset 32 across 10, rotationY 24 across 3.
        Added("clip-path", "inset() animated, wipe", Box, "clip-path:inset(0 50% 0 0);",
            animation: Moves("clip-path", "inset(0 100% 0 0)", "inset(0 0 0 0)")),
        Added("filter", "blur animated", Box, "filter:blur(12px);",
            animation: Moves("filter", "blur(0px)", "blur(12px)")),
        Added("transform", "rotateY animated", Box, "transform:rotateY(70deg);",
            animation: Moves("transform", "rotateY(0deg)", "rotateY(70deg)")),
        // A gradient stop placed in px. Through 0.35.0 only a % position was read and a px one was
        // dropped, which made every hairline and dot pattern in the corpus a fade - invisible while
        // the gradient box was the element, a field of blobs once background-size tiled it (#273).
        // Written like the rgba-vs-hex rows: the two halves SHOULD paint the same, so the honest
        // reading is NO. 1px of a 200px line is 0.5%.
        new("gradient stop", "1px, should match 0.5% of a 200px line",
            new Doc(Div, ".p{width:200px;height:80px;background-image:linear-gradient(90deg,#d9642a 1px,transparent 1px);}"),
            new Doc(Div, ".p{width:200px;height:80px;background-image:linear-gradient(90deg,#d9642a 0.5%,transparent 0.5%);}")),

        // ---- what dissecting thread-message-stack found ----------------------------------------
        // 6.5% of content with three refusals, which is as close as this corpus gets to "the
        // compiler carried everything and the picture is still wrong". Three causes, each its own
        // row. See docs/CONFORMANCE.md.

        // The `at <position>` of a radial gradient. The two halves put the SAME gradient in
        // opposite corners, so a 'yes' means the position moved it and a 'NO' means both were
        // drawn in the same place - which is the box centre, whatever was asked for. 28 blocks
        // write 63 of these, and they light the corners of a composition.
        new("radial-gradient", "at 20% 20% vs at 80% 80%",
            new Doc(Div, ".p{width:200px;height:120px;background:"
                         + "radial-gradient(circle at 20% 20%, #d9642a 0, transparent 40%);}"),
            new Doc(Div, ".p{width:200px;height:120px;background:"
                         + "radial-gradient(circle at 80% 80%, #d9642a 0, transparent 40%);}")),

        // CSS's default ending shape is an ELLIPSE, and 0.37.0 started saying so - which changes
        // gradients that were already written. The two halves are a bare gradient against an
        // explicit `circle`, in a box twice as wide as it is tall: a 'yes' means the default is
        // not a circle. 11 corpus blocks write a bare one.
        new("radial-gradient", "default shape vs explicit circle",
            new Doc(Div, ".p{width:200px;height:100px;background:"
                         + "radial-gradient(#d9642a 0, #d9642a 50%, transparent 50%);}"),
            new Doc(Div, ".p{width:200px;height:100px;background:"
                         + "radial-gradient(circle, #d9642a 0, #d9642a 50%, transparent 50%);}")),

        // Two image layers in one declaration against the first alone. CSS paints the first ON TOP
        // of the second, so they differ wherever the top one is transparent; identical means only
        // one layer was drawn. 38 blocks stack two or more.
        new("background", "two image layers vs one",
            new Doc(Div, ".p{width:200px;height:120px;background:"
                         + "linear-gradient(90deg,#d9642a,transparent 70%),"
                         + "linear-gradient(270deg,#3f6fd8,transparent 70%);}"),
            new Doc(Div, ".p{width:200px;height:120px;background:"
                         + "linear-gradient(90deg,#d9642a,transparent 70%);}")),

        // Fading to `transparent`. CSS interpolates gradient stops in PREMULTIPLIED alpha, so the
        // keyword behaves as "this colour at zero alpha" and the fade keeps its hue. Interpolated
        // naively in straight RGBA it travels through transparent BLACK and goes grey. The two
        // halves are required to render identically, so - like the rgba-versus-hex rows - the
        // honest reading is NO. 45 blocks write 124 of these.
        new("gradient stop", "transparent vs the same colour at alpha 0",
            new Doc(Div, ".p{width:200px;height:100px;background:"
                         + "linear-gradient(90deg,#f0d3a6 0,transparent 100%);}"),
            new Doc(Div, ".p{width:200px;height:100px;background:"
                         + "linear-gradient(90deg,#f0d3a6 0,rgba(240,211,166,0) 100%);}")),

        // ---- the constraint the whole compiler is built around, finally asked --------------------
        // "One animation per element; a comma-separated list runs NEITHER" is rule one in
        // AGENTS.md, it is why every tween touching an element is merged into a single
        // @keyframes, and it was measured on 0.25 and never asked again. This matrix had no row
        // for it - forty-eight rows about properties, and none about the thing the architecture
        // bends around.
        //
        // A should-match pair, like the rgba-versus-hex rows: the two animations end where the
        // static declaration already is, so `NO` means the list ran correctly and `yes` means it
        // did not. Sampled at the end of both animations.
        new("animation", "two, comma-separated, vs their end state",
            new Doc(Div, "@keyframes slide{from{transform:translateX(0)}to{transform:translateX(80px)}}"
                         + "@keyframes fade{from{opacity:1}to{opacity:0.25}}"
                         + ".p{" + Box + "animation:slide 2s linear both,fade 2s linear both;}"),
            new Doc(Div, ".p{" + Box + "transform:translateX(80px);opacity:0.25;}"),
            At: 2),

        // The same thing written as per-property lists, which is what the shorthand expands to.
        new("animation", "two via longhand lists, vs their end state",
            new Doc(Div, "@keyframes slide{from{transform:translateX(0)}to{transform:translateX(80px)}}"
                         + "@keyframes fade{from{opacity:1}to{opacity:0.25}}"
                         + ".p{" + Box + "animation-name:slide,fade;animation-duration:2s,2s;"
                         + "animation-timing-function:linear,linear;animation-fill-mode:both,both;}"),
            new Doc(Div, ".p{" + Box + "transform:translateX(80px);opacity:0.25;}"),
            At: 2),

        // The `hidden` attribute, which is display:none in every browser's UA stylesheet. The
        // control is the same element WITHOUT the attribute, so a 'yes' means hidden hid it. One
        // block writes a JSON data island this way, and the engine paints the JSON.
        new("hidden", "attribute on a div with text",
            new Doc("<div class=\"p\" hidden>Handgloves</div>", ".p{" + Ink + "}"),
            new Doc("<div class=\"p\">Handgloves</div>", ".p{" + Ink + "}")),

        new("<svg> stroke", "stroke-dashoffset animated, draw-on",
            new Doc("<div class=\"p\"><svg width=\"200\" height=\"60\" xmlns=\"http://www.w3.org/2000/svg\">"
                    + "<line class=\"l\" x1=\"0\" y1=\"30\" x2=\"200\" y2=\"30\" stroke=\"#d9642a\" stroke-width=\"20\" "
                    + "stroke-dasharray=\"200\" stroke-dashoffset=\"100\"/></svg></div>",
                ".p{width:200px;height:60px;}"),
            new Doc("<div class=\"p\"><svg width=\"200\" height=\"60\" xmlns=\"http://www.w3.org/2000/svg\">"
                    + "<line class=\"l\" x1=\"0\" y1=\"30\" x2=\"200\" y2=\"30\" stroke=\"#d9642a\" stroke-width=\"20\" "
                    + "stroke-dasharray=\"200\" stroke-dashoffset=\"0\"/></svg></div>",
                ".p{width:200px;height:60px;}"),
            new Animation("@keyframes probe{from{stroke-dashoffset:200;}to{stroke-dashoffset:0;}}"
                          + ".l{animation:probe 2s linear both;}", 2)),
    ];

    /// <summary>A rect inside an inline svg, the shape every icon in the corpus takes.</summary>
    private const string SvgRect =
        "<div class=\"p\" style=\"width:120px;height:80px;\">"
        + "<svg width=\"100\" height=\"60\" viewBox=\"0 0 100 60\" xmlns=\"http://www.w3.org/2000/svg\">"
        + "<rect class=\"r\" width=\"100\" height=\"60\" fill=\"#d9642a\"/></svg></div>";

    /// <summary>An <c>@font-face</c> rule with a weight range, the way a font service writes one
    /// for a variable face.</summary>
    private static string FaceRule(string family, string src, string weights) =>
        "@font-face{font-family:'" + family + "';font-weight:" + weights + ";src:url(" + src
        + ") format('woff2');}";

    /// <summary>Inter's latin subset as a variable face, inline. The file is the one Google Fonts
    /// serves a current Chrome for <c>Inter:wght@400;700</c> - a single file answering both
    /// weights - fetched on 2026-10-05 and kept beside this project under the OFL, so the case
    /// asks about the file a package actually carries rather than a stand-in.</summary>
    private static string VariableDataUri =>
        VariablePath is { Length: > 0 } path && File.Exists(path)
            ? "data:font/woff2;base64," + Convert.ToBase64String(File.ReadAllBytes(path))
            : "data:font/woff2;base64,";

    /// <summary>conformance/fonts/Inter-latin-wght.woff2, found the way <see cref="Woff2Path"/>
    /// finds the corpus: by walking up from the binary, because this list is a static initialiser
    /// and nothing a caller sets arrives in time.</summary>
    public static string? VariablePath
    {
        get
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            {
                var file = Path.Combine(d.FullName, "conformance", "fonts", "Inter-latin-wght.woff2");
                if (File.Exists(file)) return file;
            }
            return null;
        }
    }

    /// <summary>An <c>@font-face</c> rule, written the way a document does.</summary>
    private static string FaceRule(string family, string src) =>
        "@font-face{font-family:'" + family + "';src:url(" + src + ") format('woff2');}";

    /// <summary>The corpus's Inter, inline. Built here rather than pasted in: 30KB of base64 in a
    /// source file is unreadable and unverifiable, and the file it comes from is the same one the
    /// registered-face case above loads, so the two cases cannot drift apart.</summary>
    private static string DataUri =>
        Woff2Path is { Length: > 0 } path && File.Exists(path)
            ? "data:font/woff2;base64," + Convert.ToBase64String(File.ReadAllBytes(path))
            : "data:font/woff2;base64,";

    /// <summary>The corpus's Inter. Found here rather than handed in by the program: this list is
    /// a static initialiser, so anything a caller sets arrives after it has already been built -
    /// the first version took a settable property and every case was constructed with null.</summary>
    public static string? Woff2Path
    {
        get
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            {
                var blocks = Path.Combine(d.FullName, "corpus", "registry", "blocks");
                if (!Directory.Exists(blocks)) continue;

                return Directory.EnumerateFiles(blocks, "inter-latin-400-normal.woff2",
                        SearchOption.AllDirectories)
                    .Concat(Directory.EnumerateFiles(blocks, "*.woff2", SearchOption.AllDirectories))
                    .FirstOrDefault();
            }
            return null;
        }
    }
}
