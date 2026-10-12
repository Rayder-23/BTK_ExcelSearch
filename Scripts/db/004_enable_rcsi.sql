-- Close the app and any SSMS windows connected to this database first;
-- WITH ROLLBACK IMMEDIATE disconnects other sessions.
IF EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND is_read_committed_snapshot_on = 0)
    ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;

SELECT name, is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID();