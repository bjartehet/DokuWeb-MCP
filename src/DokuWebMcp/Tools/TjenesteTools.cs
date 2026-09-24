using System.ComponentModel;
using DokuWebMcp.Db;
using Microsoft.Data.SqlClient;
using ModelContextProtocol.Server;

namespace DokuWebMcp.Tools;

[McpServerToolType]
public static class TjenesteTools
{
    // Flags on the kunder table, mirroring the filters in DokuWeb's kunder.aspx.
    private static readonly Dictionary<string, (string Where, string Beskrivelse)> Flagg = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sla"] = ("ku.sla = 1", "Kunder med SLA"),
        ["databehandleravtale"] = ("ku.dba = 1", "Kunder med databehandleravtale"),
        ["dba"] = ("ku.dba = 1", "Kunder med databehandleravtale"),
        ["topp30"] = ("ku.viktig = 1", "Topp 30-kunder"),
        ["topp 30"] = ("ku.viktig = 1", "Topp 30-kunder"),
        ["avtaletype"] = ("ku.Avtaletype IS NOT NULL AND ku.Avtaletype <> ''", "Kunder med avtaletype registrert"),
    };

    [McpServerTool(Name = "tjenester", Title = "Kunder per tjeneste", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("""
        Finner hvilke kunder som har en gitt tjeneste i DokuWeb.
        Uten argument: oversikt over tjenestetyper med antall kunder.
        Tjenestetyper (kunde_tjeneste): Office 365, E-post, Antivirus, Web, PC-drift, Driftsavtale, Backup,
        Drift, DNS, Hjemmekontor, SMS. Treffer også på beskrivelse (f.eks. «EnterTotal», «Exchange Online»).
        Kundeflagg: sla, databehandleravtale, topp30, avtaletype.
        Slettede kunder er utelatt.
        """)]
    public static async Task<string> Tjenester(
        Database db,
        [Description("Tjenestetype, beskrivelse eller kundeflagg. Utelat for oversikt.")]
        string? tjeneste = null,
        [Description("Filtrer på ansvarlig for tjenesten, f.eks. \"Bjarte\" eller \"TerjeB\".")]
        string? ansvar = null,
        CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(tjeneste))
            {
                var typer = await db.QueryAsync("""
                    SELECT t.tjeneste AS Tjeneste, COUNT(*) AS Registreringer, COUNT(DISTINCT t.kundenr) AS Kunder
                    FROM dbo.kunde_tjeneste t
                    JOIN dbo.Kunde k ON k.Kundenummer = t.kundenr
                    WHERE ISNULL(k.slettet, 0) = 0
                    GROUP BY t.tjeneste
                    ORDER BY Kunder DESC
                    """, ct: ct);
                var flagg = await db.QueryAsync("""
                    SELECT SUM(CASE WHEN ku.sla = 1 THEN 1 ELSE 0 END) AS sla,
                           SUM(CASE WHEN ku.dba = 1 THEN 1 ELSE 0 END) AS databehandleravtale,
                           SUM(CASE WHEN ku.viktig = 1 THEN 1 ELSE 0 END) AS topp30,
                           SUM(CASE WHEN ku.Avtaletype IS NOT NULL AND ku.Avtaletype <> '' THEN 1 ELSE 0 END) AS avtaletype
                    FROM dbo.kunder ku
                    JOIN dbo.Kunde k ON k.Kundenummer = ku.Kundeid
                    WHERE ISNULL(k.slettet, 0) = 0
                    """, ct: ct);
                return Database.ToJson(new { tjenestetyper = typer, kundeflagg = flagg[0] });
            }

            var ansvarParam = new SqlParameter("@ansvar", string.IsNullOrWhiteSpace(ansvar) ? DBNull.Value : $"%{ansvar.Trim()}%");

            if (Flagg.TryGetValue(tjeneste.Trim(), out var f))
            {
                // Where clause comes from the fixed dictionary above, never from user input.
                var kunder = await db.QueryAsync($"""
                    SELECT k.Kundenummer, k.Kundenavn, ku.Hovedansvar, ku.Avtaletype, ku.sla AS SLA,
                           ku.dba AS Databehandleravtale, ku.viktig AS Topp30
                    FROM dbo.kunder ku
                    JOIN dbo.Kunde k ON k.Kundenummer = ku.Kundeid
                    WHERE ISNULL(k.slettet, 0) = 0 AND {f.Where}
                      AND (@ansvar IS NULL OR ku.Hovedansvar LIKE @ansvar)
                    ORDER BY k.Kundenavn
                    """, [ansvarParam], ct: ct);
                return Database.ToJson(new { utvalg = f.Beskrivelse, antall = kunder.Count, kunder });
            }

            var rader = await db.QueryAsync("""
                SELECT k.Kundenummer, k.Kundenavn, t.tjeneste AS Tjeneste, t.beskrivelse AS Beskrivelse,
                       t.domene AS Domene, t.ansvar AS Ansvar, t.antall AS Antall, t.notat AS Notat,
                       NULLIF(t.serverID * CASE WHEN LTRIM(RTRIM(ISNULL(s.Server, ''))) = '' THEN 0 ELSE 1 END, 0) AS ServerID, s.Server, CASE WHEN LTRIM(RTRIM(ISNULL(s.Server, ''))) = '' THEN NULL ELSE s.Aktiv END AS ServerAktiv
                FROM dbo.kunde_tjeneste t
                JOIN dbo.Kunde k ON k.Kundenummer = t.kundenr
                LEFT JOIN dbo.servere s ON s.ServerID = t.serverID
                WHERE ISNULL(k.slettet, 0) = 0
                  AND (t.tjeneste = @t OR t.beskrivelse LIKE @like)
                  AND (@ansvar IS NULL OR t.ansvar LIKE @ansvar)
                ORDER BY k.Kundenavn, t.domene
                """, [new SqlParameter("@t", tjeneste.Trim()), new SqlParameter("@like", $"%{tjeneste.Trim()}%"), ansvarParam], ct: ct);

            if (rader.Count == 0)
                return $"Ingen kunder funnet med tjenesten «{tjeneste}». Kall tjenester uten argument for å se tilgjengelige typer.";

            return Database.ToJson(new
            {
                tjeneste,
                antall_kunder = rader.Select(r => r["Kundenummer"]).Distinct().Count(),
                antall_registreringer = rader.Count,
                registreringer = rader,
            });
        }
        catch (DatabaseException ex)
        {
            return ex.Message;
        }
    }
}
