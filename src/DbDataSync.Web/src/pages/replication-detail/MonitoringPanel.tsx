import { useState } from 'react'
import { Outlet, useOutletContext } from 'react-router-dom'
import { ErrorBanner } from '../../components/ErrorBanner'
import { RefreshCountdown } from '../../components/RefreshCountdown'
import { SubTabs, type SubTab } from '../../components/SubTabs'
import { MappingReadStateDialog } from '../../components/MappingReadStateDialog'
import { PauseIcon, PlayIcon } from '../../components/icons'
import { resolveSide } from '../../api/resolveEndpoint'
import {
  useCapabilities, useMappingReadState, useReplication, useReplicationLag, useReplicationStatus,
  useRunVerification, useSetMappingReadState, useTableMappingDetails, useTableMappings,
} from '../../api/hooks'
import type {
  MappingLag, ReadHold, ReplicationTaskConfig, TableMappingConfig, TableSpec,
} from '../../api/types'
import { BulkLoadHistoryPanel } from './BulkLoadHistoryPanel'
import { BulkLoadProgressCard } from './BulkLoadProgressCard'
import { holdStateOf, HOLD_INFO } from './holdState'
import { formatLag, lagStateOf } from './lag'
import { INTENT_INFO, offeredIntents } from './readIntent'
import { PauseHistoryPanel } from './PauseHistoryPanel'
import { RunsPanel, type RunsCommand } from './RunsPanel'

const COLUMNS = '1fr 1fr 190px 250px'

const MONITORING_TABS: SubTab[] = [
  { path: null, label: 'Current Status', testId: 'monitoring-tab-current' },
  { path: 'history', label: 'Run History', testId: 'monitoring-tab-history' },
  { path: 'pause-history', label: 'Pause History', testId: 'monitoring-tab-pause-history' },
  { path: 'bulk-load-history', label: 'Bulk Load History', testId: 'monitoring-tab-bulk-load-history' },
]

/**
 * The Monitoring section's own layout route — see phase 103, phase 131 for the third sub-tab, and
 * phase 139 for the fourth.
 *
 * Four sub-tabs, the `SubTabs` convention Overview and the mapping editor already use: **Current
 * Status** is the index, so `/replications/{name}/monitoring` opens the lag table rather than
 * requiring a segment; **Run History** is what Runs used to be on its own top-level tab; **Pause
 * History** (phase 131) is who paused or resumed this replication or one of its mappings, and when;
 * **Bulk Load History** (phase 139) is every past bulk load, across every mapping.
 *
 * Takes the command down to whichever sub-tab is open, the same way the layout route above passes it
 * to this one — `RunsCommand` reaches the history panel two levels down the outlet now rather than one,
 * and nothing about it needed to change to get there.
 */
export function MonitoringSection({ replicationName, command }: {
  replicationName: string
  command: RunsCommand | null
}) {
  const base = `/replications/${encodeURIComponent(replicationName)}/monitoring`

  return (
    <div className="pane">
      <SubTabs base={base} tabs={MONITORING_TABS} testId="monitoring-subtabs" />
      <div className="subtab-panel">
        <Outlet context={{ replicationName, command } satisfies MonitoringOutletContext} />
      </div>
    </div>
  )
}

/** What each of Monitoring's sub-tabs is handed. */
export interface MonitoringOutletContext {
  replicationName: string
  command: RunsCommand | null
}

export function MonitoringCurrentStatusTab() {
  const { replicationName } = useOutletContext<MonitoringOutletContext>()
  return <MonitoringPanel replicationName={replicationName} />
}

export function MonitoringRunHistoryTab() {
  const { replicationName, command } = useOutletContext<MonitoringOutletContext>()
  return <RunsPanel replicationName={replicationName} command={command} />
}

export function MonitoringPauseHistoryTab() {
  const { replicationName } = useOutletContext<MonitoringOutletContext>()
  return <PauseHistoryPanel replicationName={replicationName} />
}

export function MonitoringBulkLoadHistoryTab() {
  const { replicationName } = useOutletContext<MonitoringOutletContext>()
  return <BulkLoadHistoryPanel replicationName={replicationName} />
}

/**
 * How far behind its source each of this replication's mappings is — see phase 86.
 *
 * Everything here comes from one call (`useReplicationLag`), including the range at the top: the
 * server ranks the mappings, so this tab and the replications list cannot disagree about which
 * mapping is furthest behind.
 *
 * **Nothing on this screen is measured against now.** Phase 85's figures are all differences
 * between a mapping's own position and the last position its source was observed at, which is why a
 * caught-up replication reads zero here and stays there rather than climbing overnight.
 *
 * No `.pane` of its own since phase 103 — `MonitoringSection` owns that, because this is now one of
 * two sub-tabs sharing it. The countdown lives in the Reader lag card's own head rather than a
 * pane-level heading: a bare "Current status" heading whose only content was the countdown said
 * nothing the tab title above it did not already say, so it is gone and the countdown moved to the
 * card that actually describes what it is refreshing.
 */
