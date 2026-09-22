import { Fragment, useEffect, useState } from 'react'
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

// Key, Source, Value, Running, Caution — Source sits right before Value (not last) since the two are
// read together: "where did this come from" answers "can I trust it" before "what does it say". Caution
// is last and narrow: most rows have none, and it's a flag to open, not something read inline.
const COLUMNS = '1.3fr 1.3fr 1.6fr 1.3fr 0.5fr'
const STATE_CONNECTION_STRING_KEY = 'DbDataSync:State:ConnectionString'
const NUMERIC = /^-?\d+(\.\d+)?$/

/** The first sentence, as a quick-read summary — the full description is one hover away on the
 * "?" icon, so the row itself only has to carry enough to place the key, not explain it fully. */
function shortDescription(description: string): string {
  const match = /^.*?[.!?](?=\s|$)/.exec(description.trim())
  return match ? match[0] : description
}

/**
 * What each group of keys is called, keyed by the first `:`-separated fragment. A group with no entry
 * here falls back to the fragment itself, so a future one gets a plain heading rather than no heading.
 */
const GROUP_NAMES: Record<string, string> = {
  App: 'Application',
  State: 'State store',
  Nuget: 'NuGet',
  Notes: 'Notes',
  Updates: 'Updates',
  Auth: 'Authentication',
}

/**
 * Buckets entries by the first `:`-separated fragment of their key (App/State/Auth/Updates/Nuget/Notes),
 * preserving catalog order both across and within groups — the backend's own `Keys` list is already
 * grouped this way, so a single pass suffices; this never re-sorts, just splits on group change.
 */
function groupByFirstFragment(entries: AdminConfigEntry[]): { group: string; label: string; entries: AdminConfigEntry[] }[] {
  const groups: { group: string; label: string; entries: AdminConfigEntry[] }[] = []
  for (const entry of entries) {
    const group = entry.key.replace(/^DbDataSync:/, '').split(':')[0]
    const current = groups[groups.length - 1]
    if (current?.group === group) current.entries.push(entry)
    else groups.push({ group, label: GROUP_NAMES[group] ?? group, entries: [entry] })
  }
  return groups
}

/**
 * A second pass inside one group's own entries, clustering consecutive entries that share a *second*
 * `:`-fragment (e.g. `Auth:Network:Admin` and `Auth:Network:Viewer` both fall under `Network`) — same
 * "contiguous, no re-sorting" reasoning `groupByFirstFragment` already gives for the first level, one
 * level deeper. A two-segment key (`State:DbPath`) has no subgroup (`null`) and renders as a bare row,
 * same as before this existed — a group can freely mix flat keys and subgrouped ones (`State` does).
 */
