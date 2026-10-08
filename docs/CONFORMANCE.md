# Conformance: knowing what the engine supports, per version

A translator that works around a limitation the engine no longer has is producing different output
than the author wrote, for no reason. A translator that *fails* to work around a limitation the
engine still has produces a video that is silently missing something.

Both are the same bug: **the translator's model of the target drifted from the target.**

This directory exists so that model is measured rather than remembered, and so a new CupriFace
release produces a diff rather than a surprise.

---

## Why this cannot be a document

It was a document, in the sibling repository, and it went stale in one release.

Between **CupriFace 0.25.0 and 0.25.1**, four things changed:

| | |
|---|---|
| `line-height` | a `px` value produced a box **3× too tall**, and `em`/`%` were ignored outright — fixed |
| comments inside `@keyframes` | silently corrupted the stop percentages, moving a bar's final width from 545px to 714px — fixed |
| `CupriDoctor` diagnostics | were process-global; a check returned findings produced by *other documents on other threads* — fixed |
| `CF0051` | **new**, and a false positive — it matched the text `repeating-linear-gradient` anywhere in the document, including comments and painted body text |

Three separate documents were confidently wrong until someone re-measured, and one of the four
changes *created* a problem rather than fixing one. A release is not a monotonic improvement, and
"we read the changelog" is not a substitute for asking the binary.

And 59% of the corpus uses `line-height`. Had CupriLex existed across that release, it would have
been carrying a workaround for a bug that no longer existed, on more than half the corpus.

---

## What it produces

A **support matrix** per engine version:

```
conformance/support/0.26.1.json
```

For each CSS property, in each value form the corpus actually uses, three independent questions —
because they fail independently and the difference decides the rewrite:

| question | how it is answered | what it means if "no" |
|---|---|---|
| **parses** | does the engine's own reader report a diagnostic for it? | it will be reported; `lint` catches it |
| **paints** | render with it and without it — do the pixels differ? | accepted and inert. The dangerous one. |
| **animates** | `@keyframes` it from A to B; do frames at `t=0` and `t=end` differ? | the animation runs and changes nothing |

A property can parse, paint, and still not animate — `background-color` does exactly that. A
property can paint and not be reported — `letter-spacing` is reported, but `left` animating to
nothing is not. The three columns are the whole point.

---

## What it produced the first time it was used in anger

The design above was written before any version bump had happened to this repository. One has now,
and it did the thing it was built for: **`git diff conformance/support/` deleted two rewrite
rules.**

```
dotnet run --project conformance -- --compare conformance/support/0.26.2.json conformance/support/0.27.0.json

  backdrop-filter (blur(6px)) parses: yes -> NO
  letter-spacing (12px) parses: NO -> yes
  letter-spacing (12px) paints: NO -> yes
  inset (0 (shorthand)) parses: NO -> yes
  inset (0 (shorthand)) paints: NO -> yes
  inset (0 (four longhands)) paints: NO -> yes
```

Read those six lines as three facts. `letter-spacing` went from ignored to implemented, and it is
80% of the corpus. `inset` went from ignored to supported, **and so did the four longhands**, which
is the half that a rewrite rule was standing in for. And `backdrop-filter` moved the other way in
the parse column, which is an improvement: it now reports why it is not drawing instead of
accepting the declaration in silence.

Across 0.26.1 to 0.27.0 the matrix took the repository from three rewrite rules to one. Each rule
had declared the condition it worked around; each condition stopped being true; the tests pinning
them failed on cue and the rules were deleted. That is the whole mechanism working end to end, and
none of it required reading a changelog.

The one thing the matrix could not do on its own: an optional package. `<svg>` reads as unsupported
until the probe calls `UseSvg()`, because that is also true of any document that does not. The
probe now enables the same packages the harness renders with, and the matrix says so.

---

## What it produced the second time, which was nothing

0.28.1's headline is WOFF 2: the format every font pipeline emits, refused by name until then, and
157 of the 187 blocks bring one. Run against 0.27.0 the matrix said:

