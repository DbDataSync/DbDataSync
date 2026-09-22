/** A byte count as a short, human string. Its own module for the same reason `tabClass.ts` is — a
 * component file that also exports a helper breaks fast refresh. */
export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}
