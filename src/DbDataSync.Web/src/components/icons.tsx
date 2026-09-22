/** Icons traced from the mockups — stroke-based, drawn inline so they scale and take `currentColor`. */

const stroke = {
  fill: 'none',
  stroke: 'currentColor',
  strokeWidth: 1.4,
} as const

/**
 * The app's mark. `public/favicon.svg` is the same glyph on the accent square the rail draws around
 * this one — changing this without changing that leaves the browser tab showing a different product.
 */
export function LogoIcon() {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"
         strokeLinecap="round" strokeLinejoin="round">
      <path d="M3 2v6h6" />
      <path d="M21 12A9 9 0 0 0 6 5.3L3 8" />
      <path d="M21 22v-6h-6" />
      <path d="M3 12a9 9 0 0 0 15 6.7l3-2.7" />
      <circle cx="12" cy="12" r="1" />
    </svg>
  )
}

export function GridIcon() {
  return (
    <svg width="15" height="15" viewBox="0 0 16 16" {...stroke}>
      <rect x="2" y="2" width="5" height="5" rx="1" />
      <rect x="9" y="2" width="5" height="5" rx="1" />
      <rect x="2" y="9" width="5" height="5" rx="1" />
      <rect x="9" y="9" width="5" height="5" rx="1" />
    </svg>
  )
}

/** Replications — three nodes joined by a flow. */
export function FlowIcon() {
  return (
    <svg width="15" height="15" viewBox="0 0 16 16" {...stroke}>
      <circle cx="4.5" cy="3.5" r="1.8" />
      <circle cx="4.5" cy="12.5" r="1.8" />
      <circle cx="11.5" cy="6" r="1.8" />
      <path d="M4.5 5.3v5.4M6.4 4.3c3.1 0 3.3 1.7 3.3 1.7" />
    </svg>
  )
}

/** Drivers/Libraries/Files — a package (box), for the artifacts a replication is built out of rather
 * than the replications themselves. Distinct from Connections' cylinder — this is what's installed on
 * the host, not what it talks to. */
export function PackageIcon() {
  return (
    <svg width="15" height="15" viewBox="0 0 16 16" {...stroke} strokeLinejoin="round">
      <path d="M8 1.5 14 4.5v7L8 14.5 2 11.5v-7Z" />
      <path d="M2 4.5 8 7.5 14 4.5" />
      <path d="M8 7.5v7" />
    </svg>
  )
}

/** Connections — a database cylinder. */
export function DatabaseIcon() {
  return (
    <svg width="15" height="15" viewBox="0 0 16 16" {...stroke}>
      <ellipse cx="8" cy="4" rx="5" ry="2" />
      <path d="M3 4v8c0 1.1 2.2 2 5 2s5-.9 5-2V4" />
      <path d="M3 8c0 1.1 2.2 2 5 2s5-.9 5-2" />
    </svg>
  )
}

/**
 * Pause — the media control, on the button that pauses and on the status indicator that reports it.
 * One glyph for both, so pausing something and then seeing it read as paused look like one idea.
 */
export function PauseIcon({ size = 14 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 16 16" {...stroke} strokeLinecap="round">
      <path d="M6 3.5v9" />
      <path d="M10 3.5v9" />
    </svg>
  )
}

/** Resume — the other half of the media pair. Stroked rather than filled, to match everything else. */
export function PlayIcon({ size = 14 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 16 16" {...stroke} strokeLinejoin="round">
      <path d="M5.5 3.4 12.4 8l-6.9 4.6z" />
    </svg>
  )
}

/** Disabled — the circle-slash. Not "off right now", but "turned off". */
export function DisabledIcon({ size = 14 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 16 16" {...stroke}>
      <circle cx="8" cy="8" r="5.3" />
      <path d="M4.3 4.3l7.4 7.4" />
    </svg>
  )
}

/**
 * Running and Idle both. A pulse line says "this is a live process" without claiming it is busy —
 * the label, and the colour, are what separate a worker mid-pass from one waiting between passes.
 */
export function PulseIcon({ size = 14 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 16 16" {...stroke} strokeLinecap="round" strokeLinejoin="round">
      <path d="M1.5 8h3l2-4 2.6 8L11.4 8h3.1" />
    </svg>
  )
}