function subgroupEntries(entries: AdminConfigEntry[]): { subgroup: string | null; entries: AdminConfigEntry[] }[] {
  const chunks: { subgroup: string | null; entries: AdminConfigEntry[] }[] = []
  for (const entry of entries) {
    const segments = entry.key.replace(/^DbDataSync:/, '').split(':')
    const subgroup = segments.length > 2 ? segments[1] : null
    const current = chunks[chunks.length - 1]
    if (current?.subgroup === subgroup) current.entries.push(entry)
    else chunks.push({ subgroup, entries: [entry] })
  }
  return chunks
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
 * A closed set of legal values (`entry.allowedValues`) as one joined button bar rather than a free-text
 * box — every mode-string setting today has 2-3 options, small enough that seeing them all beats hiding
 * them behind a dropdown click.
 */
function ModeToggle({ value, options, onChange, disabled, testId }: {
  value: string | null
  options: string[]
  onChange: (next: string) => void
  disabled?: boolean
  testId?: string
}) {
  return (
    <span className="toggle-bar" role="group" data-testid={testId}>
      {options.map((option) => (
        <button
          key={option}
          type="button"
          className={`btn btn-sm${value === option ? ' btn-primary' : ''}`}
          disabled={disabled || value === option}
          onClick={() => onChange(option)}
          data-testid={testId ? `${testId}-${option}` : undefined}
        >
          {option}
        </button>
      ))}
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

        {/* A card per group, not one card with a band between groups: the band grouped the rows but
            still read as one long table, which is the thing that made "where are the auth settings"
            a matter of reading every key. Each group carries its own header row, so a group scrolled
            to in isolation still says which column is which. */}
        <div className="config-groups" data-testid="admin-config-table">
          {isLoading && <div className="card"><div className="empty">Loading…</div></div>}
          {groupByFirstFragment(entries ?? []).map(({ group, label, entries: groupEntries }) => (
            <div className="card flush" key={group} data-testid={`admin-config-group-${group}`}>
              <div className="card-head tight">
                <span className="card-title">{label}</span>
                <span className="card-note mono">DbDataSync:{group}:*</span>
              </div>
              <div className="grid-head" style={{ gridTemplateColumns: COLUMNS, gap: 14 }}>
                <span>Key</span><span>Source</span><span>Value</span><span>Running</span><span>Caution</span>
              </div>
              {subgroupEntries(groupEntries).map(({ subgroup, entries: subEntries }, i) => (
                <Fragment key={subgroup ?? `_flat_${i}`}>
                  {subgroup && (
                    <div className="config-subgroup-head" data-testid={`admin-config-subgroup-${group}-${subgroup}`}>
                      {subgroup}
                    </div>
                  )}
                  {subEntries.map((entry) => (
                    <Row
                      key={entry.key}
                      entry={entry}
                      onSave={save}
                      onSaveSecret={entry.key === STATE_CONNECTION_STRING_KEY ? saveSecret : undefined}
                      busy={setValue.isPending || setSecret.isPending}
                    />
                  ))}
                </Fragment>
              ))}
            </div>
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

      {/* Read together with Value, right beside it — "where did this come from" answers "can I trust
          it" before "what does it say". */}
      <span className="row wrap" style={{ gap: 6 }}>
        <span className="dim" data-testid={`admin-config-source-${shortKey}`}>{entry.source}</span>

        {/* The file has this key and something outranks it, so a save here writes the file and changes
            nothing the process does — before or after a restart. Said in the Source column rather than
            as a caution under the row: it is an answer to "where does this value come from", and the
            honest answer is "not from the file you are editing". */}
        {entry.overriddenBy && (
          <span
            className="override-pill"
            title={
              `Set by ${entry.overriddenBy}, which outranks dbdatasync.config.yaml` +
              (entry.overriddenValue ? `: ${entry.overriddenValue}` : '') +
              '. Editing the value here writes the file, but this deployment will keep using the ' +
              `${entry.overriddenBy} until that is unset.`
            }
            data-testid={`admin-config-overridden-${shortKey}`}
          >
            overridden by {entry.overriddenBy}
          </span>
        )}

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

      <div>
        {entry.masked ? (
          <span className="faint" data-testid={`admin-config-value-${shortKey}`}>
            hidden — carries a credential from its source, never shown here
          </span>
        ) : entry.editable && entry.allowedValues ? (
          <ModeToggle
            value={entry.value}
            options={entry.allowedValues}
            onChange={(next) => onSave(entry.key, next)}
            disabled={busy}
            testId={`admin-config-value-${shortKey}`}
          />
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

      {/* Its own narrow column rather than a full-width box under the row (what this used to be) — a
          flag to open, not something read inline, so the row keeps the table's own rhythm. The CLI
          prints the same text before it writes, and `setup` shows it under its checkbox. */}
      {entry.caution ? <CautionFlag shortKey={shortKey} caution={entry.caution} /> : <span />}
    </div>
  )
}

/** A row's caution, collapsed to a small warn-colored flag — click opens the full text in a popup
 * rather than a native `title` tooltip, which wraps a multi-sentence warning badly and reads slowly.
 * Reuses `.modal-backdrop`/`.modal`, the same shell `LibraryFindPanel`'s `TrustInstallDialog` already
 * built, rather than a new one. */
function CautionFlag({ shortKey, caution }: { shortKey: string; caution: string }) {
  const [open, setOpen] = useState(false)

  useEffect(() => {
    if (!open) return
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false) }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [open])

  return (
    <>
      <button
        type="button"
        className="caution-flag"
        aria-label={`Caution for ${shortKey}`}
        onClick={() => setOpen(true)}
        data-testid={`admin-config-caution-${shortKey}`}
      >
        !
      </button>
      {open && (
        <div className="modal-backdrop" onMouseDown={(e) => { if (e.target === e.currentTarget) setOpen(false) }}>
          <div className="modal" role="dialog" aria-modal="true" aria-label={`Caution for ${shortKey}`}>
            <div className="card-head">
              <span className="card-title">Caution — {shortKey}</span>
            </div>
            <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
              <span className="hint">{caution}</span>
              <div className="row" style={{ justifyContent: 'flex-end' }}>
                <button type="button" className="btn btn-sm" onClick={() => setOpen(false)} data-testid={`admin-config-caution-close-${shortKey}`}>
                  Close
                </button>
              </div>
            </div>
          </div>
        </div>
      )}
    </>
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
