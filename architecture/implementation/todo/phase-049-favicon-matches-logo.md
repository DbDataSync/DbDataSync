# Phase 49 — Favicon should match the in-app logo (planned)

**Status**: Planned, not started
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
