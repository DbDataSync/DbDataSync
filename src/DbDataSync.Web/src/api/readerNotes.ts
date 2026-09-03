import type { ReaderCapability } from './types'

/** The short notes shown beside a reader's Kind. Both come from the driver's live capabilities rather
 * than from matching Kind strings, so a reader added later describes itself correctly with no change
 * here. */
export function readerNotes(reader: ReaderCapability): string[] {
  const notes: string[] = []
  if (reader.supportsSegmentation) notes.push('segmentable')
  // Stated, not prevented: append-only and append/update-only tables are well served by a reader that
  // cannot see deletes, and the operator is better placed than this picker to know which kind of table
  // this is. What it must not do is let someone assume deletes are covered when they are not.
  if (!reader.detectsDeletes) notes.push('does not detect deletes')
  return notes
}
