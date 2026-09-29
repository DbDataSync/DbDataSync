import { useEffect, useRef, useState } from 'react'
import { AdminTabs } from '../components/AdminTabs'
import { AppShell } from '../components/AppShell'
import { ErrorBanner } from '../components/ErrorBanner'
import { useIsAdmin } from '../components/useIsAdmin'
import { ACTIVE_UPDATE_PHASES, useUpdateReleases, useUpdateStatus } from '../api/hooks'
import type { UpdateChannel, UpdateCommands, UpdateHistoryEntry, UpdatePhase, UpdateRelease, UpdateStatus } from '../api/types'

// A fixed last column, not `auto`: every row is its own grid, so an `auto` track is sized by that row's own button and
// the header — which has none — would stop lining up with the rows.
const COLUMNS = '1.8fr 1.4fr 1fr 130px'

/** How long the service may be unreachable, after the CLI said it was switching, before the page stops waiting
 * quietly and says something. A restart takes seconds; this is for one that did not come back. */
const RESTART_PATIENCE_MS = 120_000

const PHASE_LABEL: Record<UpdatePhase, string> = {
  idle: 'No update in progress',
  staging: 'Downloading',
  draining: 'Waiting for running work to finish',
  applying: 'Installing beside the running version',
  restarting: 'Switched — starting the new version',
  succeeded: 'Updated',
  rolledback: 'Switched back',
  failed: 'Failed',
}

function formatUtc(iso: string | null): string {
  if (!iso) return ''
  const date = new Date(iso)
  return Number.isNaN(date.getTime()) ? '' : `${date.toISOString().slice(0, 16).replace('T', ' ')} UTC`
}

/**
 * The Updates screen: what is running, what is available, and **the commands that update it** (phase 196L).
 *
 * The console does not apply updates itself any more. An update is run from a shell on the server —
 * `dbdatasync update --apply` — which installs the new version beside the running one, switches the service over,
 * and switches back on its own if the new version does not answer. This page hands the admin exactly that command,
 * for exactly this server (its OS, its data directory), with a copy button; the server builds the text.
 *
 * It still watches: the CLI records what it is doing where this page reads it, so an update started from a shell
 * shows its progress here — including through the few seconds the service is down while it switches.
 *
 * Looking up releases calls nuget.org and GitHub, so that part is off unless `DbDataSync:Updates:Mode` turns it on.
 * The commands are shown either way: `dbdatasync update --list` asks from the server's own shell.
 */
export function AdminUpdatesPage() {
  const isAdmin = useIsAdmin()
  const { data: status, error: statusError, isError: statusFailed } = useUpdateStatus()
  const [channel, setChannel] = useState<UpdateChannel | undefined>(undefined)
  const [chosen, setChosen] = useState<UpdateRelease | null>(null)

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

  return (
    <AppShell crumbs={[{ label: 'Admin' }]} tabs={<AdminTabs />}>
      <div className="pane">
        <div className="page-head">
          <h1 className="page-title">Updates</h1>
          <span className="page-note">
            See what is available, and get the commands that update this server. An update is run from a shell on the
            server: the new version is installed beside the running one, then the service switches to it — and
            switches back on its own if the new version does not come up.
          </span>
        </div>

        <ErrorBanner error={statusFailed && !active ? statusError : null} />

        {status && !status.commands && status.commandsUnavailableReason && (
          <div className="banner" data-testid="updates-no-commands">
            <span className="mark">!</span>
            <span>{status.commandsUnavailableReason}</span>
          </div>
        )}

        {status && <InstallationCard status={status} />}

        {status && (active || outageSince) && (
          <ProgressCard status={status} outageSince={outageSince} />
        )}

        {status && !active && status.phase !== 'idle' && <LastResult status={status} />}

        {status?.commands && <HowToUpdateCard commands={status.commands} />}

        {status && !status.enabled && (
          <div className="banner" data-testid="updates-disabled">
            <span className="mark">i</span>
            <span>
              Looking up releases here is turned off, because it calls nuget.org and GitHub. Set{' '}
              <span className="mono">DbDataSync:Updates:Mode</span> to <span className="mono">manual</span> under
              Configuration to list them on this page — or run <span className="mono">dbdatasync update --list</span>{' '}
              on the server.
            </span>
          </div>
        )}

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
                disabled={!status.commands}
                onChoose={() => setChosen(release)}
              />
            ))}
          </div>
        )}

        {status && status.history.length > 0 && <HistoryCard history={status.history} />}

        {chosen && status?.commands && (
          <CommandsDialog
            release={chosen}
            commands={status.commands}
            runningVersion={status.runningVersion}
            onClose={() => setChosen(null)}
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

/** A command, as text the admin can select, with a button that copies it. */
function CopyableCommand({ command, testId }: { command: string; testId: string }) {
  const [copied, setCopied] = useState(false)

  useEffect(() => {
    if (!copied) return
    const timer = setTimeout(() => setCopied(false), 2000)
    return () => clearTimeout(timer)
  }, [copied])

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(command)
      setCopied(true)
    } catch {
      // Clipboard access can be denied by the browser; the command is still selectable as text.
    }
  }

  return (
    <div className="row" style={{ gap: 8, alignItems: 'center' }}>
      <span
        className="mono"
        style={{
          flex: 1, userSelect: 'all', overflowWrap: 'anywhere', padding: '6px 10px',
          background: 'var(--sunken)', border: '1px solid var(--card-inner-edge)', borderRadius: 6,
        }}
        data-testid={testId}
      >
        {command}
      </span>
      <button type="button" className="btn btn-sm" onClick={copy} data-testid={`${testId}-copy`}>
        {copied ? 'Copied' : 'Copy'}
      </button>
    </div>
  )
}