```
dotnet run --project conformance -- --compare conformance/support/0.27.0.json conformance/support/0.28.1.json

  nothing moved.
```

Which was true, and useless. **The matrix only ever answers the questions it contains, and it had
no question about fonts.** Forty-four cases covering colour, layout, selectors and timing, and a
release that changed how every typeface in the corpus loads went past without a mark. A matrix that
says "nothing moved" is making a claim about its own coverage as much as about the engine, and
nothing in the output distinguished the two.

Two cases now ask it, and both had to be argued with before they were worth anything:

- **A control that is absent, not different.** The first version put the woff2 face against a face
  the harness already registers. It read `yes` on an engine with no decoder at all, because an
  unresolved family does not fall back to a named one — the two documents differed either way. The
  control now names a family that does not exist, so both halves land on the *same* fallback when
  the decode fails and differ only when it succeeds. Verified both directions: `NO` with the
  decoder uninstalled, `yes` with it.
- **A refusal is an answer.** `LoadFont` on a .woff2 throws on an engine without the decoder, which
  killed every case in the run — 0.27.0 could not produce a matrix file at all. Caught, the face
  simply is not registered and the case that asks for it reads `NO`, which is the truth.

One thing found on the way, worth knowing before designing any A/B around it: **`UseWoff2()`
installs a process-wide decoder, not a document-scoped one.** Removing the call from one of three
sites and re-running measures nothing, because the first document that asked for it has already
switched the process on.

---

## What it produced the third time, which was the lesson of the second time applied

The second run said "nothing moved" because the matrix had no question about fonts. The third
addition came from the other direction: not a release, but one block taken apart to see why a
correct translation scored 40%. Six engine behaviours fell out, and **none of them was a property
the corpus survey could have flagged**. They are the ordinary CSS *around* the properties -
a percentage in a translate, the containing block of an absolute child, a margin on one, a
pseudo-element, the cascade reaching into an svg, the weight axis of a variable face - which is
exactly the kind of question a property-by-property matrix never asks.

```
transform            translateX(-50%)               yes   NO    NO
top                  50% in an unsized static parent yes   NO    -
top                  50% in a sized static parent    yes   yes   -
margin-left          80px, on position:absolute      yes   NO    -
::after              content:'' with a size and a …  yes   NO    -
::before             content:'' with a size and a …  yes   NO    -
<svg> child          stylesheet opacity:0 on a rect  yes   NO    NO
<svg>                stylesheet opacity:0 on the r…  yes   yes   yes
<svg> child          opacity="0" attribute on a re…  yes   yes   -
@font-face           variable wght axis, 700 vs 400  yes   NO    -
```

Three of the rows are controls, and they are the ones that make the other seven mean something:
`top: 50%` works in a sized parent, so the failure is the containing block and not the percentage;
the svg root takes the stylesheet, so the failure is the cascade reaching the children and not svg
styling; the attribute is honoured, so there is a path the engine does read. Reported as CupriFace
#258 to #263 on the day they were measured, with two of the seven worked around in the translator
by rules that declared the row they would be deleted on.

That was 0.34.0, released the same week, and the diff is the cleanest this file has produced:

```
dotnet run --project conformance -- --compare conformance/support/0.28.1.json conformance/support/0.34.0.json

  transform (translateX(-50%)) paints: NO -> yes
  transform (translateX(-50%)) animates: NO -> yes
  top (50% in an unsized static parent) paints: NO -> yes
  margin-left (80px, on position:absolute) paints: NO -> yes
  ::after (content:'' with a size and a background) paints: NO -> yes
  ::before (content:'' with a size and a background) paints: NO -> yes
  <svg> child (stylesheet opacity:0 on a rect) paints: NO -> yes
  <svg> child (stylesheet opacity:0 on a rect) animates: NO -> yes
  @font-face (variable wght axis, 700 vs 400) paints: NO -> yes

9 change(s).
```

