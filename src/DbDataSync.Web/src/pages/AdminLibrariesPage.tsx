import { AdminTabs } from '../components/AdminTabs'
import { AppShell } from '../components/AppShell'
import { ErrorBanner } from '../components/ErrorBanner'
import { useIsAdmin } from '../components/useIsAdmin'
import { useLibraries } from '../api/hooks'
import type { LibrarySummary } from '../api/types'

const COLUMNS = '1.2fr 1.7fr 1fr 1.3fr'

/**
 * Every installed library — what a `driver.yaml` descriptor (or, once 109g lands, the state store)
 * resolves its `DbProviderFactory` through. Read-only in this phase: search (119) and install/remove
 * (120) land later, so "Install a library" is disabled for now.
 */
export function AdminLibrariesPage() {
  const isAdmin = useIsAdmin()
  const { data: libraries, isLoading, error } = useLibraries()

  // The API enforces this for real (Policies.Admin) — this is only about not showing a Viewer a
  // screen of affordances that would 403 the moment they were used.
  if (!isAdmin) {
    return (
      <AppShell crumbs={[{ label: 'Admin' }]} tabs={<AdminTabs />}>
        <div className="pane">
          <div className="empty">This screen is for administrators.</div>
        </div>
      </AppShell>
    )
  }

  return (
    <AppShell crumbs={[{ label: 'Admin' }]} tabs={<AdminTabs />}>
      <div className="pane">
        <div className="page-head">
          <h1 className="page-title">Libraries</h1>
          <span className="page-note">
            Every ADO.NET library restored onto this host — a package DbDataSync does not reference at
            compile time, swappable by installing a new version rather than rebuilding.
          </span>
        </div>

        <ErrorBanner error={error} />

        <div className="card flush" data-testid="admin-libraries-table">
          <div className="grid-head" style={{ gridTemplateColumns: COLUMNS, gap: 14 }}>
            <span>Id</span><span>Packages</span><span>Status</span><span>Used by</span>
          </div>
          {isLoading && <div className="empty">Loading…</div>}
          {!isLoading && (libraries ?? []).length === 0 && <div className="empty">No libraries installed.</div>}
          {(libraries ?? []).map((library) => <LibraryRow key={library.id} library={library} />)}
        </div>

        <div className="row" style={{ marginTop: 16 }}>
          <button
            type="button"
            className="btn"
            disabled
            title="Search (phase 119) and install (phase 120) land in later phases"
            data-testid="admin-libraries-install"
          >
            Install a library
          </button>
        </div>
      </div>
    </AppShell>
  )
}

function LibraryRow({ library }: { library: LibrarySummary }) {
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
        <span className={`dot ${library.resolves ? 'dot-ok' : 'dot-bad'}`} />
        {library.resolves ? 'resolves' : 'does not resolve'}
      </span>
      <span className="hint">
        {library.usedBy.length > 0 ? library.usedBy.join(', ') : <span className="faint">unused</span>}
      </span>
    </div>
  )
}
