# What translates, what is rewritten, and what is refused

Three outcomes, and the third is as important as the others. Anything CupriLex cannot carry must
appear in the report **by name, with the line it was on** — because a video missing its transitions
looks like a video, and nobody finds out until someone watches all of it.

---

## Carries unchanged

| | |
|---|---|
| `:root` custom properties | the whole palette and type scale, free. `var()` and `var(--x, fallback)` both work. |
| `data-start` / `data-duration` | CupriCut's timeline vocabulary is the same one, arrived at independently. 87% of blocks already carry it. |
| flexbox with `align-items` | works |
| `position: absolute` | works — and is in 100% of blocks |
| `border-radius`, `overflow: hidden` | works, and clipping *does* respect the radius |
| `@font-face` with a `data:` URI | registers under the strict font policy, so an imported block can carry its own typeface |
| `line-height` | as of CupriFace 0.25.1. 59% of blocks need this. |

---

## Rewritten

### GSAP timeline → `@keyframes` + `animation-delay`

The whole job. See [CORPUS.md](CORPUS.md) for why there is no subset that avoids it.

The shape of it: resolve every tween to an **absolute time** on the composition's clock, group by
target element, and emit one `@keyframes` per element with stops at the percentages those times
fall at.

Four constraints from the engine make this harder than it sounds, and all four are load-bearing:

1. **One animation per element.** The engine runs exactly one; a comma-separated list runs
   *neither*, silently. So every tween touching the same element must be merged into a single
   `@keyframes`, with stops ordered by time — a tween that starts before another ends has to be
   resolved into one timeline of stops, not two animations.
2. **Only `width`, `height`, `opacity` and `transform` animate.** GSAP animates anything. A tween
   on `left`, `top`, `margin`, `color` or `backgroundColor` must be rewritten — position into
   `transform: translate`, and colour cross-faded between two stacked elements or refused.
3. **`calc()` in `animation-delay` is treated as zero.** Stagger goes in the keyframe percentages.
4. **Timing functions are honoured**, so `parseEase` maps to `cubic-bezier()` — but a per-tween
   ease inside a merged `@keyframes` cannot be expressed, because `animation-timing-function`
   applies per-animation. Either approximate with denser stops or refuse.

Constraint 1 and constraint 4 interact badly and that is the real difficulty of this compiler.
Dense stops sampled from the eased curve is the likely answer; it should be *measured* against the
browser original rather than assumed adequate.

### Everything else

| from | to | why |
|---|---|---|
| `<img src>` | `<cupri-image src>` | the engine has no raw `<img>`; it lays out and stays empty (`CF0030`) |
| `repeating-linear-gradient(...)` | one gradient with hard stops | not painted. 2% of blocks. |
| animated `left` / `top` | `transform: translate()` | accepted, runs, changes nothing |
| external font `@import` / `<link>` | download, embed as `@font-face` with a `data:` URI | 23% of blocks. Keeps the result one self-contained file. |
| `<script src=...gsap...>` | removed | consumed by the compiler, not carried |

---

## Refused, and named

| | blocks | why |
|---|---|---|
| `<canvas>` | 20% | drawn by JavaScript at runtime. There is no runtime. |
| any JS beyond the compiled subset | — | refuse rather than guess; a wrong animation is worse than a reported absence |
| `letter-spacing` | **80%** | ignored by the engine (`CF0050`). See below. |
| GSAP plugins, ScrollTrigger, physics eases | — | out of the bounded subset |
| `eventCallback` / `call` | 46 uses | not motion. Becomes a CupriCut `data-cut-event`, which is declared and reported rather than executed. |

### `letter-spacing` deserves its own note

Four blocks in five use it, and the engine ignores it. Dropping it silently would change the
typography of 80% of the corpus without saying so, and tracking is not a detail in designed work —
it is frequently the difference between a title that fits its box and one that does not.

So it is **reported on every block that uses it**, even though the report will be noisy, until
either the engine supports it or someone decides what the right approximation is. Noise that is
true beats silence that is not.

---

## The report

Not an afterthought and not a log. It is the deliverable that makes the conversion trustworthy:

- what was rewritten, and to what
- what was **refused**, by name, with the source line
- what was carried but is known to render differently — `letter-spacing` and friends
- which block it came from, for attribution

A translation with an empty report is either perfect or lying, and the corpus will say which.
