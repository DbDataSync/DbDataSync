import { useState } from 'react'
import { Link } from 'react-router-dom'
import { AdminTabs } from '../components/AdminTabs'
import { AppShell } from '../components/AppShell'
import { ErrorBanner } from '../components/ErrorBanner'
import { RestartRequiredBanner } from '../components/RestartRequiredBanner'
import { useIsAdmin } from '../components/useIsAdmin'
import { useDrivers, useInstallDriverFromCatalog, useKnownDrivers, useRestartRequired } from '../api/hooks'
import type { DriverSummary, KnownDriverSummary } from '../api/types'

const COLUMNS = '1.1fr 1.6fr 0.9fr 1.1fr 1.8fr'

const SOURCE_LABEL: Record<DriverSummary['source'], string> = {
  builtin: 'Built-in',
  descriptor: 'Descriptor',
  compiled: 'Compiled',
}

/**
 * Every registered driver — the three built-ins plus whatever `driver.yaml` descriptors (phase 109d)
 * or compiled plugins (109e) an operator has added — and the bundled catalog (117) an "Add" affordance
 * offers. A catalog install (120) is always a curated pick, so it never opens the trust dialog the
 * Libraries screen's non-curated path does.
 */
export function AdminDriversPage() {
  const isAdmin = useIsAdmin()
  const { data: drivers, isLoading, error } = useDrivers()
  const { data: knownDrivers, error: knownError } = useKnownDrivers()
  const { data: restartRequired } = useRestartRequired()
  const install = useInstallDriverFromCatalog()
  const [installError, setInstallError] = useState<unknown>(null)
  const [justInstalled, setJustInstalled] = useState<string | null>(null)

  // The API enforces this for real (Policies.Admin on the new endpoints) — this is only about not
  // showing a Viewer a screen of "Add" affordances that would 403 the moment they were used.
  if (!isAdmin) {
    return (
      <AppShell crumbs={[{ label: 'Admin' }]} tabs={<AdminTabs />}>
        <div className="pane">
          <div className="empty">This screen is for administrators.</div>
        </div>
      </AppShell>
    )
  }

  const addFromCatalog = async (knownDriverId: string, version: string) => {
    setInstallError(null)
    setJustInstalled(null)
    try {
      const result = await install.mutateAsync({ knownDriverId, version })
      setJustInstalled(result.id)
    } catch (err) {
      setInstallError(err)
    }
  }

  const installedIds = new Set((drivers ?? []).map((d) => d.id))

  return (
    <AppShell crumbs={[{ label: 'Admin' }]} tabs={<AdminTabs />}>
      <div className="pane">
        <div className="page-head">
          <h1 className="page-title">Drivers</h1>
          <span className="page-note">
            What can replicate today — the three built-in engines, plus any descriptor or compiled
            plugin installed on this host.
          </span>
        </div>

        <RestartRequiredBanner show={!!restartRequired?.required} />
        <ErrorBanner error={error ?? knownError ?? installError} />

        <div className="card flush" data-testid="admin-drivers-table">
          <div className="grid-head" style={{ gridTemplateColumns: COLUMNS, gap: 14 }}>
            <span>Id</span><span>Display name</span><span>Source</span><span>Library</span><span>Capabilities</span>
          </div>
          {isLoading && <div className="empty">Loading…</div>}
          {(drivers ?? []).map((driver) => <DriverRow key={driver.id} driver={driver} />)}
        </div>

        <div className="card" data-testid="admin-drivers-add-panel" style={{ marginTop: 20 }}>
          <div className="card-head">
            <span className="card-title">Add a driver</span>
          </div>
          <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
            <span className="hint">
              Every entry here is a curated, bundled descriptor — no trust confirmation needed. See{' '}
              <Link to="/docs/drivers-and-libraries#descriptor-drivers" data-testid="drivers-docs-link">descriptor drivers</Link>{' '}
              for what a descriptor can and can't do.
            </span>
            {(knownDrivers ?? [])
              .filter((entry) => !installedIds.has(entry.id))
              .map((entry) => (
                <KnownDriverRow
                  key={entry.id}
                  entry={entry}
                  installing={install.isPending}
                  justInstalled={justInstalled === entry.id}
                  onAdd={(version) => addFromCatalog(entry.id, version)}
                />
              ))}
            {knownDrivers?.length === 0 && <div className="empty">No bundled drivers yet.</div>}
          </div>
        </div>
      </div>
    </AppShell>
  )
}

function DriverRow({ driver }: { driver: DriverSummary }) {
  const capabilities = [
    ...driver.capabilities.readers,
    ...driver.capabilities.staging,
    ...driver.capabilities.writers,
  ]

  return (
    <div
      className="grid-row"
      style={{ gridTemplateColumns: COLUMNS, gap: 14 }}
      data-testid={`admin-driver-row-${driver.id}`}
    >
      <span className="mono">{driver.id}</span>
      <span>{driver.displayName}</span>
      <span className="dim" data-testid={`admin-driver-source-${driver.id}`}>{SOURCE_LABEL[driver.source]}</span>
      <span className="mono">{driver.library ?? <span className="faint">—</span>}</span>
      <span className="hint wrap">{capabilities.length > 0 ? capabilities.join(', ') : <span className="faint">none</span>}</span>
    </div>
  )
}

function KnownDriverRow({ entry, installing, justInstalled, onAdd }: {
  entry: KnownDriverSummary
  installing: boolean
  justInstalled: boolean
  onAdd: (version: string) => void
}) {
  const [version, setVersion] = useState('')
  const command =
    `dbdatasync config driver install <id> --library ${entry.boundLibrary} --version ${version || '<v>'} --from ${entry.id}`

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(command)
    } catch {
      // Clipboard access can be denied by the browser; the command is still selectable as text.
    }
  }

  return (
    <div
      className="row"
      style={{ justifyContent: 'space-between', alignItems: 'flex-start', gap: 12 }}
      data-testid={`admin-known-driver-${entry.id}`}
    >
      <div>
        <div className="row" style={{ gap: 8 }}>
          <strong>{entry.displayName}</strong>
          <span className="dim">({entry.id})</span>
        </div>
        <div className="hint">{entry.description}</div>
        <div className="mono hint" style={{ marginTop: 4 }}>{command}</div>
        {justInstalled && <div className="hint" style={{ color: 'var(--ok)' }}>Installed.</div>}
      </div>
      <div className="row" style={{ gap: 8, flexShrink: 0 }}>
        <input
          type="text"
          className="input sm"
          placeholder="Version"
          value={version}
          onChange={(e) => setVersion(e.target.value)}
          data-testid={`admin-known-driver-version-${entry.id}`}
        />
        <button
          type="button"
          className="btn-link quiet"
          onClick={copy}
          data-testid={`admin-known-driver-copy-${entry.id}`}
        >
          Copy command
        </button>
        <button
          type="button"
          className="btn btn-sm"
          disabled={!version || installing}
          onClick={() => onAdd(version)}
          data-testid={`admin-known-driver-add-${entry.id}`}
        >
          {installing ? 'Adding…' : 'Add'}
        </button>
      </div>
    </div>
  )
}
