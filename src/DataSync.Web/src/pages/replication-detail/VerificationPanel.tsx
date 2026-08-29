import { Link, useOutletContext, useParams } from 'react-router-dom'
import { ErrorBanner } from '../../components/ErrorBanner'
import {
  useDeleteVerificationResult, useRunVerification, useTableMapping, useUpsertTableMapping,
  useVerificationResults,
} from '../../api/hooks'
import { ChecksCard } from './ChecksCard'
import type { VerificationCheckConfig } from '../../api/types'
import type { MappingsOutletContext } from './TableMappingsPanel'

const RESULT_COLUMNS = '1.2fr .8fr .8fr 1.2fr 150px'

/**
 * A mapping's checks, and what past runs of them said.
 *
 * **The results themselves are a click away, not rendered here.** A check over a large table produces
 * a row per group — millions of them — and this screen used to render the newest result inline the
 * moment it loaded. That locked the tab up for minutes and crashed some of them, on a screen whose
 * actual job is managing checks. Opening a result is now a decision, and the result arrives a page at
 * a time.
 */
export function VerificationPanel() {
  const { replicationName, base } = useOutletContext<MappingsOutletContext>()
  const { mappingName } = useParams<{ mappingName: string }>()

  const { data: results, error } = useVerificationResults(replicationName, mappingName)
  const { data: mapping } = useTableMapping(replicationName, mappingName)
  const upsert = useUpsertTableMapping(replicationName)
  const run = useRunVerification(replicationName)
  const remove = useDeleteVerificationResult(replicationName)

  // Saved through the mapping, because a check lives on the mapping. The whole config goes back, so
  // an edit here cannot quietly drop a field this screen does not render.
  const saveChecks = async (verification: VerificationCheckConfig[]) => {
    if (!mapping) return
    await upsert.mutateAsync({ mappingName: mapping.name, mapping: { ...mapping, verification } })
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }} data-testid="verification-panel">
      <div className="page-head">
        <h2 className="page-title mono">{mappingName}</h2>
        <span className="page-note">source and target compared, on demand</span>
        <div className="right">
          <Link className="btn" to={`${base}/${encodeURIComponent(mappingName!)}`}>Back to the mapping</Link>
          <button
            type="button"
            className="btn btn-primary"
            disabled={run.isPending}
            onClick={() => run.mutate(mappingName!)}
            data-testid="run-verification-button"
          >
            {run.isPending ? 'Queueing…' : 'Run checks'}
          </button>
        </div>
      </div>

      <ErrorBanner error={error ?? run.error ?? upsert.error ?? remove.error} />

      {mapping && <ChecksCard mapping={mapping} onSave={saveChecks} saving={upsert.isPending} />}

      <div className="card flush" data-testid="verification-results-list">
        <div className="card-head tight">
          <span className="card-title sm">Results</span>
          <span className="card-note">what past runs of these checks found</span>
        </div>

        <div className="grid-head" style={{ gridTemplateColumns: RESULT_COLUMNS, gap: 14, height: 29 }}>
          <span>Check</span><span>Groups</span><span>Differing</span><span>Run</span><span />
        </div>
        {results?.length === 0 && (
          <div className="empty">No results yet — run the checks to produce one.</div>
        )}
        {(results ?? []).map((r) => (
          <div
            key={r.id}
            className="grid-row"
            style={{ gridTemplateColumns: RESULT_COLUMNS, gap: 14 }}
            data-testid={`verification-result-${r.checkName}`}
          >
            <span className="name">{r.checkName}</span>
            <span className="dim">{r.groupsCompared.toLocaleString()}</span>
            <span className="status">
              <span className={`dot ${r.differingGroups === 0 ? 'dot-ok' : 'dot-bad'}`} />
              {r.differingGroups.toLocaleString()}
            </span>
            <span className="dim">{new Date(r.completedAtUtc).toLocaleString()}</span>
            <span className="row" style={{ gap: 10, justifySelf: 'end' }}>
              <Link
                className="btn-link"
                to={`${base}/${encodeURIComponent(mappingName!)}/verification/${r.id}`}
                data-testid={`open-result-${r.id}`}
              >
                Open
              </Link>
              {/* A result is an artifact on disk. Somebody who has read one, or ran the wrong check
                  over a large table, needs a way to be rid of it that is not "find the file". */}
              <button
                type="button"
                className="btn-link quiet"
                disabled={remove.isPending}
                onClick={() => remove.mutate(r.id)}
                data-testid={`delete-result-${r.id}`}
              >
                Delete
              </button>
            </span>
          </div>
        ))}
      </div>
    </div>
  )
}
