import { useEffect, useState } from 'react'
import { ErrorBanner } from '../../components/ErrorBanner'
import { KindSelect } from '../../components/KindSelect'
import { readerOptions, stagingOptions, writerOptions } from '../../components/kindOptions'
import { JsonOptionsEditor } from '../../components/JsonOptionsEditor'
import { useReplication, useReplicationCapabilities, useUpsertReplication } from '../../api/hooks'
import type { ReplicationTaskConfig, ScheduleMode } from '../../api/types'

type Role = 'reader' | 'cache' | 'writer'

export function OverviewPanel({ replicationName }: { replicationName: string }) {
  const { data: task, error } = useReplication(replicationName)
  const capabilities = useReplicationCapabilities(replicationName)
  const upsert = useUpsertReplication()
  const [draft, setDraft] = useState<ReplicationTaskConfig | null>(null)
  // Roles whose Options textarea currently holds unparseable JSON. Save is blocked while any is
  // invalid rather than saving a stale value the editor is no longer showing.
  const [invalidOptions, setInvalidOptions] = useState<Role[]>([])

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

  const setRole = (role: Role, patch: object) =>
    setDraft({
      ...draft,
      changeProcessing: { ...draft.changeProcessing, [role]: { ...draft.changeProcessing[role], ...patch } },
    })

  const setOptions = (role: Role) => (options: Record<string, string> | null) => {
    setInvalidOptions((prev) => (options === null ? [...new Set([...prev, role])] : prev.filter((r) => r !== role)))
    if (options !== null) setRole(role, { options })
  }

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    await upsert.mutateAsync({ name: replicationName, task: draft })
  }

  return (
    <div className="card">
      <h2>Settings</h2>
      <ErrorBanner error={upsert.error ?? capabilities.error} />
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

          <KindSelect
            label="Reader"
            value={draft.changeProcessing.reader.kind}
            options={readerOptions(capabilities.readers)}
            onChange={(kind) => setRole('reader', { kind })}
            testId="reader-kind-select"
          />
          <JsonOptionsEditor
            label="Reader Options"
            value={draft.changeProcessing.reader.options}
            onChange={setOptions('reader')}
            testId="reader-options-editor"
          />

          <KindSelect
            label="Staging"
            value={draft.changeProcessing.cache.kind}
            options={stagingOptions(capabilities.stagingProviders)}
            onChange={(kind) => setRole('cache', { kind })}
            testId="cache-kind-select"
          />
          <JsonOptionsEditor
            label="Staging Options"
            value={draft.changeProcessing.cache.options}
            onChange={setOptions('cache')}
            testId="cache-options-editor"
          />

          <KindSelect
            label="Writer"
            value={draft.changeProcessing.writer.kind}
            options={writerOptions(capabilities.writers)}
            onChange={(kind) => setRole('writer', { kind })}
            testId="writer-kind-select"
          />
          <JsonOptionsEditor
            label="Writer Options"
            value={draft.changeProcessing.writer.options}
            onChange={setOptions('writer')}
            testId="writer-options-editor"
          />
        </div>

        <div className="form-actions">
          <button
            type="submit"
            className="btn btn-primary"
            disabled={upsert.isPending || invalidOptions.length > 0}
            data-testid="save-settings-button"
          >
            {upsert.isPending ? 'Saving…' : 'Save Settings'}
          </button>
          {invalidOptions.length > 0 && (
            <p className="field-error">Fix the invalid JSON in {invalidOptions.join(', ')} options before saving.</p>
          )}
        </div>
      </form>
    </div>
  )
}