Nine changes, all in the direction asked, and nothing else moved across 49 other rows - which was
checked before the branch merged, by packing it locally and running this file against it. Both
translator rules were deleted on the strength of those lines, and the tests that pinned them were
flipped. One caution the matrix cannot express: the weight row reads `yes` because 700 now differs
from 400, and it would read the same for a synthesised bold as for a real instance of the axis.
0.34.0 synthesises, because the SkiaSharp it builds against has no variation API; the row will not
notice when that changes.

### What the 0.34.0 run ranked next

With those six gone, the engine's own diagnostics over the corpus and the blocks that paint more
than the browser does were used to choose eleven more rows, so the next report could quote a row
rather than a count of `CF0050` lines. Six read yes and are no issue: svg `<text>`, a gradient
fill by reference, `stroke-dashoffset`, and `overflow: hidden` clipping a translated or a scaled
child. Five read NO on 0.34.0:

```
text-transform       uppercase                      NO    NO    -      52 blocks write it
background-size      50% 100% on a gradient         NO    NO    -      25 blocks
background           gradient + no-repeat, shorthand yes  NO    -      10 blocks, and SILENT
clip-path            circle(30%) / polygon(...)     NO    NO    -      40 blocks, with inset()
backface-visibility  hidden, face rotated 180deg    NO    NO    -      27 blocks; the 3D family
```

The shorthand row was found by accident: the first draft of the `background-size` case wrote its
control as `background: linear-gradient(...) no-repeat`, and the control painted nothing. A
`background` shorthand with any keyword after the image is accepted without a diagnostic and
paints nothing at all, which is the class of failure #201 was about. The seventeen `CF0030` lines
saying an `<svg>` "is not something the engine draws" are not an svg gap: every one of those
elements is empty in the markup and filled by the JavaScript that was removed, and the doctor's
advice to add a package that is already in use is the misleading part.

Reported upstream as CupriFace #265 (the silent shorthand), #266 (`text-transform`), #267
(`background-size`), #268 (`clip-path`), #269 (the 3D family as one issue) and #270 (the doctor's
advice), each quoting its row.

### 0.35.0, which answered all six and three older rows with them

```
dotnet run --project conformance -- --compare conformance/support/0.34.0.json conformance/support/0.35.0.json

  clip-path (inset(0 40% 0 0)) parses: NO -> yes            paints: NO -> yes
  transform (rotateY(50deg)) paints: NO -> yes
  transform (translate3d(60px,20px,0)) paints: NO -> yes
  perspective (400px) parses: NO -> yes
  text-transform (uppercase) parses: NO -> yes               paints: NO -> yes
  background-size (50% 100% on a gradient) parses: NO -> yes paints: NO -> yes
  background (gradient + no-repeat, shorthand) paints: NO -> yes
  clip-path (circle(30%)) parses: NO -> yes                  paints: NO -> yes
  clip-path (polygon(...)) parses: NO -> yes                 paints: NO -> yes
  backface-visibility (hidden, face rotated 180deg) parses: NO -> yes   paints: NO -> yes

16 change(s).
```

The `inset()` row had been NO since 0.26.1 and the two 3D rows since #201; they went with the
family. Nothing else moved. Four rows were added on the same day to ask the question the compiler
needs next - not whether a property paints but whether it **animates**, since that decides whether
refusing its tween is still honest:

```
clip-path            inset() animated, wipe         yes   yes   yes
transform            rotateY animated               yes   yes   yes
<svg> stroke         stroke-dashoffset animated     yes   yes   yes
filter               blur animated                  yes   yes   NO
```

Three of those are compiler work waiting to happen: `clipPath` is 89 refused tweens across 16
blocks, `strokeDashoffset` 32 across 10, `rotationY` 24 across 3. `filter`, at 166 tweens across
16 blocks, stays refused and the refusal is still true.

