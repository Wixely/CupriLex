# What cannot be carried, and why

Three outcomes for every block: it translates, it needs work here, or **no amount of work here
will reach it**. This document is the third list, because a gap that cannot close is a different
kind of fact from one that has not closed yet, and conflating the two is how a roadmap fills up
with items nobody can finish.

Everything below is measured. The classification script and the corpus survey are in
[CORPUS.md](CORPUS.md); the refusal counts come from running the packager over all 172 measured
blocks, which renders nothing and so can be re-run in a minute.

---

## The shape of the corpus

165 blocks on disk, 7 of which carry a `demo` variant that is measured separately: 172 measured.

| what the block is | blocks | mean of content | median |
|---|---|---|---|
| **translatable** — its DOM is in the markup and its motion is GSAP over it | **81** | **62.6%** | 67.5% |
| partly script-built — some of it translates, some is created at runtime | 15 | 37.2% | 40.0% |
| DOM built by script — the markup is a shell and `createElement` fills it | 33 | 16.2% | 5.5% |
| 2D canvas — the picture is drawn with `getContext("2d")` | 17 | 25.0% | 2.0% |
| WebGL canvas — the picture is a shader | 19 | 50.6% | 60.9% |

**84 of 172 blocks have a ceiling that nothing in this repository can lift.** The corpus mean is
held at roughly half by those 84; the 81 that are actually translatable average 62.6%.

That table is the single most useful thing in this document. A corpus mean of 49.2% reads as "this
tool gets about half of it", and that is not what is happening: it gets about two thirds of the
blocks it can see at all, and nothing of the ones it cannot.

The scores in the table were measured on CupriFace 0.38.0, one release before the one pinned now,
so the translatable row is a floor rather than a reading. The classification is what matters here
and it does not move with the engine.

---

## 1. The DOM is built by JavaScript — 33 blocks

The markup holds a shell and the script fills it. `carousel-circle-1` is five elements of markup
and ten `createElement`/`innerHTML` writes; `claude-exchange` and `chatgpt-exchange` build an
entire chat transcript, status bar and keyboard from nothing.

**Why it cannot be fixed here.** A tween's target has to be a CSS selector, and an element that
does not exist in the document has no selector. Not "a selector this compiler cannot work out" —
*no* selector, because there is no element. The refusals say exactly that, in two forms:

```
a .to() on `spinner`, which could not be reduced to a CSS selector
an animation on '#mk-lg-dot-0-0', which matches no element in the translated document
```

201 refusals of the first kind and 71 of the second, and they are nearly all these 33 blocks.

The only way through is to run the script, which is the one thing this tool does not do — and the
reason is not squeamishness. Running it means a DOM, a layout engine, a network stack and a
JavaScript runtime, which is a browser; the whole point is to produce something CupriFace renders
without one. A tool that needed a browser to produce its output would have no reason to exist.

**All 25 `carousel-*` blocks are in this group**, which is 15% of the corpus in one family. They
are a parametric rig: a path is sampled, N cards are created, and every card's position, scale and
depth-rotation is computed per frame from the path. The single real GSAP tween is the entry fade.
Even granting the elements, the motion is not in the timeline — it is in the function that runs
sixty times a second.

**Not worth chasing further.** The honest limit is that a block whose markup is a shell is not a
composition this tool can translate, and saying so is more useful than another round of selector
inference.

## 2. The picture is a canvas — 36 blocks

19 WebGL and 17 2D. `vfx-liquid-glass`, `transitions-destruction`, `cosmic-orb`, `oscilloscope-trace`
and the rest draw into a `<canvas>` from an `onUpdate` callback that runs every frame.

The GSAP timeline in these blocks does not animate anything you can see. It animates a **plain
object** so that a callback can read numbers off it:

```
a .to() of the plain object `animState` - it animates numbers for a callback to use,
and there is no JavaScript to run the callback
```

160 refusals of that kind, and they are these blocks. There is no CSS that expresses "call this
function with 0.37 and let it draw" — the drawing *is* the composition.

