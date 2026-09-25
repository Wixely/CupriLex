# CupriLex — the plan

Ordered so that each milestone produces something measurable, and so nothing is built on a guess
about the target. Read [README.md](README.md) and [docs/CORPUS.md](docs/CORPUS.md) first — the
corpus survey is what set this order.

---

## Milestone 0 — know the ground *(done)*

The corpus, surveyed before any design. It found that **all 187 blocks are 100% JavaScript-animated
and not one uses CSS `@keyframes`**, which moved the GSAP compiler from "the hard half, attempt the
subset" to "the only half, and the corpus is unusable without it".

- `tools/fetch-corpus.py` — pulls the registry on demand; not vendored
- `tools/survey.py` — reproduces every number in the docs
- `docs/CORPUS.md` — the findings and what they imply

---

## Milestone 1 — conformance, before any rewrite rule

**Why first.** Nine of the corpus's twenty notable features are marked *unknown* in
[CORPUS.md](docs/CORPUS.md) — `<svg>` at 32% of blocks, 3D transforms at 28%, `clip-path` at 21%,
`filter` at 19%, grid at 14%. Every rewrite rule written before those are answered is a guess, and
half of them would be workarounds for things that already work.

1. **The probe.** For each property × value form: does it *parse*, does it *paint*, does it
   *animate*. Three questions because they fail independently — see
   [docs/CONFORMANCE.md](docs/CONFORMANCE.md).
2. **The matrix**, committed per engine version, so `git diff conformance/support/` is the list of
   things a new CupriFace release changed.
3. **Answer the nine unknowns**, and fold the answers back into `docs/CORPUS.md`.

**Done when** a support matrix exists for the current engine and every "unknown" in the corpus
table has become a yes or a no.

---

## Milestone 2 — the comparison harness *(done)*

`harness/`, and [docs/HARNESS.md](docs/HARNESS.md).

```
dotnet run --project harness -- bar-chart-race     # one block, with its triptych
dotnet run --project harness -- --all              # the corpus, and a baseline.json
```

A headless browser drives each block over the DevTools protocol, **seeked** rather than played —
every block builds a paused GSAP timeline and registers it, so the reference can be asked for an
exact instant. CupriFace renders the same instants. The frames are compared and a triptych is
written: browser, engine, difference.

### The baseline, CupriFace 0.26.1

| | |
|---|---|
| scored | 150 of 187 |
| mean matching | 61.6% |
| **over the 125 blocks whose reference actually moves** | **55.2%** — frame-wide, the measure in use at the time |
| blocks where the engine rendered the same frame at every time | **150 of 150** |

The baseline is terrible, as expected, and *how* it is terrible is the useful part: not one block
animated. The engine renders a still of the authored markup and nothing else, which is exactly what
a corpus with no CSS animation in it should do before the compiler exists.

### What it found on the way

- **An engine crash costs 35 blocks.** A `border` shorthand whose colour is a spaced `rgb()` throws
  out of CupriFace's colour parser. Those blocks cannot be loaded at all. Diagnosed, pinned by a
  test, and added to the conformance matrix. *(That diagnosis was only a third of it: two more
  parsers reach the same fault, and one of them is not fixed by removing the spaces. See Milestone
  3.)*
- **13 blocks render nothing anywhere.** Their whole composition sits inside a `<template>`, inert
  until a host clones it in.
- **25 blocks expect their host to have created `window.__timelines`** and throw without it.
- **A third of the corpus declares a duration its timeline does not span.** 50 of 150.
- **The conformance probe was reporting a crash as "paints".** A document that throws rendered as
  null, null differs from the control, and the matrix said yes. Fixed, and the border case is now
  the entry that proves it.

---

## Milestone 3 — the compiler *(done)*

`compiler/`, and [docs/COMPILER.md](docs/COMPILER.md).

```
dotnet run --project cli -- shapes             # what the corpus writes
dotnet run --project cli -- reach              # how much of it is carried
dotnet run --project cli -- translate <block>  # one block, with its CSS
```

Timelines are read out of a syntax tree — Acornima parses, nothing executes — resolved to absolute
times, merged per element into a single `@keyframes`, and everything outside the subset is refused
by name.

### The measurement that shaped it

