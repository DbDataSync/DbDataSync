import { ErrorBanner } from '../../components/ErrorBanner'
import { RefreshCountdown } from '../../components/RefreshCountdown'
import { ShellActions } from '../../components/ShellActions'
import { resolveSide } from '../../api/resolveEndpoint'
import { useReplication, useReplicationLag, useTableMappingDetails, useTableMappings } from '../../api/hooks'
import type { MappingLag, ReplicationTaskConfig, TableSpec } from '../../api/types'
import { formatLag, lagStateOf } from './lag'

const COLUMNS = '1fr 1fr 190px'

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
 */
export function MonitoringPanel({ replicationName }: { replicationName: string }) {
  const { data: task } = useReplication(replicationName)
  const { data: names } = useTableMappings(replicationName)
  const { data: lag, isLoading, error, dataUpdatedAt } = useReplicationLag(replicationName)

  // The mapping configs the sidebar has already loaded, for the source/target each row names. The
  // lag endpoint answers "how far behind", not "behind what" — which is config, and is already here.
  const mappings = useTableMappingDetails(replicationName, names)

  return (
    <div className="pane">
      <ShellActions>
        <RefreshCountdown label="Lag" dataUpdatedAt={dataUpdatedAt} testId="lag-countdown" />
      </ShellActions>

      <ErrorBanner error={error} />

      <div className="card" data-testid="monitoring-range">
        <div className="card-head tight">
          <span className="card-title sm">Reader lag</span>
          <span className="card-note">
            measured against each source's own last-known position, never against the clock
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
          <span>Source</span><span>Target</span><span>Lag</span>
        </div>

        {(names ?? []).map((name, i) => (
          <MappingLagRow
            key={name}
            name={name}
            task={task}
            source={mappings[i]?.data?.sources[0]}
            target={mappings[i]?.data?.targets[0]}
            lag={lag?.mappings[name]}
          />
        ))}

        {names?.length === 0 && <div className="empty">This replication has no table mappings yet.</div>}
        {!names && <div className="empty">Loading…</div>}
      </div>
    </div>
  )
}

function MappingLagRow({ name, task, source, target, lag }: {
  name: string
  task: ReplicationTaskConfig | undefined
  source: TableSpec | undefined
  target: TableSpec | undefined
  lag: MappingLag | undefined
}) {
  return (
    <div
      className="grid-row tall"
      style={{ gridTemplateColumns: COLUMNS, gap: 14 }}
      data-testid={`monitoring-row-${name}`}
      data-lag-state={lag ? lagStateOf(lag) : undefined}
    >
      <Side task={task} spec={source} which="source" />
      <Side task={task} spec={target} which="target" />
      <LagCell name={name} lag={lag} />
    </div>
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
