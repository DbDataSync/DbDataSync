import { execFileSync } from 'node:child_process'

export const DB_NAME = 'DataSyncPlaywrightTest'
export const SOURCE_TABLE = 'PwSrcItems'
export const TARGET_TABLE = 'PwTgtItems'
export const SRC_CONNECTION_NAME = 'playwright-src'
export const TGT_CONNECTION_NAME = 'playwright-tgt'
export const SA_PASSWORD = 'DataSync_Test_Pw1'

/**
 * Where sqlcmd lives in the running container, and whether it needs `-C`.
 *
 * The path moved: images from 2025 onward ship `/opt/mssql-tools18` only, and that sqlcmd defaults to
 * encrypted connections, so it fails against the container's self-signed certificate without `-C`.
 * `:2022-latest` moves, so a machine can be holding either generation — resolved once by asking the
 * container rather than assuming.
 */
let sqlcmd: string[] | undefined

function sqlcmdArgs(): string[] {
  if (sqlcmd) return sqlcmd
  try {
    execFileSync('docker', ['exec', 'datasync-mssql-source', 'test', '-x', '/opt/mssql-tools18/bin/sqlcmd'])
    sqlcmd = ['/opt/mssql-tools18/bin/sqlcmd', '-C']
  } catch {
    sqlcmd = ['/opt/mssql-tools/bin/sqlcmd']
  }
  return sqlcmd
}

export function runSql(sql: string, database?: string): void {
  const args = ['exec', 'datasync-mssql-source', ...sqlcmdArgs(), '-S', 'localhost', '-U', 'sa', '-P', SA_PASSWORD]
  if (database) args.push('-d', database)
  args.push('-Q', sql)
  execFileSync('docker', args, { stdio: 'inherit' })
}

/**
 * Same as runSql, but captures and returns stdout instead of inheriting it — for assertions.
 * Passes `-h -1` (no column headers) so callers get just data rows back, not sqlcmd's header +
 * separator lines mixed in with them.
 */
export function querySql(sql: string, database?: string): string {
  const args = ['exec', 'datasync-mssql-source', ...sqlcmdArgs(), '-S', 'localhost', '-U', 'sa', '-P', SA_PASSWORD, '-h', '-1']
  if (database) args.push('-d', database)
  args.push('-Q', sql)
  return execFileSync('docker', args, { encoding: 'utf-8' })
}
