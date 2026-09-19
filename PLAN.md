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

## Milestone 2 — the comparison harness

The instrument this project is steered by, and it must exist before the rewrite rules it will
judge.

1. Render a block in a **browser** at N sample times — headless Edge or Chrome, already proven to
   work for this in the sibling repository.
2. Render the translated block through **CupriCut** at the same times.
3. Compare frames and produce one number per block, plus the difference image.

**Done when** `compare <block>` gives a score for any block in the corpus, and a corpus-wide run
gives the baseline that every later milestone moves.

The baseline will be terrible. That is the point: it is the only honest measure of progress, and
"how many rewrite rules exist" is not.

---

## Milestone 3 — the compiler

The feature. See [docs/TRANSLATION.md](docs/TRANSLATION.md) for the four engine constraints that
make it awkward, particularly the interaction between *one animation per element* and *per-tween
easing*, which is the genuinely hard part.

1. **Parse** the four verbs — `to`, `set`, `fromTo`, `from` — plus `timeline`, `add`, `addLabel`
   and position parameters. That is 2618 of ~3100 calls.
2. **Resolve** every tween to an absolute time and a target element.
3. **Merge** per element into a single `@keyframes`, because the engine runs exactly one animation
   per element and a comma list runs neither.
4. **Emit**, and **refuse** anything outside the subset by name.

Static analysis, not execution. A block whose timeline is built in a loop over parsed data is the
case that will decide how far this can go, and it should be found early — pick a block that does
it and try it on purpose rather than discovering it at 60%.

**Done when** the corpus score moves substantially, and every refusal is named.

---

## Milestone 4 — the rest of the rewrites

Cheap once the compiler exists, and each one is a measurable step on the corpus score.

- `<img>` → `<cupri-image>`
- repeating gradients → hard stops
- animated `left`/`top` → `transform`
- external fonts → downloaded and embedded as `@font-face` with a `data:` URI
- `eventCallback` / `call` → CupriCut `data-cut-event`
- `data-composition-variables` → kept, since a translated block should still be a template

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

- **Per-tween easing inside a merged `@keyframes`.** `animation-timing-function` is per-animation,
  and the engine allows one animation per element. Dense stops sampled off the eased curve is the
  likely answer, but "how dense" is a measured question — against the comparison harness, not by
  eye.
- **Whether `<svg>` survives.** 32% of blocks. Unknown until Milestone 1 answers it, and the answer
  changes how much of the corpus is reachable at all.
- **What to do about `letter-spacing`.** 80% of blocks, ignored by the engine. Report every time,
  approximate with word spacing, or press for engine support. Currently: report.
- **Blocks that build timelines from data.** A loop over a parsed array is not statically
  resolvable in general. Find the real examples before deciding how hard to try.
