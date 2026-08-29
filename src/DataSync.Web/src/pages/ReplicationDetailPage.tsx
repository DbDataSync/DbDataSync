import { useEffect, useState } from 'react'
import { NavLink, Outlet, useNavigate, useParams } from 'react-router-dom'
import { AppShell } from '../components/AppShell'
import { tabClass } from '../components/tabClass'
import { ErrorBanner } from '../components/ErrorBanner'
import { MetricsCard } from './replication-detail/MetricsCard'
import { ScheduleCard } from './replication-detail/ScheduleCard'
import { StatusCard } from './replication-detail/StatusCard'
import {
  useDeleteReplication, useReplication, useSetReplicationEnabled, useUpsertReplication,
} from '../api/hooks'
import type { ReplicationTaskConfig } from '../api/types'
import type { RunsCommand } from './replication-detail/RunsPanel'

/** The design labels the config-history tab "Version Control"; it is the same git log. */
const TABS: { path: string; label: string; testId: string }[] = [
  { path: 'overview', label: 'Overview', testId: 'tab-overview' },
  { path: 'mappings', label: 'Table Mappings', testId: 'tab-mappings' },
  { path: 'runs', label: 'Runs', testId: 'tab-runs' },
  { path: 'history', label: 'Version Control', testId: 'tab-history' },
]

/**
 * A layout route: the four tabs are four routes sharing this chrome, rendered through the `Outlet`.
 *
 * The chrome does not unmount when the tab changes, which is what lets the Backfill…/Run Now buttons
 * live up here and still reach the Runs panel — see `RunsCommand`.
 *
 * It owns the **draft** too, since phase 46. That is not tidying: the draft used to live in
 * `OverviewPanel`, which unmounts the moment somebody clicks Runs, so a half-finished pipeline edit
 * was lost by looking at something else. Held here it survives, because this component does not
 * unmount until the replication does.
 *
 * Status and Schedule live here for the same reason — a rail beside the `Outlet` rather than inside
 * it, so they are the same cards showing the same thing on all four tabs rather than four mounts of
 * them on one.
 */
export function ReplicationDetailPage() {
  const { name } = useParams<{ name: string }>()
  const navigate = useNavigate()
  const del = useDeleteReplication()
  const { data: task, error } = useReplication(name)
  const upsert = useUpsertReplication()
  const setEnabled = useSetReplicationEnabled(name ?? '')

  // A command from the chrome down into the Runs panel. The nonce is what makes a repeat of the
  // same command distinguishable from no command at all.
  //
  // Deliberately component state rather than location state: an action must not re-fire because
  // someone reloaded the page or pressed Back onto this history entry.
  const [command, setCommand] = useState<RunsCommand | null>(null)
  const [draft, setDraft] = useState<ReplicationTaskConfig | null>(null)

  // Seeded once, from the first load. Deliberately not re-seeded when `task` changes: the Enabled
  // toggle invalidates that query on every click, and re-seeding there would throw away whatever the
  // operator was in the middle of editing.
  useEffect(() => {
    if (task && !draft) setDraft(task)
  }, [task, draft])

  const send = (kind: RunsCommand['kind']) => {
    navigate(`/replications/${encodeURIComponent(name!)}/runs`)
    setCommand({ kind, nonce: Date.now() })
  }

  if (!name) return null

  const onDelete = async () => {
    await del.mutateAsync(name)
    navigate('/replications')
  }

  // Enabled is the saved state, never the draft's: it commits on its own, so the draft has no opinion
  // about it and a stale one would flicker the accent on every save.
  const enabled = task?.enabled ?? true

  const save = async () => {
    if (draft) await upsert.mutateAsync({ name, task: { ...draft, enabled } })
  }

  return (
    <AppShell
      crumbs={[{ label: 'Replications', to: '/replications' }, { label: name, mono: true, heading: true }]}
      tabs={
        <>
          {TABS.map((t) => (
            <NavLink
              key={t.path}
              to={`/replications/${encodeURIComponent(name)}/${t.path}`}
              className={tabClass}
              data-testid={t.testId}
            >
              {t.label}
            </NavLink>
          ))}
          <div className="actions">
            {/* Enabled sits in the chrome rather than on the Schedule card, and commits on its own.
                Two controls that save differently should not sit next to each other looking alike. */}
            <span className="row" style={{ gap: 7, marginRight: 4 }}>
              <button
                type="button"
                className={`toggle ${enabled ? 'on' : ''}`}
                onClick={() => setEnabled.mutate(!enabled)}
                aria-pressed={enabled}
                disabled={setEnabled.isPending || !task}
                data-testid="enabled-toggle"
              />
              <span style={{ font: '500 11.5px var(--ui)', color: 'var(--ink-4)' }}>
                {enabled ? 'Enabled' : 'Disabled'}
              </span>
            </span>

            {/* The design puts the run controls in the chrome, reachable from any tab — clicking
                either moves to Runs so the result is visible where it lands. */}
            <button
              className="btn btn-chrome"
              onClick={() => send('backfill')}
              data-testid="backfill-button"
            >
              Backfill…
            </button>
            <button
              className="btn btn-primary btn-chrome"
              onClick={() => send('run')}
              data-testid="trigger-run-button"
            >
              Run Now
            </button>
            {/* Up here rather than buried in the Pipeline card, because it saves the whole draft —
                endpoints, pipeline and script bindings — not just the card it used to sit in. */}
            <button
              className="btn btn-primary btn-chrome"
              onClick={save}
              disabled={upsert.isPending || !draft}
              data-testid="save-settings-button"
            >
              {upsert.isPending ? 'Saving…' : 'Save settings'}
            </button>
            <button className="btn btn-danger btn-chrome" onClick={onDelete} data-testid="delete-replication-button">
              Delete
            </button>
          </div>
        </>
      }
    >
      <div style={{ display: 'flex', gap: 18, alignItems: 'flex-start', minHeight: 0 }}>
        <div style={{ flex: 1, minWidth: 0 }}>
          {/* Each tab's panel takes the replication name, the pending command and the draft from here
              rather than re-deriving them, so a panel never has to know it is mounted under a layout
              route. */}
          <Outlet
            context={{
              replicationName: name, command, draft, setDraft, saving: upsert.isPending, save,
            } satisfies ReplicationOutletContext}
          />
        </div>

        <div style={{ width: 288, flex: 'none', display: 'flex', flexDirection: 'column', gap: 14 }}>
          <ErrorBanner error={error ?? upsert.error ?? setEnabled.error} />
          <StatusCard replicationName={name} enabled={enabled} />
          <MetricsCard replicationName={name} />
          {draft && <ScheduleCard draft={draft} enabled={enabled} onChange={setDraft} />}
        </div>
      </div>
    </AppShell>
  )
}

/**
 * What every tab under this layout is given.
 *
 * The draft is here rather than in whichever panel edits it, so that editing on Overview and then
 * looking at Runs does not discard the edit — the panels unmount, this does not.
 */
export interface ReplicationOutletContext {
  replicationName: string
  command: RunsCommand | null
  draft: ReplicationTaskConfig | null
  setDraft: (next: ReplicationTaskConfig) => void
  saving: boolean
  save: () => Promise<void>
}