export function MonitoringPanel({ replicationName }: { replicationName: string }) {
  const { data: task } = useReplication(replicationName)
  const { data: names } = useTableMappings(replicationName)
  const { data: lag, isLoading, error, dataUpdatedAt } = useReplicationLag(replicationName)
  // The other gate a row's "why is nothing happening" has to account for — phase 64's per-replication
  // pause, coarser than and checked ahead of any one mapping's own ReadHold. See holdState.ts.
  const { data: status } = useReplicationStatus(replicationName)

  // The mapping configs the sidebar has already loaded, for the source/target each row names. The
  // lag endpoint answers "how far behind", not "behind what" — which is config, and is already here.
  const mappings = useTableMappingDetails(replicationName, names)

  return (
    <>
      <ErrorBanner error={error} />

      {/* Above the lag cards because it answers a more urgent question than they do — "is the reload
          I started still going, and how far in is it" — and unlike them it is only here at all while
          that question has an answer. */}
      <BulkLoadProgressCard replicationName={replicationName} />

      <div className="card" data-testid="monitoring-range">
        <div className="card-head tight">
          <span className="card-title sm">Reader lag</span>
          <span className="card-note">
            measured against each source's own last-known position, never against the clock
          </span>
          <span className="spacer">
            <RefreshCountdown label="Lag" dataUpdatedAt={dataUpdatedAt} testId="lag-countdown" />
          </span>
        </div>
        <div className="card-body">
          <Figure
            label="Across mappings"
            value={rangeText(lag?.lowestLagMs ?? null, lag?.highestLagMs ?? null, isLoading)}
            testId="monitoring-range-figure"
          />
          {lag?.rangeIncludesEstimates && (
            <span className="hint" data-testid="monitoring-range-estimated">
              Includes at least one estimated figure, whose precision is this system's polling
              interval rather than anything the source stated.
            </span>
          )}
        </div>
      </div>

      <div className="card flush" data-testid="monitoring-mappings">
        <div className="grid-head" style={{ gridTemplateColumns: COLUMNS, gap: 14 }}>
          <span>Source</span><span>Target</span><span>Lag</span><span>Intent &amp; hold</span>
        </div>

        {(names ?? []).map((name, i) => (
          <MappingLagRow
            key={name}
            replicationName={replicationName}
            name={name}
            task={task}
            mapping={mappings[i]?.data}
            source={mappings[i]?.data?.sources[0]}
            target={mappings[i]?.data?.targets[0]}
            lag={lag?.mappings[name]}
            taskEnabled={task?.enabled ?? true}
            taskPaused={status?.paused ?? false}
          />
        ))}

        {names?.length === 0 && <div className="empty">This replication has no table mappings yet.</div>}
        {!names && <div className="empty">Loading…</div>}
      </div>
    </>
  )
}

function MappingLagRow({ replicationName, name, task, mapping, source, target, lag, taskEnabled, taskPaused }: {
  replicationName: string
  name: string
  task: ReplicationTaskConfig | undefined
  mapping: TableMappingConfig | undefined
  source: TableSpec | undefined
  target: TableSpec | undefined
  lag: MappingLag | undefined
  /** Config's durable intent — see `ReplicationDetailPage`'s own Enabled toggle. */
  taskEnabled: boolean
  /** State's temporary hold, per replication (phase 64) — the coarser of the two grains this row's
   * own IntentHoldCell has to make legible together. */
  taskPaused: boolean
}) {
  const readState = useMappingReadState(replicationName, name)
  const hold: ReadHold = readState.data?.hold ?? 'None'
  const holdState = holdStateOf(taskEnabled, taskPaused, hold)

  return (
    <div
      className="grid-row auto"
      style={{ gridTemplateColumns: COLUMNS, gap: 14 }}
      data-testid={`monitoring-row-${name}`}
      data-lag-state={lag ? lagStateOf(lag) : undefined}
      data-hold-state={holdState}
    >
      <Side task={task} spec={source} which="source" />
      <Side task={task} spec={target} which="target" />
      <LagCell name={name} lag={lag} />
      <IntentHoldCell
        replicationName={replicationName}
        name={name}
        task={task}
        mapping={mapping}
        source={source}
        lag={lag}
        readState={readState}
        holdState={holdState}
      />
    </div>
  )
}

/**
 * What this mapping does next, and whether anything is stopping it — see phase 102.
 *
 * A held mapping is the interesting state on this screen: it is the one where nothing is happening
 * and will not until somebody acts, and it has to read as that rather than as merely idle. The
 * precedence `holdState` (computed one level up, so the row itself can carry the same `data-hold-state`
 * `data-lag-state` already models) mirrors `StatusCard`'s own `StateIndicator` — a replication-wide
 * disable or pause is the more fundamental fact, so a mapping's own hold clearing does not, on its
 * own, make the row read as running while the replication is still held.
 */
