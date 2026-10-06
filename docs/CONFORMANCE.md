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
