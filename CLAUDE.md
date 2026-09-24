# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What this is

An MCP server (C#, .NET 10, official `ModelContextProtocol` SDK) that exposes the DokuWeb database (`timeregSQL` on `web01.enternett.no`, SQL Server) to Claude. Owned by Next Systems; users are IT operations staff.

- The DokuWeb web application itself lives in `C:\Utvikling\DokuWeb-Next` (ASP.NET WebForms). The roadmap is in `DokuWeb-Next/docs/mcp-plan.md`.
- Full database schema: `DokuWeb-Next/database-schema-utf8.sql`. Look up column names and types there before writing SQL.
- Existing report logic in DokuWeb-Next `.aspx.cs` files is the reference for "correct" numbers — mirror it when a tool answers the same question as a DokuWeb page.

## Build & test

```
dotnet build -c Release
```

Quick protocol smoke test (no DB needed): pipe `initialize`, `notifications/initialized`, `tools/list` as newline-delimited JSON-RPC into `dotnet src/DokuWebMcp/bin/Release/net10.0/DokuWebMcp.dll`.

The DB is only reachable over the work VPN from allow-listed IT addresses.

The registered MCP server runs the **Release** build, and any Claude session using it locks that DLL. Develop and test against the **Debug** build (`dotnet build -c Debug`); only build Release when the user has closed sessions using `dokuweb`.

## Rules

- **This repo is not DokuWeb-Next.** Its "no async / no DI" rule does not apply here — the MCP SDK is built on async and Microsoft.Extensions hosting/DI. Use idiomatic modern C#.
- **Read-only.** Tools run as SQL login `claude_ro` (SELECT only, see `sql/`). Never add write tools without explicit agreement; write access is planned for phase 4 with a separate `claude_rw` login, narrow tools and an audit log.
- **Parameterized SQL only** — never concatenate user input into SQL.
- All DB access goes through `Db/Database.cs` (row cap, timeout, friendly VPN/connectivity errors).
- **stdout is the MCP transport.** Never write to stdout (no `Console.WriteLine`); log via `ILogger` (goes to stderr).
- Tools catch `DatabaseException` and return its message as text, so Claude sees a useful error instead of a protocol failure.
- Secrets (connection strings, later Finago keys) go in `dotnet user-secrets` or environment variables (`DOKUWEB_` prefix), never in git.
- DB permission changes are written as numbered scripts in `sql/` and run manually — never executed by Claude.
- New tables used by a tool must be added to the GRANT list in a new `sql/` script.

## Tool conventions

- Tool names in Norwegian snake_case (`sok_kunde`, `hent_server`), one file per domain in `Tools/` (`KundeTools.cs`, `ServerTools.cs`, ...).
- Descriptions in Norwegian, written for Claude: what it returns, which ID to pass to other tools.
- Mark tools `ReadOnly = true, Idempotent = true, OpenWorld = false`.
- Return compact JSON via `Database.ToJson`, or a short Norwegian sentence when there are no hits.
- Code comments in English.

## Data model gotchas

- `Kunde` (core data, PK `Kundenummer`) vs `kunder` (operational extension, FK `Kundeid` → `Kunde.Kundenummer`). Join with `OUTER APPLY (SELECT TOP 1 ...)` to avoid duplicates.
- `kunder.dba` = has a data processing agreement (databehandleravtale), not "database admin". `kunder.viktig` = top 30 customer.
- `servere.Passord` is a password group (PG1, PG2, KP, Gen2), not a stored password. Exposed as `Passordgruppe`.
- `kunde_tjeneste.serverID = 118` is a placeholder server with a blank name meaning "no server" — tools null it out.
- `server_backup` is joined on `server_navn = servere.Server` in DokuWeb (ServerID is 0 on some rows); match on either.
- IP columns are `binary(4)` (network byte order). `Database` converts them to dotted strings; use `Database.IpParameter` for lookups. An IP is "i bruk" when linked to an active server or it has a row in `IP_merknad`.
- `IP_merknad` has many rows with NULL/blank `Merknad`; only non-blank notes mark an IP as in use.
- `[VM-import]` was empty as of 2026-09-24 (the import in `servere.aspx` hasn't been run), so `hent_server` usually reports no VMware data.
- **Time tracking:** `Timereg.Varighet_ansatt` = hours worked, `Varighet_kunde` = hours billed to customer (what DokuWeb sums). `Varighet` is legacy, `Fakturering` is always 0. `Varighet_kunde` is also set on internal projects - see `Tools/Intern.cs` for what counts as internal.
- `Timereg.Ansvarlig` and `Prosjekt.Ansvarlig` hold full names ("Bjarte Hetland"); `Ansatt.kortform` is the short form. Employee filters accept kortform, first name or full name.
- `Timereg.Beskrivelse` is ntext with a different collation than `Kommentar` - use `TimeregTools.BeskrivelseSql` (COLLATE DATABASE_DEFAULT).
- Avtaletype: 1 Faktureres fortløpende, 2 Forhåndsfakturert, 3 Internt, 4 Fakturerbare tjenester, 5 Overforbruk av timer, 6 Avtalt pris, 7 Internt prosjekt.
- Invoicing mirrors `til_fakturering_oversikt.aspx`: avtaletype 1, open projects, entries after `Prosjekt.[Siste faktura]`, from 2025-03-01 (before that EnterNett invoiced), excluding customers 300560 (NEXT SYSTEMS AS) and 300111 (TILFELDIGE KUNDER - invoiced directly, Siste faktura never updated).
- String values are trimmed and blank strings returned as null (legacy data has stray tabs/spaces).
- `Backup`, `Database` etc. are T-SQL reserved words — bracket them.
- `Sak_kundeview` is unused — ignore it.
- `Kunde.FinagoID` links customers to Finago (relevant for phase 4 invoicing).
