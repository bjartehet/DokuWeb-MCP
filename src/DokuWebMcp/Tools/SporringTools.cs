using System.ComponentModel;
using System.Text.RegularExpressions;
using DokuWebMcp.Db;
using Microsoft.Data.SqlClient;
using ModelContextProtocol.Server;

namespace DokuWebMcp.Tools;

[McpServerToolType]
public static partial class SporringTools
{
    // Defense in depth only - the real guard is that claude_ro has SELECT permission and nothing else.
    [GeneratedRegex(@"\b(insert|update|delete|merge|drop|alter|create|exec|execute|grant|revoke|deny|truncate|into|dbcc|shutdown|openrowset|opendatasource|openquery)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenKeywords();

    [McpServerTool(Name = "beskriv_tabeller", Title = "Beskriv tabeller", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("""
        Viser tabellene i DokuWeb-databasen som er tilgjengelige for lesing, med kolonner og datatyper.
        Bruk før run_readonly_query for å finne riktige kolonnenavn.
        Uten argument: liste over tabeller. Med tabell: kolonnene i den tabellen.
        """)]
    public static async Task<string> BeskrivTabeller(
        Database db,
        [Description("Tabellnavn (valgfritt). Utelat for å liste alle tilgjengelige tabeller.")]
        string? tabell = null,
        CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(tabell))
            {
                // INFORMATION_SCHEMA only shows objects the login has permission on.
                var tables = await db.QueryAsync("""
                    SELECT TABLE_NAME AS tabell, TABLE_TYPE AS type
                    FROM INFORMATION_SCHEMA.TABLES
                    ORDER BY TABLE_NAME
                    """, ct: ct);
                return Database.ToJson(new { tabeller = tables });
            }

            var cols = await db.QueryAsync("""
                SELECT COLUMN_NAME AS kolonne, DATA_TYPE AS type, CHARACTER_MAXIMUM_LENGTH AS lengde, IS_NULLABLE AS nullbar
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_NAME = @t
                ORDER BY ORDINAL_POSITION
                """, [new SqlParameter("@t", tabell.Trim())], ct: ct);

            return cols.Count == 0
                ? $"Fant ikke tabellen «{tabell}», eller den er ikke tilgjengelig for lesing."
                : Database.ToJson(new { tabell, merknad = "binary(4)-kolonner er IPv4-adresser og vises som tekst i spørringsresultater.", kolonner = cols });
        }
        catch (DatabaseException ex)
        {
            return ex.Message;
        }
    }

    [McpServerTool(Name = "run_readonly_query", Title = "Kjør SQL-spørring (kun lesing)", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("""
        Kjører en egendefinert SELECT-spørring (T-SQL, SQL Server) mot DokuWeb-databasen timeregSQL.
        Bruk bare når de faste verktøyene (sok_kunde, hent_kunde, sok_server, hent_server, ip_oppslag,
        ip_segment, tjenester) ikke dekker spørsmålet. Kjør beskriv_tabeller først for kolonnenavn.
        Kun SELECT/WITH er tillatt. Bruk TOP eller WHERE for å begrense resultatet; maks 500 rader returneres.
        Tips: Kunde.Kundenummer = kunder.Kundeid = kunde_tjeneste.kundenr = servere.kundeid.
        IP-kolonner er binary(4) og vises som tekst; sammenlign med 0xC0A80101-format i WHERE.
        Kolonner som er reserverte ord eller har mellomrom/spesialtegn må ha hakeparenteser:
        [Backup], [Database], [Guest OS], [Admin#bruker], [VM-import], [Host CPU].
        """)]
    public static async Task<string> RunReadonlyQuery(
        Database db,
        [Description("SELECT-spørringen som skal kjøres.")]
        string sql,
        [Description("Maks antall rader (1-500). Standard 200.")]
        int maks = 200,
        CancellationToken ct = default)
    {
        var trimmed = (sql ?? "").Trim().TrimEnd(';').Trim();
        if (trimmed.Length == 0)
            return "Oppgi en spørring.";
        if (!(trimmed.StartsWith("select", StringComparison.OrdinalIgnoreCase)
              || trimmed.StartsWith("with", StringComparison.OrdinalIgnoreCase)))
            return "Kun spørringer som starter med SELECT eller WITH er tillatt.";
        if (trimmed.Contains(';'))
            return "Kun én spørring om gangen (fjern semikolon).";
        var forbidden = ForbiddenKeywords().Match(trimmed);
        if (forbidden.Success)
            return $"Nøkkelordet «{forbidden.Value}» er ikke tillatt i lesespørringer.";

        try
        {
            var (rows, truncated) = await db.QueryWithTruncationAsync(trimmed, maxRows: Math.Clamp(maks, 1, Database.MaxRows), ct: ct);
            return Database.ToJson(new
            {
                antall = rows.Count,
                avkortet = truncated ? $"Ja - flere rader finnes. Begrens spørringen eller øk maks (maks {Database.MaxRows})." : null,
                rader = rows,
            });
        }
        catch (DatabaseException ex)
        {
            return ex.Message;
        }
    }
}
