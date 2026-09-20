# The comparison harness: how right is a translated block?

One number per block, against a real browser, with the picture that explains it.

```
dotnet run --project harness -- bar-chart-race       # one block
dotnet run --project harness -- --all                # the corpus, and a baseline.json
```

This is the instrument the project is steered by. Progress is **the fraction of the corpus that
renders correctly** — not how many rewrite rules exist, not how many blocks load without an error.
A rewrite rule that is not measured here has not been shown to help.

---

## What it does

1. **Renders the block in a browser**, seeked to N times across its declared duration.
2. **Renders the translated block through CupriFace** at the same times.
3. **Compares the frames** and writes a triptych — browser, engine, difference — for the worst
   sample, or for every sample with `--frames`.

The translation in step 2 is currently the identity: markup through, motion refused by name. That
is deliberate. Milestone 2 exists to produce the number Milestone 3 has to move, and a baseline
flattered by a half-written rewrite rule would be measuring the rule rather than the corpus.

---

## Seeked, not played

A screenshot of a composition that is playing is a screenshot of whenever the screenshot happened.

Every block in the corpus registers a GSAP timeline on `window.__timelines` — measured, 187 of
187, and 183 of them create it already paused — so the reference can be asked for an exact time
rather than raced for one. The four that do not are paused here before being seeked:

```js
tl.pause();
tl.time(t, false);
gsap.globalTimeline.pause();
gsap.globalTimeline.time(t, false);
```

The global timeline is seeked as well as the registered ones, because a bare `gsap.to(...)` never
reaches `window.__timelines`; it plays on the global timeline from page load and would otherwise be
frozen at "however long the page took to load" in every frame.

**The harness provides the host contract these blocks expect.** 162 of the 187 create
`window.__timelines` themselves. The other 25 — every `carousel-*` among them — write
`window.__timelines[DATA.id] = tl` straight into an object they expect their host to have made.
Without it that line throws, no timeline is ever registered, and the block is unmeasurable. So a
one-line `window.__timelines = window.__timelines || {}` is injected before the document's own
scripts run.

`window.__hyperframes` is deliberately **not** stubbed. All 44 blocks that read it guard the access
and fall back to their authored defaults, which is the same content the engine is given; stubbing
it would make the two sides diverge on purpose.

---

## The number, and what it is worth

| | |
|---|---|
| **matching** | the share of pixels not visibly different — any channel off by more than 8 of 255. The headline. |
| **mean error** | mean absolute difference per channel. Reported second, because it is far too kind. |
| **reference moves** | how much the browser's own frames differ from its first one. The calibration. |
| **engine moves** | the same for the engine's frames. **Zero means nothing animated at all.** |

Both numbers are generous to a composition that leaves most of the frame flat, and it is worth
being blunt about how generous. `carousel-circle-1` renders in the engine as an empty grey
rectangle — not one of its cards appears — and scores **99.4% mean similarity**, because white
cards on light grey are a small per-pixel difference over a tenth of the frame. On the matching
measure the same frame scores 90.9%, which is better but still not a number to quote alone.

That is why every report carries the differing share, the movement columns, and a picture. It is
also asserted in `ComparisonTests`, so nobody has to rediscover it:

> `A_frame_missing_a_tenth_of_itself_still_scores_over_ninety_percent_similar`

**A block whose reference could not be produced is never scored zero.** It is reported as
unmeasured, by name, with the reason. Unmeasured and wrong are different things, and a zero would
average into the corpus number as though it were a measurement.

---

## The baseline

The first corpus-wide run. **CupriFace 0.26.1, Edg/153.0.4234.32, Windows, 5 samples per block,
187 blocks in 5 minutes 46 seconds.**

| | |
|---|---|
| scored | **150** |
| unmeasured | **37** |
| mean matching | **61.6%** |
| median | 79.2% |
| **mean matching, over the 125 blocks whose reference actually moves** | **55.2%** — the number to beat |
| blocks where the engine rendered the same frame at every time | **150 of 150** |

```
90-100%   52 blocks
 70-90%   30
 40-70%   22
 10-40%   17
  0-10%   29
```

**Not one block animated.** That is the expected result and the point of measuring it: there is no
compiler yet, so the engine renders a still of whatever the markup says before any timeline touches
it, and 150 of 150 confirm it rather than assume it.

**Twenty-five of the 150 have a reference that barely moves** — under 1% of pixels change across
the whole composition — so their scores are not evidence of anything. `editorial-flash-overlay` is
the extreme: it scores **100%**, and its reference does not change by a single pixel. The two
renderers agree about a still. That is why the corpus number worth quoting is the one over blocks
whose reference actually moves.

### The 37 that could not be scored

| | |
|---|---|
| **35** | the engine threw on the document — the `border` shorthand crash, below |
| 1 | `vfx-iphone-device`: never became ready in the browser |
| 1 | `vfx-shatter`: the block's own script throws — `ctxA.drawElementImage is not a function` |

