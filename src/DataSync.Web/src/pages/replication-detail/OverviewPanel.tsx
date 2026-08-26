import { useEffect, useState } from 'react'
import { ErrorBanner } from '../../components/ErrorBanner'
import { useReplication, useUpsertReplication } from '../../api/hooks'
import type { ReplicationTaskConfig, ScheduleMode } from '../../api/types'
import { READER_KINDS, CACHE_KINDS, WRITER_KINDS } from '../../driverKinds'

export function OverviewPanel({ replicationName }: { replicationName: string }) {
  const { data: task, error } = useReplication(replicationName)
  const upsert = useUpsertReplication()
  const [draft, setDraft] = useState<ReplicationTaskConfig | null>(null)

  useEffect(() => {
    if (task && !draft) setDraft(task)
  }, [task, draft])

  if (!draft) {
    return (
      <div className="card">
        <ErrorBanner error={error} />
        <p className="muted">Loading…</p>
      </div>
    )
  }

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    await upsert.mutateAsync({ name: replicationName, task: draft })
  }

  return (
    <div className="card">
      <h2>Settings</h2>
      <ErrorBanner error={upsert.error} />
      <form onSubmit={save} className="stack">
        <label className="row">
          <input
            type="checkbox"
            checked={draft.enabled}
            onChange={(e) => setDraft({ ...draft, enabled: e.target.checked })}
          />
          Enabled
        </label>

        <div className="form-grid">
          <div className="form-field">
            <label>Schedule Mode</label>
            <select
              value={draft.scheduling.mode}
              onChange={(e) =>
                setDraft({
                  ...draft,
                  scheduling: { ...draft.scheduling, mode: e.target.value as ScheduleMode },
                })
              }
            >
              <option value="Continuous">Continuous</option>
              <option value="Periodic">Periodic (cron)</option>
            </select>
          </div>
          {draft.scheduling.mode === 'Continuous' ? (
            <div className="form-field">
              <label>Frequency (seconds)</label>
              <input
                type="number"
                min={1}
                value={draft.scheduling.frequencySeconds ?? 60}
                onChange={(e) =>
                  setDraft({ ...draft, scheduling: { ...draft.scheduling, frequencySeconds: Number(e.target.value) } })
                }
              />
            </div>
          ) : (
            <div className="form-field">
              <label>Cron Expression</label>
              <input
                value={draft.scheduling.cronExpression ?? ''}
                onChange={(e) => setDraft({ ...draft, scheduling: { ...draft.scheduling, cronExpression: e.target.value } })}
              />
            </div>
          )}

          <div className="form-field">
            <label>Reader</label>
            <select
              value={draft.changeProcessing.reader.kind}
              onChange={(e) =>
                setDraft({
                  ...draft,
                  changeProcessing: {
                    ...draft.changeProcessing,
                    reader: { ...draft.changeProcessing.reader, kind: e.target.value },
                  },
                })
              }
            >
              {READER_KINDS.map((k) => (
                <option key={k} value={k}>
                  {k}
                </option>
              ))}
            </select>
          </div>
          <div className="form-field">
            <label>Staging</label>
            <select
              value={draft.changeProcessing.cache.kind}
              onChange={(e) =>
                setDraft({
                  ...draft,
                  changeProcessing: { ...draft.changeProcessing, cache: { ...draft.changeProcessing.cache, kind: e.target.value } },
                })
              }
            >
              {CACHE_KINDS.map((k) => (
                <option key={k} value={k}>
                  {k}
                </option>
              ))}
            </select>
          </div>
          <div className="form-field">
            <label>Writer</label>
            <select
              value={draft.changeProcessing.writer.kind}
              onChange={(e) =>
                setDraft({
                  ...draft,
                  changeProcessing: { ...draft.changeProcessing, writer: { ...draft.changeProcessing.writer, kind: e.target.value } },
                })
              }
            >
              {WRITER_KINDS.map((k) => (
                <option key={k} value={k}>
                  {k}
                </option>
              ))}
            </select>
          </div>
        </div>

        <div className="form-actions">
          <button type="submit" className="btn btn-primary" disabled={upsert.isPending} data-testid="save-settings-button">
            {upsert.isPending ? 'Saving…' : 'Save Settings'}
          </button>
        </div>
      </form>
    </div>
  )
}
