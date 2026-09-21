# Conformance: knowing what the engine supports, per version

A translator that works around a limitation the engine no longer has is producing different output
than the author wrote, for no reason. A translator that *fails* to work around a limitation the
engine still has produces a video that is silently missing something.

Both are the same bug: **the translator's model of the target drifted from the target.**

This directory exists so that model is measured rather than remembered, and so a new CupriFace
release produces a diff rather than a surprise.

---

## Why this cannot be a document

It was a document, in the sibling repository, and it went stale in one release.

Between **CupriFace 0.25.0 and 0.25.1**, four things changed:

| | |
|---|---|
| `line-height` | a `px` value produced a box **3× too tall**, and `em`/`%` were ignored outright — fixed |
| comments inside `@keyframes` | silently corrupted the stop percentages, moving a bar's final width from 545px to 714px — fixed |
| `CupriDoctor` diagnostics | were process-global; a check returned findings produced by *other documents on other threads* — fixed |
| `CF0051` | **new**, and a false positive — it matched the text `repeating-linear-gradient` anywhere in the document, including comments and painted body text |

Three separate documents were confidently wrong until someone re-measured, and one of the four
changes *created* a problem rather than fixing one. A release is not a monotonic improvement, and
"we read the changelog" is not a substitute for asking the binary.

And 59% of the corpus uses `line-height`. Had CupriLex existed across that release, it would have
been carrying a workaround for a bug that no longer existed, on more than half the corpus.

---

## What it produces

A **support matrix** per engine version:

```
conformance/support/0.26.1.json
```

For each CSS property, in each value form the corpus actually uses, three independent questions —
because they fail independently and the difference decides the rewrite:

| question | how it is answered | what it means if "no" |
|---|---|---|
| **parses** | does the engine's own reader report a diagnostic for it? | it will be reported; `lint` catches it |
| **paints** | render with it and without it — do the pixels differ? | accepted and inert. The dangerous one. |
| **animates** | `@keyframes` it from A to B; do frames at `t=0` and `t=end` differ? | the animation runs and changes nothing |

A property can parse, paint, and still not animate — `background-color` does exactly that. A
property can paint and not be reported — `letter-spacing` is reported, but `left` animating to
nothing is not. The three columns are the whole point.

---

## What it produced the first time it was used in anger

The design above was written before any version bump had happened to this repository. One has now,
and it did the thing it was built for: **`git diff conformance/support/` deleted two rewrite
rules.**

```
dotnet run --project conformance -- --compare conformance/support/0.26.2.json conformance/support/0.27.0.json

  backdrop-filter (blur(6px)) parses: yes -> NO
  letter-spacing (12px) parses: NO -> yes
  letter-spacing (12px) paints: NO -> yes
  inset (0 (shorthand)) parses: NO -> yes
  inset (0 (shorthand)) paints: NO -> yes
  inset (0 (four longhands)) paints: NO -> yes
```

Read those six lines as three facts. `letter-spacing` went from ignored to implemented, and it is
80% of the corpus. `inset` went from ignored to supported, **and so did the four longhands**, which
is the half that a rewrite rule was standing in for. And `backdrop-filter` moved the other way in
the parse column, which is an improvement: it now reports why it is not drawing instead of
accepting the declaration in silence.

Across 0.26.1 to 0.27.0 the matrix took the repository from three rewrite rules to one. Each rule
had declared the condition it worked around; each condition stopped being true; the tests pinning
them failed on cue and the rules were deleted. That is the whole mechanism working end to end, and
none of it required reading a changelog.

The one thing the matrix could not do on its own: an optional package. `<svg>` reads as unsupported
until the probe calls `UseSvg()`, because that is also true of any document that does not. The
probe now enables the same packages the harness renders with, and the matrix says so.

---

## How a translator uses it

Every rewrite rule declares what it is working around:

```
rule: repeating-gradient -> hard stops        because  repeating-linear-gradient.paints == false
rule: letter-spacing     -> drop and report   because  letter-spacing.paints == false
rule: top/left animation -> transform         because  left.animates == false
```

Then:

- a rule whose condition is **no longer true** is dead, and the build says so;
- a property that becomes supported means a rewrite can be *removed*, which is output that moves
  closer to what the author wrote;
- a property that becomes *unsupported* — it happens, see `CF0051` — means a rule is missing.

The matrix is committed. That is the point: `git diff conformance/support/` across two versions is
the list of things to reconsider, and it is reviewable.

---

## Running it

```
dotnet run --project conformance -- --out conformance/support
dotnet run --project conformance -- --compare 0.26.1 0.27.0
```

The probe renders; it does not read the engine's source or its release notes. Every answer is a
pixel comparison, for the reason at the top of [AGENTS.md](../AGENTS.md).

---

## Method, and its limits

**Measured by rendering.** Two documents identical but for one declaration, rendered at the same
`t`, compared pixel by pixel. If they differ, the property paints. The comparison is exact rather
than perceptual: anti-aliasing noise is not a concern because the two documents differ only in the
declaration under test.

**Honest about what it cannot see.** A property that paints *identically* for the specific values
probed will read as unsupported. `letter-spacing: 0` would. So the probe uses values chosen to make
a difference obvious — large tracking, a visible blur radius, a clip that removes half the shape —
and the chosen value is recorded in the matrix beside the answer, so a "no" can be re-examined
rather than trusted blindly.

**One engine, one machine, one platform.** The matrix records the CupriFace version, the runtime,
and the OS. A support matrix from another machine is a different file, not a contradiction —
CupriCut promises identical pixels *per OS*, not across them.

**Not a browser comparison.** This measures what the engine does, not how far that is from CSS.
The gap to a browser is the corpus's job to reveal.
