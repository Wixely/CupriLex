# The GSAP compiler

**Timelines in, `@keyframes` out, and a refusal by name for everything that could not come.**

```
dotnet run --project compiler -- shapes            # what the corpus writes
dotnet run --project compiler -- reach             # how much of it is carried
dotnet run --project compiler -- translate <block> # one block, with its CSS
```

The whole feature, and the reason there is no subset of this corpus that works without it: all 187
blocks are animated entirely in JavaScript — see [CORPUS.md](CORPUS.md).

---

## Static analysis, and where its ceiling is

The decision never to run a block's JavaScript is in [PLAN.md](../PLAN.md) under *do not re-open*,
and it is worth saying what it costs, because the number was measured before any of this was
designed.

`shapes` reads every GSAP call out of a syntax tree and classifies its arguments:

| | of 2579 motion calls |
|---|---|
| straight-line code | 39% |
| straight-line, with literal values, and a target worth resolving | 34% |
| **blocks with nothing at all outside that subset** | **18 of 174** |

So a compiler that reads only what is written plainly reaches about a tenth of the corpus. The
other nine tenths write their timelines through variables, running totals, helper functions and
loops.

The answer is not an interpreter. It is the part of static analysis that every compiler does
anyway — **constant folding and binding resolution**:

- `const D = 0.6` then `duration: D * 2` folds to 1.2
- `const hero = document.querySelector(".hero")` then `tl.to(hero, …)` resolves to the selector
  `.hero` — the *text*, never the element, so the document is never consulted and the compiler
  cannot be wrong about what it matched
- `let t = 0; … t += 0.5` is followed through straight-line code. A third of the corpus keeps time
  this way, and treating the reassignment as unknowable refused about ninety tweens over a running
  total that is perfectly knowable
- an immediately invoked function is read as if its body were written where it stands, because it
  is. Nearly every block wraps everything in one, and counting those bodies as "inside a function"
  reported that almost nothing in the corpus was straight-line code

What stays refused: loops, callbacks, functions invoked more than once, anything depending on a
value that is not in the source. Those are named and counted, never half-carried.

---

## The four engine constraints, and what each one forced

From [TRANSLATION.md](TRANSLATION.md), all four measured rather than assumed:

**One animation per element, and a comma-separated list runs neither.** So everything that ever
touches an element — five tweens on three properties at four different times — becomes a single
`@keyframes`, which means resolving every tween to an absolute time first and then writing the
element's whole state at every stop.

**Only `width`, `height`, `opacity` and `transform` animate.** Everything else is refused by name.
A tween of `backgroundColor` would parse, run, and change nothing, which is the failure this
repository is arranged around.

**Timing functions are honoured, but per animation.** A per-tween ease cannot be expressed in a
merged animation, so the curve is evaluated and sampled into stops and the animation itself runs
`linear`. Eight samples per eased tween, which is where this started rather than where measurement
put it — the honest way to choose that number is against the comparison harness.

**`calc()` in `animation-delay` is zero.** Nothing relies on a delay: every offset is baked into
the stop percentages.

---

## What a tween starts from

A `.to()` animates from wherever the element already is, which is not knowable until everything
before it has been resolved. So tweens are threaded in time order, per element and per property,
carrying the value forward.

| | |
|---|---|
| `.to()` | from the running value, to the values written |
| `.from()` | from the values written, to the resting value — **backwards**, and reading it as a `to` plays the composition inside out |
| `.fromTo()` | both ends given |
| `.set()` | a step: the previous value is held until a hair before it |

The resting value is the transform identity, or `1` for opacity. For `width` and `height` there is
no such answer — it is whatever the stylesheet says — so a tween that would need to know is refused
rather than guessed at.

Two tweens of one property overlapping in time is refused too. One element gets one animation, and
one animation cannot hold two answers.

---

## What it reaches

Run `reach` for the current numbers. At the time of writing:

```
187 blocks
 67 carry at least one animated element (36%)
520 elements animated in total, median 4 per block
1370 refusals, 2751 motion calls in the source
112 more are held at their end state rather than animated
 28 animation(s) across 7 block(s) land on a selector that matches nothing
```

Those last two lines were added after the fact, and the first line was a fifth higher before they
existed. Both are cases where the compiler did its job and the frame still cannot show it, which
reads as a complete translation and renders as a still.

The refusals are grouped by cause with examples quoted from the source, because a count says how
big a problem is and an example says what to do about it. The largest groups are properties the
engine cannot animate, calls inside loops and callbacks, and targets that are not elements at all —
`gsap.to(clock, { t: 1 })` animates a plain object for an `onUpdate` to read, and there is no
JavaScript to run the callback.

**Reach is not the score.** Frames decide the score, and a block whose every tween was read can
still render wrongly. What it was worth, against the browser:

| | before the compiler | with it, on 0.26.1 | on 0.27.0 | on 0.28.1 | with start values |
|---|---|---|---|---|---|
| blocks scored | 150 of 187 | 183 of 187 | **185 of 187** | 185 of 187 | 185 of 187 |
| mean of frame, over blocks whose reference moves | 55.2% | 62.8% | 62.7% | 62.7% | 63.8% |
| **mean of content**, all scored blocks | — | — | 38.1% | 38.2% | **38.2%** |
| blocks where the engine rendered one still frame | **150 of 150** | 145 of 183 | 147 of 185 | 147 of 185 | **140 of 185** |
| blocks the engine paints almost nothing in | — | — | — | 93 of 185 | **84 of 185** |
| the frame replaced rather than shifted | — | — | — | 12.8% | **11.0%** |

### Loops over data the document states

`for (let i = 0; i < 6; i++)`, `for (const row of ROWS)` and `ROWS.forEach((row, i) => ...)` are
written out, once per iteration, with the counter or the item and its index bound. It is still
static analysis: the start, the bound and the step are resolved by the same evaluator that resolves
a duration, and a loop whose extent cannot be resolved is left unread and refused exactly as before.
Nothing is executed. A loop longer than 256 iterations is refused rather than truncated, because
half a loop is motion that stops for no reason.

**622 of the corpus's 1,110 loop-bodied GSAP calls** iterate something the source states outright.
Reading them put 70 more elements under animation, 883 to 953.

**The corpus moved 0.08 of a point.** Three blocks improved, one lost 0.4, and the rest did not
notice. What the change actually produced was a better report, because the calls it reached then
refused for reasons of their own:

| refusal | before | after |
|---|---|---|
| an animation matching no element in the document | 28 | 71 |
| `.add()` of a nested timeline, not flattened | 41 | 125 |
| a target like `cards[i]` or `spinner`, not a selector | — | 126 |

The loops were never the obstacle. What is inside them addresses elements the document does not
contain, because the JavaScript that would have built them is the JavaScript that was removed. This
is the fifth measurement to land on that sentence.

### Start values, read out of the stylesheet

A tween starts from wherever the element already is, and GSAP reads that out of the computed style
before it animates. A compiler that never runs the document assumed `opacity: 1` and the identity
transform, so `.to(el, { opacity: 1 })` on an element the stylesheet authors at `opacity: 0`
produced a keyframe whose every stop said 1 — a fade in a browser and a still here. That was
**112 animations across 45 blocks**, the largest correctable gap the refusal counts named.

`compiler/Authored.cs` resolves the document's own cascade for the elements a selector matches:
rules in specificity then source order, `@media` descended into, the `style` attribute last.
**62 flat animations are left.**

Two rules make it safe rather than clever:

- **Declared values, never computed ones.** `ComputeStyle()` is the obvious call and it throws on
  `translate(-50%, -50%)`, the commonest centring idiom in this corpus. It is also the wrong
  question: what a tween starts from is what the author wrote, not a pixel matrix resolved against
  a viewport the compiler does not have.
- **Anything unresolvable answers nothing.** A selector matching no element, two elements whose
  rules disagree, a `matrix()` or a `var()`, a start in a different unit from the tween — all keep
  the old assumption and the old refusal. A wrong start value is an animation that runs from the
  wrong place, which is worse than one that does not run: the second shows up in the report.

Untouched transform components are carried into every stop for the same reason. Without it an
element authored `translate(-50%, -50%) scale(0)` and tweened on `scale` alone emitted
`transform: scale(1)`, which replaces the whole declaration and drops the centring — correct about
the property it carried and wrong about where the element is.

**It did not move the mean, and the reason is instructive.** 10 blocks better, 9 worse, 38.2%
either way. The losers are almost all `transitions-*`, where an element wrongly held visible was
covering the whole frame; hiding it correctly exposed other content that is wrongly visible for
reasons this change does not address. Two wrongs were cancelling. Every other measure improved.

38 blocks animate where none did. The engine upgrade added two more scorable blocks and left the
frame-wide mean where it was, which is itself worth knowing: on the 183 blocks scored in both runs
it moved 67.1% to 67.3%, 15 blocks improved by about three points each and two lost under two.

The content row has no earlier figures because the measure did not exist until the frame-wide one
was caught ranking the corpus backwards. It is 29 points below the frame-wide number and it is the
one to steer by; see [HARNESS.md](HARNESS.md) for why there are now four.

---

## Two ways a translation can be complete and still show nothing

Both were found by asking why 47 blocks carried compiled motion and rendered an identical frame at
every sample, which was more blocks than the 38 that moved.

**The selector matches nothing.** Resolution never consults the document, which is what makes it
static and what makes it impossible for the compiler to be wrong about which elements it meant. The
cost is that a selector naming an element the script was going to build resolves perfectly and then
finds nothing, and the rule is emitted valid and inert. 28 animations across 7 blocks do this.
`chatgpt-exchange` is the clearest case: all twelve of its animations land on elements that do not
exist in the static document. Checking this is verification rather than resolution, so it happens
once at the end against the document about to be written, and it is reported by name.

**The tween ends where the compiler assumed it began.** `.to(el, { opacity: 1 })` is a real fade in
a browser when the stylesheet authors that element as `opacity: 0`, because GSAP reads the computed
style. The compiler cannot, so it assumes the resting value and emits a `@keyframes` holding 1
throughout. 112 of what the reach report used to call animated elements were this.

Deleting those as no-ops is the obvious cleanup. It is also wrong, and the corpus said so within a
minute: `flowchart-vertical` fell from **97.9% to 0.9%**. The flat animation is wrong about the
motion and right about the **end state**, and with `both` it is the only thing holding those
elements visible at all. Every one of them is authored hidden and revealed by the script.

So they are emitted, reported, and not counted as motion. The real fix is to read the element's
authored value, which means resolving the cascade for one element without running the document.
That has not been attempted.

---

## The rewrites that came out of running it, and the two that are already gone

Three rules were pulled forward from Milestone 4 because each was blocking the *measurement* of the
compiler rather than merely improving on it. Each declared the engine behaviour it worked around,
each condition went into the conformance matrix, and each was pinned by a test built to fail when
the engine stopped needing it.

Two of those tests have now failed, which is the system working:

- **Colours to hex** worked around a crash reachable from three parsers that cost 35 blocks their
  score entirely. Fixed in **CupriFace 0.26.2**. Rule deleted.
- **`inset: 0` to a percentage size** worked around an overlay with no size, at 115 blocks of 187.
  Fixed in **0.27.0**, including the deeper half where the four longhands were accepted and sized
  to nothing. Rule deleted.

Both were reported upstream and both were fixed there, which is a better outcome than either rule:
the engine now renders what the author wrote, and this repository emits it unchanged. What the
exercise cost while it lasted is written up in [TRANSLATION.md](TRANSLATION.md), because the route
to each was wrong twice before it was right.

One rewrite remains.

**`<template>` inlined.** 13 blocks put the whole composition inside one, scripts included, and
template content is inert in any browser until a host clones it in.