The last one is worth keeping: the block is broken in a browser too, so there is no reference to be
had and nothing for a translator to fix.

### What it read after the compiler

Same harness, same engine, same browser, with [the GSAP compiler](COMPILER.md) in the seam:

| | baseline | with the compiler |
|---|---|---|
| scored | 150 | **183** |
| unmeasured | 37 | **4** |
| mean matching | 61.6% | 67.1% |
| **over blocks whose reference moves** | **55.2%** | **62.8%** |
| engine rendered one still frame | **150 of 150** | 145 of 183 |

Three of the four blocks still unmeasured are the same engine crash in a form the rewrite does not
reach and a block whose own script throws in a browser. The one number that did **not** move the
way it looks: the mean over moving blocks is not a like-for-like comparison, because 33 blocks that
previously could not be loaded at all are now in the population, and they score below average.

---

## Reading a triptych

![browser, engine, difference](images/harness-bar-chart-race.png)

Left is the browser at that instant, middle is CupriFace, right is where they differ — magenta,
amplified three times because the differences that matter are often a few levels of grey across a
whole title, and an unamplified diff of those is a black rectangle.

They are only meaningful together. An engine frame alone looks plausible; it is the panel beside it
that shows the bars never grew.

---

## What it is careful about

**Fonts.** The engine is given CupriCut's shipped faces when that repository is beside this one. A
document with no registered face is at the mercy of whatever the machine has installed, which makes
a text-heavy corpus score differently on two machines for no engine reason.

**Subpixel antialiasing.** The browser runs with `--disable-lcd-text`. Windows fringes text with
colour the engine never draws, and it would have been counted as a difference on every letter of
every block.

**Scrollbars, GPU, device scale.** Hidden, disabled, pinned to 1. A scrollbar is fifteen pixels of
pure difference; a GPU raster is one machine's answer rather than a reference.

**The page it is actually looking at.** After a navigate, `document.readyState` describes the
*previous* block for a moment — it polls green immediately and would be screenshotted five times.
The harness waits for the href it asked for, plus fonts loaded and images complete.

**Relative asset paths.** A browser resolves `assets/logo.png` against the document's location;
`CupriDocument.Load` is handed a string, which has no location. The harness makes those absolute
before the engine sees them. Not a rewrite rule — restoring what reading a file into a string took
away. Without it, twenty-three blocks would be scored against a reference that has its images while
the engine's copy does not.

---

## What the first run found

Two things worth knowing before reading any score, both found by running rather than by reading.

**An engine crash on ordinary CSS, costing 35 of the 187 blocks.** *(As diagnosed on the first
run. Two more parsers reach the same fault, and one of them survives the obvious fix - see
[COMPILER.md](COMPILER.md).)* `border: 1px solid rgba(198, 173, 144, 0.32)` throws
`ArgumentOutOfRangeException: length ('-6')` out of `Colors.TryParse`. The shorthand parser splits
the value on spaces and offers each token to the colour parser, so `rgba(198,` arrives with an open
parenthesis and no close, and `text[(IndexOf('(') + 1)..IndexOf(')')]` becomes `[5..-1]`. It
reaches blocks that never write `rgba` beside `border` at all: `beat-freeze-cut` writes
`border: 1px solid var(--bfc-line)`, and that custom property holds a spaced `rgba()`. Two blocks
cannot be loaded at all because of it. Pinned by `EngineBugTests`, which will fail when it is
fixed — which is the point. The longhand `border-color` takes the same value without complaint.

**Declared duration and timeline duration disagree, in a third of the corpus.** 50 of the 150
scored blocks declare a `data-duration` more than a quarter of a second away from what their GSAP
timeline actually spans — `bar-chart-race` declares 12 seconds against a timeline of 10, so a sixth
of its reference is a still frame. The harness samples the declared duration, because that is what
a renderer would use, records the timeline's own span beside it, and says so when they disagree.
The compiler will have to decide which one a translated composition keeps.

---

## Running it

```
dotnet run --project harness -- <block>     score one block
dotnet run --project harness -- --all       score the corpus

  --samples N    times across the declared duration (default 5)
  --out DIR      where frames and baseline.json go (default harness/out)
  --frames       keep every sample's images, not only the worst
  --limit N      stop after N blocks, for a quick look
```

Needs the corpus (`python tools/fetch-corpus.py`), a browser — Edge or Chrome, or
`CUPRILEX_BROWSER` pointing at one — and the network, because every block loads GSAP from a CDN.

`harness/out/` is ignored by git. `baseline.json` holds every block's score and the engine, browser
and platform that produced it; `blocks.jsonl` is appended as the run goes, so a process that dies
at block 150 does not cost the 149 measurements before it.
