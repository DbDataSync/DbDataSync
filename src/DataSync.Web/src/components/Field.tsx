import type { ReactNode } from 'react'

/** Label above control, 11px medium label in `--ink-5`, 5px gap — the design's only field shape. */
export function Field({ label, children, style, alignLabel }: {
  label: ReactNode
  children: ReactNode
  style?: React.CSSProperties
  /** The design right-aligns the label of a narrow field sitting beside a wide one (Port, Buckets). */
  alignLabel?: 'right'
}) {
  return (
    <div className="field" style={style}>
      <label className={alignLabel === 'right' ? 'right' : undefined}>{label}</label>
      {children}
    </div>
  )
}

/** A read-only value rendered in a control's clothes, for things the design shows boxed but which
 * cannot be edited here (an inherited endpoint, a derived count). */
export function ReadOnlyValue({ children }: { children: ReactNode }) {
  return (
    <span className="input" style={{ display: 'flex', alignItems: 'center', background: 'var(--sunken)', color: 'var(--ink-3)', borderColor: '#f0eee8' }}>
      {children}
    </span>
  )
}
