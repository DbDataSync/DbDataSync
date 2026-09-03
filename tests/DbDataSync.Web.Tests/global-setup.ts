import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { DB_NAME, runSql, SOURCE_TABLE, TARGET_TABLE } from './test-db'

// Kept in sync with playwright.config.ts's scratchRepoRoot.
const scratchRepoRoot = path.join(os.tmpdir(), 'dbdatasync-web-e2e-scratch-repo')

export default async function globalSetup() {
  // Fresh config repo for the API to auto-init and auto-commit into during the test run.
  fs.rmSync(scratchRepoRoot, { recursive: true, force: true })
  fs.mkdirSync(scratchRepoRoot, { recursive: true })

  // Fresh SQL Server database with source (Change Tracking enabled) + target tables, seeded with two
  // rows — the same shape used throughout Phase 3/4/5's manual and automated verification.
  runSql(`
    IF DB_ID('${DB_NAME}') IS NOT NULL
    BEGIN
      ALTER DATABASE [${DB_NAME}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
      DROP DATABASE [${DB_NAME}];
    END
    CREATE DATABASE [${DB_NAME}];
    ALTER DATABASE [${DB_NAME}] SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = OFF);
  `)

  runSql(
    `
    CREATE TABLE dbo.[${SOURCE_TABLE}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);
    ALTER TABLE dbo.[${SOURCE_TABLE}] ENABLE CHANGE_TRACKING;
    CREATE TABLE dbo.[${TARGET_TABLE}] (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);
    INSERT INTO dbo.[${SOURCE_TABLE}] (Id, Name) VALUES (1, 'Widget'), (2, 'Gadget');
    `,
    DB_NAME,
  )
}
