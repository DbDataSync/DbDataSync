/** Icons traced from the mockups — stroke-based, drawn inline so they scale and take `currentColor`. */

const stroke = {
  fill: 'none',
  stroke: 'currentColor',
  strokeWidth: 1.4,
} as const

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