function InstallationCard({ status }: { status: UpdateStatus }) {
  const slots = status.slots
  return (
    <div className="card" data-testid="updates-installation">
      <div className="card-head">
        <span className="card-title">This installation</span>
      </div>
      <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
        <div className="row" style={{ flexDirection: 'row', gap: 28, flexWrap: 'wrap' }}>
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
        {slots && (
          <div className="row" style={{ flexDirection: 'row', gap: 28, flexWrap: 'wrap' }} data-testid="updates-slots">
            {slots.slots.map((slot) => (
              <span key={slot.name} data-testid={`updates-slot-${slot.name}`}>
                <span className="hint">Slot {slot.name} </span>
                <span className="mono">
                  {slot.ambiguous ? 'more than one install' : slot.version ?? 'empty'}
                </span>
                {slot.current && <span className="hint"> · running</span>}
                {!slot.current && slot.version && <span className="hint"> · kept, to switch back to</span>}
              </span>
            ))}
            <span className="hint mono">{slots.root}</span>
          </div>
        )}
        {slots?.checks.map((check) => (
          <span key={check.message} className="hint" data-testid="updates-slot-check">
            <strong>{check.level === 'warning' ? 'Warning' : 'Note'}:</strong> {check.message}
          </span>
        ))}
      </div>
    </div>
  )
}