The plan said to find the hard case early. `shapes` found it before a line of the compiler was
written, by classifying the *arguments* of all 2579 motion calls rather than counting the calls:

| | |
|---|---|
| straight-line code | 39% |
| straight-line, literal values, resolvable target | 34% |
| **blocks with nothing outside that subset** | **18 of 174** |

A compiler that reads only what is plainly written reaches a tenth of the corpus. What closed most
of the gap was not an interpreter but ordinary static analysis: constant folding, binding
resolution, following a running `t += 0.5` through straight-line code, reading an immediately
invoked function as the straight-line code it is, and stepping into a helper at the one place it
is called. 331 of the corpus's 813 declared functions are called exactly once.

### What it does with the four constraints

**One animation per element** is the constraint everything else bends around: every tween touching
an element merges into one `@keyframes`, which is why tweens are threaded in time order and a
second tween of a property already in flight is refused. **Per-tween easing** cannot be expressed
in a merged animation, so the curve is evaluated and sampled into stops and the animation runs
linear — eight samples, chosen arbitrarily and still owed a measurement.

### What it scored

| | before | after |
|---|---|---|
| blocks scored | 150 of 187 | **183 of 187** |
| mean of frame, over blocks whose reference moves | 55.2% | 62.8% |
| **mean of content** (the measure that replaced it) | — | **38.2%** on 0.28.1 |
| blocks rendering one still frame | **150 of 150** | 145 of 183 |

38 blocks now animate and 33 more can be loaded at all. The mean is not a like-for-like
comparison: the 33 newly measurable blocks score below average, which is the honest reason the
headline moved less than the work did.

### Three rewrites pulled forward from Milestone 4

Each was blocking the *measurement* of the compiler, and each condition is in the conformance
matrix so it can be deleted when the engine no longer needs it.

- **Colours to hex.** One engine bug reached from three parsers cost 35 blocks their score
  entirely. The first fix — unspacing colours inside `border` — recovered 25 and left 10, which is
  what a corpus run is for.
- **`inset: 0` to a percentage size**, for the 115 blocks whose overlays had no size at all. The
  obvious expansion to four longhands was written, measured, and found not to work.
- **`<template>` inlined**, for the 13 blocks that render nothing anywhere without it.

---

## Milestone 4 — the rest of the rewrites

Cheap once the compiler exists, and each one is a measurable step on the corpus score.

Three of these were done early, in Milestone 3, because each was blocking the measurement of the
compiler rather than merely improving on it: **colours to hex**, **`inset: 0` to a percentage
size**, and **`<template>` inlined**. What is left:

- ~~`<img>` → `<cupri-image>`~~ **Done**, and it was the largest single gain the corpus has had:
  **38.3% → 40.8%**. Four blocks went from nothing to a perfect score, because the composition WAS
  an image. It had been documented as done in two files for weeks while no code did it, which is
  the one kind of mistake this repository is arranged to catch and did not - the harness never
  noticed, because a missing image and a wrong image both just score badly. Linting the packages
  against a real consumer is what found it
- **`letter-spacing`**, at 80% of blocks, still ignored by the engine and still only reported
- repeating gradients → hard stops
- animated `left`/`top` → `transform`
- external fonts → **fetched and carried in the `.cutpkg` as files**, not embedded as a `data:`
  URI, and not a markup rewrite at all. See Milestone 5. Measured at under a third of a point on
  the score, so it is scheduled as output quality rather than as progress
- `eventCallback` / `call` → CupriCut `data-cut-event`
- `data-composition-variables` → kept, since a translated block should still be a template

And the compiler's own unfinished business, which the refusal counts rank:

- ~~**reading an element's authored start value.**~~ **Done.** 112 compiled animations held one
  value for their whole length, because `.to(el, {opacity: 1})` on an element the stylesheet
  authors as `opacity: 0` is a fade in a browser and a still here. The declared value is now read
  out of the document's own cascade - `compiler/Authored.cs` - and **62 are left**. Untouched
  transform components are carried too, so an animation no longer replaces an authored
  `translate(-50%, -50%)` with its own partial transform.

  It did not move the corpus mean. 10 blocks better, 9 worse, 38.2% before and after. Everything
  else moved: 7 more blocks animate, 9 more paint anything at all, and the severe share fell from
  12.8% to 11.0%. The 9 losers are almost all `transitions-*`, and the reason is worth keeping:
  an element wrongly held visible was covering the frame, and hiding it correctly exposed other
  content that is wrongly visible for reasons this change does not touch. Two wrongs were
  cancelling, and only one of them is fixed.