/** Scripts — angle brackets around a slash, the universal shorthand for code. */
export function CodeIcon() {
  return (
    <svg width="15" height="15" viewBox="0 0 16 16" {...stroke}>
      <path d="M5.5 4.5 2 8l3.5 3.5" />
      <path d="M10.5 4.5 14 8l-3.5 3.5" />
      <path d="M9.2 3.2 6.8 12.8" />
    </svg>
  )
}

/** Docs — an open book. */
export function BookIcon() {
  return (
    <svg width="15" height="15" viewBox="0 0 16 16" {...stroke} strokeLinejoin="round">
      <path d="M8 4.2C6.9 3.3 5 3 2.5 3v8.5c2.5 0 4.4.3 5.5 1.2 1.1-.9 3-1.2 5.5-1.2V3C11 3 9.1 3.3 8 4.2Z" />
      <path d="M8 4.2v8.5" />
    </svg>
  )
}

/**
 * Admin — a gear, the universal mark for "settings" and nothing this app already uses elsewhere.
 * One traced outline: eight flat-topped teeth on a root circle (joined by short arcs, not straight V-cuts,
 * so the valleys read as part of a wheel rather than a star) plus the center hole — not the previous
 * version's thin radiating lines, which read as a sun, and not a first attempt at separate rect/line
 * teeth either, which at this stroke width had no room left for a hollow interior and read as solid
 * blobs. Verified by rendering both the raw path and the actual 15x15 glyph before landing on this one.
 */
export function GearIcon() {
  return (
    <svg width="15" height="15" viewBox="0 0 16 16" {...stroke} strokeLinejoin="round">
      <path d="M 5.72 4.35 A 4.3 4.3 0 0 1 7.03 3.81 L 6.61 1.96 L 9.39 1.96 L 8.97 3.81
               A 4.3 4.3 0 0 1 10.28 4.35 L 11.29 2.74 L 13.26 4.71 L 11.65 5.72
               A 4.3 4.3 0 0 1 12.19 7.03 L 14.04 6.61 L 14.04 9.39 L 12.19 8.97
               A 4.3 4.3 0 0 1 11.65 10.28 L 13.26 11.29 L 11.29 13.26 L 10.28 11.65
               A 4.3 4.3 0 0 1 8.97 12.19 L 9.39 14.04 L 6.61 14.04 L 7.03 12.19
               A 4.3 4.3 0 0 1 5.72 11.65 L 4.71 13.26 L 2.74 11.29 L 4.35 10.28
               A 4.3 4.3 0 0 1 3.81 8.97 L 1.96 9.39 L 1.96 6.61 L 3.81 7.03
               A 4.3 4.3 0 0 1 4.35 5.72 L 2.74 4.71 L 4.71 2.74 Z" />
      <circle cx="8" cy="8" r="1.9" />
    </svg>
  )
}

/** A plain checkmark — no circle, so it reads as "confirmed" beside text rather than as a status dot
 * (the rail/badge convention elsewhere already owns that shape). */
export function CheckIcon({ size = 12 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 16 16" {...stroke} strokeLinecap="round" strokeLinejoin="round">
      <path d="M3 8.5 6.2 11.7 13 4.5" />
    </svg>
  )
}

/** More detail available on hover — a circled question mark, for a short label with a longer
 * explanation behind it (the admin config table's descriptions, so far). */
export function HelpIcon({ size = 12 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 16 16" {...stroke} strokeLinecap="round" strokeLinejoin="round">
      <circle cx="8" cy="8" r="6.5" />
      <path d="M6.2 6.3a1.8 1.8 0 1 1 2.6 1.6c-.7.4-1 .8-1 1.6" />
      <circle cx="8" cy="11.5" r="0.2" fill="currentColor" stroke="none" />
    </svg>
  )
}

/** Notifications — a bell, drawn open-bottomed so the badge can sit over its shoulder. */
export function BellIcon({ size = 15 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 16 16" {...stroke} strokeLinecap="round" strokeLinejoin="round">
      <path d="M3.5 11.5V7a4.5 4.5 0 0 1 9 0v4.5" />
      <path d="M2.5 11.5h11" />
      <path d="M6.5 13.5a1.6 1.6 0 0 0 3 0" />
    </svg>
  )
}
