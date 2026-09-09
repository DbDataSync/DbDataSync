import { useEffect, useState } from 'react'
import { AdminTabs } from '../components/AdminTabs'
import { AppShell } from '../components/AppShell'
import { ErrorBanner } from '../components/ErrorBanner'
import { useIsAdmin } from '../components/useIsAdmin'
import { useKnownLibraries, useLibraries, useSearchLibraries } from '../api/hooks'
import type { KnownLibrarySummary, LibrarySearchResult, LibrarySummary } from '../api/types'

const COLUMNS = '1.2fr 1.7fr 1fr 1.3fr'

/**
 * Every installed library — what a `driver.yaml` descriptor (or, once 109g lands, the state store)
 * resolves its `DbProviderFactory` through — plus (phase 119) a NuGet search box for finding one to
 * add. Read-only in this phase: there is no Install button yet, only a copyable CLI command; the
 * one-click install itself is phase 120.
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

        <LibraryFindPanel installedIds={new Set((libraries ?? []).map((l) => l.id))} />
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

function formatDownloads(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`
  if (n >= 1_000) return `${(n / 1_000).toFixed(1)}K`
  return String(n)
}

/**
 * Finding a library to install — a NuGet search box when the server says it's enabled and reachable,
 * curated quick-add chips above it either way, and a manual package-id/version entry as the fallback
 * (disabled search, a failed call, or a package the search box didn't turn up). Every path ends the
 * same way: a package id + version yields a copyable `config library install` command — there is no
 * Install button here, that's phase 120.
 */
