import { useState } from 'react'
import { useRefreshMappingMetadata } from '../../api/hooks'
import { ErrorBanner } from '../../components/ErrorBanner'
import type { MetadataRefreshResult, MetadataRefreshSide, TableMappingConfig } from '../../api/types'

interface Props {
  replicationName: string
  /** Undefined for a mapping that has not been saved yet — there is nothing on disk to refresh. */
  existing: TableMappingConfig | undefined
}

/**
 * The mapping's cached picture of its two tables, and the one action that updates it — phase 90.
 *
 * **Why a cache exists at all, given that `ColumnMapping` deliberately stores no types.** Every
 * reader and every writer asks the source or target for its column types, keys and nullability on
 * every pass, because the mapping does not record them — and it does not record them on purpose,
 * since freezing an inference risks building a `MERGE` against a primary key that has since gone.
 * The audit that produced this phase confirmed both halves of that. Caching the shape anyway is only
 * safe if going stale is something an operator can see and decide about, which is what this card is:
 * the age of the picture, and the button that takes a new one.
 *
 * **Nothing reads these fields yet.** Refreshing changes what is stored and changes no behaviour —
 * every reader and writer still queries live, exactly as before. Switching them onto the cache is a
 * later phase, reviewed on its own.
 *
 * **One button for both sides, not two.** The question an operator has is "is my picture of this
 * mapping current", not "is my picture of the source current" — and one press is one endpoint, one
 * save and one commit where two would be two of each, on a pair of tables that are only interesting
 * relative to one another. The *result* is still reported per side, because that is where an answer
 * differs: a source that read fine beside a target that provisioning has yet to create.
 */
export function CachedMetadataCard({ replicationName, existing }: Props) {
  const refresh = useRefreshMappingMetadata(replicationName, existing?.name)
  const [result, setResult] = useState<MetadataRefreshResult | null>(null)

  // The refresh's own answer wins once there is one: it is what the server just saved, and the
  // `existing` prop is the draft's copy from before the button was pressed.
  const mapping = result?.mapping ?? existing
  const capturedUtc = mapping?.columnsCapturedUtc ?? null

  if (!existing) {
    return (
      <div className="card">
        <div className="card-head tight"><span className="card-title sm">Cached metadata</span></div>
        <div className="empty" data-testid="cached-metadata-after-save-hint">
          Save the mapping and its source and target columns are captured here — from the lists this
          editor already loaded, not a second query.
        </div>
      </div>
    )
  }

  return (
    <div className="card" data-testid="cached-metadata-card">
      <div className="card-head tight">
        <span className="card-title sm">Cached metadata</span>
        <span className="card-note">
          {capturedUtc === null
            ? <>never captured — nothing yet reads these, and Refresh is the only thing that writes them</>
            : <>captured {formatAgo(capturedUtc)} ago · only Refresh updates it, never a run and never a save</>}
        </span>
        <button
          type="button"
          className="btn btn-sm spacer"
          disabled={refresh.isPending}
          data-testid="refresh-metadata-button"
          onClick={async () => setResult(await refresh.mutateAsync())}
        >
          {refresh.isPending ? 'Refreshing…' : 'Refresh metadata'}
        </button>
      </div>

      <ErrorBanner error={refresh.error} />

      <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
        <SideRow
          label="Source"
          columns={mapping?.sourceColumns?.length ?? 0}
          outcome={result?.source ?? null}
        />
        <SideRow
          label="Target"
          columns={mapping?.targetColumns?.length ?? 0}
          outcome={result?.target ?? null}
        />
      </div>
    </div>
  )
}

/**
 * One side's cached column count, and — once a refresh has run — what that refresh did to it.
 *
 * The outcome is stated in names rather than counts. "2 changed" is a number an operator has to go
 * and investigate; `Region, Currency` is the investigation.
 */
function SideRow({ label, columns, outcome }: {
  label: string
  columns: number
  outcome: MetadataRefreshSide | null
}) {
  return (
    <div className="row" style={{ gap: 10, alignItems: 'baseline' }} data-testid={`cached-metadata-${label.toLowerCase()}`}>
      <span style={{ font: '500 11.5px var(--ui)', color: 'var(--ink-4)', minWidth: 54 }}>{label}</span>
      <span className="mono">{columns} {columns === 1 ? 'column' : 'columns'}</span>
      {outcome !== null && (
        outcome.refreshed
          ? <span className="faint" data-testid={`cached-metadata-${label.toLowerCase()}-outcome`}>{describe(outcome)}</span>
          : (
            // A side that could not be read kept its cached columns rather than being emptied, and
            // saying which it was matters: an unreachable source and a target that does not exist
            // yet call for completely different next actions.
            <span className="badge" title={outcome.unavailable ?? undefined} data-testid={`cached-metadata-${label.toLowerCase()}-outcome`}>
              NOT READ
            </span>
          )
      )}
    </div>
  )
}

function describe(side: MetadataRefreshSide): string {
  const parts = [
    side.added.length > 0 ? `added ${side.added.join(', ')}` : null,
    side.removed.length > 0 ? `removed ${side.removed.join(', ')}` : null,
    side.changed.length > 0 ? `changed ${side.changed.join(', ')}` : null,
  ].filter((p): p is string => p !== null)

  // "No change" is the answer an operator most wants stated plainly, and an empty span would read as
  // the refresh having not happened.
  return parts.length === 0 ? 'no change' : parts.join(' · ')
}

function formatAgo(iso: string): string {
  const seconds = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000)
  if (seconds < 90) return `${Math.round(seconds)}s`
  if (seconds < 5400) return `${Math.round(seconds / 60)}m`
  if (seconds < 172_800) return `${Math.round(seconds / 3600)}h`
  return `${Math.round(seconds / 86_400)}d`
}
