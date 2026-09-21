import { useState } from 'react'
import { Link } from 'react-router-dom'
import { AdminTabs } from '../components/AdminTabs'
import { AppShell } from '../components/AppShell'
import { ErrorBanner } from '../components/ErrorBanner'
import { EditableValue } from '../components/EditableValue'
import { RestartRequiredBanner } from '../components/RestartRequiredBanner'
import { CheckIcon, HelpIcon } from '../components/icons'
import { useIsAdmin } from '../components/useIsAdmin'
import { useAdminConfig, useRestartRequired, useSetAdminConfig, useSetAdminConfigSecret } from '../api/hooks'
import type { AdminConfigEntry } from '../api/types'

const COLUMNS = '1.3fr 1.6fr 1.3fr 1.3fr'
const STATE_CONNECTION_STRING_KEY = 'DbDataSync:State:ConnectionString'
const NUMERIC = /^-?\d+(\.\d+)?$/

/** The first sentence, as a quick-read summary — the full description is one hover away on the
 * "?" icon, so the row itself only has to carry enough to place the key, not explain it fully. */
function shortDescription(description: string): string {
  const match = /^.*?[.!?](?=\s|$)/.exec(description.trim())
  return match ? match[0] : description
}

/** A value with its unit pill beside it, or just the value — a key can carry a unit
 * (RunRetentionDays' is "days") and still have a non-numeric or unset value nothing should be
 * attached to, so the check is on the actual value in front of you, not on the key alone. */
function ValueWithUnit({ value, unit, testId }: { value: string; unit: string | null; testId: string }) {
  return (
    <span className="row" style={{ gap: 6 }} data-testid={testId}>
      <span className="mono">{value}</span>
      {unit && NUMERIC.test(value) && <span className="unit-pill">{unit}</span>}
    </span>
  )
}