function IntentHoldCell({ replicationName, name, task, mapping, source, lag, readState, holdState }: {
  replicationName: string
  name: string
  task: ReplicationTaskConfig | undefined
  mapping: TableMappingConfig | undefined
  source: TableSpec | undefined
  lag: MappingLag | undefined
  readState: ReturnType<typeof useMappingReadState>
  holdState: ReturnType<typeof holdStateOf>
}) {
  const [dialogOpen, setDialogOpen] = useState(false)
  const setReadState = useSetMappingReadState(replicationName, name)
  const runVerification = useRunVerification(replicationName)

  const resolvedSource = source ? resolveSide(task?.endpoints?.source ?? null, source) : undefined
  const capabilities = useCapabilities(resolvedSource?.connectionName)

  // The reader lag already resolved for this row (`lag.readerKind`) is the same Kind PipelineResolution
  // would hand a pass — reusing it here means no second resolution that could disagree with what the
  // Lag cell beside it is already describing.
  const readerCapability = capabilities.data?.readers.find((r) => r.kind === lag?.readerKind)
  const offered = offeredIntents(readerCapability?.supportedIntents)

  const intent = readState.data?.intent
  const hold: ReadHold = readState.data?.hold ?? 'None'
  const loading = !readState.data

  const maskedByReplication = hold !== 'None' && (holdState === 'replication-disabled' || holdState === 'replication-paused')

  const pauseTogglable = hold !== 'PositionExpired'
  const paused = hold === 'Paused'

  const togglePause = () => {
    if (!intent) return
    setReadState.mutate({ intent, hold: paused ? 'None' : 'Paused' })
  }

  return (
    <span style={{ display: 'flex', flexDirection: 'column', gap: 4, minWidth: 0 }}>
      <span className="mono sm" data-testid={`monitoring-intent-${name}`}>
        {intent ? INTENT_INFO[intent].label : '…'}
      </span>

      {holdState !== 'none' && (
        <span
          className="row"
          style={{ gap: 6 }}
          data-testid={`monitoring-hold-${name}`}
          data-hold-state={holdState}
        >
          <span className={`dot ${HOLD_INFO[holdState].dot}`} aria-hidden="true" />
          <span style={{ font: '500 11px var(--ui)', color: 'var(--ink-3)' }}>{HOLD_INFO[holdState].label}</span>
        </span>
      )}

      {/* Both grains stay visible even when one masks the other — an operator who resumes the
          replication still needs to know this table has its own hold to clear, too. */}
      {maskedByReplication && (
        <span className="faint sm" data-testid={`monitoring-hold-own-${name}`}>
          also {hold === 'Paused' ? 'paused on its own' : 'held: position expired'} — resuming the
          replication alone will not start it
        </span>
      )}

      <span className="row" style={{ gap: 10 }}>
        <button
          type="button"
          className="btn-link"
          disabled={loading}
          onClick={() => setDialogOpen(true)}
          data-testid={`monitoring-manage-${name}`}
        >
          {hold === 'PositionExpired' ? 'Recover…' : 'Manage…'}
        </button>

        {pauseTogglable && (
          <button
            type="button"
            className="btn-link quiet"
            disabled={loading || setReadState.isPending}
            onClick={togglePause}
            data-testid={`monitoring-pause-${name}`}
            title="A per-table hold, finer than the replication's own pause — beside it, not instead of it."
          >
            <span className="row" style={{ gap: 4 }}>
              {paused ? <PlayIcon size={11} /> : <PauseIcon size={11} />}
              {paused ? 'Resume' : 'Pause'}
            </span>
          </button>
        )}
      </span>

      {dialogOpen && intent && (
        <MappingReadStateDialog
          mappingName={name}
          sourceLabel={resolvedSource ? `${resolvedSource.schema}.${resolvedSource.table}` : name}
          currentIntent={intent}
          currentHold={hold}
          offered={offered}
          hasVerificationChecks={(mapping?.verification?.length ?? 0) > 0}
          verificationHref={`/replications/${encodeURIComponent(replicationName)}/mappings/${encodeURIComponent(name)}/verification`}
          verificationPending={runVerification.isPending}
          onRunVerification={() => runVerification.mutate(name)}
          busy={setReadState.isPending}
          error={setReadState.error}
          onConfirm={(next) => setReadState.mutate(next, { onSuccess: () => setDialogOpen(false) })}
          onCancel={() => setDialogOpen(false)}
        />
      )}
    </span>
  )
}

