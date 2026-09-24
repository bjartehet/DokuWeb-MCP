-- =================================================================
-- 03_claude_ro_timereg.sql
-- Phase 2: read access to time tracking and projects for claude_ro.
-- Run manually against web01.enternett.no after 01 and 02.
-- -----------------------------------------------------------------
-- 2026-09-24 Initial version
-- =================================================================

USE [timeregSQL];
GO

GRANT SELECT ON [dbo].[Prosjekt]   TO [claude_ro];
GRANT SELECT ON [dbo].[Timereg]    TO [claude_ro];
GRANT SELECT ON [dbo].[Avtaletype] TO [claude_ro];
GRANT SELECT ON [dbo].[Enhet]      TO [claude_ro];

-- Employees: only the columns needed to map kortform -> name.
-- (tilgang, phone numbers etc. are left out.)
GRANT SELECT ON [dbo].[Ansatt] ([Navn], [kortform], [Avdeling], [aktiv], [timereg_required]) TO [claude_ro];
GO

SELECT OBJECT_NAME(major_id) AS tabell,
       COL_NAME(major_id, minor_id) AS kolonne,
       permission_name
FROM sys.database_permissions
WHERE grantee_principal_id = DATABASE_PRINCIPAL_ID(N'claude_ro')
ORDER BY tabell, kolonne;
GO
