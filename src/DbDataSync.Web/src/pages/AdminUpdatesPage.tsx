import { useEffect, useRef, useState } from 'react'
import { AdminTabs } from '../components/AdminTabs'
import { AppShell } from '../components/AppShell'
import { ErrorBanner } from '../components/ErrorBanner'
import { useIsAdmin } from '../components/useIsAdmin'
import { ACTIVE_UPDATE_PHASES, useApplyUpdate, useUpdateReleases, useUpdateStatus } from '../api/hooks'
import type { UpdateChannel, UpdateHistoryEntry, UpdatePhase, UpdateRelease, UpdateStatus } from '../api/types'

// A fixed last column, not `auto`: every row is its own grid, so an `auto` track is sized by that row's own button and
// the header — which has none — would stop lining up with the rows.
const COLUMNS = '1.8fr 1.4fr 1fr 110px'

/** How long the service may be unreachable, after it said it was restarting, before the page stops waiting
 * quietly and says something. A restart takes seconds; this is for one that did not come back. */
const RESTART_PATIENCE_MS = 120_000

const PHASE_LABEL: Record<UpdatePhase, string> = {
  idle: 'No update in progress',
  staging: 'Downloading',
  draining: 'Waiting for running work to finish',
  applying: 'Restarting to install',
  restarting: 'Installed — proving itself',
  succeeded: 'Updated',
  rolledback: 'Rolled back',
  failed: 'Failed',
}

function formatUtc(iso: string | null): string {
  if (!iso) return ''
  const date = new Date(iso)
  return Number.isNaN(date.getTime()) ? '' : `${date.toISOString().slice(0, 16).replace('T', ' ')} UTC`
}

/**
 * Updating this installation from the console (phase 159): the releases on each enabled channel, an Update button
 * on each, and what happens after it is pressed.
 *
 * What the page has to be honest about is that the service **goes away**. Pressing Update winds work down and
 * restarts the process, so for a few seconds every request fails, and the page keeps asking through that rather
 * than reporting an error — the same session cookie still works afterwards, because sessions live in the state
 * database, not in memory. If the service is not back within two minutes it says so, and where to look.
 *
 * Closed by default on the server: with `DbDataSync:SelfUpdateEnabled` off nothing here can apply anything, and
 * the page says how to turn it on rather than showing buttons that would refuse.
 */
export function AdminUpdatesPage() {
  const isAdmin = useIsAdmin()
  const { data: status, error: statusError, isError: statusFailed } = useUpdateStatus()
  const [channel, setChannel] = useState<UpdateChannel | undefined>(undefined)
  const [confirming, setConfirming] = useState<UpdateRelease | null>(null)
  const apply = useApplyUpdate()

  // The first enabled channel, until the admin picks another — the list of channels arrives with the status.
  const activeChannel = channel ?? status?.channels[0]
  const canList = !!status?.enabled
  const releases = useUpdateReleases(activeChannel, isAdmin && canList)

  const active = !!status && ACTIVE_UPDATE_PHASES.includes(status.phase)
  const outageSince = useOutageStart(active && statusFailed)

  if (!isAdmin) {
    return (
      <AppShell crumbs={[{ label: 'Admin' }]} tabs={<AdminTabs />}>
        <div className="pane">
          <div className="empty">This screen is for administrators.</div>
        </div>
      </AppShell>
    )
  }

  const confirm = async (release: UpdateRelease) => {
    try {
      await apply.mutateAsync(release.version)
    } catch {
      // Shown by the error banner below, from the mutation's own state.
    }
    setConfirming(null)
  }

  return (
    <AppShell crumbs={[{ label: 'Admin' }]} tabs={<AdminTabs />}>
      <div className="pane">
        <div className="page-head">
          <h1 className="page-title">Updates</h1>
          <span className="page-note">
            Install a newer version of DbDataSync on this server. The service restarts to apply it, and rolls
            back on its own if the new version does not come up healthy.
          </span>
        </div>

        <ErrorBanner error={apply.error ?? (statusFailed && !active ? statusError : null)} />

        {status && !status.enabled && (
          <div className="banner" data-testid="updates-disabled">
            <span className="mark">!</span>
            <span>
              Updating from the console is turned off. Set <span className="mono">DbDataSync:SelfUpdateEnabled</span>{' '}
              to <span className="mono">true</span> under Configuration to turn it on — it replaces the code the
              service runs, so it is off unless someone chooses it.
            </span>
          </div>
        )}

        {status?.enabled && !status.canApply && (
          <div className="banner" data-testid="updates-cannot-apply">
            <span className="mark">!</span>
            <span>
              {status.cannotApplyReason} You can still see what is available; run{' '}
              <span className="mono">dbdatasync update</span> on the server to install it.
            </span>
          </div>
        )}

        {status && <InstallationCard status={status} />}

        {status && (active || outageSince) && (
          <ProgressCard status={status} outageSince={outageSince} />
        )}

        {status && !active && status.phase !== 'idle' && <LastResult status={status} />}

        {status?.enabled && (
          <div className="card flush" data-testid="updates-releases">
            <div className="card-head">
              <span className="card-title">Available releases</span>
              <span className="row" style={{ gap: 8 }}>
                {status.channels.map((name) => (
                  <button
                    key={name}
                    type="button"
                    className={`btn btn-sm ${name === activeChannel ? 'btn-primary' : ''}`}
                    onClick={() => setChannel(name)}
                    data-testid={`updates-channel-${name}`}
                  >
                    {name}
                  </button>
                ))}
                <button
                  type="button"
                  className="btn btn-sm"
                  disabled={releases.isFetching}
                  onClick={() => releases.refetch()}
                  data-testid="updates-check"
                >
                  {releases.isFetching ? 'Checking…' : 'Check again'}
                </button>
              </span>
            </div>
            <div className="grid-head" style={{ gridTemplateColumns: COLUMNS, gap: 14 }}>
              <span>Version</span><span>Built</span><span>State</span><span></span>
            </div>
            {releases.isLoading && <div className="empty">Loading…</div>}
            {releases.isError && (
              <div className="empty" data-testid="updates-releases-error">
                {releases.error instanceof Error ? releases.error.message : 'The releases could not be read.'}
              </div>
            )}
            {releases.data?.warnings.map((warning) => (
              <div key={warning} className="hint" style={{ padding: '6px 14px' }}>{warning}</div>
            ))}
            {releases.data && releases.data.releases.length === 0 && <div className="empty">No releases found.</div>}
            {releases.data?.releases.map((release) => (
              <ReleaseRow
                key={release.version}
                release={release}
                disabled={!status.canApply || active || apply.isPending}
                onUpdate={() => setConfirming(release)}
              />
            ))}
          </div>
        )}

        {status && status.history.length > 0 && <HistoryCard history={status.history} />}

        {confirming && (
          <ConfirmDialog
            release={confirming}
            runningVersion={status?.runningVersion ?? null}
            busy={apply.isPending}
            onConfirm={() => confirm(confirming)}
            onCancel={() => setConfirming(null)}
          />
        )}
      </div>
    </AppShell>
  )
}

