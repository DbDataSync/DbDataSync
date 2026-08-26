import type { KindOption } from './KindSelect'
import type { ReaderCapability, StagingCapability, WriterCapability } from '../api/types'

// Turns a capabilities response into pickable options. Each note is a capability the *driver*
// declared, surfaced so the choice between two Kinds is legible without knowing what the names mean.

export const readerOptions = (readers: ReaderCapability[]): KindOption[] =>
  readers.map((r) => ({ kind: r.kind, note: r.supportsSegmentation ? 'segmentable' : undefined }))

export const writerOptions = (writers: WriterCapability[]): KindOption[] =>
  writers.map((w) => ({ kind: w.kind, note: w.supportsReconciliation ? 'reconciling' : 'upsert-only' }))

export const stagingOptions = (providers: StagingCapability[]): KindOption[] =>
  providers.map((p) => ({ kind: p.kind }))
