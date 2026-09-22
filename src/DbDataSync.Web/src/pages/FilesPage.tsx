import { useRef, useState } from 'react'
import { DriversTabs } from '../components/DriversTabs'
import { AppShell } from '../components/AppShell'
import { ErrorBanner } from '../components/ErrorBanner'
import { useIsAdmin } from '../components/useIsAdmin'
import { useFiles, useRemoveFile, useUploadFiles } from '../api/hooks'
import type { FileSummary } from '../api/types'

const COLUMNS = '2fr 1fr 1.4fr 1.4fr 1fr'

/**
 * Phase 173V — `files/`, a standard place for anything an operator uploads for a driver to reference (a
 * JDBC jar, so far the only real case). Modeled directly on `LibrariesPage`'s own row/remove/force
 * pattern — deliberately simpler: no search box, no known-catalog chips, nothing that screen has that a
 * plain "manage your files" screen doesn't need.
 */
export function FilesPage() {
  const isAdmin = useIsAdmin()
  const { data: files, isLoading, error } = useFiles()
  const remove = useRemoveFile()
  const upload = useUploadFiles()
  const [removeError, setRemoveError] = useState<unknown>(null)
  const [uploadError, setUploadError] = useState<unknown>(null)
  const [uploadResults, setUploadResults] = useState<{ name: string; succeeded: boolean; error: string | null }[] | null>(null)
  const fileInputRef = useRef<HTMLInputElement>(null)

  if (!isAdmin) {
    return (
      <AppShell crumbs={[{ label: 'Drivers' }]} tabs={<DriversTabs />}>
        <div className="pane">
          <div className="empty">This screen is for administrators.</div>
        </div>
      </AppShell>
    )
  }

  const doRemove = async (name: string, force: boolean) => {
    setRemoveError(null)
    try {
      await remove.mutateAsync({ name, force })
    } catch (err) {
      setRemoveError(err)
    }
  }

  const doUpload = async (fileList: FileList | null) => {
    if (!fileList || fileList.length === 0) return
    setUploadError(null)
    setUploadResults(null)
    try {
      const results = await upload.mutateAsync(fileList)
      setUploadResults(results)
    } catch (err) {
      setUploadError(err)
    } finally {
      if (fileInputRef.current) fileInputRef.current.value = ''
    }
  }

  return (
    <AppShell crumbs={[{ label: 'Drivers' }]} tabs={<DriversTabs />}>
      <div className="pane">
        <div className="page-head">
          <h1 className="page-title">Files</h1>
          <span className="page-note">
            Files an operator has uploaded for a driver to reference — a JDBC driver jar, so far the only
            real case. Distinct from Libraries (restored from NuGet): these are supplied, not restored.
          </span>
        </div>

        <ErrorBanner error={error ?? removeError ?? uploadError} />

        <div className="card flush" data-testid="admin-files-table">
          <div className="grid-head" style={{ gridTemplateColumns: COLUMNS, gap: 14 }}>
            <span>Name</span><span>Size</span><span>Uploaded</span><span>Used by</span><span></span>
          </div>
          {isLoading && <div className="empty">Loading…</div>}
          {!isLoading && (files ?? []).length === 0 && <div className="empty">No files uploaded.</div>}
          {(files ?? []).map((file) => (
            <FileRow key={file.name} file={file} onRemove={doRemove} busy={remove.isPending} />
          ))}
        </div>

        <div className="card" data-testid="admin-files-upload-panel" style={{ marginTop: 20 }}>
          <div className="card-head">
            <span className="card-title">Upload a file</span>
          </div>
          <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
            <div className="row" style={{ gap: 8 }}>
              <input
                ref={fileInputRef}
                type="file"
                accept=".jar"
                multiple
                onChange={(e) => void doUpload(e.target.files)}
                disabled={upload.isPending}
                data-testid="admin-files-upload-input"
              />
              {upload.isPending && <span className="hint">Uploading…</span>}
            </div>
            <div className="hint">.jar files only, for now.</div>
            {uploadResults && (
              <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
                {uploadResults.map((r) => (
                  <div
                    key={r.name}
                    className="hint"
                    style={{ color: r.succeeded ? 'var(--ok)' : 'var(--bad)' }}
                    data-testid={`admin-files-upload-result-${r.name}`}
                  >
                    {r.name}: {r.succeeded ? 'uploaded' : r.error}
                  </div>
                ))}
              </div>
            )}
          </div>
        </div>
      </div>
    </AppShell>
  )
}

function FileRow({ file, onRemove, busy }: {
  file: FileSummary
  onRemove: (name: string, force: boolean) => void
  busy: boolean
}) {
  const inUse = file.usedBy.length > 0

  const removeDirectly = () => {
    if (window.confirm(`Remove file '${file.name}'?`)) onRemove(file.name, false)
  }

  const forceRemove = () => {
    if (window.confirm(
      `'${file.name}' is still named by: ${file.usedBy.join(', ')}. Removing it anyway will make ` +
      `${file.usedBy.length === 1 ? 'that driver' : 'those drivers'} fail to load on the next restart. Continue?`,
    )) {
      onRemove(file.name, true)
    }
  }

  return (
    <div
      className="grid-row"
      style={{ gridTemplateColumns: COLUMNS, gap: 14 }}
      data-testid={`admin-file-row-${file.name}`}
    >
      <span className="mono">{file.name}</span>
      <span className="hint">{formatBytes(file.sizeBytes)}</span>
      <span className="hint">{new Date(file.uploadedAt).toLocaleString()}</span>
      <span className="hint">
        {inUse ? file.usedBy.join(', ') : <span className="faint">unused</span>}
      </span>
      <span className="row" style={{ gap: 8, justifyContent: 'flex-end' }}>
        <button
          type="button"
          className="btn-link quiet"
          disabled={inUse || busy}
          title={inUse ? `Still named by: ${file.usedBy.join(', ')}` : undefined}
          onClick={removeDirectly}
          data-testid={`admin-file-remove-${file.name}`}
        >
          Remove
        </button>
        {inUse && (
          <button
            type="button"
            className="btn-link quiet"
            disabled={busy}
            onClick={forceRemove}
            data-testid={`admin-file-force-remove-${file.name}`}
          >
            Force
          </button>
        )}
      </span>
    </div>
  )
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}
