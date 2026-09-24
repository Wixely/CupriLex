# The output: a translated block as a `.cutpkg`

```
dotnet run --project harness -- <block> --package out
dotnet run --project harness -- --all --package out
```

Renders nothing. A package is the end of the pipeline, not a measurement, so it costs no browser
and no engine.

---

## What it is

A `.cutpkg` is **CupriCut's format, not this repository's**: a zip holding `project.json` — the
same schema a `.cut.json` parses to — and an `assets/` folder. CupriLex writes one; CupriCut reads
it. Nothing about it is ours to design.

```
project.json                              the manifest
report.md                                 what came across and what did not
assets/inter-latin-400-normal.woff2       the bytes, stored rather than deflated
assets/logo.png
```

Each asset is keyed by **the name the HTML and CSS refer to it by**. A block's
`url('assets/fonts/inter-400.woff2')` becomes `url('inter-400.woff2')` in the package, and the
manifest maps that key to its entry. CupriCut hands the bytes back as a `data:` URI when it opens
the package, which is why the markup can carry no path at all.

**Nothing depends on this tool at render time.** That is the whole reason to write a package rather
than a document and a folder: a project that has been imported is just a project, and CupriLex is
not in the loop when it is rendered, copied or committed.

---

## Three decisions it makes

**Fonts travel as files, not as base64.** Decided in [PLAN.md](../PLAN.md) and measured first: the
document's own `@font-face` is left exactly as the author wrote it and the bytes ride in the
container. Inlining them adds a third to their size, duplicates a family into every block that
shares it, and edits markup that otherwise survives translation untouched.

**A package runs the longer of the declared duration and the motion.** A third of this corpus
declares a duration its own timeline does not span, and the two failures are not symmetrical: too
long holds a last frame, too short cuts the composition off mid-move. The disagreement is written
into the report rather than resolved silently, because only the author knows which number was the
mistake.

**`data-start` and `data-duration` are removed.** Those attributes are how a *loose* HTML block
declares its own length — it has nowhere else to put it — and a package has `render.duration`.
Carrying both states one fact twice, and the second copy costs something real: CupriCut reads them
as "this element is a timeline window" and then needs that element's one animation slot for itself,
which it reports as `CUT003`. 13 of the 187 corpus blocks were that error before this. Nothing is
lost by removing them, because a browser composition puts its motion in a timeline and has never
heard of CupriCut's windows.

**Font stacks are trimmed to the faces the package can answer.** This one is not tidiness. A strict
font policy refuses a family it has no face for, and measured on CupriFace 0.28.1 it does so *even
when a registered family follows it in the stack*:

| declaration | with Noto Sans registered |
|---|---|
| `"NoSuchFamily", "Noto Sans", sans-serif` | **refused** |
| `sans-serif` | renders |
| `serif` | renders |
| `monospace`, `cursive` | **refused** — nothing registered answers them |

So a stack is answered by its *names*, not by its order, and one unknown name anywhere in it stops
the render. Corpus stacks name `Segoe UI`, `SF Mono`, `Menlo`, `Arial` — faces that belong to an
operating system and will never be in a package, which the browser only ever used because it was
running on a machine that had them. They are removed, and every name removed is in the report. A
stack that loses everything gets `sans-serif`, because the browser fell back to the platform
default there and a generic is the only way to write that down without naming a typeface nobody
chose.

A reference that names no file on disk is **reported and left exactly as written**. A broken link
that is named can be fixed; one that has been quietly removed cannot.

---

## The report is a deliverable

`report.md` travels inside the package because that is the only place it stays attached to the
thing it describes. A refusal list printed to a terminal is gone by the time anyone asks why a
rendered composition is missing its transitions.

It opens with what was **carried** — elements animated, assets, size, duration — and only then with
what was not, grouped by shape so that thirty refusals for one reason are visibly different from
thirty for thirty reasons. A clean translation says so in words rather than showing an empty
section, because an empty report means perfect or lying and the file has to say which.

The manifest's `meta.notes` carries the same summary in a form CupriCut shows without unzipping.

---

## Verified against the reader

The corpus packages as 187 files and 15.9 MB. Opened with CupriCut's own CLI:

```
cupricut inspect claude-exchange.cutpkg

  claude-exchange.cutpkg  1080x1920 at 30 fps, 21.4s
  REFERENCES
    eb-garamond-latin-400-normal.woff2
    …
```

The size, the frame rate, the duration and every asset come back. `cupricut lint` reports no
`CUT003`.

**Measured against it, package by package.** CupriCut was upgraded to 0.28.1 and every package
linted:

| | before trimming | after |
|---|---|---|
| cannot be built at all | 105 | **63** |
| builds, lints as an error | 29 | 61 |
| builds and lints clean | 53 | **63** |

The 42 packages that moved were all the same failure: a stack naming a face nothing could answer.

**What is left, and none of it is the packaging.** 54 still fail on a font, and 51 of those are one
cause — their stacks trim down to `monospace`, and CupriCut registers only Noto Sans, so nothing
answers the generic. Shipping one monospace face there fixes 51 packages without a line changing
here. Carrying a real monospace face in the package fixes them properly, and that is the fetch work
still outstanding. The other 9 are the WOFF 2 decoder rejecting a Caveat font that every browser
reads, which is an unfiled CupriFace defect.

---

## The schema is copied, not referenced

`project.json` is written from a small DTO in `compiler/Package.cs` rather than from CupriCut's
`CutProject`. A project reference would make the compiler need a renderer in order to build, which
is a worse trade than the drift it would prevent. The cost is real, so the field names are asserted
one at a time in `PackageTests` against a package that is written and read back.
