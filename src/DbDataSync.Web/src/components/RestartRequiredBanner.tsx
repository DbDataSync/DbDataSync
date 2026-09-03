/**
 * "This process resolves its configuration once at startup" — introduced by phase 81's config screen
 * and reused unchanged here for phase 83's certificate screen, per that phase's own design: binding,
 * issuing or renewing a certificate is exactly the same shape of change as editing a `DbDataSync:*` value
 * — nothing takes effect until the next restart — so both screens say so with the same words rather than
 * inventing a second banner that means the same thing.
 *
 * Plain component state on each page, not shared or persisted: a hard reload is a fine proxy for "this
 * session," since the thing being warned about (the running process has not restarted) is itself cleared
 * by an actual restart.
 */
export function RestartRequiredBanner({ show }: { show: boolean }) {
  if (!show) return null

  return (
    <div className="banner" data-testid="restart-required-banner">
      <span className="mark">!</span>
      <span>
        A value was changed. This process resolves its configuration once at startup, so nothing here
        takes effect until DbDataSync restarts.
      </span>
    </div>
  )
}
