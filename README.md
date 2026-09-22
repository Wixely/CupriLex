# CupriLex

**Browser HTML in. CupriFace-safe HTML out. A report of everything that could not come with it.**

CupriLex translates compositions written for a browser — specifically
[HyperFrames](https://github.com/heygen-com/hyperframes) blocks — into HTML and CSS that
[CupriFace](https://github.com/Wixely/CupriFace) renders, so that
[CupriCut](https://github.com/Wixely/CupriCut) can turn them into video without a browser anywhere.

**The report matters more than the conversion.** Silently dropping a transition is how somebody
ships a video missing its transitions. Anything this tool cannot carry, it says so, by name, with
the line it was on.

---

## Read this before anything else

The corpus was surveyed before a line of the translator was designed, and it says something the
original plan did not expect.

**All 187 HyperFrames blocks are animated entirely in JavaScript. Not one uses CSS `@keyframes`
or `animation:`.**

| | |
|---|---|
| blocks surveyed | **187** |
| load GSAP | **187 — 100%** |
| have an inline `<script>` | **187 — 100%** |
| use `@keyframes` | **0** |
| use CSS `animation:` | **0** |
| use CSS `transition:` | **0** |

CupriFace has no JavaScript engine, by design. So there is no subset of this corpus that works
without the GSAP compiler — it is not the hard half of the job, it is the *whole* job. Everything
else (tokens, typography, layout) is the easy part that arrives for free once motion is solved.

Reproduce these numbers with `python tools/survey.py` — see [docs/CORPUS.md](docs/CORPUS.md).

---

## What has to happen, in order

1. **Know what the engine supports**, mechanically, per version — `conformance/`. Without this the
   translator is guessing about the target, and the guesses go stale every release. *(done)*
2. **Measure how far off it is**, against a real browser, frame by frame — `harness/`. *(done)*
3. **Compile GSAP timelines to `@keyframes`** for the verbs the corpus actually uses —
   `compiler/`. *(done)*
4. **Rewrite what can be rewritten** and **report what cannot**. *(in progress: three rules exist,
   each because it was blocking a measurement)*

See [PLAN.md](PLAN.md).

---

## Where it stands

A headless browser renders each block seeked to an exact instant; CupriFace renders the same
instant; the frames are compared. One number per block, and the picture that explains it.

```
dotnet run --project harness -- notes-reveal          # one block
dotnet run --project harness -- --all --out review --gallery
```

The second writes `review/index.html`: every block, worst first, with its images. That is the
fastest way to see the current state, and `review/` is ignored.

**A block the compiler carries.** The composition arrives, its SVG draws, its layout holds. What is
missing is the handwritten face, which falls back to a plain one, and the card's rotation.

![notes-reveal: browser, engine, difference](docs/images/harness-notes-reveal.png)

**A block it refuses.** Every bar, label and gridline here is built by `document.createElement`, and
the timeline that animates them is assembled in a loop over parsed data. There is nothing in the
markup for a JavaScript-free renderer to draw, and the compiler says so rather than guessing.

![bar-chart-race: browser, engine, difference](docs/images/harness-bar-chart-race.png)

### The numbers

On CupriFace 0.28.1, over 187 blocks — see [docs/HARNESS.md](docs/HARNESS.md):

| | |
|---|---|
| blocks scored | **185 of 187** |
| **mean of content** | **38.2%** — the number to beat |
| median | 40.0% |
| mean of frame | 67.0% |
| where it is wrong, off by | 33.0% of full scale |
| blocks that render one still frame | 147 of 185 |
| **blocks the engine paints almost nothing in** | **93 of 185** |

That last row is the constraint, and it was not visible until it was measured. Half the corpus
renders under 0.5% of its own frame — the composition is built or painted by the JavaScript that
had to be removed — so those blocks cannot be improved by fidelity work of any kind. Three correct
fixes in a row moved the mean by nothing for this reason: a timeline cursor, an easing parameter,
and a WOFF 2 font decoder that changed the pixels of exactly **3 of 185** blocks.

**Read the content number, not the frame number.** They differ by 29 points because these
compositions paint on a small share of a large frame, so counting the empty background rewards a
translation that loses the content. One block scores 98.3% of frame and 0.0% of content. The four
measures and why there are four are set out in [docs/HARNESS.md](docs/HARNESS.md).

The earlier figures in this repository's history — 55.2%, 62.8% — were the frame-wide measure. They
were not wrong, but they flattered, and the content measure replaces them as the headline.

---

## The GSAP surface that actually has to be understood

Counted across the whole corpus, so it is scope rather than speculation:

| call | uses | what it becomes |
|---|---|---|
| `.to()` | 1129 | a `@keyframes` from current state to the target |
| `.set()` | 1045 | a static declaration, or a zero-length step |
| `.fromTo()` | 362 | a `@keyframes` with both ends given |
| `.timeline()` | 216 | the clock everything is placed on |
| `.from()` | 82 | a `@keyframes` to the element's authored state |
| `.add()` | 68 | nesting, and position parameters |
| `.parseEase()` | 52 | a `cubic-bezier()` |
| `.time()` / `.seek()` | 51 | absolute placement |
| `.eventCallback()` / `.call()` | 46 | **not translatable — becomes a CupriCut event** |
| `.addLabel()` | 14 | a named position other tweens are relative to |

Four verbs — `to`, `set`, `fromTo`, `from` — are 2618 of the calls. That is the compiler.

---

## What this is not

- **Not a GSAP implementation.** A bounded subset, compiled ahead of time. Anything outside it is
  refused and named, never approximated.
- **Not a runtime dependency.** A composition is translated **once**, into a `.cutpkg` that is
  already self-contained. A project that has been imported is just a project; nothing depends on
  this tool at render time.
- **Not a general HTML-to-CupriFace converter**, though it may become one. The corpus is the
  target, and the corpus is what keeps the scope honest.

---

## Building and testing

```
python tools/fetch-corpus.py                        # the corpus, ~110 MB, not vendored
dotnet build harness -c Release
dotnet run --project tests/CupriLex.Harness.Tests   # the tests
```

The tests are run by **running them**, not with `dotnet test`. They are xunit v3, which is a
self-hosting executable on Microsoft.Testing.Platform, and the .NET 10 SDK still routes
`dotnet test` down the VSTest path and refuses before discovering anything — with both documented
opt-ins in place.

Restoring needs a GitHub Packages credential, because CupriFace is served from there and GitHub
requires authentication even for a public NuGet package. See the comment in
[NuGet.config](NuGet.config).

---

## Licence and attribution

HyperFrames is Apache 2.0, so its blocks and spec text may be used with attribution — see
[NOTICE](NOTICE). Anything translated should say which block it came from, not because the licence
demands a notice in a rendered video, but because a project that cannot say where its design came
from is one nobody can re-license later.

The corpus itself is **not vendored** into this repository. `python tools/fetch-corpus.py` pulls it
on demand into `corpus/`, which is ignored. A survey that anyone can re-run beats a snapshot that
silently ages.