**The WebGL group scores deceptively well** (50.6% mean, 60.9% median), and that is a measurement
artefact worth naming rather than banking. These are full-frame transitions: the browser reference
spends much of the timeline showing something close to a static image, so an engine render that
paints the static layers and none of the shader still matches a lot of pixels. The harness says so
in its own output — `still reference 22 of 168 blocks change under 1% of their pixels over time;
their score says little`. Do not read these scores as partial success.

**A block whose reference barely moves has a score that is noise, and it will mislead you.**
`frost-sequence-camera-orbit` paints 0.03% of its frame and reads 40.0% on one run and 60.0% on
the next, tracking a browser reference that is itself not identical between runs. I credited a
compiler change with that twenty points before checking, and the change had no pixel effect at
all. The harness already says which blocks these are — `still reference 22 of 168 blocks change
under 1% of their pixels over time; their score says little` — so the rule is: **read a per-block
delta only after checking the block is not in that set.**

**Four blocks cannot be measured at all**, and three of the four are canvas:

| block | why there is no reference |
|---|---|
| `cuboid-carousel`, `orbit-card` | the block's own script throws in the browser |
| `vfx-iphone-device` | never becomes ready within 30s |
| `vfx-shatter` | seeking to 3s fails: `ctxA.drawElementImage is not a function` |

Those are defects in the source blocks, not in this tool, and they are reported rather than
skipped so the count of 172 stays honest.

## 3. Motion that is not expressible in CSS at all

Carried by nothing, and not because the engine is missing a feature.

**`textContent` — 32 tweens across 7 blocks.** GSAP's TextPlugin retypes a string character by
character. CSS has no property that animates text content. The `code-*` family and
`vfx-text-cursor` are built on it. There is no rewrite; a cross-fade between two stacked elements
could fake a *replacement* but not a typing effect, and it would need an element per frame.

**`visibility` — 43 tweens across 34 blocks.** `autoAlpha` is already mapped to `opacity`, which
is the part that matters. A bare `visibility` tween is usually a composition hiding its root at
the very end (`tl.set("#root", { visibility: "hidden" }, DUR - 0.02)`). Carryable in principle;
worth approximately nothing, because it affects the last twentieth of a second of a timeline the
harness samples five times.