/** When the service first stopped answering while an update was in flight — null while it is answering. */
function useOutageStart(unreachable: boolean): number | null {
  const [since, setSince] = useState<number | null>(null)
  const previous = useRef(false)

  useEffect(() => {
    if (unreachable && !previous.current) setSince(Date.now())
    if (!unreachable && previous.current) setSince(null)
    previous.current = unreachable
  }, [unreachable])

  return since
}

function InstallationCard({ status }: { status: UpdateStatus }) {
  return (
    <div className="card" data-testid="updates-installation">
      <div className="card-head">
        <span className="card-title">This installation</span>
      </div>
      {/* .card-body is a column; this one is a row of facts. */}
      <div className="card-body row" style={{ flexDirection: 'row', gap: 28, flexWrap: 'wrap' }}>
        <span>
          <span className="hint">Running </span>
          <span className="mono" data-testid="updates-running-version">{status.runningVersion ?? 'unknown'}</span>
        </span>
        <span>
          <span className="hint">Installed as </span>
          <span className="mono">{status.installKind}</span>
        </span>
        <span>
          <span className="hint">Channels </span>
          <span className="mono">{status.channels.join(', ')}</span>
        </span>
      </div>
    </div>
  )
}

function ProgressCard({ status, outageSince }: { status: UpdateStatus; outageSince: number | null }) {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    if (!outageSince) return
    const timer = setInterval(() => setNow(Date.now()), 1000)
    return () => clearInterval(timer)
  }, [outageSince])

  const overdue = outageSince !== null && now - outageSince > RESTART_PATIENCE_MS

  return (
    <div className="card" data-testid="updates-progress">
      <div className="card-head">
        <span className="card-title">
          {status.toVersion ? `Updating to ${status.toVersion}` : 'Updating'}
        </span>
      </div>
      <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        <span className="status">
          <span className={`dot ${overdue ? 'dot-bad' : 'dot-warn'}`} />
          <span data-testid="updates-phase">
            {outageSince ? 'The service is restarting…' : PHASE_LABEL[status.phase]}
          </span>
        </span>
        {status.message && !outageSince && <span className="hint">{status.message}</span>}
        {overdue && (
          <span className="hint" data-testid="updates-overdue">
            The service has not answered for two minutes. On the server, check{' '}
            <span className="mono">journalctl -u dbdatasync</span> and <span className="mono">{status.logPath}</span>{' '}
            — if the new version cannot start, the next start rolls it back.
          </span>
        )}
        {status.phase === 'restarting' && !outageSince && (
          <span className="hint">
            The new version is running. It counts as having worked once it has been serving for a while; until then
            a restart rolls it back.
          </span>
        )}
      </div>
    </div>
  )
}

