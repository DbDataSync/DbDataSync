import { useState } from 'react'
import { ErrorBanner } from '../../components/ErrorBanner'
import { DiffViewer } from '../../components/DiffViewer'
import {
  useReplicationCommitDiff,
  useReplicationHistory,
  useReplicationRestorePreview,
  useRestoreReplication,
} from '../../api/hooks'
import type { ConfigDiff, ConfigFileChange } from '../../api/types'

const COLUMNS = '1.1fr .8fr 2.4fr .6fr auto'

/** The last segment, which is the mapping's name for everything but `task.yaml` — the full
 * repository path is noise once you are already looking at one replication's history. */
function fileLabel(change: ConfigFileChange) {
  return change.path.split('/').pop() ?? change.path
}

/**
 * "Version Control" in the design; the same auto-commit log it has always been, with phase 35's two
 * actions on it.
 *
 * The mockup's "Diff vs production" is still omitted, and still for the reason it always was: it needs
 * more than one environment to diff against, which is a product decision rather than a missing
 * endpoint. Diffing against a chosen commit is the part that is meaningful today.
 */
export function HistoryPanel({ replicationName }: { replicationName: string }) {
  const { data: commits, isLoading, error } = useReplicationHistory(replicationName)
  const [viewing, setViewing] = useState<string | null>(null)
  const [restoring, setRestoring] = useState<string | null>(null)

  return (
    <div className="pane">
      <div className="page-head">
        <h2 className="page-title">Config history</h2>
        <span className="page-note">Every config change is an auto-commit. This is the replication's git log.</span>
      </div>

      <ErrorBanner error={error} />

      <div className="card flush" data-testid="history-table">
        <div className="grid-head" style={{ gridTemplateColumns: COLUMNS, gap: 14 }}>
          <span>When</span><span>Author</span><span>Message</span><span>Commit</span><span />
        </div>
        {isLoading && <div className="empty">Loading…</div>}
        {commits?.length === 0 && <div className="empty">No history yet.</div>}
        {(commits ?? []).map((c) => (
          <div key={c.sha}>
            <div className="grid-row short" style={{ gridTemplateColumns: COLUMNS, gap: 14 }} data-testid="history-row">
              <span className="dim">{new Date(c.whenUtc).toLocaleString()}</span>
              <span className="dim" style={{ fontFamily: 'var(--ui)' }}>{c.authorName}</span>
              <span style={{ fontFamily: 'var(--ui)', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{c.message}</span>
              <span style={{ color: 'var(--accent)' }}>{c.sha.slice(0, 8)}</span>
              <span style={{ display: 'flex', gap: 6 }}>
                <button
                  type="button"
                  className="btn btn-sm"
                  data-testid="view-changes"
                  onClick={() => setViewing(viewing === c.sha ? null : c.sha)}
                >
                  {viewing === c.sha ? 'Hide changes' : 'View changes'}
                </button>
                <button
                  type="button"
                  className="btn btn-sm"
                  data-testid="restore-here"
                  onClick={() => setRestoring(c.sha)}
                >
                  Restore to here
                </button>
              </span>
            </div>
            {viewing === c.sha && <CommitChanges replicationName={replicationName} sha={c.sha} />}
          </div>
        ))}
      </div>

      {restoring && (
        <RestoreConfirmation
          replicationName={replicationName}
          sha={restoring}
          onClose={() => setRestoring(null)}
        />
      )}
    </div>
  )
}

/** What one commit changed. Read-only — editing a diff is not a thing this offers, and the viewer it
 * opens has no way to. */
function CommitChanges({ replicationName, sha }: { replicationName: string; sha: string }) {
  const { data, isLoading, error } = useReplicationCommitDiff(replicationName, sha)
  return (
    <div className="diff-panel">
      <DiffPanes diff={data} isLoading={isLoading} error={error} testIdPrefix="commit" />
    </div>
  )
}

/** One file at a time, picked from the list of what changed — a commit touching six mappings is six
 * documents, not one scroll. */
function DiffPanes({
  diff, isLoading, error, testIdPrefix,
}: { diff?: ConfigDiff; isLoading: boolean; error: unknown; testIdPrefix: string }) {
  const [selected, setSelected] = useState(0)

  if (isLoading) return <div className="empty">Loading changes…</div>
  if (error) return <ErrorBanner error={error} />
  if (!diff || diff.changes.length === 0) return <div className="empty">Nothing changed here in this commit.</div>

  const change = diff.changes[Math.min(selected, diff.changes.length - 1)]

  return (
    <div data-testid={`${testIdPrefix}-diff`}>
      <div className="diff-files">
        {diff.changes.map((c, i) => (
          <button
            key={c.path}
            type="button"
            className={`btn btn-sm${i === selected ? ' btn-primary' : ''}`}
            data-testid="diff-file"
            onClick={() => setSelected(i)}
          >
            <span className={`diff-kind ${c.kind.toLowerCase()}`}>{c.kind[0]}</span>
            {fileLabel(c)}
          </button>
        ))}
      </div>
      {change.before === null && change.after === null ? (
        <div className="empty" data-testid="diff-truncated">
          This file is too large to show here — the change is real and is listed above, only its
          content was left out.
        </div>
      ) : (
        <DiffViewer before={change.before} after={change.after} testId={`${testIdPrefix}-diff-editor`} />
      )}
    </div>
  )
}

/**
 * What a restore would do, before it does it.
 *
 * The preview is deliberately **not** the commit's own patch. A commit that only renamed a column may,
 * restored today, delete three mappings created since — none of which appear in that commit's patch.
 * So this asks a different endpoint, anchored at the current config rather than at the commit's
 * parent, and shows what will actually change.
 */
function RestoreConfirmation({
  replicationName, sha, onClose,
}: { replicationName: string; sha: string; onClose: () => void }) {
  const { data, isLoading, error } = useReplicationRestorePreview(replicationName, sha)
  const restore = useRestoreReplication(replicationName)

  const removed = data?.changes.filter((c) => c.kind === 'Deleted') ?? []
  const added = data?.changes.filter((c) => c.kind === 'Added') ?? []
  const changed = data?.changes.filter((c) => c.kind === 'Modified' || c.kind === 'Renamed') ?? []
  const nothingToDo = data?.changes.length === 0

  return (
    <div className="modal-backdrop" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose() }}>
      <div className="modal diff" role="dialog" aria-modal="true" data-testid="restore-confirm">
        <div className="card-head">
          <span className="card-title">Restore to {sha.slice(0, 8)}</span>
        </div>
        <div className="card-body">
          <ErrorBanner error={error} />
          <ErrorBanner error={restore.error} />

          {isLoading && <div className="empty">Working out what would change…</div>}

          {nothingToDo && (
            <span className="hint">
              This replication's config is already exactly as it was at that commit, so there is
              nothing to restore and nothing would be recorded.
            </span>
          )}

          {data && data.changes.length > 0 && !restore.data && (
            <>
              <span className="hint">
                This puts the config back the way it was and records it as a <strong>new</strong>{' '}
                commit. Nothing is removed from the log, and the restore is itself restorable.
              </span>
              <ul className="restore-summary" data-testid="restore-summary">
                {removed.length > 0 && <li><strong>{removed.length}</strong> removed — {removed.map(fileLabel).join(', ')}</li>}
                {added.length > 0 && <li><strong>{added.length}</strong> brought back — {added.map(fileLabel).join(', ')}</li>}
                {changed.length > 0 && <li><strong>{changed.length}</strong> changed — {changed.map(fileLabel).join(', ')}</li>}
              </ul>
              <span className="hint">
                A replication that is running is not stopped for this. A pass already under way
                finishes; a queued item for a mapping this removes fails that item, not the worker.
                Pause the replication first if that matters.
              </span>
              <DiffPanes diff={data} isLoading={false} error={null} testIdPrefix="restore" />
            </>
          )}

          {restore.data && (
            <div className="banner" data-testid="restore-done">
              <div>
                {restore.data.commitSha
                  ? `Restored, recorded as commit ${restore.data.commitSha.slice(0, 8)}.`
                  : 'Nothing to restore — the config was already in that state.'}
              </div>
              {restore.data.warnings.map((w) => <div key={w} className="hint warn">{w}</div>)}
            </div>
          )}

          <div className="row" style={{ gap: 8, justifyContent: 'flex-end' }}>
            <button type="button" className="btn btn-sm" onClick={onClose} data-testid="restore-cancel">
              {restore.data ? 'Close' : 'Cancel'}
            </button>
            {!restore.data && (
              <button
                type="button"
                className="btn btn-sm btn-primary"
                data-testid="restore-confirm-go"
                disabled={restore.isPending || isLoading || nothingToDo}
                onClick={() => restore.mutate(sha)}
              >
                {restore.isPending ? 'Restoring…' : 'Restore'}
              </button>
            )}
          </div>
        </div>
      </div>
    </div>
  )
}
