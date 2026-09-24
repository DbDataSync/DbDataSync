import type { ReaderConfig, ReplicationTaskConfig } from '../../api/types'
import type { PipelineOverrides } from './MappingPipelineCard'

/**
 * The reader whose entire configuration is a query somebody wrote, and the option that holds it.
 *
 * Spelled once here rather than in each of the three places that ask about it. Matched by Kind, which
 * is the only handle the SPA has: capabilities describe *what* a reader takes, never *why*, and a
 * source tab that swaps its pickers for an editor is making a judgement about the shape of a
 * particular reader that no declaration expresses.
 *
 * **Two Kind names, not one.** "DuckDbQuery" is DuckDB's own — the reader began there, and existing
 * mappings already have it saved. "Query" is what every other driver offers the identical reader under
 * (see the backend's own `RawQueryRegistration`): a Postgres or MsSql source reporting "DuckDbQuery" as
 * its reader kind would be a confusing label for a capability that has nothing to do with DuckDB.
 */
export const QUERY_READER_KINDS: ReadonlySet<string> = new Set(['DuckDbQuery', 'Query'])
/** What a fresh query-source reader override is seeded with, absent any existing reader to clone —
 * the neutral name, since DuckDB mappings always already have a reader (theirs) to clone from instead. */
export const DEFAULT_QUERY_READER_KIND = 'Query'
export const QUERY_OPTION = 'query'

/** This mapping's reader: its own override, or the replication's if it has not overridden one. */
export function effectiveReader(
  task: ReplicationTaskConfig | undefined,
  pipeline: PipelineOverrides,
): ReaderConfig | undefined {
  return pipeline.readerOverride ?? task?.changeProcessing?.reader
}

export function isQuerySource(
  task: ReplicationTaskConfig | undefined,
  pipeline: PipelineOverrides,
): boolean {
  const kind = effectiveReader(task, pipeline)?.kind
  return kind !== undefined && QUERY_READER_KINDS.has(kind)
}

export function queryOf(
  task: ReplicationTaskConfig | undefined,
  pipeline: PipelineOverrides,
): string {
  return effectiveReader(task, pipeline)?.options?.[QUERY_OPTION] ?? ''
}

/**
 * Editing the query from the source tab, given that the query lives in the reader's options.
 *
 * **Typing into it overrides the reader**, seeding the override from whatever is currently in effect
 * — the same gesture `MappingPipelineCard`'s natural-key field already makes, and for the same
 * reason: there is nowhere else for the value to live. It is also the right default here rather than
 * merely the available one. A query is what *this table* reads; two mappings sharing a replication's
 * reader almost never want to share its statement, and quietly writing into the replication's reader
 * from a table's own tab would change every other table that inherits it.
 */
export function withQuery(
  task: ReplicationTaskConfig | undefined,
  pipeline: PipelineOverrides,
  query: string,
): PipelineOverrides {
  const base = pipeline.readerOverride
    ?? structuredClone(task?.changeProcessing?.reader)
    ?? { kind: DEFAULT_QUERY_READER_KIND, options: {} }

  return {
    ...pipeline,
    readerOverride: { ...base, options: { ...base.options, [QUERY_OPTION]: query } },
  }
}
