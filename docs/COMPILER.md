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
 84 carry at least one animated element (45%)
632 elements animated in total, median 4 per block
1230 refusals, 2751 motion calls in the source
```

The refusals are grouped by cause with examples quoted from the source, because a count says how
big a problem is and an example says what to do about it. The largest groups are properties the
engine cannot animate, calls inside loops and callbacks, and targets that are not elements at all —
`gsap.to(clock, { t: 1 })` animates a plain object for an `onUpdate` to read, and there is no
JavaScript to run the callback.

**Reach is not the score.** Frames decide the score, and a block whose every tween was read can
still render wrongly. What it was worth, against the browser:

| | before the compiler | after |
|---|---|---|
| blocks scored | 150 of 187 | **183 of 187** |
| mean matching, over blocks whose reference moves | 55.2% | **62.8%** |
| blocks where the engine rendered one still frame | **150 of 150** | 145 of 183 |

So 38 blocks now animate, 33 more can be loaded at all, and the number the project is steered by
moved seven and a half points. The two halves are not separable: the rewrites made 33 blocks
measurable *and* the measurement is what found the rewrites. See [HARNESS.md](HARNESS.md).

---

## Three rewrites that came out of running it

Each is a Milestone 4 rule pulled forward, because each was blocking the measurement of the
compiler rather than merely improving on it. Every one has a measured condition in
`conformance/support/0.26.1.json`, and each should be **deleted** when the engine no longer needs
it — `EngineBugTests` fails on that day and says so.

**Colours to hex.** `rgba(198, 173, 144, 0.32)` crashes CupriFace 0.26.1 in three different
parsers, and 35 blocks could not be loaded at all. The fault is one function, `Colors.TryParse`,
which takes the text between parentheses and gets a length of -6 when there is no closing one;
three callers hand it a fragment that has none. The first version of this rule took the spaces out
of colours inside `border` declarations, which recovered 25 blocks and left 10 — the rest were in
gradient layers and a `drop-shadow()`, and the `drop-shadow` one crashes with or without spaces
because the filter parser's own regular expression stops at the first `)`. A hex colour has no
parentheses at all, which is why the rule is the shape it is.

The alpha is **truncated, not rounded**, because that is what the engine does to its own `rgba()`:
`(byte)(0.65f * 255f)` is 165, and the nearest value is 166. Rounding cost six text-heavy blocks
two and a half points of score each — a remarkable amount for one level of alpha out of 255, and
completely invisible until two renders were put side by side. The conformance matrix holds both
halves of that pair, the rounded hex that differs from `rgba()` and the truncated one that does
not.

**`inset: 0` to a percentage size.** 115 blocks of 187 use it and the engine ignores it, so the
overlay meant to cover the composition has no size. The obvious expansion — the four longhands —
was written first and then measured, and it does **not** work: the engine accepts
`top/right/bottom/left` and still gives the element no size. `width: 100%; height: 100%` does work.
Both answers are in the matrix, and that measurement is the only reason the rule is right.

**`<template>` inlined.** 13 blocks put the whole composition inside one, scripts included, and
template content is inert in any browser until a host clones it in.
