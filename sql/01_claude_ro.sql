-- =================================================================
-- 01_claude_ro.sql
-- Creates the read-only login used by the DokuWeb MCP server.
-- Run manually against web01.enternett.no by someone with sysadmin
-- (or securityadmin + db_owner on timeregSQL).
-- -----------------------------------------------------------------
-- 2026-09-24 Initial version - phase 1 tables (customers, servers,
--            IP, services)
-- =================================================================

-- 1. Server login. Replace the password before running.
USE [master];
GO
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'claude_ro')
    CREATE LOGIN [claude_ro]
        WITH PASSWORD = N'<<SETT_STERKT_PASSORD>>',
             DEFAULT_DATABASE = [timeregSQL],
             CHECK_POLICY = ON,
             CHECK_EXPIRATION = OFF;
GO

-- 2. Database user with SELECT on phase 1 tables only.
USE [timeregSQL];
GO
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'claude_ro')
    CREATE USER [claude_ro] FOR LOGIN [claude_ro] WITH DEFAULT_SCHEMA = [dbo];
GO

-- Customers
GRANT SELECT ON [dbo].[Kunde]          TO [claude_ro];
GRANT SELECT ON [dbo].[kunder]         TO [claude_ro];
GRANT SELECT ON [dbo].[kunde_tjeneste] TO [claude_ro];
-- Servers
GRANT SELECT ON [dbo].[servere]        TO [claude_ro];
GRANT SELECT ON [dbo].[server_backup]  TO [claude_ro];
GRANT SELECT ON [dbo].[Datasenter]     TO [claude_ro];
-- IP
GRANT SELECT ON [dbo].[IP_adresse]     TO [claude_ro];
GRANT SELECT ON [dbo].[server_ip]      TO [claude_ro];
GRANT SELECT ON [dbo].[IP_segment]     TO [claude_ro];
GO

-- 3. Verify (should list exactly the tables above).
SELECT OBJECT_NAME(major_id) AS tabell, permission_name
FROM sys.database_permissions
WHERE grantee_principal_id = DATABASE_PRINCIPAL_ID(N'claude_ro')
ORDER BY tabell;
GO
