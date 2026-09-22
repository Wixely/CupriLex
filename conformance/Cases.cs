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

        // ---- the other half of the font question: how the bytes arrive -------------------------
        // An @font-face src the document carries itself. docs/CORPUS.md says a data: URI works and
        // proposes it as the rewrite for the 44 blocks that fetch a face over the network, which
        // makes it load-bearing enough to measure rather than believe.
        new("@font-face", "src: data: URI",
            new Doc(Text, FaceRule("DataFace", DataUri) + ".p{font-family:'DataFace';"
                          + "font-size:40px;color:#d9642a;}"),
            new Doc(Text, ".p{font-family:'NoSuchFaceExists';font-size:40px;color:#d9642a;}")),
    ];

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
