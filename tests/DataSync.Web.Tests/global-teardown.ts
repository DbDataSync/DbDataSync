import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { DB_NAME, runSql } from './test-db'

const scratchRepoRoot = path.join(os.tmpdir(), 'datasync-web-e2e-scratch-repo')

export default async function globalTeardown() {
  runSql(`
    IF DB_ID('${DB_NAME}') IS NOT NULL
    BEGIN
      ALTER DATABASE [${DB_NAME}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
      DROP DATABASE [${DB_NAME}];
    END
  `)

  fs.rmSync(scratchRepoRoot, { recursive: true, force: true })
}