function LibraryFindPanel({ installedIds }: { installedIds: Set<string> }) {
  const { data: knownLibraries } = useKnownLibraries()
  const search = useSearchLibraries()
  const [query, setQuery] = useState('')
  const [probed, setProbed] = useState(false)
  const [manualId, setManualId] = useState('')
  const [manualVersion, setManualVersion] = useState('')
  // versionLocked distinguishes a version already chosen from a definite list (a search result's
  // <select>, or manual entry's own version field before "Use" is clicked) from one still needing
  // typed input — InstallCommand only renders an editable version box in the latter case, so the box
  // doesn't vanish out from under the operator mid-keystroke once it stops being empty.
  const [selected, setSelected] = useState<{ id: string; version: string; versionLocked: boolean } | null>(null)

  // One silent probe on mount — an operator shouldn't have to type something and get refused just to
  // learn the box doesn't work in this deployment.
  useEffect(() => {
    search.mutate('', { onSettled: () => setProbed(true) })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const searchWorks = probed && !search.isError && search.data?.status === 'ok'
  const results = search.data?.status === 'ok' ? (search.data.results ?? []) : []

  const runSearch = (e: React.FormEvent) => {
    e.preventDefault()
    search.mutate(query)
  }

  const isCurated = (id: string) => (knownLibraries ?? []).some((k) => k.packageId === id)

  return (
    <div className="card" data-testid="admin-libraries-find-panel" style={{ marginTop: 20 }}>
      <div className="card-head">
        <span className="card-title">Find a library to install</span>
      </div>
      <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
        {(knownLibraries ?? []).length > 0 && (
          <div className="row" style={{ gap: 8, flexWrap: 'wrap' }}>
            {(knownLibraries ?? [])
              .filter((k) => !installedIds.has(k.id))
              .map((entry) => (
                <QuickAddChip
                  key={entry.id}
                  entry={entry}
                  onPick={() => setSelected({ id: entry.packageId, version: '', versionLocked: false })}
                />
              ))}
          </div>
        )}

        {!probed ? (
          <div className="hint">Checking whether NuGet search is available…</div>
        ) : searchWorks ? (
          <>
            <form className="row" style={{ gap: 8 }} onSubmit={runSearch}>
              <input
                type="text"
                className="input"
                placeholder="Search NuGet — e.g. mysql"
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                data-testid="admin-libraries-search-input"
              />
              <button type="submit" className="btn btn-sm" disabled={search.isPending} data-testid="admin-libraries-search-submit">
                Search
              </button>
            </form>

            {search.isPending && <div className="hint">Searching…</div>}
            {!search.isPending && results.length === 0 && query && (
              <div className="hint">No results for "{query}".</div>
            )}
            {results.map((result) => (
              <SearchResultRow
                key={result.id}
                result={result}
                curated={isCurated(result.id)}
                onSelect={(version) => setSelected({ id: result.id, version, versionLocked: true })}
              />
            ))}
          </>
        ) : (
          <>
            <div className="hint">
              NuGet search isn't available in this deployment — enter a package id and version directly.
            </div>
            <div className="row" style={{ gap: 8 }}>
              <input
                type="text"
                className="input"
                placeholder="Package id — e.g. MySqlConnector"
                value={manualId}
                onChange={(e) => setManualId(e.target.value)}
                data-testid="admin-libraries-manual-id"
              />
              <input
                type="text"
                className="input sm"
                placeholder="Version"
                value={manualVersion}
                onChange={(e) => setManualVersion(e.target.value)}
                data-testid="admin-libraries-manual-version"
              />
              <button
                type="button"
                className="btn btn-sm"
                disabled={!manualId || !manualVersion}
                onClick={() => setSelected({ id: manualId, version: manualVersion, versionLocked: true })}
                data-testid="admin-libraries-manual-use"
              >
                Use
              </button>
            </div>
          </>
        )}

        {selected && (
          <InstallCommand
            id={selected.id}
            version={selected.version}
            versionLocked={selected.versionLocked}
            curated={isCurated(selected.id)}
            onChangeVersion={(version) => setSelected({ ...selected, version })}
          />
        )}
      </div>
    </div>
  )
}

function QuickAddChip({ entry, onPick }: { entry: KnownLibrarySummary; onPick: () => void }) {
  return (
    <button
      type="button"
      className="btn-link quiet"
      title={entry.description}
      onClick={onPick}
      data-testid={`admin-libraries-chip-${entry.id}`}
    >
      + {entry.displayName}
    </button>
  )
}

function SearchResultRow({ result, curated, onSelect }: {
  result: LibrarySearchResult
  curated: boolean
  onSelect: (version: string) => void
}) {
  const [version, setVersion] = useState('')

  return (
    <div
      className="row"
      style={{ justifyContent: 'space-between', alignItems: 'center', gap: 12 }}
      data-testid={`admin-libraries-result-${result.id}`}
    >
      <div>
        <div className="row" style={{ gap: 8 }}>
          <strong>{result.id}</strong>
          {result.verified && <span className="unit-pill" title="Verified publisher">verified</span>}
          {curated && <span className="unit-pill">vetted</span>}
          <span className="dim">{formatDownloads(result.totalDownloads)} downloads</span>
        </div>
        <div className="hint">{result.description}</div>
      </div>
      <div className="row" style={{ gap: 8, flexShrink: 0 }}>
        <select
          className="input sm"
          value={version}
          onChange={(e) => {
            setVersion(e.target.value)
            if (e.target.value) onSelect(e.target.value)
          }}
          data-testid={`admin-libraries-result-version-${result.id}`}
        >
          <option value="">Version…</option>
          {result.versions.map((v) => <option key={v} value={v}>{v}</option>)}
        </select>
      </div>
    </div>
  )
}

function InstallCommand({ id, version, versionLocked, curated, onChangeVersion }: {
  id: string
  version: string
  /** True once a version came from a definite list (a search result, or manual entry's own field) —
   * only false still needs an editable box, so it doesn't disappear mid-keystroke the moment
   * `version` stops being empty. */
  versionLocked: boolean
  curated: boolean
  onChangeVersion: (version: string) => void
}) {
  const ready = !!version
  const command = `dbdatasync config library install ${id} --version ${version || '<v>'}`

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(command)
    } catch {
      // Clipboard access can be denied by the browser; the command is still selectable as text.
    }
  }

  return (
    <div className="banner" style={{ display: 'flex', flexDirection: 'column', gap: 8 }} data-testid="admin-libraries-install-command">
      {!versionLocked && (
        <div className="row" style={{ gap: 8 }}>
          <span>Version:</span>
          <input
            type="text"
            className="input sm"
            value={version}
            onChange={(e) => onChangeVersion(e.target.value)}
            placeholder="required"
            data-testid="admin-libraries-command-version"
          />
        </div>
      )}
      <div className="mono">{command}</div>
      {!curated && (
        <div className="hint">
          "{id}" isn't one of the bundled, vetted libraries — installing it runs its code inside the
          DbDataSync host with the host's own privileges, the same trust as a hook or a script. Only
          install a package you've vetted yourself.
        </div>
      )}
      <div>
        <button
          type="button"
          className="btn-link quiet"
          disabled={!ready}
          onClick={copy}
          data-testid="admin-libraries-command-copy"
        >
          Copy command
        </button>
      </div>
    </div>
  )
}
