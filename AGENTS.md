# Working on CupriLex

Read [README.md](README.md) first — particularly the finding that the entire corpus is
JavaScript-animated. Then this.

---

## The one rule

**Measure it, then claim it.**

This project exists downstream of an engine whose behaviour is frequently not what a browser would
do, and whose failures are usually *silent* — a property that parses, runs, and changes nothing.
Every non-obvious claim in the sibling repository was wrong at least once before it was measured,
including several written down confidently by people who had looked at a render.

So: never write "the engine does X" from reasoning, from a browser's behaviour, or from a previous
version. Render something, read the pixels, and let the number decide. Where a claim survives that,
put the measurement in a test so the claim cannot rot.

The same applies to this tool's own output. A translation that *looks* right in a diff has proven
nothing. Render both and compare frames.

---

## What you are translating into

CupriFace is not a browser. The complete, measured list of what it does and does not support lives
in the sibling repository at **`CupriCut/docs/AUTHORING.md`**, and every claim in it is asserted by
`CupriCut/tests/CupriCut.Tests/AuthoringGuideTests.cs`. Read both. The short version:

- Motion is `@keyframes` plus `animation-delay`. There is no JavaScript.
- **One animation per element.** A comma-separated list runs *neither* — the declaration fails to
  parse and is dropped silently.
- Only `width`, `height`, `opacity` and `transform` animate. `left`, `top`, `margin`, `padding`,
  `color`, `background`, `visibility` and `display` are accepted, run, and change nothing.
- `calc()` in `animation-delay` is treated as **zero**.
- Timing functions *are* honoured — do not assume linear when solving for where something is.
- `letter-spacing` is ignored. **80% of the corpus uses it.**

Do not trust that list against a newer engine than the one it was measured on. That is what
`conformance/` is for.

---

## House style

Inherited from CupriCut, and worth keeping because it is the reason that repository is navigable.

**Comments explain why, not what.** Prefer the reason a thing is the way it is, especially when the
obvious alternative was tried and failed. A comment that records a measurement — "62 of 200 clean
documents reported the other's warning" — is worth ten that restate the code.

**Name the failure that motivated the code.** `A_comma_separated_list_runs_NEITHER_animation` is a
better test name than `TestAnimationParsing`, because it says what breaks.

**Report faithfully.** If something is unverified, say so. If a number came from one machine, say
which. Never describe work as done when it is done-except-for.

**Prose in commits.** What changed, why, what it cost, and what was learned that the next person
would otherwise learn the hard way.

---

## The two mechanisms this repository is built around

### 1. The corpus — `tools/`, `corpus/`

187 real, designed, animated compositions. Not vendored: `python tools/fetch-corpus.py` pulls
them, `corpus/` is ignored. They are the test set, and the only honest measure of progress is
**what fraction of them render correctly**, not how many rewrite rules exist.

Run `python tools/survey.py` after any corpus refresh. If the numbers move, the plan may need to.

### 2. Conformance — `conformance/`

The engine gains CSS support between versions. When it does, this translator should *stop* working
around the thing that now works — a rewrite that is no longer needed is a bug, because it produces
different output than the author wrote for no reason.

`conformance/` probes the engine mechanically and emits a support matrix per version. Diff two
matrices and you have the list of things to reconsider. See
[docs/CONFORMANCE.md](docs/CONFORMANCE.md).

**This is not optional housekeeping.** Between CupriFace 0.25.0 and 0.25.1, four behaviours
changed — `line-height` went from broken to correct, comments inside `@keyframes` stopped
corrupting them, a diagnostics bug was fixed, and a *new* false-positive check appeared. Three
documents in the sibling repository were quietly wrong until someone re-measured.

---

## Definition of done, for any change

- [ ] The claim is measured, and the measurement is a test.
- [ ] The corpus number moved, or you can say why it did not.
- [ ] Anything that could not be carried is *named* in the report, not dropped.
- [ ] `docs/TRANSLATION.md` says what the new rule does and what it refuses.
- [ ] Rendered output compared against the browser original, not just eyeballed in a diff.
