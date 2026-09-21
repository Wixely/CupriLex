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
| **over the 125 blocks whose reference actually moves** | **55.2%** — the number every later milestone has to move |
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
dotnet run --project compiler -- shapes             # what the corpus writes
dotnet run --project compiler -- reach              # how much of it is carried
dotnet run --project compiler -- translate <block>  # one block, with its CSS
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
| mean matching, over blocks whose reference moves | 55.2% | **62.8%** |
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

- `<img>` → `<cupri-image>`
- **`letter-spacing`**, at 80% of blocks, still ignored by the engine and still only reported
- repeating gradients → hard stops
- animated `left`/`top` → `transform`
- external fonts → downloaded and embedded as `@font-face` with a `data:` URI
- `eventCallback` / `call` → CupriCut `data-cut-event`
- `data-composition-variables` → kept, since a translated block should still be a template

And the compiler's own unfinished business, which the refusal counts rank:

- **reading an element's authored start value.** 112 compiled animations hold one value for their
  whole length, because `.to(el, {opacity: 1})` on an element the stylesheet authors as
  `opacity: 0` is a fade in a browser and a still here. This is the largest correctable gap, and it
  needs the cascade resolved for one element without running the document.
- **a tween of a property the engine cannot animate**, the largest single refusal group. Nothing to
  rewrite in most cases, but `backgroundColor` could cross-fade two stacked elements.
- **`.add()` of a nested timeline**, refused rather than flattened
- **`repeat` and `stagger`**, both of which need more than one animation per element
- **how dense the ease sampling should be.** Eight stops was a guess and is still a guess; the
  comparison harness is what should answer it.

---

## Milestone 5 — output

A translated block becomes a **`.cutpkg`**: one file, assets and fonts as bytes, already
self-contained. A project that has been imported is just a project, and nothing depends on this
tool at render time.

Plus the report, which is the thing that makes the output trustworthy.

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
- **Whether `<svg>` survives.** Answered, and the answer is no: the engine does not draw it, and
  32% of blocks use it. Rasterising at import is the only route left, and nobody has tried it.
- **What to do about `letter-spacing`.** 80% of blocks, ignored by the engine. Report every time,
  approximate with word spacing, or press for engine support. Currently: report.
- **Blocks that build timelines from data.** Answered as far as measurement can: a loop is refused
  and counted, and the loop-shaped refusals are now among the largest groups. The question left is
  whether to unroll a loop over a *literal* array, which is statically resolvable, and how much of
  the corpus that would actually reach.
- **Whether the engine's bugs are ours to work around.** Three rewrites now exist only because
  CupriFace 0.26.1 crashes or ignores something. They are measured, named, and pinned by tests that
  fail when the engine is fixed — but every one of them is output that differs from what the author
  wrote, and the better fix is upstream.