One regression came with the release, and it is the honest kind: a property that now paints,
painting wrongly. `notes-reveal` fell from 86.4% to 82.0% on its last frame alone, where the
paper's dot grid - `radial-gradient(circle, rgba(…) 2px, transparent 2.6px)` tiled at `36px 36px`
- draws each tile mostly dark. Measured directly: a 1.5px dot on a 36px tile covers **38.6%** of
the box, and the same share at 3px and at a 120px tile, where CSS would give 0.5%, 2.2% and 0.2%.
The gradient's stops are not being resolved against the tile. The same tile idiom draws the
1px grid lines behind every `code-*` block (`linear-gradient(rgba(…) 1px, transparent 1px)` at
`64px 64px`), and those five blocks each fell 12 points, from 76-86% to 65-74%. Over the corpus
the release moved the mean by nothing: 43.5% to 43.5%, 10 blocks better and 13 worse, the gains
(`split-flap-board` 0% to 74%, the `lt-*` family a few points each) cancelled by the tile.
Reported as CupriFace #273, with the three measurements.

### 0.36.0, the next day

#273 turned out to be older than 0.35.0: a gradient stop placed in **px** had never been read at
all, only a `%` one, so every hairline and dot pattern in the corpus had been a fade across its
gradient box since the beginning - invisible while that box was the element, and a field of blobs
once 0.35.0 tiled it. 0.36.0 reads the px and divides it by the gradient line over the box the
gradient fills, which under `background-size` is the tile.

The matrix could not have caught it, because no row asked, and a paints/does-not-paint row cannot
ask "is this the *right* picture". The row added for it is written like the rgba-versus-hex pair:
a 1px stop against a 0.5% stop on a 200px line, two documents that *should* render the same, so
the honest reading is NO:

```
gradient stop        1px, should match 0.5% of a 200px line   yes   NO    -
```

Over the corpus: **43.5% to 44.3%, 8 blocks better and 1 worse.** `code-typing` came back from
73.8% to 85.8%, `notes-reveal` went past where it had ever been (86.4% to 94.0%, its dots now
drawing rather than fading), and the two `flowchart` blocks roughly doubled, 30.2% to 63.5% and
33.1% to 59.0% - neither of which anyone had connected to gradients.

That last pair is the argument for measuring a release rather than reading its notes. #273 was
filed about a dot grid; the fix reached two flowcharts whose connector lines are drawn with
px-stopped gradients, and nothing in the issue or the release notes would have predicted them.

The weight case needs a variable face, and the corpus has none, so one is kept beside the probe:
`conformance/fonts/Inter-latin-wght.woff2`, the file Google Fonts actually serves for
`Inter:wght@400;700` and so the file a package actually carries. The run prints whether it was
found, because a missing file reads `NO` for the wrong reason, the same way the WOFF 2 case does.

---

### What dissecting thread-message-stack found

The second block taken apart rather than counted, chosen the same way x-post was: it paints
plenty and scores badly, which means it drew the WRONG thing rather than nothing. 6.5% of
content, three refusals, and the same ~7% at every sample including `t=0` - so not a motion
failure at all, but a first frame that is already wrong.

Three causes, none of them a property anybody had thought to probe:

```
radial-gradient      at 20% 20% vs at 80% 80%       yes   NO    -      28 blocks, 63 declarations
background           two image layers vs one        yes   NO    -      38 blocks
hidden               attribute on a div with text   yes   NO    -       1 block
```

**The `at <position>` of a radial gradient is ignored, and every one is drawn in the centre of its
box.** The radius is right - a centred `circle` with a hard stop at 50% measures 111px against the
111.8px CSS gives for `farthest-corner` - and the position is not read at all, in percentages,
pixels or keywords:

```
at 50% 50%      disc centre (199, 99)  r=39     CSS says (200,100) r=40
at 18% 20%      disc centre (199, 99)  r=39     CSS says ( 72, 40) r=40
at 75% 18%      disc centre (199, 99)  r=39     CSS says (300, 36) r=40
at 100px 50px   disc centre (199, 99)  r=39     CSS says (100, 50) r=40
at left top     disc centre (199, 99)  r=39     CSS says (  0,  0) r=40
```