**A stagger — 14 refusals.** `stagger: 0.25` over a selector that matches N elements needs one
animation per element with a different delay. **CupriFace 0.38.0 lifted the one-animation-per-element
constraint (#284), so this is now expressible and has moved off this list.** It is work, not a
wall, and it is now the largest thing the lifted constraint actually buys — see
[TRANSLATION.md](TRANSLATION.md), which records why exact per-tween eases turned out not to be.

**A nested timeline added with `.add()` — 26 refusals.** Flattening a sub-timeline onto its
parent's clock is tractable static analysis and belongs on the work list, not here.

## 4. A time that is not knowable — 62 refusals

```
a .set() placed at `t_at(f)`, which is not knowable, so the clock cannot be advanced past it
a .to() placed at `phraseAExit`, which is not knowable
```

A tween's position on the timeline comes from a function call or a binding that depends on
something measured. Where the dependency is arithmetic the evaluator already folds it; what is
left depends on the DOM (an element's measured width), on fetched data, or on a host object
(`window.__hyperframes.getVariables()`).

**The host-variable case is the interesting one and it is genuinely unfixable.** HyperFrames
compositions read authorable variables at runtime:

```js
const V = window.__hyperframes.getVariables();
DATA.text.words = String(V[REG.textVars.line1]).split(" ");
```

The literal in the markup is only a *default*. The real value arrives from whatever is rendering
the block, so the compiler cannot know whether the default survives — and guessing would mean
emitting a timeline whose word count is wrong whenever anybody edits the text. The refusal is the
right answer.

## 5. Things that look unfixable in the report and are not

Kept here deliberately, because this list is only trustworthy if it does not quietly collect items
that turned out to be work.

**296 tweens were named as behind an unfollowed construct and were not lost at all.** The refusal
"N `.to()` call(s) inside a loop, a callback or a function this compiler does not follow" ran to
598 tweens across 99 blocks and bundled three different causes under one sentence. Separating them
showed that most were not losses:

| what it turned out to be | tweens |
|---|---|
| behind a test the compiler had DECIDED, in a branch that does not run | 178 |
| not tweens — three.js `group.rotation.set()`, `Float32Array.from()` | 37 |
| a feature switched off: a zero-iteration loop, an empty list | included above |
| still genuinely behind an unfollowed construct | 316 |

Total refusals over the corpus went 1,747 → 1,584 → 1,534 → **1,282** across those fixes and the
filter and z-index work, and not one of the removals was a tween that stopped being reported while
still being lost.

A report that invents losses is as bad as one that hides them, and it is worse for anybody
deciding whether a block is worth carrying. Both were fixed; see the commit log.

**`filter` and `z-index` were on this list and are not any more.** Both were refused as properties
the engine does not animate, which was true when it was written. This repository filed #290 and
#291 after measuring that z-index did nothing to 258 declarations across 71 blocks and that filter
painted but never moved; CupriFace 0.39.0 fixed both, and 104 filter tweens across 16 blocks and
32 z-index tweens across 9 are carried now. Seventeen blocks improved, the whole `transitions-*`
family by about twenty points each and `vfx-text-cursor` from 2.6% to 65.6%.

That is the pattern worth keeping: a refusal names the engine's actual animatable set, so a
refusal that has gone stale is visible the moment the matrix is re-run against a new release. See
[CONFORMANCE.md](CONFORMANCE.md).

**An overlapping tween of the same property — was 36 refusals across 6 blocks, now 13 in one.**

The refusal used to read "and one element gets one animation", which pointed at the constraint
0.38.0 lifted and implied a second animation and an emitter rewrite. **It was the wrong reason.**
GSAP does not overwrite by default: both tweens run and the timeline applies them in order every
tick, so the later-added one is what the frame shows for as long as it lasts. So the question is
only which one ends first — and once the refusal was made to say, counting answered it: 32 of the
36 have the earlier tween ending first, most of them a one-millisecond sliver between back-to-back
tweens. Those need no second animation, only the right stops, and they are carried now.

What is left is the four where the earlier tween **outlasts** the later one, so the picture goes
back to it afterwards: before, during, after — three régimes, which one track of stops cannot say.
13 refusals, all in `blue-sweater-intro-video`. That tail does need a second animation, scoped to
the later tween's window with no fill so the primary shows through on both sides, and for a
transform component it would have to write the whole transform during that window. One block, so
it is written down here rather than done.

**The engine's blur radius is right, and I checked rather than assumed.** When those two blocks
fell, the obvious suspicion was that CupriFace used the blur radius as the Gaussian standard
deviation where CSS halves it — the `box-shadow` rule. Measured on both sides by fitting the
alpha profile across a hard edge (50% crossing to 84.13% crossing is one sigma):

| | blur(4px) | blur(8px) | blur(16px) |
|---|---|---|---|
| Chrome, CSS `filter` on an element | 4.00 | 8.00 | 16.01 |
| CupriFace 0.39.0 | 4.28 | 7.69 | 15.63 |

Filter Effects says `blur(<length>)` is `feGaussianBlur` with `stdDeviation` equal to the length,
so sigma = radius with no halving, and both agree. The engine is within a few percent, which is a
kernel approximation and not a defect. **No issue filed**, and the suspicion is recorded here so
nobody spends an afternoon on it twice.

---

## How to re-derive all of this

```
dotnet run --project harness -c Release -- --all --package <dir>   # refusals, no browser
dotnet run --project harness -c Release -- --all --gallery --out review
dotnet run --project conformance -c Release -- --out conformance/support
dotnet run --project conformance -c Release -- --compare conformance/support/0.38.0.json conformance/support/0.39.0.json
```

The first is the one to run before believing anything here. Every count in this document came out
of the `report.md` inside each `.cutpkg`, which is the same text a user of this tool reads.
