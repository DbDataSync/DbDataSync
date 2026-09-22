import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { DB_NAME, runSql } from './test-db'

const scratchRepoRoot = path.join(os.tmpdir(), 'dbdatasync-web-e2e-scratch-repo')

export default async function globalTeardown() {
  runSql(`
    IF DB_ID('${DB_NAME}') IS NOT NULL
    BEGIN
      ALTER DATABASE [${DB_NAME}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
      DROP DATABASE [${DB_NAME}];
    END
  `)

  // The API can still be writing into this directory as teardown runs (observed: ENOTEMPTY here after
  // every one of 116 tests passed, on a commit that touched neither the API nor this file — CI run
  // 35496282777). force:true only silences "already gone"; it does not retry a directory that changed
  // out from under the delete. maxRetries covers ENOTEMPTY specifically (Node's own retry list), and a
  // leaked scratch dir under the OS temp path is the OS's to clean up either way — worth a loud warning,
  // not a red job that already ran every test correctly. See
  // architecture/planning/todo/follow-up-a-temp-dir-that-cannot-be-deleted-fails-a-job-whose-tests-all-passed.md.
  try {
    fs.rmSync(scratchRepoRoot, { recursive: true, force: true, maxRetries: 10, retryDelay: 200 })
  } catch (err) {
    console.error(`WARNING: could not delete scratch repo '${scratchRepoRoot}': ${err}. Leaving it for the OS to reclaim.`)
  }
}
