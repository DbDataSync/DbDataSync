-- Run once, automatically, on first container initialization — see 10-grants.sql's own header comment
-- for the mechanism.
--
-- Why a table needs provisioning here at all, rather than created fresh by each test: confirmed by
-- extensive live testing (bind vs. literal SCN bounds, delay up to 90s, a same-table DML "warm-up", a
-- fresh connection, no primary key/index, explicit COMMITs — every one of those ruled out) that Oracle's
-- Flashback Version Query genuinely cannot produce VERSIONS BETWEEN SCN history for a table whose own
-- CREATE TABLE is very recent, even when the requested SCN window falls entirely after creation —
-- ORA-01466 ("table definition has changed"). A table that has simply existed for a while has no such
-- problem, SCN freshness of the query itself is irrelevant. This table exists purely to have real age by
-- the time DbDataSync.Drivers.Oracle.Tests.OracleFlashbackReaderTests runs against it — each test cleans
-- its own rows out rather than the table being dropped and recreated per test.
ALTER SESSION SET CONTAINER = FREEPDB1;
ALTER SESSION SET CURRENT_SCHEMA = dbdatasync;

CREATE TABLE fb_probe_shared (
    id   NUMBER PRIMARY KEY,
    name VARCHAR2(50) NOT NULL
);

EXIT;
