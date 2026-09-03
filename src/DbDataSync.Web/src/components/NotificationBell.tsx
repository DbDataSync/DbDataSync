import { useState } from 'react'
import { useMarkNotificationsSeen, useNotifications } from '../api/hooks'
import { BellIcon } from './icons'

/** How many the dropdown shows. It is a bell, not a notification centre — enough to answer "what
 * just happened", and a full paged view is a later phase if anybody wants one. */
const VISIBLE = 10

/**
 * The notification bell — phase 77's whole UI surface.
 *
 * **A badge and a short list, not a notification centre.** The point of this phase was to prove the
 * pipeline end to end: something failed, a row was written, this deployment's user has an unread
 * count, and dismissing it sticks. A filterable, paged, per-kind-muted screen is all buildable on the
 * same endpoint later and none of it would have proved anything more.
 *
 * **Opening marks everything seen**, rather than a separate dismiss control. The unread count answers
 * "is there anything I have not looked at", and opening the list is looking at it — a badge that
 * survives being read is a badge people learn to ignore.
 */
export function NotificationBell() {
  const [open, setOpen] = useState(false)
  const { data: feed } = useNotifications()
  const markSeen = useMarkNotificationsSeen()

  if (!feed) return null

  const recent = [...feed.notifications].reverse().slice(0, VISIBLE)
  const highestId = feed.notifications.at(-1)?.id

  function toggle() {
    const opening = !open
    setOpen(opening)
    // Only where a cursor can actually be stored. With authentication disabled there is nobody to
    // store one for, and firing the request anyway would be asking the server to say "no" on every
    // click of a badge that is never going to clear.
    if (opening && feed?.personalized && highestId !== undefined && feed.unreadCount > 0)
      markSeen.mutate(highestId)
  }

  return (
    <span style={{ position: 'relative', display: 'inline-flex' }}>
      <button
        type="button"
        className="btn btn-chrome"
        onClick={toggle}
        title={feed.unreadCount > 0 ? `${feed.unreadCount} unread notification(s)` : 'Notifications'}
        data-testid="notification-bell"
      >
        <BellIcon />
        {feed.unreadCount > 0 && (
          <span
            className="badge"
            style={{ marginLeft: 5 }}
            data-testid="notification-badge"
          >
            {feed.unreadCount > 99 ? '99+' : feed.unreadCount}
          </span>
        )}
      </button>

      {open && (
        <>
          {/* A full-viewport catcher, so clicking anywhere else closes the list. Cheaper and more
              reliable than a document listener that has to be attached and torn down in step with
              the open state. */}
          <span
            onClick={() => setOpen(false)}
            style={{ position: 'fixed', inset: 0, zIndex: 40 }}
          />
          <div
            style={{
              position: 'absolute', top: '100%', right: 0, zIndex: 41, marginTop: 6,
              width: 380, maxHeight: 420, overflowY: 'auto',
              background: 'var(--surface-1, #fff)', border: '1px solid var(--line, #ddd)',
              borderRadius: 6, boxShadow: '0 8px 24px rgba(0,0,0,.18)',
            }}
            data-testid="notification-list"
          >
            {recent.length === 0 ? (
              <p style={{ margin: 0, padding: '14px 12px', font: '12px var(--ui)', color: 'var(--ink-faint)' }}>
                Nothing has needed your attention.
              </p>
            ) : (
              recent.map(n => (
                <div
                  key={n.id}
                  style={{ padding: '9px 12px', borderBottom: '1px solid var(--line, #eee)' }}
                >
                  <div style={{ font: '12px var(--ui)', color: 'var(--ink-2)' }}>{n.message}</div>
                  <div style={{ font: '11px var(--ui)', color: 'var(--ink-faint)', marginTop: 3 }}>
                    {new Date(n.createdAtUtc).toLocaleString()}
                  </div>
                </div>
              ))
            )}
            {!feed.personalized && (
              // Said rather than left to be discovered. Without a signed-in identity there is no
              // cursor to advance, so this count is the whole feed and always will be.
              <p style={{ margin: 0, padding: '9px 12px', font: '11px var(--ui)', color: 'var(--ink-faint)' }}>
                This deployment does not sign anybody in, so notifications cannot be marked as read.
              </p>
            )}
          </div>
        </>
      )}
    </span>
  )
}
