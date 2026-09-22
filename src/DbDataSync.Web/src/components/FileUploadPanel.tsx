import { useRef, useState } from 'react'
import { useUploadFiles } from '../api/hooks'
import type { UploadResult } from '../api/types'

/**
 * The `files/` store's own upload control — a file input plus per-file results (each file in a
 * multi-file request is reported independently, not all-or-nothing over one stale name). Extracted
 * from `FilesPage` (previously page-local) so the driver-authoring form's own jar picker can embed the
 * identical upload mechanism instead of a second copy.
 * <para>
 * `accept`/`hint` are parameterized rather than hardcoded to `.jar` — this store isn't jar-specific by
 * design (see `user-provided-files-store.md`), even though every caller today happens to want jars.
 * `onUploaded` fires with the names that actually succeeded, so an embedding picker (unlike this
 * component's own standalone use on `FilesPage`, which just shows the results) can react — e.g.
 * pre-selecting a newly uploaded jar.
 * </para>
 */
export function FileUploadPanel({
  accept = '.jar', hint = '.jar files only, for now.', onUploaded,
}: {
  accept?: string
  hint?: string
  onUploaded?: (names: string[]) => void
}) {
  const upload = useUploadFiles()
  const [uploadError, setUploadError] = useState<unknown>(null)
  const [uploadResults, setUploadResults] = useState<UploadResult[] | null>(null)
  const fileInputRef = useRef<HTMLInputElement>(null)

  const doUpload = async (fileList: FileList | null) => {
    if (!fileList || fileList.length === 0) return
    setUploadError(null)
    setUploadResults(null)
    try {
      const results = await upload.mutateAsync(fileList)
      setUploadResults(results)
      const succeeded = results.filter((r) => r.succeeded).map((r) => r.name)
      if (succeeded.length > 0) onUploaded?.(succeeded)
    } catch (err) {
      setUploadError(err)
    } finally {
      if (fileInputRef.current) fileInputRef.current.value = ''
    }
  }

  return (
    <div className="card" data-testid="files-upload-panel">
      <div className="card-head">
        <span className="card-title">Upload a file</span>
      </div>
      <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
        <div className="row" style={{ gap: 8 }}>
          <input
            ref={fileInputRef}
            type="file"
            accept={accept}
            multiple
            onChange={(e) => void doUpload(e.target.files)}
            disabled={upload.isPending}
            data-testid="files-upload-input"
          />
          {upload.isPending && <span className="hint">Uploading…</span>}
        </div>
        <div className="hint">{hint}</div>
        {uploadResults && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
            {uploadResults.map((r) => (
              <div
                key={r.name}
                className="hint"
                style={{ color: r.succeeded ? 'var(--ok)' : 'var(--bad)' }}
                data-testid={`files-upload-result-${r.name}`}
              >
                {r.name}: {r.succeeded ? 'uploaded' : r.error}
              </div>
            ))}
          </div>
        )}
        {uploadError !== null && (
          <div className="hint" style={{ color: 'var(--bad)' }}>
            {uploadError instanceof Error ? uploadError.message : 'Upload failed.'}
          </div>
        )}
      </div>
    </div>
  )
}
