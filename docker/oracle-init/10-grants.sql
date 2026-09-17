-- Run once, automatically, on first container initialization — gvenzl/oracle-free's own entrypoint
-- executes every *.sql file under /container-entrypoint-initdb.d as `sqlplus / as sysdba`, after the
-- APP_USER has already been created (confirmed by reading container-entrypoint.sh directly, not
-- assumed). That connection lands in CDB$ROOT, not the app's own pluggable database, so the container
-- switch below is required before the grants mean anything.
--
-- Why these two, and only these two: OracleFlashbackReader (phase 148) needs EXECUTE on DBMS_FLASHBACK
-- to capture a precise SCN — confirmed by testing that an ordinary connection gets ORA-00904 without it,
-- a grant Oracle does not hand out by default. FLASHBACK ANY TABLE is granted too so the reader's own
-- integration tests can read tables regardless of which schema owns them; a deployment where the
-- connecting user always owns its own source tables does not need it at all (Oracle's ordinary
-- object-privilege model already gives an owner full rights on their own objects — also confirmed by
-- testing, not assumed).
ALTER SESSION SET CONTAINER = FREEPDB1;

GRANT EXECUTE ON DBMS_FLASHBACK TO dbdatasync;
GRANT FLASHBACK ANY TABLE TO dbdatasync;

EXIT;
