import { useEffect, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { AppShell } from '../components/AppShell'
import { CodeEditor } from '../components/CodeEditor'
import { DriversTabs } from '../components/DriversTabs'
import { ErrorBanner } from '../components/ErrorBanner'
import { Field } from '../components/Field'
import { FileUploadPanel } from '../components/FileUploadPanel'
import { LibraryFindPanel } from '../components/LibraryFindPanel'
import {
  useCreateDriver, useDriverYaml, useFiles, useKnownDriverKinds, useLibraries, useUpdateDriverYaml,
} from '../api/hooks'
import {
  assembleDriverYaml, parseDriverYaml, roundTripsCleanly, RAW_BODY_SKELETON,
  type Base, type JdbcConnectionStringKeysForm,
} from './driverYamlAssembly'

interface FormState {
  id: string
  displayName: string
  base: Base
  library: string
  driverClass: string
  driverJarPaths: string[]
  readers: string[]
  staging: string[]
  writers: string[]
  rawBody: string
  urlTemplate: string
  connectionStringKeys: JdbcConnectionStringKeysForm
  connectionStringKeysExtra: string
  jdbcExtra: string
}

const EMPTY_CONNECTION_STRING_KEYS: JdbcConnectionStringKeysForm = {
  host: '', port: '', database: '', username: '', password: '', connectTimeout: '',
}

const EMPTY: FormState = {
  id: '', displayName: '', base: 'adonet', library: '', driverClass: '', driverJarPaths: [],
  readers: [], staging: [], writers: [], rawBody: RAW_BODY_SKELETON,
  urlTemplate: '', connectionStringKeys: EMPTY_CONNECTION_STRING_KEYS, connectionStringKeysExtra: '', jdbcExtra: '',
}

/** `JdbcGenericDriver.DefaultConnectionStringKeys` — shown as placeholder text on each connection-string
 * key field so "blank means this" is visible rather than assumed. `port`/`connectTimeout` have no JDBC
 * default key of their own (`JdbcDriverSpec.ConnectionStringKeys`' own doc comment), so their placeholder
 * says so rather than showing a spelling that doesn't exist. */
const JDBC_DEFAULT_KEYS: Record<keyof JdbcConnectionStringKeysForm, string> = {
  host: 'host', port: 'not set', database: 'database', username: 'user', password: 'password',
  connectTimeout: 'not set',
}

/**
 * `driver.yaml` authoring — `driver-yaml-authoring-ui.md`, built. Structured controls for id/displayName/
 * base/capabilities/jar-or-library, one raw YAML editor for dialect+typeMap+metadataQueries (see that
 * doc's own "why one block, not three structured pieces" reasoning — splitting metadata-queries out
 * needs parsing the raw dialect text to know whether `catalog: query` is already set, real complexity a
 * v1 doesn't need). One route per action (`/drivers/new`, `/drivers/:id/edit`) rendering this same
 * component, matching `ScriptEditPage`'s own new-vs-existing shape — `useParams` decides which.
 */
export function DriverEditPage() {
  const { id: existingId } = useParams<{ id: string }>()
  const isNew = !existingId
  const navigate = useNavigate()

  const { data: loaded } = useDriverYaml(existingId)
  const { data: knownKinds } = useKnownDriverKinds()
  const { data: libraries } = useLibraries()
  const { data: files } = useFiles()
  const create = useCreateDriver()
  const update = useUpdateDriverYaml()

  const [form, setForm] = useState<FormState>(EMPTY)
  const [saveError, setSaveError] = useState<unknown>(null)
  // LibraryFindPanel is the full chips+search+install flow LibrariesPage uses as its whole page body —
  // too much of this form to show unconditionally when the common case (the library is already
  // installed) never touches it. Gated behind a popup instead; closed automatically once a pick lands.
  const [installPanelOpen, setInstallPanelOpen] = useState(false)
  // Which driver's yaml the form was last populated from — React's own "adjust state during render"
  // pattern (https://react.dev/learn/you-might-not-need-an-effect#adjusting-some-state-when-a-prop-changes)
  // rather than an effect: setState here runs before the browser paints, avoiding the extra render an
  // effect would cost, and — the actual reason it has to be this shape, not just style — a re-fetch of
  // the *same* driver (e.g. `useLibraries`' own invalidation after installing one from this same form)
  // must not clobber an in-progress edit the way re-running on every `loaded` identity change would.
  const [populatedFor, setPopulatedFor] = useState<string | undefined>(undefined)
  // Phase 180N. 'structured' is the default for a new driver and for anything this app itself wrote
  // (roundTripsCleanly true); a hand-authored file that doesn't split cleanly opens in 'raw' instead of
  // being silently reinterpreted — see rawModeReason below for the banner explaining why.
  const [mode, setMode] = useState<'structured' | 'raw'>('structured')
  const [rawText, setRawText] = useState('')
  const [rawModeReason, setRawModeReason] = useState<string | null>(null)

  if (loaded && populatedFor !== existingId) {
    const parsed = parseDriverYaml(loaded.yaml)
    const clean = roundTripsCleanly(loaded.yaml)
    setForm(parsed)
    setRawText(loaded.yaml)
    setMode(clean ? 'structured' : 'raw')
    setRawModeReason(clean
      ? null
      : "This file doesn't match the structured editor's expected shape — opened in raw mode so nothing is silently reinterpreted.")
    setPopulatedFor(existingId)
  }

  const saving = create.isPending || update.isPending

  const switchToRaw = () => {
    setRawText(assembleDriverYaml(form))
    setRawModeReason(null)
    setMode('raw')
  }

  const switchToStructured = () => {
    const parsed = parseDriverYaml(rawText)
    if (assembleDriverYaml(parsed) !== rawText) {
      const proceed = window.confirm(
        "Switching to the structured view may not preserve everything in this YAML if it doesn't " +
        'match the console’s own generated shape — switch anyway?',
      )
      if (!proceed) return
    }
    setForm(parsed)
    setMode('structured')
  }

  const save = async () => {
    setSaveError(null)
    const yaml = mode === 'raw' ? rawText : assembleDriverYaml(form)
    try {
      if (isNew) await create.mutateAsync(yaml)
      else await update.mutateAsync({ id: existingId, yaml })
      navigate('/drivers')
    } catch (err) {
      setSaveError(err)
    }
  }

  const toggle = (list: string[], value: string): string[] =>
    list.includes(value) ? list.filter((v) => v !== value) : [...list, value]

  const installedLibraryIds = new Set((libraries ?? []).map((l) => l.id))

  return (
    <AppShell crumbs={[{ label: 'Drivers' }]} tabs={<DriversTabs />}>
      <div className="pane">
        <div className="page-head">
          <h1 className="page-title">{isNew ? 'New driver' : `Edit ${existingId}`}</h1>
          <span className="page-note">
            A <code>driver.yaml</code> descriptor — the same file <code>dbdatasync config driver install</code>
            writes, editable here instead.
          </span>
          <div className="right" style={{ gap: 16 }}>
            <label className="row" style={{ gap: 6 }}>
              <input
                type="radio"
                checked={mode === 'structured'}
                onChange={() => { if (mode !== 'structured') switchToStructured() }}
                data-testid="driver-edit-mode-structured"
              />
              Structured
            </label>
            <label className="row" style={{ gap: 6 }}>
              <input
                type="radio"
                checked={mode === 'raw'}
                onChange={() => { if (mode !== 'raw') switchToRaw() }}
                data-testid="driver-edit-mode-raw"
              />
              Raw YAML
            </label>
          </div>
        </div>

        <ErrorBanner error={saveError} />

        {mode === 'raw' ? (
          <div className="card" data-testid="driver-edit-form">
            <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              {rawModeReason && <span className="hint warn" data-testid="driver-edit-raw-mode-reason">{rawModeReason}</span>}
              <CodeEditor
                language="yaml"
                value={rawText}
                onChange={setRawText}
                minLines={24}
                testId="driver-edit-raw-yaml"
              />
              <div className="row" style={{ gap: 8 }}>
                <button
                  type="button"
                  className="btn btn-primary"
                  disabled={saving || !rawText.trim()}
                  onClick={() => void save()}
                  data-testid="driver-edit-save"
                >
                  {saving ? 'Saving…' : 'Save'}
                </button>
              </div>
            </div>
          </div>
        ) : (
        <div className="card" data-testid="driver-edit-form">
          <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
            <div className="row" style={{ gap: 12 }}>
              <Field label="Id">
                <input
                  type="text"
                  className="input"
                  value={form.id}
                  disabled={!isNew}
                  onChange={(e) => setForm({ ...form, id: e.target.value })}
                  data-testid="driver-edit-id"
                />
              </Field>
              <Field label="Display name">
                <input
                  type="text"
                  className="input"
                  value={form.displayName}
                  onChange={(e) => setForm({ ...form, displayName: e.target.value })}
                  data-testid="driver-edit-display-name"
                />
              </Field>
            </div>

            <Field label="Base">
              <div className="row" style={{ gap: 16 }}>
                <label className="row" style={{ gap: 6 }}>
                  <input
                    type="radio"
                    checked={form.base === 'adonet'}
                    onChange={() => setForm({ ...form, base: 'adonet' })}
                    data-testid="driver-edit-base-adonet"
                  />
                  ADO.NET
                </label>
                <label className="row" style={{ gap: 6 }}>
                  <input
                    type="radio"
                    checked={form.base === 'jdbc'}
                    onChange={() => setForm({ ...form, base: 'jdbc' })}
                    data-testid="driver-edit-base-jdbc"
                  />
                  JDBC
                </label>
              </div>
            </Field>

            {form.base === 'adonet' ? (
              <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                <Field label="Library">
                  <div className="row" style={{ gap: 10 }}>
                    <select
                      className="input"
                      value={form.library}
                      onChange={(e) => setForm({ ...form, library: e.target.value })}
                      data-testid="driver-edit-library-select"
                    >
                      <option value="">Choose an installed library…</option>
                      {(libraries ?? []).map((l) => <option key={l.id} value={l.id}>{l.id}</option>)}
                    </select>
                    <button
                      type="button"
                      className="btn-link"
                      style={{ flexShrink: 0 }}
                      onClick={() => setInstallPanelOpen(true)}
                      data-testid="driver-edit-open-install-library"
                    >
                      Install a new library…
                    </button>
                  </div>
                </Field>
                {installPanelOpen && (
                  <InstallLibraryDialog
                    installedIds={installedLibraryIds}
                    // Selects the library into the form immediately, but leaves the dialog open —
                    // LibraryFindPanel's own "Installed" confirmation (and, for a non-curated pick, the
                    // detected factory type) is worth seeing rather than the popup vanishing the instant
                    // the install call resolves. The operator closes it themselves once they've seen it.
                    onInstalled={(installedId) => setForm({ ...form, library: installedId })}
                    onCancel={() => setInstallPanelOpen(false)}
                  />
                )}
              </div>
            ) : (
              <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                <Field label="Driver class">
                  <input
                    type="text"
                    className="input"
                    placeholder="org.postgresql.Driver"
                    value={form.driverClass}
                    onChange={(e) => setForm({ ...form, driverClass: e.target.value })}
                    data-testid="driver-edit-driver-class"
                  />
                </Field>
                <Field label="Jar(s)">
                  <div className="card flush" data-testid="driver-edit-jar-picker">
                    {(files ?? []).length === 0 && <div className="empty">No files uploaded yet.</div>}
                    {(files ?? []).map((f) => (
                      <label key={f.name} className="row" style={{ gap: 8, padding: '4px 10px' }}>
                        <input
                          type="checkbox"
                          checked={form.driverJarPaths.includes(f.name)}
                          onChange={() => setForm({ ...form, driverJarPaths: toggle(form.driverJarPaths, f.name) })}
                          data-testid={`driver-edit-jar-${f.name}`}
                        />
                        {f.name}
                      </label>
                    ))}
                  </div>
                </Field>
                <FileUploadPanel
                  onUploaded={(names) => setForm({ ...form, driverJarPaths: [...form.driverJarPaths, ...names] })}
                />
                <Field label="URL template">
                  <input
                    type="text"
                    className="input mono"
                    placeholder="jdbc:postgresql://{host}:{port}/{database}"
                    value={form.urlTemplate}
                    onChange={(e) => setForm({ ...form, urlTemplate: e.target.value })}
                    data-testid="driver-edit-url-template"
                  />
                  <span className="hint">
                    {'{host}'} {'{port}'} {'{database}'} {'{username}'} — each is placed if present in the
                    template, or sent as a connection property otherwise. Never {'{password}'} — a
                    credential is always sent as a property, added automatically.
                  </span>
                </Field>
                <details data-testid="driver-edit-connection-string-keys">
                  <summary className="dim" style={{ cursor: 'pointer' }}>Connection-string keys (advanced)</summary>
                  <div style={{ display: 'flex', flexDirection: 'column', gap: 8, marginTop: 8 }}>
                    {(Object.keys(EMPTY_CONNECTION_STRING_KEYS) as (keyof JdbcConnectionStringKeysForm)[]).map((key) => (
                      <Field key={key} label={key}>
                        <input
                          type="text"
                          className="input mono"
                          placeholder={JDBC_DEFAULT_KEYS[key]}
                          value={form.connectionStringKeys[key]}
                          onChange={(e) => setForm({
                            ...form,
                            connectionStringKeys: { ...form.connectionStringKeys, [key]: e.target.value },
                          })}
                          data-testid={`driver-edit-connection-string-key-${key}`}
                        />
                      </Field>
                    ))}
                  </div>
                </details>
              </div>
            )}

            <Field label="Pipeline phases">
              <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
                <KindGroup
                  label="Readers" options={knownKinds?.readers ?? []} selected={form.readers}
                  onToggle={(k) => setForm({ ...form, readers: toggle(form.readers, k) })}
                />
                <KindGroup
                  label="Staging" options={knownKinds?.staging ?? []} selected={form.staging}
                  onToggle={(k) => setForm({ ...form, staging: toggle(form.staging, k) })}
                />
                <KindGroup
                  label="Writers" options={knownKinds?.writers ?? []} selected={form.writers}
                  onToggle={(k) => setForm({ ...form, writers: toggle(form.writers, k) })}
                />
              </div>
            </Field>

            <Field label="Dialect, type map, and metadata queries (raw YAML)">
              <CodeEditor
                language="yaml"
                value={form.rawBody}
                onChange={(rawBody) => setForm({ ...form, rawBody })}
                minLines={8}
                testId="driver-edit-raw-body"
              />
            </Field>

            <div className="row" style={{ gap: 8 }}>
              <button
                type="button"
                className="btn btn-primary"
                disabled={saving || !form.id || !form.displayName}
                onClick={() => void save()}
                data-testid="driver-edit-save"
              >
                {saving ? 'Saving…' : 'Save'}
              </button>
            </div>
          </div>
        </div>
        )}
      </div>
    </AppShell>
  )
}

/** `LibraryFindPanel` in the same `.modal-backdrop`/`.modal` shell `LibraryFindPanel.tsx`'s own
 * `TrustInstallDialog` already built — the popup this form's "Install a new library…" button opens,
 * instead of the panel sitting inline and full-size whether or not it's ever touched. */
function InstallLibraryDialog({ installedIds, onInstalled, onCancel }: {
  installedIds: Set<string>
  onInstalled: (id: string) => void
  onCancel: () => void
}) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onCancel() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onCancel])

  return (
    <div className="modal-backdrop" onMouseDown={(e) => { if (e.target === e.currentTarget) onCancel() }}>
      <div className="modal wide" role="dialog" aria-modal="true" aria-label="Install a library" data-testid="driver-edit-install-library-dialog">
        <div className="card-head">
          <span className="card-title">Install a library</span>
          <button type="button" className="btn-link quiet" style={{ marginLeft: 'auto' }} onClick={onCancel} data-testid="driver-edit-install-library-close">
            Close
          </button>
        </div>
        <div className="card-body">
          <LibraryFindPanel installedIds={installedIds} onInstalled={onInstalled} />
        </div>
      </div>
    </div>
  )
}

function KindGroup({ label, options, selected, onToggle }: {
  label: string
  options: string[]
  selected: string[]
  onToggle: (kind: string) => void
}) {
  return (
    <div className="row" style={{ gap: 10, flexWrap: 'wrap', alignItems: 'center' }}>
      <span className="dim" style={{ minWidth: 70 }}>{label}:</span>
      {options.length === 0 && <span className="faint">none</span>}
      {options.map((kind) => (
        <label key={kind} className="row" style={{ gap: 4 }}>
          <input
            type="checkbox"
            checked={selected.includes(kind)}
            onChange={() => onToggle(kind)}
            data-testid={`driver-edit-kind-${kind}`}
          />
          {kind}
        </label>
      ))}
    </div>
  )
}
