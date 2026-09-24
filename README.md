# DokuWeb-MCP

MCP-server som gir Claude lesetilgang til DokuWeb-databasen (`timeregSQL` på `web01.enternett.no`).
Se planen i `DokuWeb-Next/docs/mcp-plan.md`.

Status: fase 1 (kunder, servere, IP, tjenester) og fase 2 (timeføring og fakturagrunnlag) er på plass.

## Oppsett (én gang)

1. **Databasebruker.** Sett passord i `sql/01_claude_ro.sql` og kjør skriptet mot `web01.enternett.no` (SSMS). Ikke sjekk inn passordet. Kjør deretter `sql/02_claude_ro_ip_vmware.sql` og `sql/03_claude_ro_timereg.sql`.

2. **Tilkoblingsstreng** (lagres i user secrets, utenfor repoet):
   ```
   dotnet user-secrets set "ConnectionStrings:DokuWeb" "Server=web01.enternett.no;Database=timeregSQL;User ID=claude_ro;Password=<passord>;TrustServerCertificate=True" --project C:\Utvikling\Dokuweb-MCP\src\DokuWebMcp
   ```
   Kontroller med `dotnet user-secrets list --project C:\Utvikling\Dokuweb-MCP\src\DokuWebMcp`.
   `TrustServerCertificate=True` trengs hvis SQL Server bruker et selvsignert sertifikat.

3. **Bygg:**
   ```
   dotnet build -c Release
   ```

4. **Registrer i Claude Code** (tilgjengelig i alle prosjekter):
   ```
   claude mcp add --scope user dokuweb -- dotnet C:\Utvikling\Dokuweb-MCP\src\DokuWebMcp\bin\Release\net10.0\DokuWebMcp.dll
   ```
   **Claude Desktop:** legg til i `%APPDATA%\Claude\claude_desktop_config.json`:
   ```json
   {
     "mcpServers": {
       "dokuweb": {
         "command": "dotnet",
         "args": ["C:\\Utvikling\\Dokuweb-MCP\\src\\DokuWebMcp\\bin\\Release\\net10.0\\DokuWebMcp.dll"]
       }
     }
   }
   ```

VPN må være tilkoblet — databasen er kun tilgjengelig fra godkjente IT-adresser.

## Verktøy

| Verktøy | Beskrivelse |
|---------|-------------|
| `sok_kunde` | Søk på kundenavn, kallenavn, org.nr eller kundenummer |
| `hent_kunde` | Samlet kundebilde: kundedata, driftsinfo, tjenester og aktive servere |
| `sok_server` | Søk/filtrer servere på fritekst, kunde, OS, datasenter, ansvarlig |
| `hent_server` | Alle detaljer om én server: IP-er, backup-jobber, tjenester, VMware-data, driftsnotat |
| `ip_oppslag` | Hvem bruker en IP-adresse, segment, merknad, ledig/i bruk |
| `ip_segment` | Oversikt over IP-segmenter, eller adresser i ett segment (evt. bare ledige) |
| `tjenester` | Hvilke kunder har en tjeneste (M365, web, antivirus, EnterTotal …) eller et flagg (SLA, databehandleravtale, topp 30) |
| `sok_prosjekt` | Søk etter prosjekter i timeregistreringen |
| `hent_prosjekt` | Prosjektavtale, timer siden siste faktura, timer per måned, siste føringer |
| `timer` | Summer timer for en periode, gruppert på kunde/prosjekt/ansatt/enhet/avtaletype/måned/uke/dag |
| `timeforinger` | Enkeltstående timeføringer med fritekstsøk |
| `til_fakturering` | Fakturagrunnlag (som DokuWebs «Til fakturering»), med detaljer per enhet |
| `forhandsfakturerte` | Status for forhåndsfakturerte prosjekter: avtalt, brukt, rest |
| `beskriv_tabeller` | Tabeller og kolonner som er tilgjengelige for lesing |
| `run_readonly_query` | Fri SELECT-spørring når de faste verktøyene ikke strekker til |

## Utvikling

En Claude-økt som bruker serveren låser `bin\Release\...\DokuWebMcp.dll`, så `dotnet build -c Release` feiler mens en slik økt kjører.

- Utvikle og test med **Debug**-bygget (`dotnet build -c Debug`), som ikke er låst.
- Når endringene er klare: lukk økter som bruker `dokuweb`, kjør `dotnet build -c Release`, og start en ny økt.

## Struktur

```
sql/01_claude_ro.sql          Lesebruker med SELECT på fase 1-tabeller
src/DokuWebMcp/Program.cs     Oppstart (stdio-transport)
src/DokuWebMcp/Db/            Databasetilgang (parameterisert, radtak, tidsgrense)
src/DokuWebMcp/Tools/         MCP-verktøy, én fil per område
```
