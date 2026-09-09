import { DB_NAME, runSql, SOURCE_TABLE, TARGET_TABLE } from './test-db'

// The scratch repo itself — cleared, recreated, and seeded with the phase 118 descriptor driver and
// its library — is prepared synchronously in playwright.config.ts, not here. See that file's comment
// on why: it has to finish before the API's webServer command even launches, and global-setup.ts runs
// concurrently with (not strictly before) webServer.

export default async function globalSetup() {
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
