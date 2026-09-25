# The library: `CupriLex.Compiler`

```
dotnet add package CupriLex.Compiler --version 0.1.0-alpha.1 --prerelease
```

From the same GitHub feed CupriCut already authenticates to for CupriFace. **Alpha**, and the
version says so: the surface below is what a consumer may rely on, and an alpha is when it can
still change.

---

## What is in it

The whole translation, and nothing that walks a corpus. 24 public types:

| | |
|---|---|
| **translate** | `Translator.Of` → `Translated` (`Html`, `Motion` as a `Sheet`, `Refusals`) |
| **ask before fetching** | `External.Of` → `Request`, `Fetches`; `IConsent` and `Consent`; `IFetch`, `Http`, `Fetched`; `WebFonts.GatherAsync` → `Gathered`, `Web` |
| **package** | `Package.Write`, `Composition`, `Packaged`, `Asset`, `Collected` |
| **decide** | `Fallbacks`, `Dropped`, `Unresolved`, `Typeface` |
| **report** | `Refusal` |

## What is deliberately not in it

`Reader`, `Emit`, `Evaluator`, `Scope`, `Clock`, `RawTween`, `Value`, `Properties`, `Ease`,
`ScriptReader`, `Authored`, `Transform`, `Faces`, `Images`, `Reporting`, `Assets`, `Motion` — 27
types in all — are `internal`. They are how a translation is *produced*, and none of them is a
question a consumer asks.

This was done **before** the first alpha rather than after, on purpose. Every one of them was
public while this repository was the only thing compiling against it, and an alpha that shipped
them would have had somebody depending on `Emit` by accident within a week. After that they could
not move.

Two assemblies see them anyway, through `InternalsVisibleTo`: the tests, which assert how a
translation is produced, and `cuprilex`, this repository's survey tool, whose `shapes` and `reach`
reports are *about* the internals by definition.

---

## The projects

| | | |
|---|---|---|
| `compiler/` | `CupriLex.Compiler` | the library. **The only thing published** |
| `cli/` | `cuprilex` | `shapes`, `reach`, `translate`, `faces` — the surveys this repository steers by |
| `harness/` | | the comparison harness, and the packager's command line |
| `conformance/` | | what the engine actually supports, measured by rendering |

`cli/` exists because of the split. `Program`, `Shapes`, `Reach` and `CorpusPath` used to sit in
the library and would have shipped inside it — a corpus-directory walk in somebody else's package.

The harness now carries its own copy of that walk. Twelve lines duplicated is a smaller cost than
one published type nobody wanted.

---

## Dependencies a consumer inherits

| | | |
|---|---|---|
| `Acornima` | 1.8.0 | the JavaScript parser. No JavaScript *engine*, by design |
| `AngleSharp` | 1.7.0 | **the same version CupriFace 0.28.1 depends on**, so a host resolves one |
| `AngleSharp.Css` | 1.0.0-beta.154 | prerelease, because that is the only form it has ever had |

The last one is the reason the package is marked prerelease whatever its own version says: NuGet
will not let a stable package depend on a prerelease, and pretending otherwise would be a lie about
what is being installed.

---

## Continuous integration

`.github/workflows/ci.yml`, and **this repository had none until the day it published a
package** — everything in it was built and tested on one machine by hand, with nothing saying so.

`build` runs on every push and pull request: restore, build, fetch the corpus, run the tests, run
the conformance probe, pack. `publish` runs only on a `v*` tag and packs at the tagged version, so
a release cannot disagree with what it ships.

One check is there because of somebody else's expensive lesson. CupriFace v0.28.0 announced
`CupriFace.Woff2` in its release notes and packed nine packages; every check passed. So here,
anything that does not opt out with `IsPackable=false` must produce a nupkg or the job fails naming
the project. Three of the four projects opt out, and each says why in its own file.