That is what this block's background is made of: four radials lighting four different corners,
all collapsed onto the middle and overlapping into one dull glow. The blocks that write these are
disproportionately the ones scoring badly - `vpn-youtube-spot`, `beat-freeze-cut`,
`slack-notification-ad`, `transitions-light`, `macos-tahoe-liquid-glass`, `ui-3d-reveal` and
`ios26-liquid-glass` are all in the paints-a-lot-scores-badly table.

**Only the first image layer of a background is painted.** `red, blue` renders identically to
`red` alone, and `blue, red` identically to `blue`. A solid colour as the last layer IS painted
underneath, so it is layering of IMAGES specifically. The 0.35.0 notes state this as a known
limit; 38 blocks stack two or more.

**The `hidden` attribute does nothing.** This block keeps its message data in a
`<div hidden data-hf-primitive-data>{...}</div>` island, which a browser hides through its UA
stylesheet, and the engine paints the raw JSON across the top of the frame. An author-level
`[hidden]{display:none}` fixes it, and `display:none` works, so it is one missing UA rule.

WHAT EACH COST, WHICH IS NOT WHAT IT LOOKS LIKE

Emulated one at a time against the browser frames:

| | mean content correct |
|---|---|
| as translated | 6.5% |
| + `hidden` honoured | 6.5% |
| + the four background layers stacked | 7.8% |
| + both | 7.9% |

The JSON text is the most visible thing wrong with the frame and it is worth **nothing**, because
those pixels were already wrong: removing bad ink does not make a pixel right when the thing that
belongs there is a gradient the engine is not drawing. And stacking the layers is worth only 1.3
because they were still all being centred. The three causes are not additive - the background is
one picture, and it is wrong until all of them are right.

The ceiling is the honest part. The bubbles themselves are built by `document.createElement` from
that JSON, so they can never render, but at `t=0` no bubble has appeared yet and the engine still
scores 7.9% of content. **The background alone is ~92 points of this block's gap**, and the
JavaScript that cannot be carried accounts for about five.

All three went upstream as CupriFace #278, #279 and #280. None is worked around here, and the
reasons differ. The gradient prelude and the layer list would both mean replacing a background
with generated child elements - DOM surgery that needs the element's size in px, cannot reach a
pseudo-element, and would be wrong anyway until both landed, since every generated layer would
still be centred. The `hidden` attribute could be answered in three lines, by putting
`[hidden]{display:none}` first in the emitted stylesheet where an author rule still beats it; it
is not done because it buys nothing measurable, and the argument for doing it is that a package
built today paints JSON across the frame.

The engine's own source said where two of the three came from, which is worth doing before
writing an issue: `ParseGradient` steps over the prelude with a comment saying so, and
`ParseBackgroundImage` takes `SplitTopLevel(t, ',').FirstOrDefault()`. A report that names the
line is a report somebody can act on.

### 0.37.0 answered all three, and it was the largest jump the corpus has had

**44.3% to 46.0%, 15 blocks better and 2 worse.** `slack-notification-ad` — the motion canary, and
for months the worst-scoring one — went from **20.0% to 93.6%**. `message-thread-reveal` 39.5 to
87.4, `north-korea-locked-down` 23.2 to 68.4, `oscilloscope-trace` 38.0 to 71.3, `beat-freeze-cut`
23.9 to 44.9, `vpn-youtube-spot` 34.3 to 52.9.

Three gaps found by dissecting one block were worth more than the eleven found by ranking the
engine's own diagnostics. That is the argument for the method, not for the properties.

