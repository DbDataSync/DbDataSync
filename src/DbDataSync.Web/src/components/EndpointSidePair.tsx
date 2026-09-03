import type { ReactNode } from 'react'

export type EndpointSide = 'source' | 'target'

/**
 * Source and target, side by side, with the arrow between them.
 *
 * **One component for both screens.** The replication Overview and the table-mapping editor both
 * answer "where does this read from and write to", and they had drifted into two unrelated layouts —
 * one shared card with a 3-column grid, and two loose cards of unequal height — because nothing made
 * them share anything. This is that something.
 *
 * The pair owns the card shells, the arrow, the equal-height layout and the side accent colour;
 * callers supply only what goes inside each card, which is genuinely different between the two
 * screens (an endpoint on one, a whole mapping side on the other).
 */
export function EndpointSidePair({ source, target }: { source: ReactNode; target: ReactNode }) {
  return (
    <div className="side-pair">
      {source}
      <span className="side-arrow" aria-hidden="true"><span>→</span></span>
      {target}
    </div>
  )
}

/**
 * One card of the pair. Separate from <see cref="EndpointSidePair"/> so a caller can put its own
 * content in the head — the Overview needs a per-side "inherited by N mappings" note, and the mapping
 * editor needs an override toggle, and neither belongs to the other.
 */
export function EndpointSideCard({ side, title, head, children, testId }: {
  side: EndpointSide
  title: string
  /** Rendered in the card head after the title — a note, a badge, a toggle. */
  head?: ReactNode
  children: ReactNode
  testId?: string
}) {
  return (
    <div className={`card side-${side}`} data-testid={testId} data-side={side}>
      <div className="card-head tight">
        <span className={`card-title sm side-${side}`}>{title}</span>
        {head}
      </div>
      <div className="card-body">{children}</div>
    </div>
  )
}