/** The commands, in the order an admin uses them, and where to run them. */
function HowToUpdateCard({ commands }: { commands: UpdateCommands }) {
  const applyTemplate = commands.apply.replace(commands.versionPlaceholder, '<version>')
  return (
    <div className="card" data-testid="updates-how">
      <div className="card-head">
        <span className="card-title">How to update</span>
      </div>
      <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
        <span className="hint" data-testid="updates-where">{commands.where}</span>

        <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
          <span>1. See what is available (no privileges needed):</span>
          <CopyableCommand command={commands.list} testId="updates-command-list" />
        </div>

        <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
          <span>
            2. {commands.printsOnly ? 'Print the commands that install a version' : 'Install a version and switch to it'} —
            or choose one below for its exact command:
          </span>
          <CopyableCommand command={applyTemplate} testId="updates-command-apply" />
        </div>

        {commands.rollback && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
            <span>3. Switch back to the version before, if the new one misbehaves later:</span>
            <CopyableCommand command={commands.rollback} testId="updates-command-rollback" />
          </div>
        )}

        <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
          <span>{commands.rollback ? '4' : '3'}. See both installed versions and the last update:</span>
          <CopyableCommand command={commands.status} testId="updates-command-status" />
        </div>

        {commands.convertsFirst && (
          <span className="hint" data-testid="updates-converts-first">
            This install predates versioned slots. The first update converts it, once: the running version is kept as
            one slot and a small launcher takes the place of the <span className="mono">dbdatasync</span> command. The
            service and PATH need no change. From then on, update with these commands rather than{' '}
            <span className="mono">dotnet tool update</span>.
          </span>
        )}
        {commands.printsOnly && (
          <span className="hint" data-testid="updates-prints-only">
            This is a per-user global tool, which cannot keep two versions side by side, so the command prints the{' '}
            <span className="mono">dotnet tool</span> commands to run. Install machine-wide (see the install docs) to
            update and switch back with one command.
          </span>
        )}
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
          {status.requestedBy ? <span className="hint"> · started by {status.requestedBy} from a shell</span> : null}
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
            The service has not answered for two minutes. Look at the terminal the update was run from, and at{' '}
            <span className="mono">{status.logPath}</span> on the server.
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

function ReleaseRow({ release, disabled, onChoose }: {
  release: UpdateRelease
  disabled: boolean
  onChoose: () => void
}) {
  const state = release.installed ? 'running' : release.newer ? 'newer' : 'older'

  return (
    <div
      className="grid-row"
      style={{ gridTemplateColumns: COLUMNS, gap: 14 }}
      data-testid={`updates-release-${release.version}`}
    >
      <a className="mono" href={release.url} target="_blank" rel="noopener noreferrer">
        {release.version}
      </a>
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
          onClick={onChoose}
          data-testid={`updates-commands-${release.version}`}
        >
          Commands…
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

/** One release's command, ready to copy — and what running it does, said plainly. */
function CommandsDialog({ release, commands, runningVersion, onClose }: {
  release: UpdateRelease
  commands: UpdateCommands
  runningVersion: string | null
  onClose: () => void
}) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  const older = !release.newer && !release.installed
  const command = commands.apply.replace(commands.versionPlaceholder, release.version)

  return (
    <div className="modal-backdrop" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose() }}>
      <div
        className="modal"
        role="dialog"
        aria-modal="true"
        aria-label={`Commands for ${release.version}`}
        data-testid="updates-commands-dialog"
      >
        <div className="card-head">
          <span className="card-title">{older ? 'Install' : 'Update to'} {release.version}</span>
        </div>
        <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          <span className="hint">{commands.where}</span>
          <CopyableCommand command={command} testId="updates-dialog-command" />
          {commands.printsOnly ? (
            <span className="hint">
              This prints the <span className="mono">dotnet tool</span> commands that install it; nothing is changed until
              you run those.
            </span>
          ) : (
            <span className="hint" data-testid="updates-dialog-explanation">
              {runningVersion ? <>Running <span className="mono">{runningVersion}</span>. </> : null}
              It installs {release.version} beside the running version, then stops the service, switches to the new
              version and starts it — a few seconds of downtime. If it does not answer, it switches back on its own.
              The version you are leaving stays installed, so{' '}
              <span className="mono">dbdatasync update --rollback</span> returns to it later without downloading
              anything. It asks before stopping anything; add <span className="mono">--yes</span> to skip the question.
            </span>
          )}
          {commands.convertsFirst && (
            <span className="hint">
              The first time, it also converts this install to versioned slots, and says so as it does.
            </span>
          )}
          {older && (
            <span className="hint" data-testid="updates-dialog-older">
              This is <strong>older</strong> than the running version.
            </span>
          )}
          {release.channel === 'snapshot' && (
            <span className="hint" data-testid="updates-dialog-snapshot">
              A snapshot is a <strong>development build</strong>: the newest commit that passed automated tests, not
              a release. Its download is checked against a checksum published beside it, which catches a corrupted
              or truncated download — not a tampered one.
            </span>
          )}
          {release.channel === 'beta' && (
            <span className="hint">A beta is a prerelease, published ahead of a stable release.</span>
          )}
          <div className="row" style={{ gap: 8, justifyContent: 'flex-end' }}>
            <button type="button" className="btn" onClick={onClose} data-testid="updates-dialog-close">
              Close
            </button>
          </div>
        </div>
      </div>
    </div>
  )
}
