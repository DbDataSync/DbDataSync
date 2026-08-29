# Phase 49 — Favicon should match the in-app logo

**Status**: Done
**Plan reference**: none — small enough to skip a separate planning doc.

## The bug

`index.html`'s favicon (`public/favicon.svg`) is unrelated artwork — a purple/blue gradient blob, not
the icon actually used in the app. The real logo, rendered in the rail's top-left corner
(`AppShell.tsx`: `<div className="rail-mark"><LogoIcon /></div>`), is `icons.tsx`'s `LogoIcon` — a simple
two-arrow circular-sync glyph:

```tsx
<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"
     strokeLinecap="round" strokeLinejoin="round">
  <path d="M3 2v6h6" />
  <path d="M21 12A9 9 0 0 0 6 5.3L3 8" />
  <path d="M21 22v-6h-6" />
  <path d="M3 12a9 9 0 0 0 15 6.7l3-2.7" />
  <circle cx="12" cy="12" r="1" />
</svg>
```

The browser tab shows the blob; the app itself shows the sync-arrows glyph. They should be the same icon.

## The fix

Replace `public/favicon.svg` with `LogoIcon`'s actual path data, rendered on a solid or brand-colored
background (a bare `stroke="currentColor"` glyph needs an explicit color and fill for a favicon context,
since there's no surrounding `color` to inherit the way there is in the rail). Pick a background using the
app's own accent (`--accent` or similar token) rather than inventing a new color for this one asset.

Keep it as an SVG (already linked as `type="image/svg+xml"`) so it stays crisp at every tab size, and stays
trivially edtiable if `LogoIcon` itself ever changes — the two should be kept in sync by hand until/unless
this is worth generating from one source.

## How to verify when built

- Browser tab icon visually matches the rail's logo mark (same glyph, recognizably the same icon).
- Favicon still renders correctly at small sizes (tab bar, bookmark, mobile) — spot-check, since SVG
  favicons can lose fine detail at 16px.

## Open questions

- Whether to generate the favicon from `LogoIcon`'s JSX at build time (one source of truth) or keep it as
  a hand-maintained static SVG — the static file is simpler and this asset changes rarely, so hand-
  maintained is probably fine unless `LogoIcon` turns out to change often.

---

# Retrospective

The favicon is now `LogoIcon`'s glyph on `.rail-mark`'s accent square — the same mark, the same colour,
drawn the way the app draws it three centimetres away.

Two small decisions.

**The stroke is heavier than the rail's.** 2.6 rather than 2, on the same 24-unit glyph. A favicon is
read at 16px, where a hairline stroke thins out into the tab bar and the glyph reads as a smudge.
Rendered at 16, 24, 32 and 64 px against both a light and a dark tab bar before settling on it — the
plan asked for that spot-check and it changed the answer.

**Hand-maintained, with something watching.** The plan's open question was whether to generate the file
from the JSX. Generating it would mean a build step for one asset that changes almost never. Playwright
33 reads `LogoIcon`'s path data straight out of `icons.tsx` and asserts every path appears in the served
favicon, and that the square uses the `--accent` token's value rather than a colour invented for this
one file. Two hand-maintained copies of one glyph are fine as long as something notices when they stop
matching, and now something does — which is a cheaper answer than a build step and catches the same
mistake.

## Verification

- Playwright 33 — the served favicon carrying every one of `LogoIcon`'s paths, the accent square
  matching the `--accent` token, and the document still linking `/favicon.svg`.
- Rendered at four sizes on light and dark, as above.
- Full .NET suite green: 711 tests. Playwright: 35 green.

## Open questions

- ~~**Generate from the JSX, or hand-maintain.**~~ Hand-maintained, with a test that fails when the two
  drift.