The flips were checked rather than trusted. Position is exact in percentages, pixels and keywords;
extent is exact too (`circle at 75% 18%` with a 34% stop measures 575px against the 575 CSS
computes, `at 62% 74%` gives 458 against 459); and the default ellipse the release notes warn
about measures 282 × 142 against the 282.8 × 141.4 CSS requires. Two of four extent readings
looked wrong and were this probe scanning into the box edge, not the engine.

### The bug that was hiding under them

`thread-message-stack`, the block all three came from, moved only 6.5% to 9.4%. Its engine ink
tripled to 0.723 against the browser's 0.921 and its "off by" fell from 26.5% to 17.8%, so the
layers and positions plainly landed. The pixels still miss an 8-of-255 tolerance nearly
everywhere, and the reason is a fourth bug the first three were masking:

```
gradient stop   transparent vs the same colour at alpha 0   yes   yes   -
```

**Read that row as a should-match pair, like the rgba-versus-hex ones: `yes` means the two
documents DIFFER, and they are required not to.** CSS interpolates gradient stops in premultiplied
alpha, so `transparent` behaves as the neighbouring colour at zero alpha. The engine interpolates
in straight RGBA, so the keyword is transparent BLACK and every fade drags grey:

| gradient | `transparent` keyword | explicit alpha-0 colour |
|---|---|---|
| `#f0d3a6` → out | (207, 197, 182) | (247, 233, 210) |
| `#e8a86a` → out, radial | (198, 181, 164) | (244, 214, 185) |

45 of 172 blocks write 124 of these. Reported as CupriFace #283.

It also explains the run's one real loser. `macos-tahoe-liquid-glass` fell 15.5% to 9.1% while its
ink went 0.243 to 0.600 against a browser painting 0.736, its "off by" fell 50.2 to 43.1 and its
severe share fell too. It writes seven fades to `transparent`. The block got closer by every
measure except the one in the headline, because a block that moves from painting nothing to
painting nearly the right thing in slightly wrong colours trades absent pixels for wrong ones, and
the content measure counts the second and not the first. Worth remembering before reading a single
block's drop as a regression.

---

## How a translator uses it

Every rewrite rule declares what it is working around:

```
rule: repeating-gradient -> hard stops        because  repeating-linear-gradient.paints == false
rule: letter-spacing     -> drop and report   because  letter-spacing.paints == false
rule: top/left animation -> transform         because  left.animates == false
```

Then:

- a rule whose condition is **no longer true** is dead, and the build says so;
- a property that becomes supported means a rewrite can be *removed*, which is output that moves
  closer to what the author wrote;
- a property that becomes *unsupported* — it happens, see `CF0051` — means a rule is missing.

The matrix is committed. That is the point: `git diff conformance/support/` across two versions is
the list of things to reconsider, and it is reviewable.

---

## Running it

```
dotnet run --project conformance -- --out conformance/support
dotnet run --project conformance -- --compare 0.27.0 0.28.1
```

The probe renders; it does not read the engine's source or its release notes. Every answer is a
pixel comparison, for the reason at the top of [AGENTS.md](../AGENTS.md).

---

## Method, and its limits

**Measured by rendering.** Two documents identical but for one declaration, rendered at the same
`t`, compared pixel by pixel. If they differ, the property paints. The comparison is exact rather
than perceptual: anti-aliasing noise is not a concern because the two documents differ only in the
declaration under test.

**Honest about what it cannot see.** A property that paints *identically* for the specific values
probed will read as unsupported. `letter-spacing: 0` would. So the probe uses values chosen to make
a difference obvious — large tracking, a visible blur radius, a clip that removes half the shape —
and the chosen value is recorded in the matrix beside the answer, so a "no" can be re-examined
rather than trusted blindly.

**One engine, one machine, one platform.** The matrix records the CupriFace version, the runtime,
and the OS. A support matrix from another machine is a different file, not a contradiction —
CupriCut promises identical pixels *per OS*, not across them.

**Not a browser comparison.** This measures what the engine does, not how far that is from CSS.
The gap to a browser is the corpus's job to reveal.
