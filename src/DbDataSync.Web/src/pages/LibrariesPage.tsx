import { useState } from 'react'
import { DriversTabs } from '../components/DriversTabs'
import { AppShell } from '../components/AppShell'
import { ErrorBanner } from '../components/ErrorBanner'
import { LibraryFindPanel } from '../components/LibraryFindPanel'
import { RestartRequiredBanner } from '../components/RestartRequiredBanner'
import { useIsAdmin } from '../components/useIsAdmin'
import { useLibraries, useRemoveLibrary, useRestartRequired } from '../api/hooks'
import type { LibrarySummary } from '../api/types'

const COLUMNS = '1.2fr 1.7fr 1fr 1.3fr 1fr'

/**
 * Every installed library — what a `driver.yaml` descriptor (or, once 109g lands, the state store)
 * resolves its `DbProviderFactory` through — plus (119) a NuGet search box and (120) installing or
 * removing one directly from here. The find/install flow itself is `LibraryFindPanel` — extracted so
 * the driver-authoring form can embed the identical thing rather than a second copy.
 */
export function LibrariesPage() {
  const isAdmin = useIsAdmin()
  const { data: libraries, isLoading, error } = useLibraries()
  const { data: restartRequired } = useRestartRequired()
  const remove = useRemoveLibrary()
  const [removeError, setRemoveError] = useState<unknown>(null)

  // The API enforces this for real (Policies.Admin) — this is only about not showing a Viewer a
  // screen of affordances that would 403 the moment they were used.
  if (!isAdmin) {
    return (
      <AppShell crumbs={[{ label: 'Drivers' }]} tabs={<DriversTabs />}>
        <div className="pane">
          <div className="empty">This screen is for administrators.</div>
        </div>
      </AppShell>
    )
  }

  const doRemove = async (id: string, force: boolean) => {
    setRemoveError(null)
    try {
      await remove.mutateAsync({ id, force })
    } catch (err) {
      setRemoveError(err)
    }
  }

  return (
    <AppShell crumbs={[{ label: 'Drivers' }]} tabs={<DriversTabs />}>
      <div className="pane">
        <div className="page-head">
          <h1 className="page-title">Libraries</h1>
          <span className="page-note">
            Every ADO.NET library restored onto this host — a package DbDataSync does not reference at
            compile time, swappable by installing a new version rather than rebuilding.
          </span>
        </div>

        <RestartRequiredBanner show={!!restartRequired?.required} />
        <ErrorBanner error={error ?? removeError} />

        <div className="card flush" data-testid="admin-libraries-table">
          <div className="grid-head" style={{ gridTemplateColumns: COLUMNS, gap: 14 }}>
            <span>Id</span><span>Packages</span><span>Status</span><span>Used by</span><span></span>
          </div>
          {isLoading && <div className="empty">Loading…</div>}
          {!isLoading && (libraries ?? []).length === 0 && <div className="empty">No libraries installed.</div>}
          {(libraries ?? []).map((library) => (
            <LibraryRow key={library.id} library={library} onRemove={doRemove} busy={remove.isPending} />
          ))}
        </div>

        <LibraryFindPanel installedIds={new Set((libraries ?? []).map((l) => l.id))} />
      </div>
    </AppShell>
  )
}

function LibraryRow({ library, onRemove, busy }: {
  library: LibrarySummary
  onRemove: (id: string, force: boolean) => void
  busy: boolean
}) {
  const inUse = library.usedBy.length > 0

  const removeDirectly = () => {
    if (window.confirm(`Remove library '${library.id}'? This takes effect after a restart.`))
      onRemove(library.id, false)
  }

  const forceRemove = () => {
    if (window.confirm(
      `'${library.id}' is still named by: ${library.usedBy.join(', ')}. Removing it anyway will make ` +
      `${library.usedBy.length === 1 ? 'that driver' : 'those drivers'} fail to load on the next restart. Continue?`,
    )) {
      onRemove(library.id, true)
    }
  }

  return (
    <div
      className="grid-row"
      style={{ gridTemplateColumns: COLUMNS, gap: 14 }}
      data-testid={`admin-library-row-${library.id}`}
    >
      <span className="row" style={{ gap: 6 }}>
        <span className="mono">{library.id}</span>
        {library.curated && (
          <span className="unit-pill" data-testid={`admin-library-curated-${library.id}`}>vetted</span>
        )}
      </span>
      <span className="hint">{library.packages.map((p) => `${p.id} ${p.version}`).join(', ')}</span>
      <span className="status" data-testid={`admin-library-resolves-${library.id}`}>
        {library.pendingRestore ? (
          <>
            <span className="dot dot-bad" />
            <span title={`No SDK was available to restore this — run \`dbdatasync config library sync ${library.id}\` on a host with the SDK.`}>
              pending restore
            </span>
          </>
        ) : (
          <>
            <span className={`dot ${library.resolves ? 'dot-ok' : 'dot-bad'}`} />
            {library.resolves ? 'resolves' : 'does not resolve'}
          </>
        )}
      </span>
      <span className="hint">
        {inUse ? library.usedBy.join(', ') : <span className="faint">unused</span>}
      </span>
      <span className="row" style={{ gap: 8, justifyContent: 'flex-end' }}>
        <button
          type="button"
          className="btn-link quiet"
          disabled={inUse || busy}
          title={inUse ? `Still named by: ${library.usedBy.join(', ')}` : undefined}
          onClick={removeDirectly}
          data-testid={`admin-library-remove-${library.id}`}
        >
          Remove
        </button>
        {inUse && (
          <button
            type="button"
            className="btn-link quiet"
            disabled={busy}
            onClick={forceRemove}
            data-testid={`admin-library-force-remove-${library.id}`}
          >
            Force
          </button>
        )}
      </span>
    </div>
  )
}
