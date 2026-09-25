# External requests: what leaves the machine, and who said so

A composition written for a browser fetches things. A font service, usually: 64 of the corpus's
187 blocks link or `@import` a stylesheet from `fonts.googleapis.com`, which then names files on
`fonts.gstatic.com`. None of that survives translation, because the engine has no network — so the
family is named and nothing answers it, and a renderer with a strict font policy refuses to draw at
all.

CupriLex can fetch them and carry them inside the `.cutpkg`. **It never does so on its own.**

---

## The shape of it

```csharp
// 1. What would this document ask for? Nothing is fetched to answer this.
IReadOnlyList<Request> wanted = External.Of(document);

// 2. Somebody decides. A prompt, a settings screen, a policy file, a test.
IConsent consent = Consent.All;                       // or None, Hosts(…), Urls(…)

// 3. Only now does anything leave the machine.
Gathered got = await WebFonts.GatherAsync(document, consent, new Http());
```

Three pieces, deliberately separate. The list can be shown to a person without committing to
anything. The decision is a value a host can store, serialise or default. The fetch takes both and
an `IFetch`, so a host that supplies none cannot reach anything whatever the consent says.

**This is a runtime capability, not a build step.** CupriCut opening an HTML file asks the same
question, shows the same list, and fetches on the same terms.

---

## The list is meant to be read by a person

A URL is not something anyone can consent to. Each request carries what it is for:

```
[ 19 block(s)] https://fonts.googleapis.com/css2?family=Space+Mono:wght@400;700&family=Bebas+Neue
              a stylesheet the document links (Google Fonts: Space Mono, Bebas Neue)
```

Two things are deliberately **not** in the list:

- **`rel="preconnect"` and `dns-prefetch`.** Hints to a browser with no content. Asking somebody to
  approve a fetch that never happens teaches them to click yes.
- **`<script src>`.** Translation removes scripts, so it is not a request this tool would ever
  make, and listing it would invite approval for something that cannot happen.

---

## Consent is per URL, because a stylesheet hides the real requests

A font service answers a stylesheet request with `@font-face` rules pointing at files **on another
host**. Those URLs do not exist until the stylesheet has been fetched, so a person who approved
"a stylesheet from fonts.googleapis.com" has not been shown them.

They come back through the same consent, marked `Discovered`, and:

| decision | approves the stylesheet | approves the files it names |
|---|---|---|
| `Consent.All` | yes | yes |
| `Consent.Hosts("fonts.googleapis.com")` | yes | **no** — different host |
| `Consent.Hosts(…googleapis, …gstatic)` | yes | yes |
| `Consent.Urls([sheet])` | yes | yes, on hosts already approved |
| `Consent.None` | no | never reached |

`Consent.None` is the default everywhere. A tool that fetches unless told not to has made the
decision for whoever is running it, and "it only downloads fonts" is a claim about this version
rather than about the design.

---

## From the command line

```
dotnet run --project harness -- --all --package out --download all
dotnet run --project harness -- --all --package out --download none      # the default
dotnet run --project harness -- --all --package out                      # shows the list, asks
```

Asked **once** for the whole run, before anything is fetched or written. A prompt per block would
be 187 decisions nobody can make.

---

## What a fetch does to the document

The link and the `@import` are removed and replaced with local rules:

```css
@font-face { font-family: "Space Mono"; font-style: normal; font-weight: 400;
             src: url('space-mono-400.woff2') format('woff2'); unicode-range: U+0102-0103, …; }
```

The bytes go in `assets/`, keyed as always by the name the markup uses. After this the document
points at nothing off the machine, which is the whole point of a package.

**The `unicode-range` comes too.** A service sends one rule per subset — latin, latin-ext,
vietnamese — all for the same family and weight. Without the range they arrive as several rules
nothing can tell apart, a renderer picks one, and the composition loses whichever characters lived
in the others.

**Only `woff2` is taken.** A service offers several formats, the engine reads this one as of
CupriFace 0.28.1, and carrying a TTF as well would double the package for nothing.

The request is asked as a current Chrome, and that is not decoration: Google Fonts serves TTF to an
old user agent and WOFF 2 to a modern one, so the string decides the format of the reply.

---

## A generic is the one thing a package cannot carry

`font-family: monospace` names no face, so there is nothing to fetch. Measured on CupriFace 0.28.1
with Noto Sans registered: `sans-serif` and `serif` render, `monospace` and `cursive` are refused,
because no registered face serves them.

Where the document has already said what it means — `"Space Mono", monospace` in one rule and a
bare `monospace` in another — the pairing is read off its own CSS and written into the bare stack.
That is not a substitution; it is the author's own words, and only families the package actually
carries are used.

Where a document names nothing but proprietary system faces — `Menlo, Monaco, Consolas,
"Courier New", monospace` — there is no such evidence, and carrying a stand-in would mean choosing
a typeface nobody asked for. Those are left, and reported.