/**
 * Every DbDataSync:* key docs/configuration.md documents, its live effective value and source, and — for the keys
 * dbdatasync.config.yaml's writer can actually address — a way to change it (phase 81).
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
  // The server flag (below) is what actually persists across a reload or a different admin's tab —
  // this local flag only saves waiting on that query's own invalidate-triggered refetch to show the
  // banner the instant *this* tab's own save finishes.
  const [restartNeeded, setRestartNeeded] = useState(false)
  const { data: restartRequired } = useRestartRequired()
  const [mutationError, setMutationError] = useState<unknown>(null)

  // The API enforces this for real (every endpoint here is Policies.Admin) — this is only about not
  // showing a Viewer a screen of edit controls that would 403 the moment they were used.
  if (!isAdmin) {
    return (
      <AppShell crumbs={[{ label: 'Admin' }]} tabs={<AdminTabs />}>
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
    <AppShell crumbs={[{ label: 'Admin' }]} tabs={<AdminTabs />}>
      <div className="pane">
        <div className="page-head">
          <h1 className="page-title">Configuration</h1>
          <span className="page-note">
            Every DbDataSync:* setting this build reads, where its current value comes from, and what can
            be changed here. See the <Link to="/docs/configuration" data-testid="config-docs-link">configuration reference</Link> for
            the full reference.
          </span>
        </div>

        <RestartRequiredBanner show={restartNeeded || !!restartRequired?.required} />

        <ErrorBanner error={error ?? mutationError} />

        <div className="card flush" data-testid="admin-config-table">
          <div className="grid-head" style={{ gridTemplateColumns: COLUMNS, gap: 14 }}>
            <span>Key</span><span>Value</span><span>Running</span><span>Source</span>
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
  const shortKey = entry.key.replace(/^DbDataSync:/, '')
  const short = shortDescription(entry.description)
  const hasMore = short !== entry.description.trim()

  return (
    <div
      className="grid-row"
      style={{ gridTemplateColumns: COLUMNS, gap: 14, alignItems: 'start', height: 'auto', paddingTop: 10 }}
      data-testid={`admin-config-row-${shortKey}`}
    >
      <div>
        <span className="mono">{shortKey}</span>
        <div
          className="hint wrap row"
          style={{ gap: 4, marginBottom: 8 }}
          data-testid={`admin-config-description-${shortKey}`}
        >
          {hasMore && (
            <span
              title={entry.description}
              style={{ cursor: 'help', flexShrink: 0, display: 'inline-flex', alignItems: 'center' }}
            >
              <HelpIcon />
            </span>
          )}
          <span>{short}</span>
        </div>
      </div>

      <div>
        {entry.masked ? (
          <span className="faint" data-testid={`admin-config-value-${shortKey}`}>
            hidden — carries a credential from its source, never shown here
          </span>
        ) : entry.editable ? (
          <span className="row" style={{ gap: 6 }}>
            <EditableValue
              value={entry.value}
              onChange={(next) => next && onSave(entry.key, next)}
              label={shortKey}
              monospace
              testId={`admin-config-value-${shortKey}`}
            />
            {entry.unit && entry.value && NUMERIC.test(entry.value) && (
              <span className="unit-pill">{entry.unit}</span>
            )}
          </span>
        ) : entry.value ? (
          <ValueWithUnit value={entry.value} unit={entry.unit} testId={`admin-config-value-${shortKey}`} />
        ) : (
          <span className="faint" data-testid={`admin-config-value-${shortKey}`}>not set</span>
        )}

        {onSaveSecret && <SetSecretControl onSet={(value) => onSaveSecret(entry.key, value)} busy={busy} />}
      </div>

      {/* Its own column, not a note under Value — a save or an override changes Value immediately,
          and Running is the whole reason to still be able to see what was there before, side by side. */}
      {entry.masked ? (
        <span className="faint" data-testid={`admin-config-running-${shortKey}`}>hidden</span>
      ) : entry.runningValue === entry.value ? (
        <span className="row" style={{ gap: 5, color: 'var(--ok)' }} data-testid={`admin-config-running-${shortKey}`}>
          <CheckIcon />
          Same
        </span>
      ) : entry.runningValue ? (
        <ValueWithUnit
          value={entry.runningValue}
          unit={entry.unit}
          testId={`admin-config-running-${shortKey}`}
        />
      ) : (
        <span className="faint" data-testid={`admin-config-running-${shortKey}`}>not set</span>
      )}

      <span className="row" style={{ gap: 6 }}>
        <span className="dim" data-testid={`admin-config-source-${shortKey}`}>{entry.source}</span>

        {entry.canAdopt && (
          <button
            type="button"
            className="btn-link quiet"
            disabled={busy}
            title="Write the current value into dbdatasync.config.yaml, making it the value used after a restart"
            onClick={() => entry.value && onSave(entry.key, entry.value)}
            data-testid={`admin-config-adopt-${shortKey}`}
          >
            Override
          </button>
        )}

        {entry.canReset && (
          <button
            type="button"
            className="btn-link quiet"
            disabled={busy}
            title={`Reset to the application default (${entry.defaultValue}), used after a restart`}
            onClick={() => entry.defaultValue && onSave(entry.key, entry.defaultValue)}
            data-testid={`admin-config-reset-${shortKey}`}
          >
            Reset
          </button>
        )}
      </span>

      {entry.caution && (
        // In both states, in the row of the setting it is about: what somebody deciding needs to read, not a confirmation
        // after they have already chosen. Its own full-width line — the key column is too narrow to hold a warning without
        // cutting it off, and a warning that is cut off is worse than none. The CLI prints the same text before it writes,
        // and `setup` shows it under its checkbox.
        <div className="config-caution" style={{ gridColumn: '1 / -1' }} data-testid={`admin-config-caution-${shortKey}`}>
          <span className="mark">!</span>
          <span>{entry.caution}</span>
        </div>
      )}
    </div>
  )
}

/** StateConnectionString's password — a separate control from the value above, because it writes
 * through the secret store rather than dbdatasync.config.yaml and the page never learns what it holds. */
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
