import { useEffect, useState } from 'react'
import { ErrorBanner } from './ErrorBanner'
import { useInstallLibrary, useKnownLibraries, useSearchLibraries } from '../api/hooks'
import type { KnownLibrarySummary, LibrarySearchResult } from '../api/types'

type Selection = { id: string; version: string; versionLocked: boolean; factoryType: string }

/**
 * Finding a library to install — a NuGet search box when the server says it's enabled and reachable,
 * curated quick-add chips above it either way, and a manual package-id/version entry as the fallback
 * (disabled search, a failed call, or a package the search box didn't turn up). Every path ends the
 * same way: a package id + version (+ factory type, for a non-curated one) either installs directly
 * (a curated pick) or opens a trust confirmation first (anything else) — install.mutateAsync either
 * way, so the copyable CLI command is a fallback for a failed install, not the only option.
 * <para>
 * Extracted from `LibrariesPage` (previously page-local) so the driver-authoring form's own
 * connection-library picker can embed the identical flow instead of a second copy — `LibrariesPage`
 * itself is unchanged in behaviour, just importing this rather than defining it. `onInstalled` is that
 * embedding's own hook: `LibrariesPage` doesn't need it (the installed-libraries table already re-fetches
 * off the same query invalidation `useInstallLibrary` triggers), but a picker embedded in another form
 * needs to know which id just became available to select, the same shape `FileUploadPanel`'s own
 * `onUploaded` callback already has.
 * </para>
 */
export function LibraryFindPanel({ installedIds, onInstalled }: {
  installedIds: Set<string>
  onInstalled?: (id: string) => void
}) {
  const { data: knownLibraries } = useKnownLibraries()
  const search = useSearchLibraries()
  const install = useInstallLibrary()
  const [query, setQuery] = useState('')
  const [probed, setProbed] = useState(false)
  const [manualId, setManualId] = useState('')
  const [manualVersion, setManualVersion] = useState('')
  const [selected, setSelected] = useState<Selection | null>(null)
  const [confirmTrust, setConfirmTrust] = useState(false)
  const [installError, setInstallError] = useState<unknown>(null)
  const [installedOk, setInstalledOk] = useState(false)
  const [installedFactoryType, setInstalledFactoryType] = useState<string | null>(null)

  // One silent probe on mount — an operator shouldn't have to type something and get refused just to
  // learn the box doesn't work in this deployment.
  useEffect(() => {
    search.mutate('', { onSettled: () => setProbed(true) })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const searchWorks = probed && !search.isError && search.data?.status === 'ok'
  const results = search.data?.status === 'ok' ? (search.data.results ?? []) : []
  const uninstalledKnown = (knownLibraries ?? []).filter((k) => !installedIds.has(k.id))

  const runSearch = (e: React.FormEvent) => {
    e.preventDefault()
    search.mutate(query)
  }

  const isCurated = (id: string) => (knownLibraries ?? []).some((k) => k.packageId === id)

  const pick = (next: Selection) => {
    setSelected(next)
    setInstalledOk(false)
    setInstalledFactoryType(null)
    setInstallError(null)
  }

  const doInstall = async (target: Selection) => {
    setInstallError(null)
    try {
      const manifest = await install.mutateAsync({
        packageId: target.id,
        version: target.version,
        factoryType: target.factoryType || undefined,
      })
      setInstalledOk(true)
      setInstalledFactoryType(manifest.factoryType)
      onInstalled?.(manifest.id)
    } catch (err) {
      setInstallError(err)
    }
  }

  const requestInstall = () => {
    if (!selected) return
    if (isCurated(selected.id)) void doInstall(selected)
    else setConfirmTrust(true)
  }

  return (
    <div className="card" data-testid="admin-libraries-find-panel" style={{ marginTop: 20 }}>
      <div className="card-head">
        <span className="card-title">Find a library to install</span>
      </div>
      {/* `.find-layout` rather than an inline `display: flex`, because `.card-body` already sets
          `flex-direction: column` — an inline style that overrides `display` and not `flexDirection`
          leaves the two children stacked, which is exactly how this "sidebar" was rendering. */}
      <div className="card-body">
        <div className="find-layout">
          {uninstalledKnown.length > 0 && (
            <aside className="find-aside" data-testid="admin-libraries-known-sidebar">
              <span className="card-title">Well-known libraries</span>
              {uninstalledKnown.map((entry) => (
                <QuickAddChip
                  key={entry.id}
                  entry={entry}
                  onPick={() => pick({
                    id: entry.packageId, version: entry.pinnedVersion, versionLocked: false, factoryType: '',
                  })}
                />
              ))}
            </aside>
          )}

          <div className="find-main">
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
                    onSelect={(version) => pick({ id: result.id, version, versionLocked: true, factoryType: '' })}
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
                    onClick={() => pick({ id: manualId, version: manualVersion, versionLocked: true, factoryType: '' })}
                    data-testid="admin-libraries-manual-use"
                  >
                    Use
                  </button>
                </div>
              </>
            )}

            {selected && (
              <InstallCommand
                selection={selected}
                curated={isCurated(selected.id)}
                installing={install.isPending}
                installedOk={installedOk}
                installedFactoryType={installedFactoryType}
                onChangeVersion={(version) => pick({ ...selected, version })}
                onChangeFactoryType={(factoryType) => setSelected({ ...selected, factoryType })}
                onInstall={requestInstall}
              />
            )}

            <ErrorBanner error={installError} />
          </div>
        </div>
      </div>

      {confirmTrust && selected && (
        <TrustInstallDialog
          id={selected.id}
          busy={install.isPending}
          onCancel={() => setConfirmTrust(false)}
          onConfirm={() => {
            setConfirmTrust(false)
            void doInstall(selected)
          }}
        />
      )}
    </div>
  )
}

/** Splits a curated `displayName` like `"MySQL / MariaDB (MySqlConnector)"` into the human label and
 * the parenthesized real package name — `KnownLibraries.All`'s own doc comment describes exactly this
 * shape ("a human-readable label... not the bare package id"), but every entry bakes both into one
 * string. No entry lacks the parenthesized part today, but a label without one still renders fine (just
 * no subtitle) rather than breaking. */
function splitDisplayName(displayName: string): { label: string; packageName: string | null } {
  const match = /^(.*?)\s*\(([^)]+)\)\s*$/.exec(displayName)
  return match ? { label: match[1], packageName: match[2] } : { label: displayName, packageName: null }
}

function QuickAddChip({ entry, onPick }: { entry: KnownLibrarySummary; onPick: () => void }) {
  const { label, packageName } = splitDisplayName(entry.displayName)
  return (
    <button
      type="button"
      className="btn-link"
      title={entry.description}
      onClick={onPick}
      data-testid={`admin-libraries-chip-${entry.id}`}
      style={{ display: 'block', textAlign: 'left', width: '100%', padding: '3px 0' }}
    >
      <span>+ {label}</span>
      {packageName && <div className="chip-subtitle">{packageName}</div>}
    </button>
  )
}

function formatDownloads(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`
  if (n >= 1_000) return `${(n / 1_000).toFixed(1)}K`
  return String(n)
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
          <a href={`https://www.nuget.org/packages/${result.id}`} target="_blank" rel="noopener noreferrer">
            <strong>{result.id}</strong>
          </a>
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

/** Seconds since `active` last became `true`, ticking every second while it stays `true` — the only
 * feedback available for an install today short of streaming `dotnet publish`'s own output (a bigger
 * change, tracked separately): an install with a cold NuGet cache or a large dependency closure can run
 * long enough that a static "Installing…" label reads as hung. */
function useElapsedSeconds(active: boolean): number {
  const [seconds, setSeconds] = useState(0)
  useEffect(() => {
    if (!active) { setSeconds(0); return }
    // Already 0 here: the effect above already reset it, either just now (install finished) or on
    // this component's first render (the initial `useState(0)`) — nothing left to reset before timing.
    const start = Date.now()
    const id = window.setInterval(() => setSeconds(Math.floor((Date.now() - start) / 1000)), 1000)
    return () => window.clearInterval(id)
  }, [active])
  return seconds
}

function InstallCommand({
  selection, curated, installing, installedOk, installedFactoryType, onChangeVersion, onChangeFactoryType, onInstall,
}: {
  selection: Selection
  curated: boolean
  installing: boolean
  installedOk: boolean
  installedFactoryType: string | null
  onChangeVersion: (version: string) => void
  onChangeFactoryType: (factoryType: string) => void
  onInstall: () => void
}) {
  const { id, version, versionLocked, factoryType } = selection
  const hasVersion = !!version
  // A non-curated package no longer needs factoryType typed in before Install enables — leaving it
  // blank lets the server's reflection-assist (phase 122) try first, against the restored package
  // itself, before falling back to requiring one explicitly.
  const needsFactoryType = !curated
  const canInstall = hasVersion && !installing
  const command = `dbdatasync config library install ${id} --version ${version || '<v>'}`
  const elapsedSeconds = useElapsedSeconds(installing)

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
      {needsFactoryType && (
        <div className="row" style={{ gap: 8 }}>
          <span>Factory type:</span>
          <input
            type="text"
            className="input"
            value={factoryType}
            onChange={(e) => onChangeFactoryType(e.target.value)}
            placeholder='Optional — leave blank to try auto-detect, or "Namespace.FactoryClass, AssemblyName"'
            data-testid="admin-libraries-command-factory-type"
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
      {installedOk && (
        <div className="hint" style={{ color: 'var(--ok)' }}>
          Installed{installedFactoryType && !factoryType ? ` — detected factory: ${installedFactoryType}` : ''}.
        </div>
      )}
      <div className="row" style={{ gap: 8 }}>
        <button
          type="button"
          className="btn btn-sm"
          disabled={!canInstall}
          onClick={onInstall}
          data-testid="admin-libraries-install-button"
        >
          {installing ? (
            <span className="row" style={{ gap: 6 }}>
              <span className="spinner" aria-hidden="true" />
              Installing… ({elapsedSeconds}s)
            </span>
          ) : 'Install'}
        </button>
        <button
          type="button"
          className="btn-link quiet"
          disabled={!hasVersion}
          onClick={copy}
          data-testid="admin-libraries-command-copy"
        >
          Copy command
        </button>
      </div>
    </div>
  )
}

/** The one non-curated-install gate: installing a package DbDataSync didn't vet runs its code inside
 * this host with the host's own privileges — a catalog pick never shows this. */
function TrustInstallDialog({ id, busy, onConfirm, onCancel }: {
  id: string
  busy: boolean
  onConfirm: () => void
  onCancel: () => void
}) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onCancel() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onCancel])

  return (
    <div className="modal-backdrop" onMouseDown={(e) => { if (e.target === e.currentTarget) onCancel() }}>
      <div
        className="modal"
        role="dialog"
        aria-modal="true"
        aria-label="Confirm installing an unvetted package"
        data-testid="admin-libraries-trust-dialog"
      >
        <div className="card-head">
          <span className="card-title">Install "{id}"?</span>
        </div>
        <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          <span className="hint">
            Installing this package runs its code inside the DbDataSync host, with the host's own
            privileges — the same trust as a hook or a script. Only continue for a package you have
            vetted yourself.
          </span>
          <div className="row" style={{ gap: 8, justifyContent: 'flex-end' }}>
            <button type="button" className="btn" onClick={onCancel} data-testid="admin-libraries-trust-cancel">
              Cancel
            </button>
            <button
              type="button"
              className="btn btn-primary"
              disabled={busy}
              onClick={onConfirm}
              data-testid="admin-libraries-trust-confirm"
            >
              {busy ? 'Installing…' : 'Install anyway'}
            </button>
          </div>
        </div>
      </div>
    </div>
  )
}