/**
 * A mapping side, in `MappingsOverview`'s existing two-line form — `{connectionName} · {database}`
 * over `{schema}.{table}` — resolved through the same inheritance the server applies, so a mapping
 * that states neither endpoint shows the replication's rather than a blank.
 */
function Side({ task, spec, which }: {
  task: ReplicationTaskConfig | undefined
  spec: TableSpec | undefined
  which: 'source' | 'target'
}) {
  if (!spec) return <span className="faint">…</span>

  const resolved = resolveSide(task?.endpoints?.[which] ?? null, spec)
  return (
    <span style={{ display: 'flex', flexDirection: 'column', gap: 1, minWidth: 0 }}>
      <span className="name">{resolved.schema}.{resolved.table}</span>
      <span className="faint sm">
        {resolved.connectionName ? `${resolved.connectionName} · ${resolved.database}` : 'no endpoint set'}
      </span>
    </span>
  )
}

/**
 * The lag itself, in whichever of the four forms this mapping can honestly take.
 *
 * The exact and estimated forms differ by more than a word: the estimate is bracketed with a
 * ~ and carries a badge naming it, because the two numbers are not the same kind of thing and a
 * reader scanning a column of them should not have to read a label to notice.
 */
function LagCell({ name, lag }: { name: string; lag: MappingLag | undefined }) {
  if (!lag) return <span className="faint" data-testid={`monitoring-lag-${name}`}>…</span>

  const state = lagStateOf(lag)
  const behind = lag.versionsBehind === null
    ? null
    : <span className="faint sm">{lag.versionsBehind.toLocaleString()} version{lag.versionsBehind === 1 ? '' : 's'} behind</span>

  return (
    <span
      style={{ display: 'flex', flexDirection: 'column', gap: 2 }}
      data-testid={`monitoring-lag-${name}`}
    >
      {state === 'exact' && (
        <span className="row" style={{ gap: 6 }}>
          <span style={{ font: '500 13px var(--ui)', color: 'var(--ink)' }}>{formatLag(lag.exactLagMs!)}</span>
          <span className="badge badge-accent">exact</span>
        </span>
      )}

      {state === 'estimated' && (
        <span className="row" style={{ gap: 6 }}>
          <span style={{ font: '500 13px var(--ui)', color: 'var(--ink-4)' }}>~{formatLag(lag.estimatedLagMs!)}</span>
          <span className="badge badge-primary">estimated</span>
        </span>
      )}

      {/* Never a dash. A reader with no lag capability at all is a permanent answer — "do not
          expect a number here" — and the reader kind is named so it is clear which mechanism is
          saying so. */}
      {state === 'not-applicable' && (
        <span className="faint">not applicable <span className="mono sm">{lag.readerKind}</span></span>
      )}

      {/* Also never a dash, and deliberately worded as a wait rather than as an absence: this
          mapping's mechanism does report lag, and will, once it has run or once the gate has
          written enough history to place it. */}
      {state === 'no-data' && <span className="faint">no data yet</span>}

      {behind}

      {/* What the figure above is measured *against*, which is the question every lag number
          invites and none of them answered until phase 88. Rendered for every state that consulted
          a reading, including the ones with no figure: "we last saw the source ten seconds ago and
          still cannot place this mapping" and "we have not looked in an hour" are different
          problems, and only the second is about the poller. */}
      {lag.asOfUtc && (
        <span
          className="faint sm"
          data-testid={`monitoring-asof-${name}`}
          title={
            `The source's own position as of ${new Date(lag.asOfUtc).toLocaleString()} — the ` +
            'reading this lag is the distance from. Not the current time, and not when this page ' +
            'last refreshed.'
          }
        >
          as of {new Date(lag.asOfUtc).toLocaleTimeString()}
        </span>
      )}
    </span>
  )
}

function Figure({ label, value, testId }: { label: string; value: string; testId: string }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }} data-testid={testId}>
      <span style={{ font: '500 10.5px var(--ui)', color: 'var(--ink-4)', textTransform: 'uppercase', letterSpacing: '.04em' }}>
        {label}
      </span>
      <span style={{ font: '500 13px var(--ui)', color: 'var(--ink)' }}>{value}</span>
    </div>
  )
}

/**
 * `lowest X · highest Y`, in `MetricsCard`'s Figure-string style.
 *
 * Null is not zero, and says so in words. The server excludes mappings with nothing to report from
 * the range entirely, so a replication where none of them can answer has no range — which is a
 * different statement from "caught up" and has to read like one.
 */
function rangeText(lowest: number | null, highest: number | null, isLoading: boolean): string {
  if (isLoading) return 'loading…'
  if (lowest === null || highest === null) return 'no mapping reports lag yet'
  if (lowest === highest) return formatLag(lowest)
  return `lowest ${formatLag(lowest)} · highest ${formatLag(highest)}`
}