- **a tween of a property the engine cannot animate**, the largest single refusal group. Nothing to
  rewrite in most cases, but `backgroundColor` could cross-fade two stacked elements.
- **`.add()` of a nested timeline**, refused rather than flattened. **Now the second largest
  refusal group at 125**, up from 41: unrolling the loops reached a great many `.add()` calls that
  were never visited before
- **`repeat` and `stagger`**, both of which need more than one animation per element
- **how dense the ease sampling should be.** Eight stops was a guess and is still a guess; the
  comparison harness is what should answer it.

---

## Milestone 5 — output *(done)*

A translated block becomes a **`.cutpkg`**: one file, assets and fonts as bytes, already
self-contained. A project that has been imported is just a project, and nothing depends on this
tool at render time.

Plus the report, which is the thing that makes the output trustworthy.

```
dotnet run --project harness -- --all --package out
```

187 packages, 15.9 MB, 1,800 refusals recorded inside them. See [docs/PACKAGE.md](docs/PACKAGE.md).

**Verified against the reader, not against the specification.** CupriCut's own CLI opens the
packages, reads back the size, frame rate, duration and every asset, and lints them without a
`CUT003`. It cannot render them yet, and that is not a packaging problem: CupriCut pins CupriFace
**0.26.1**, five releases behind, so it rejects the documents for WOFF 2 fonts (0.28.1), `inset`
(0.27.0) and the `border` shorthand `rgba()` crash (0.26.2) — the last being the same crash this
repository wrote a rewrite rule for and deleted when it was fixed. **Upgrading CupriCut is what
makes these render**, and it is the obvious next thing to do outside this repository.

### What it had to decide

- **A package runs the longer of the declared duration and the motion.** The open question from
  Milestone 3, answered here because this is where it lands. The two failures are not symmetrical:
  too long holds a last frame, too short cuts the composition off mid-move. The disagreement goes
  in the report rather than being resolved silently.
- **`data-start` and `data-duration` are removed.** They are how a *loose* block declares its own
  length; a package has `render.duration`. Carrying both costs the element its one animation slot,
  which CupriCut reports as `CUT003` — 13 of the 187 blocks were that error before this.

### Fonts travel as files, not as `data:` URIs

The package carries the face as bytes and the host registers it. The CSS is left exactly as the
author wrote it: `font-family: 'Inter'` resolves because a face whose internal family name is Inter
has been registered, and the document is never touched.

Measured rather than assumed. Pointing the harness at a directory holding the corpus's own Inter
and re-running is the same act a host performs on an unpacked `.cutpkg`, and the fallback stops:

```
FONT x1: asked for Inter - registered: noto sans        before
                         - registered: ..., inter       after, and the line is gone
```

The alternative was to download each face and rewrite it into the document as an `@font-face` with
a base64 `src`. That also works - the conformance matrix has a case for it, and it passes - but it
is worse on every axis that matters: a third larger than the bytes it carries, duplicated into
every block that shares a family, and it edits markup that otherwise survives translation
unchanged. The only thing it buys is a document that is self-contained on its own, and a `.cutpkg`
is already the unit of self-containment.

**What it is worth on the score: nothing, and that is not a reason to skip it.** Eight blocks
measured both ways, chosen as the ones a missing Inter should hurt most:

| | before | after |
|---|---|---|
| `us-map-bubble` | 95.5% | 95.5% |
| `spain-map` | 82.2% | 82.2% |
| `us-map-flow` | 84.3% | 84.0% |
| `code-snippet-visual-studio-light` | 80.4% | 80.3% |

Under a third of a point either way, and twice negative. The same result as the WOFF 2 decoder, for
the same reason: these compositions paint text on a small share of a large frame, so a typeface is
a small number of pixels however wrong it is. It still has to be right. A delivered package that
renders a designed composition in Noto Sans is visibly wrong to the person who made it, and no
pixel measure this repository has is going to say so.

