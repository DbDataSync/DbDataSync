import type { EndpointRef, ResolvedRef, TableSpec } from './types'

/**
 * The endpoint a mapping side actually uses, once the replication's is applied. Mirrors the server's
 * `EndpointResolution`: each field falls back independently, so a mapping can override just the
 * database on the replication's connection.
 */
export function resolveSide(inherited: EndpointRef | null, spec: TableSpec): ResolvedRef {
  return {
    connectionName: spec.connectionName ?? inherited?.connectionName ?? '',
    database: spec.database ?? inherited?.database ?? '',
    schema: spec.schema,
    table: spec.table,
  }
}
