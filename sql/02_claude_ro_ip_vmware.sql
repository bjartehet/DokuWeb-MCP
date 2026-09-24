-- =================================================================
-- 02_claude_ro_ip_vmware.sql
-- Extends claude_ro with tables needed by ip_oppslag / ip_segment
-- (IP notes) and hent_server (VMware data).
-- Run manually against web01.enternett.no after 01_claude_ro.sql.
-- -----------------------------------------------------------------
-- 2026-09-24 Initial version
-- =================================================================

USE [timeregSQL];
GO

GRANT SELECT ON [dbo].[IP_merknad] TO [claude_ro];
GRANT SELECT ON [dbo].[VM-import]  TO [claude_ro];
GO

SELECT OBJECT_NAME(major_id) AS tabell, permission_name
FROM sys.database_permissions
WHERE grantee_principal_id = DATABASE_PRINCIPAL_ID(N'claude_ro')
ORDER BY tabell;
GO
