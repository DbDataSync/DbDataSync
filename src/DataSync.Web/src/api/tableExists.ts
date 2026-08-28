import type { TableMetadata } from './types'

/**
 * Whether a named table is one the catalog already has.
 *
 * Exact, case-sensitive comparison — the same one the server's own lookup makes. On an engine that
 * folds case, typing `orders` where the catalog says `Orders` reads as "does not exist yet" here and
 * the provisioner then finds the table and reports there is nothing to do. That is the harmless
 * direction to be wrong in; the other one would skip creating a table that genuinely is not there.
 *
 * `undefined` while the catalog is still loading, so a caller can tell "not there" from "not known
 * yet" and avoid flashing "will be created" at someone who picked an existing table.
 */
export function tableExists(
  tables: TableMetadata[] | undefined, schema: string, table: string,
): boolean | undefined {
  if (!table) return false
  if (!tables) return undefined
  return tables.some((t) => t.schema === schema && t.table === table)
}
