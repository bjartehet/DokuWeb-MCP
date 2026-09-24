using System.ComponentModel;
using DokuWebMcp.Db;
using Microsoft.Data.SqlClient;
using ModelContextProtocol.Server;

namespace DokuWebMcp.Tools;

[McpServerToolType]
public static class KundeTools
{
    [McpServerTool(Name = "sok_kunde", Title = "Søk etter kunde", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("""
        Søker etter kunder i DokuWeb på kundenavn, kallenavn, organisasjonsnummer eller kundenummer.
        Returnerer kundenummer, navn, org.nr, Finago-ID og driftsinfo (hovedansvarlig tekniker,
        avtaletype, SLA, aktiv). Bruk kundenummeret videre i andre verktøy.
        Slettede kunder er utelatt med mindre inkluderSlettede=true.
        """)]
    public static async Task<string> SokKunde(
        Database db,
        [Description("Søketekst: del av navn/kallenavn, org.nr (mellomrom ignoreres) eller eksakt kundenummer.")]
        string sok,
        [Description("Ta med kunder merket som slettet. Standard false.")]
        bool inkluderSlettede = false,
        [Description("Maks antall treff (1-100). Standard 25.")]
        int maks = 25,
        CancellationToken ct = default)
    {
        sok = (sok ?? "").Trim();
        if (sok.Length == 0)
            return "Oppgi en søketekst.";
        maks = Math.Clamp(maks, 1, 100);

        // Kunde = core customer data; kunder = operational extension (FK Kundeid -> Kunde.Kundenummer).
        // OUTER APPLY TOP 1 guards against duplicate rows in kunder.
        const string sql = """
            SELECT TOP (@maks)
                k.Kundenummer, k.Kundenavn, k.Kallenavn, k.Organisasjonsnummer,
                k.Status, k.Poststed, k.FinagoID, k.slettet,
                ku.Hovedansvar, ku.Avtaletype, ku.sla AS SLA, ku.Aktiv, ku.viktig AS Topp30
            FROM dbo.Kunde k
            OUTER APPLY (SELECT TOP 1 * FROM dbo.kunder x WHERE x.Kundeid = k.Kundenummer) ku
            WHERE (@inkluderSlettede = 1 OR ISNULL(k.slettet, 0) = 0)
              AND (   k.Kundenavn LIKE @like
                   OR k.Kallenavn LIKE @like
                   OR REPLACE(k.Organisasjonsnummer, ' ', '') LIKE @orgLike
                   OR CAST(k.Kundenummer AS nvarchar(20)) = @sok)
            ORDER BY k.Kundenavn
            """;

        var parameters = new[]
        {
            new SqlParameter("@maks", maks),
            new SqlParameter("@inkluderSlettede", inkluderSlettede),
            new SqlParameter("@like", $"%{sok}%"),
            new SqlParameter("@orgLike", $"%{sok.Replace(" ", "")}%"),
            new SqlParameter("@sok", sok),
        };

        try
        {
            var rows = await db.QueryAsync(sql, parameters, maks, ct);
            return rows.Count == 0
                ? $"Ingen kunder funnet for «{sok}»."
                : Database.ToJson(new { antall = rows.Count, kunder = rows });
        }
        catch (DatabaseException ex)
        {
            return ex.Message;
        }
    }

    [McpServerTool(Name = "hent_kunde", Title = "Hent kunde", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("""
        Henter samlet kundebilde fra DokuWeb for ett kundenummer: kundedata, kontaktinfo, driftsinfo
        (hovedansvarlig, avtaletype, SLA, databehandleravtale, topp 30, notater), tjenester
        (e-post, M365, web, antivirus, backup osv. med domene, ansvarlig og server) og aktive servere.
        Finn kundenummeret med sok_kunde først.
        """)]
    public static async Task<string> HentKunde(
        Database db,
        [Description("Kundenummer (Kunde.Kundenummer), f.eks. 300494.")]
        int kundenummer,
        CancellationToken ct = default)
    {
        var p = () => new[] { new SqlParameter("@nr", kundenummer) };

        try
        {
            var kunde = await db.QueryAsync("""
                SELECT k.Kundenummer, k.Kundenavn, k.Kallenavn, k.Organisasjonsnummer, k.Status,
                       k.Adresse, k.Postnummer, k.Poststed, k.Telefon, k.FinagoID, k.slettet,
                       ku.Hovedansvar, ku.Avtaletype, ku.Aktiv, ku.sla AS SLA,
                       ku.dba AS Databehandleravtale, ku.viktig AS Topp30, ku.partner AS Partner,
                       ku.Kontaktperson, ku.Epost, ku.Epost2, ku.Mobilnr, ku.Lokasjon,
                       ku.Domene, ku.Web, ku.Antivirus, ku.[Backup], ku.[Database], ku.Dokumentasjon,
                       ku.Beskrivelse, ku.Kommentar, ku.Annet
                FROM dbo.Kunde k
                OUTER APPLY (SELECT TOP 1 * FROM dbo.kunder x WHERE x.Kundeid = k.Kundenummer) ku
                WHERE k.Kundenummer = @nr
                """, p(), ct: ct);

            if (kunde.Count == 0)
                return $"Fant ingen kunde med kundenummer {kundenummer}.";

            var tjenester = await db.QueryAsync("""
                SELECT t.ID, t.tjeneste AS Tjeneste, t.beskrivelse AS Beskrivelse, t.domene AS Domene,
                       t.ansvar AS Ansvar, t.URL, t.antall AS Antall, t.notat AS Notat,
                       t.m365_avtaledato AS M365Avtaledato,
                       NULLIF(t.serverID * CASE WHEN LTRIM(RTRIM(ISNULL(s.Server, ''))) = '' THEN 0 ELSE 1 END, 0) AS ServerID, s.Server, CASE WHEN LTRIM(RTRIM(ISNULL(s.Server, ''))) = '' THEN NULL ELSE s.Aktiv END AS ServerAktiv
                FROM dbo.kunde_tjeneste t
                LEFT JOIN dbo.servere s ON s.ServerID = t.serverID
                WHERE t.kundenr = @nr
                ORDER BY t.tjeneste, t.domene
                """, p(), ct: ct);

            var servere = await db.QueryAsync("""
                SELECT s.ServerID, s.Server, s.Type, s.OS, s.Beskrivelse, s.IPadresse, s.Ansvar, d.Datasenter
                FROM dbo.servere s
                LEFT JOIN dbo.Datasenter d ON d.DatasenterID = s.DatasenterID
                WHERE s.kundeid = @nr AND s.Aktiv = 1
                ORDER BY s.Server
                """, p(), ct: ct);

            var inaktive = await db.QueryAsync(
                "SELECT COUNT(*) AS n FROM dbo.servere WHERE kundeid = @nr AND ISNULL(Aktiv, 0) = 0", p(), ct: ct);

            return Database.ToJson(new
            {
                kunde = kunde[0],
                tjenester,
                aktive_servere = servere,
                inaktive_servere = inaktive[0]["n"],
            });
        }
        catch (DatabaseException ex)
        {
            return ex.Message;
        }
    }
}