So this is **output quality, scheduled with Milestone 5** - not a way to move the corpus number,
and it should not be sold as one.

### What is still missing, which is the bytes

Registering a face only helps when there is a file. Of the families the corpus asks for and does
not get, the corpus itself ships only Inter:

| family | blocks | have the bytes? |
|---|---|---|
| Inter | 40 | yes, three weights, in one block's assets |
| JetBrains Mono, Space Mono, Bebas Neue, Lato | 69 | no - open licences, fetchable at translate time |
| SF Mono, Menlo, Helvetica Neue, Georgia | 26 | **no, and never** - proprietary system faces |

The last row is the one with no good answer. A block that asks for Menlo cannot be packaged with
Menlo, so the package has to carry a substitute and the report has to say which, by name, every
time. That is a decision about honesty rather than about fonts.

---

## Decided — do not re-open

| | |
|---|---|
| **Its own repository** | The sibling plan said start it inside CupriCut as `Services/Lex/` and extract later, when there was a corpus worth regression-testing and a second consumer. There is a corpus of 187 now, on day one, and it is large enough to steer the design — so the tight loop argument no longer holds and a repository boundary costs less than the coupling would. |
| **The corpus is not vendored** | Somebody else's work, under Apache 2.0, that changes. Fetched on demand so the survey always describes what is actually there. |
| **Static analysis, never execution** | No JS runtime, not even a sandboxed one. Refusing a block is an acceptable outcome; rendering it wrongly is not. |
| **Translate once, not at render time** | Output is a `.cutpkg`. Nothing depends on this tool to render. |
| **The report is a deliverable** | Not a log. An empty report means perfect or lying. |
| **Progress is corpus score** | Not rule count, not blocks-without-errors. Frames compared against the browser. |

---

## Open — decide before the code that depends on them

- **How dense the ease sampling should be.** Settled in shape — the curve is evaluated and sampled
  into stops, because `animation-timing-function` is per-animation and an element gets one
  animation. Not settled in number: it is eight stops per eased tween because eight was a round
  number, and the comparison harness is what should decide it.
- ~~**Whether `<svg>` survives.**~~ **Closed by 0.27.0**, and the answer reversed: the engine does
  draw inline `<svg>`, through the optional `CupriFace.Svg` package and a `UseSvg()` call. 32% of
  blocks use it and they now get real paths. Gradients, `<text>` and `<use>` still do not draw.
- ~~**What to do about `letter-spacing`.**~~ **Closed by 0.27.0**: implemented in the engine, so
  there is nothing to approximate and nothing to report. It was 80% of blocks.
- ~~**External fonts.**~~ **Closed**: the package carries the bytes and the host registers them.
  See Milestone 5. Both halves were measured first - `.woff2` decodes as of 0.28.1, and a `data:`
  URI works too and was rejected anyway for being the larger, more invasive way to do it.
- ~~**Blocks that build timelines from data.**~~ **Closed, and the answer is "it reaches a lot and
  is worth almost nothing".** A loop whose extent the document states is now written out: a
  `forEach` over a stated array, a `for` with a literal bound or one over such an array's length,
  and `for-of`. That is **622 of the corpus's 1,110 loop-bodied GSAP calls**, and unrolling them
  put **70 more elements** under animation, 883 to 953.

  The corpus moved 38.20% to 38.28%, and three blocks account for nearly all of it. The reason is
  in the refusals it uncovered: animations matching no element went from 28 to 71, `.add()` of a
  nested timeline from 41 to 125, and 126 refusals appeared naming targets like `cards[i]` and
  `spinner` - DOM references held in variables rather than selector strings. The loops were never
  the obstacle. What is inside them addresses elements the document does not contain, because the
  JavaScript that would have built them is the JavaScript that was removed.

  Kept anyway, and not only because it is correct. The refusals are now specific: "targets
  `cards[i]`, which could not be reduced to a CSS selector" instead of "N calls inside a loop",
  which is the difference between a report that names the obstacle and one that names the shape of
  the code around it.
- **Whether the engine's bugs are ours to work around.** Three rewrites now exist only because
  CupriFace 0.26.1 crashes or ignores something. They are measured, named, and pinned by tests that
  fail when the engine is fixed — but every one of them is output that differs from what the author
  wrote, and the better fix is upstream.
