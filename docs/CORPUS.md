# The corpus

187 HyperFrames blocks, plus 220 components and 9 examples. Real, designed, animated compositions
by people who were not thinking about this engine — which is exactly what makes them worth testing
against.

```
python tools/fetch-corpus.py       # ~110 MB, keeps registry/ only
python tools/survey.py             # the numbers below
python tools/survey.py --json docs/survey.json
```

Not vendored. See [tools/fetch-corpus.py](../tools/fetch-corpus.py) for why.

---

## The finding that reshaped the plan

**Not one block uses CSS animation.**

```
   187  100%  loads GSAP
   187  100%  inline <script>
     0    0%  @keyframes
     0    0%  CSS animation:
     0    0%  CSS transition:
```

The original plan for this work — written in `CupriCut/PLAN.md` before anyone counted — assumed
design tokens and typography would transfer nearly free and that the GSAP compiler was the *hard
half*, worth attempting for "the subset a frame pack actually uses".

Half of that is right. Tokens and typography *do* transfer nearly free: `:root` custom properties,
`var()` with fallbacks, and the `data-start` / `data-duration` vocabulary all work unchanged, and
87% of blocks already carry those attributes.

The other half is wrong in an important way. There is no subset of this corpus that works without
the compiler, because **every block is 100% JavaScript-animated**. A block imported without its
motion is a still image. So the compiler is not an optional second phase — it is the feature, and
everything else is the part that arrives for free once it exists.

---

## What has to be understood

### GSAP, by occurrence

| call | uses |
|---|---|
| `to` | 1129 |
| `set` | 1045 |
| `fromTo` | 362 |
| `timeline` | 216 |
| `from` | 82 |
| `add` | 68 |
| `parseEase` | 52 |
| `time` | 44 |
| `eventCallback` | 23 |
| `call` | 23 |
| `addLabel` | 14 |
| `duration` | 9 |
| `seek` | 7 |
| `clear` | 3 |
| `pause`, `defaults` | 1 each |

**Four verbs are 2618 of ~3100 calls.** `to`, `set`, `fromTo`, `from`. A compiler that handles
those four, `timeline`, `add`, `addLabel` and position parameters covers the corpus; everything
below `addLabel` in that table is a long tail to refuse by name.

`eventCallback` and `call` are interesting: they are the only calls that are *not* motion. They
are what CupriCut's `data-cut-event` is for — declared and reported, never executed.

### Features that will need a decision, by how many blocks they affect

Measured against **CupriFace 0.26.1** by `conformance/` — see
[`conformance/support/0.26.1.json`](../conformance/support/0.26.1.json). Nothing below is a guess.

| | blocks | CupriFace 0.26.1 |
|---|---|---|
| `position: absolute` | 187 — 100% | **works** |
| `letter-spacing` | **150 — 80%** | **ignored** (`CF0050`). Four in five blocks lose their tracking. |
| `line-height` | 112 — 59% | **works** — as of 0.25.1; badly broken before it |
| `var()` | 73 — 39% | **works**, fallbacks included |
| `<svg>` | **61 — 32%** | **NOT rendered.** The single largest gap. |
| 3D transforms | **54 — 28%** | **NOT rendered** — `rotateY`, `translate3d` and `perspective` all paint nothing |
| external font | 44 — 23% | download and embed as `@font-face` with a `data:` URI — **works** |
| `clip-path` | **40 — 21%** | **NOT rendered** |
| `<canvas>` | **38 — 20%** | drawn by JavaScript. Untranslatable — name it and refuse. |
| `filter:` | 37 — 19% | **works** — `blur()` and `brightness()` both paint |
| `display: grid` | 27 — 14% | **works** |
| `<img>` | 23 — 12% | becomes `<cupri-image>` |
| `@font-face` | 16 — 8% | **works**, `data:` URIs included |
| `backdrop-filter` | 12 — 6% | **NOT rendered** |
| `mix-blend-mode` | 11 — 5% | **NOT rendered** |
| repeating gradient | 5 — 2% | **NOT painted**; rewrite to hard stops |
| `<video>` | 1 | one block |

Also measured, and not to be assumed from a browser: `box-shadow` and `gap` **work**; `text-shadow`
and `align-self` do **not**; `calc()` in `animation-delay` collapses to **zero**; and of everything
tested only `width`, `height`, `opacity` and `transform` **animate** — `left`, `margin-left`,
`background-color` and `color` all paint perfectly well and animate not at all.

### What that means for reach

Three of the four biggest unknowns came back negative, and they are not small:
**`<svg>` at 32%, 3D transforms at 28%, `clip-path` at 21%.** With `<canvas>` at 20%, a large part
of the corpus uses at least one visual feature the engine does not render, quite apart from the
motion.

That is not a reason to stop — it is the reason to have measured before writing rewrite rules, and
it changes what "supported" can honestly mean. Some of these have workarounds (a `clip-path: inset`
is an `overflow: hidden` parent; a static `<svg>` could be rasterised at import). Some do not.

### What the harness then measured

The [comparison harness](HARNESS.md) has since scored the corpus frame by frame against a browser,
and found three things the survey above could not see, because none of them is a CSS property:

| | blocks | |
|---|---|---|
| a `border` shorthand with a spaced `rgb()` | **35** | **crashes CupriFace 0.26.1.** The document cannot be loaded at all, so these blocks have no score, not a bad one. |
| the composition inside a `<template>` | **13** | inert. Renders nothing in any browser until a host clones it in. |
| `window.__timelines` expected to already exist | **25** | the block throws without it, and never registers its timeline |
| `data-duration` disagreeing with the timeline's own span | **50 of 150** | by more than a quarter of a second. The compiler will have to decide which one a translated composition keeps. |

---

## What a block looks like

One self-contained HTML file, median 12 KB, largest 95 KB:

- `<head>` pulls GSAP from a CDN and carries an inline `<style>`
- the root element has `data-composition-id`, `data-width`, `data-height`, `data-start`,
  `data-duration`
- `data-composition-variables` holds a JSON schema of the template's inputs — 25% of blocks
- a sibling `registry-item.json` repeats the dimensions, duration and variables as a manifest
- an inline `<script>` builds a GSAP timeline

The variables schema is worth noticing: it is the same idea as a `.cut.json` being a template whose
text and figures are the only thing that changes between renders. A translated block should keep
it.

---

## How progress is measured

**The fraction of the corpus that renders correctly.** Not the number of rewrite rules, not the
number of blocks that parse without an error — how many produce frames that match the browser.

That instrument now exists — `harness/`, see [HARNESS.md](HARNESS.md) — and its first reading is
**55.2% matching over the 125 blocks whose reference actually moves**, with **150 of 150 scored
blocks rendering one still frame**. Nothing animates yet, which is the honest place to start from.