function LastResult({ status }: { status: UpdateStatus }) {
  const good = status.phase === 'succeeded'
  return (
    <div className={`banner ${good ? '' : 'error'}`} role={good ? undefined : 'alert'} data-testid="updates-last-result">
      <span className="mark">{good ? '✓' : '!'}</span>
      <span>
        {good ? status.message : (
          <>
            <strong>{PHASE_LABEL[status.phase]}.</strong> {status.message} Details are in{' '}
            <span className="mono">{status.logPath}</span>.
          </>
        )}
      </span>
    </div>
  )
}

function ReleaseRow({ release, disabled, onUpdate }: {
  release: UpdateRelease
  disabled: boolean
  onUpdate: () => void
}) {
  const state = release.installed ? 'running' : release.newer ? 'newer' : 'older'

  return (
    <div
      className="grid-row"
      style={{ gridTemplateColumns: COLUMNS, gap: 14 }}
      data-testid={`updates-release-${release.version}`}
    >
      <span className="mono">{release.version}</span>
      <span className="hint">{formatUtc(release.builtUtc)}</span>
      <span className="status">
        <span className={`dot ${release.installed ? 'dot-ok' : release.newer ? 'dot-warn' : 'dot-idle'}`} />
        {state}
      </span>
      <span className="row" style={{ justifyContent: 'flex-end' }}>
        <button
          type="button"
          className="btn btn-sm"
          disabled={disabled || release.installed}
          onClick={onUpdate}
          data-testid={`updates-apply-${release.version}`}
        >
          {release.newer ? 'Update…' : 'Install…'}
        </button>
      </span>
    </div>
  )
}

function HistoryCard({ history }: { history: UpdateHistoryEntry[] }) {
  return (
    <div className="card flush" data-testid="updates-history">
      <div className="card-head">
        <span className="card-title">Recent updates</span>
      </div>
      {history.map((entry) => (
        <div
          key={`${entry.atUtc}-${entry.phase}`}
          className="grid-row"
          style={{ gridTemplateColumns: '1.2fr 1fr 2fr', gap: 14 }}
        >
          <span className="hint">{formatUtc(entry.atUtc)}</span>
          <span className="status">
            <span className={`dot ${entry.phase === 'succeeded' ? 'dot-ok' : 'dot-bad'}`} />
            {PHASE_LABEL[entry.phase]}
          </span>
          <span className="hint">
            {entry.fromVersion ?? '?'} → {entry.toVersion ?? '?'}
            {entry.requestedBy ? ` · ${entry.requestedBy}` : ''}
          </span>
        </div>
      ))}
    </div>
  )
}

/** The one gate: what pressing Confirm does, said plainly — and, for a snapshot, what its trust rests on. */
function ConfirmDialog({ release, runningVersion, busy, onConfirm, onCancel }: {
  release: UpdateRelease
  runningVersion: string | null
  busy: boolean
  onConfirm: () => void
  onCancel: () => void
}) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onCancel() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onCancel])

  const older = !release.newer && !release.installed

  return (
    <div className="modal-backdrop" onMouseDown={(e) => { if (e.target === e.currentTarget) onCancel() }}>
      <div
        className="modal"
        role="dialog"
        aria-modal="true"
        aria-label={`Confirm updating to ${release.version}`}
        data-testid="updates-confirm-dialog"
      >
        <div className="card-head">
          <span className="card-title">{older ? 'Install' : 'Update to'} {release.version}?</span>
        </div>
        <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          <span className="hint">
            {runningVersion ? <>Running <span className="mono">{runningVersion}</span>. </> : null}
            The service stops starting new work, lets what is running finish, then <strong>restarts</strong> to
            install this. Anything still running when the wait ends is picked up again after the restart. If the new
            version does not come up healthy, the next start puts the current one back.
          </span>
          {older && (
            <span className="hint" data-testid="updates-confirm-older">
              This is <strong>older</strong> than the running version. It is installed by removing the current one and
              installing this one.
            </span>
          )}
          {release.channel === 'snapshot' && (
            <span className="hint" data-testid="updates-confirm-snapshot">
              A snapshot is a <strong>development build</strong>: the newest commit that passed automated tests, not
              a release. Its download is checked against a checksum published beside it, which catches a corrupted
              or truncated download — not a tampered one.
            </span>
          )}
          {release.channel === 'beta' && (
            <span className="hint">A beta is a prerelease, published ahead of a stable release.</span>
          )}
          <div className="row" style={{ gap: 8, justifyContent: 'flex-end' }}>
            <button type="button" className="btn" onClick={onCancel} data-testid="updates-confirm-cancel">
              Cancel
            </button>
            <button
              type="button"
              className="btn btn-primary"
              disabled={busy}
              onClick={onConfirm}
              data-testid="updates-confirm"
            >
              {busy ? 'Starting…' : 'Update and restart'}
            </button>
          </div>
        </div>
      </div>
    </div>
  )
}
