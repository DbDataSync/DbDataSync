import { execFileSync } from 'node:child_process'

export const DB_NAME = 'DataSyncPlaywrightTest'
export const SOURCE_TABLE = 'PwSrcItems'
export const TARGET_TABLE = 'PwTgtItems'
export const SRC_CONNECTION_NAME = 'playwright-src'
export const TGT_CONNECTION_NAME = 'playwright-tgt'
export const SA_PASSWORD = 'DataSync_Test_Pw1'

export function runSql(sql: string, database?: string): void {
  const args = ['exec', 'datasync-mssql-source', '/opt/mssql-tools/bin/sqlcmd', '-S', 'localhost', '-U', 'sa', '-P', SA_PASSWORD]
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
  const args = ['exec', 'datasync-mssql-source', '/opt/mssql-tools/bin/sqlcmd', '-S', 'localhost', '-U', 'sa', '-P', SA_PASSWORD, '-h', '-1']
  if (database) args.push('-d', database)
  args.push('-Q', sql)
  return execFileSync('docker', args, { encoding: 'utf-8' })
}
