import { useState } from 'react'
import { AppShell } from '../components/AppShell'
import { ErrorBanner } from '../components/ErrorBanner'
import { EditableValue } from '../components/EditableValue'
import { useIsAdmin } from '../components/useIsAdmin'
import { useAdminConfig, useSetAdminConfig, useSetAdminConfigSecret } from '../api/hooks'
import type { AdminConfigEntry } from '../api/types'

const COLUMNS = '1.7fr 1.9fr 1fr 1.3fr'
const STATE_CONNECTION_STRING_KEY = 'DataSync:StateConnectionString'

/**
 * Every DataSync:* key CONFIG.md documents, its live effective value and source, and — for the keys
 * datasync.config.yaml's writer can actually address — a way to change it (phase 81).
 *
 * Nothing here takes effect in the running process until it restarts: `ApiOptions` (and the sibling
 * `AuthOptions`/`PasskeyOptions`) are resolved once at startup, so a save or an adopt only changes what
 * the *next* start will read. The banner below says so rather than letting a save look like it just
 * took effect.
 */
export function AdminConfigPage() {
  const isAdmin = useIsAdmin()
  const { data: entries, isLoading, error } = useAdminConfig()
  const setValue = useSetAdminConfig()
  const setSecret = useSetAdminConfigSecret()
  const [restartNeeded, setRestartNeeded] = useState(false)
  const [mutationError, setMutationError] = useState<unknown>(null)

  // The API enforces this for real (every endpoint here is Policies.Admin) — this is only about not
  // showing a Viewer a screen of edit controls that would 403 the moment they were used.
  if (!isAdmin) {
    return (
      <AppShell crumbs={[{ label: 'Admin' }]}>
        <div className="pane">
          <div className="empty">This screen is for administrators.</div>
        </div>
      </AppShell>
    )
  }

  const save = async (key: string, value: string) => {
    setMutationError(null)
    try {
      await setValue.mutateAsync({ key, value })
      setRestartNeeded(true)
    } catch (err) {
      setMutationError(err)
    }
  }

  const saveSecret = async (key: string, value: string) => {
    setMutationError(null)
    try {
      await setSecret.mutateAsync({ key, value })
      setRestartNeeded(true)
    } catch (err) {
      setMutationError(err)
    }
  }

  return (
    <AppShell crumbs={[{ label: 'Admin' }]}>
      <div className="pane">
        <div className="page-head">
          <h1 className="page-title">Configuration</h1>
          <span className="page-note">
            Every DataSync:* setting this build reads, where its current value comes from, and what can
            be changed here. See CONFIG.md for the full reference.
          </span>
        </div>

        {restartNeeded && (
          <div className="banner" data-testid="restart-required-banner">
            <span className="mark">!</span>
            <span>
              A value was changed. This process resolves its configuration once at startup, so nothing
              here takes effect until DataSync restarts.
            </span>
          </div>
        )}

        <ErrorBanner error={error ?? mutationError} />

        <div className="card flush" data-testid="admin-config-table">
          <div className="grid-head" style={{ gridTemplateColumns: COLUMNS, gap: 14 }}>
            <span>Key</span><span>Value</span><span>Source</span><span />
          </div>
          {isLoading && <div className="empty">Loading…</div>}
          {(entries ?? []).map((entry) => (
            <Row
              key={entry.key}
              entry={entry}
              onSave={save}
              onSaveSecret={entry.key === STATE_CONNECTION_STRING_KEY ? saveSecret : undefined}
              busy={setValue.isPending || setSecret.isPending}
            />
          ))}
        </div>
      </div>
    </AppShell>
  )
}

function Row({ entry, onSave, onSaveSecret, busy }: {
  entry: AdminConfigEntry
  onSave: (key: string, value: string) => void
  /** Present only for StateConnectionString — the one key with a secret alongside its plain value. */
  onSaveSecret?: (key: string, value: string) => void
  busy: boolean
}) {
  const shortKey = entry.key.replace(/^DataSync:/, '')

  return (
    <div className="grid-row" style={{ gridTemplateColumns: COLUMNS, gap: 14, alignItems: 'start' }} data-testid={`admin-config-row-${shortKey}`}>
      <div>
        <span className="mono">{shortKey}</span>
        <div className="hint">{entry.description}</div>
      </div>

      <div>
        {entry.masked ? (
          <span className="faint" data-testid={`admin-config-value-${shortKey}`}>
            hidden — carries a credential from its source, never shown here
          </span>
        ) : entry.editable ? (
          <EditableValue
            value={entry.value}
            onChange={(next) => next && onSave(entry.key, next)}
            label={shortKey}
            monospace
            testId={`admin-config-value-${shortKey}`}
          />
        ) : (
          <span className="mono" data-testid={`admin-config-value-${shortKey}`}>
            {entry.value ?? <span className="faint">not set</span>}
          </span>
        )}

        {onSaveSecret && <SetSecretControl onSet={(value) => onSaveSecret(entry.key, value)} busy={busy} />}
      </div>

      <span className="dim" data-testid={`admin-config-source-${shortKey}`}>{entry.source}</span>

      <span>
        {entry.canAdopt && (
          <button
            type="button"
            className="btn-link"
            disabled={busy}
            title="Write this value into datasync.config.yaml, making it the value used after a restart"
            onClick={() => entry.value && onSave(entry.key, entry.value)}
            data-testid={`admin-config-adopt-${shortKey}`}
          >
            Adopt into file
          </button>
        )}
      </span>
    </div>
  )
}

/** StateConnectionString's password — a separate control from the value above, because it writes
 * through the secret store rather than datasync.config.yaml and the page never learns what it holds. */
function SetSecretControl({ onSet, busy }: { onSet: (value: string) => void; busy: boolean }) {
  const [editing, setEditing] = useState(false)
  const [draft, setDraft] = useState('')

  if (!editing) {
    return (
      <div>
        <button type="button" className="btn-link quiet" onClick={() => setEditing(true)} data-testid="admin-config-set-secret">
          Set password…
        </button>
      </div>
    )
  }

  const commit = () => {
    if (!draft) return
    onSet(draft)
    setDraft('')
    setEditing(false)
  }

  return (
    <div className="row" style={{ gap: 6, marginTop: 4 }}>
      <input
        type="password"
        className="input sm"
        autoFocus
        value={draft}
        placeholder="New password"
        aria-label="StateConnectionString password"
        onChange={(e) => setDraft(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Enter') { e.preventDefault(); commit() }
          if (e.key === 'Escape') { setDraft(''); setEditing(false) }
        }}
        data-testid="admin-config-secret-input"
      />
      <button type="button" className="btn btn-sm" disabled={!draft || busy} onClick={commit} data-testid="admin-config-secret-save">
        Save
      </button>
      <button type="button" className="btn btn-sm" onClick={() => { setDraft(''); setEditing(false) }}>
        Cancel
      </button>
    </div>
  )
}
