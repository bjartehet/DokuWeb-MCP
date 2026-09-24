using System.ComponentModel;
using DokuWebMcp.Db;
using Microsoft.Data.SqlClient;
using ModelContextProtocol.Server;

namespace DokuWebMcp.Tools;

/// <summary>
/// Invoice basis, mirroring DokuWeb's timereg/til_fakturering_* and forhandsfakturerte_prosjekter pages.
/// </summary>
[McpServerToolType]
public static class FaktureringTools
{
    // Same rules as til_fakturering_oversikt.aspx: hours before Next Systems took over the portfolio
    // (March 2025) were invoiced by EnterNett, and NEXT SYSTEMS AS itself is internal time.
    private static readonly DateTime FaktureringFra = new(2025, 3, 1);
    private const int InternKunde = 300560;
    // TILFELDIGE KUNDER - catch-all invoiced directly outside DokuWeb; Siste faktura is never updated,
    // so it would dominate the invoice basis. Excluded from the overview at the user's request.
    private const int TilfeldigeKunder = 300111;

    // Avtaletype.ID values used by DokuWeb's invoicing pages.
    private const int AvtaletypeFortlopende = 1;
    private const int AvtaletypeForhandsfakturert = 2;

    [McpServerTool(Name = "til_fakturering", Title = "Til fakturering", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("""
        Fakturagrunnlag for prosjekter med avtaletype «Faktureres fortløpende» (1), samme utvalg som DokuWebs side
        «Til fakturering»: åpne prosjekter med fakturerbare timer ført etter siste faktura.
        Timer før 01.03.2025 (fakturert av EnterNett), intern tid på NEXT SYSTEMS AS og TILFELDIGE KUNDER
        (faktureres direkte utenfor DokuWeb) er utelatt.
        Uten prosjektId: oversikt per prosjekt med ufakturerte timer, timepris og estimert beløp.
        Med prosjektId: alle ufakturerte føringer for prosjektet, gruppert per enhet.
        Estimert beløp = timer × timepris i avtalen; faktisk faktura kan avvike.
        """)]
    public static async Task<string> TilFakturering(
        Database db,
        [Description("ProsjektID for detaljer. Utelat for oversikt over alle prosjekter.")]
        int? prosjektId = null,
        [Description("Kun prosjekter for denne ansvarlige: kortform, fornavn eller fullt navn.")]
        string? ansvarlig = null,
        CancellationToken ct = default)
    {
        try
        {
            if (prosjektId is null)
            {
                // Siste faktura NULL = never invoiced; DokuWeb's page drops those, here they are included and flagged.
                var rows = await db.QueryAsync("""
                    SELECT k.Kundenummer, k.Kundenavn, k.FinagoID, p.ProsjektID, p.Prosjektnavn, p.Ansvarlig,
                           CAST(p.[Siste faktura] AS date) AS SisteFaktura,
                           p.[Timepris avtale] AS Timepris,
                           ROUND(SUM(t.Varighet_kunde), 2) AS UfakturerteTimer,
                           ROUND(SUM(t.Varighet_kunde) * p.[Timepris avtale], 0) AS EstimertBelop,
                           COUNT(*) AS Foringer,
                           CAST(MIN(t.Dato) AS date) AS ForsteForing, CAST(MAX(t.Dato) AS date) AS SisteForing
                    FROM dbo.Prosjekt p
                    JOIN dbo.Kunde k ON k.Kundenummer = p.Kundenummer
                    JOIN dbo.Timereg t ON t.ProsjektID = p.ProsjektID
                    WHERE p.Avtaletype = @avtaletype AND p.Avsluttet = 0
                      AND t.Dato > ISNULL(p.[Siste faktura], '19000101')
                      AND t.Dato >= @fra
                      AND k.Kundenummer NOT IN (@intern, @tilfeldige)
                      AND (@ansvarlig IS NULL OR p.Ansvarlig = @ansvarlig OR p.Ansvarlig LIKE @ansvarlig + N' %' OR p.Ansvarlig IN (SELECT Navn FROM dbo.Ansatt WHERE kortform = @ansvarlig))
                    GROUP BY k.Kundenummer, k.Kundenavn, k.FinagoID, p.ProsjektID, p.Prosjektnavn, p.Ansvarlig,
                             p.[Siste faktura], p.[Timepris avtale]
                    HAVING SUM(t.Varighet_kunde) > 0
                    ORDER BY UfakturerteTimer DESC, k.Kundenavn, p.Prosjektnavn
                    """,
                    [
                        new SqlParameter("@avtaletype", AvtaletypeFortlopende),
                        new SqlParameter("@fra", FaktureringFra),
                        new SqlParameter("@intern", InternKunde),
                        new SqlParameter("@tilfeldige", TilfeldigeKunder),
                        new SqlParameter("@ansvarlig", string.IsNullOrWhiteSpace(ansvarlig) ? DBNull.Value : ansvarlig.Trim()),
                    ], ct: ct);

                foreach (var r in rows)
                    if (r["SisteFaktura"] is null)
                        r["Merknad"] = "Aldri fakturert (vises ikke på DokuWebs side Til fakturering)";

                return Database.ToJson(new
                {
                    antall_prosjekter = rows.Count,
                    sum_timer = Math.Round(rows.Sum(r => Convert.ToDouble(r["UfakturerteTimer"] ?? 0.0)), 2),
                    sum_estimert_belop = Math.Round(rows.Sum(r => Convert.ToDouble(r["EstimertBelop"] ?? 0.0)), 0),
                    prosjekter = rows,
                });
            }

            var prosjekt = await db.QueryAsync("""
                SELECT p.ProsjektID, p.Prosjektnavn, p.Kundenummer, k.Kundenavn, k.FinagoID,
                       k.Fornavn + N' ' + k.Etternavn AS Kontakt, k.Telefon,
                       p.Avtaletype AS AvtaletypeID, a.Avtaletype, p.Ansvarlig,
                       p.[Timepris avtale] AS Timepris, p.[Antall timer avtalt] AS TimerAvtalt,
                       CAST(p.[Siste faktura] AS date) AS SisteFaktura, p.Avsluttet
                FROM dbo.Prosjekt p
                JOIN dbo.Kunde k ON k.Kundenummer = p.Kundenummer
                LEFT JOIN dbo.Avtaletype a ON a.ID = p.Avtaletype
                WHERE p.ProsjektID = @id
                """, [new SqlParameter("@id", prosjektId.Value)], ct: ct);

            if (prosjekt.Count == 0)
                return $"Fant ikke prosjekt {prosjektId}.";

            var foringer = await db.QueryAsync($"""
                SELECT ISNULL(e.Enhetsnavn, '(ingen enhet)') AS Enhet, CAST(t.Dato AS date) AS Dato, t.Ansvarlig AS Ansatt,
                       t.Varighet_kunde AS Fakturerbart, t.Varighet_ansatt AS Timer,
                       {TimeregTools.BeskrivelseSql} AS Beskrivelse,
                       NULLIF(t.kilometer, 0) AS Km
                FROM dbo.Timereg t
                JOIN dbo.Prosjekt p ON p.ProsjektID = t.ProsjektID
                LEFT JOIN dbo.Enhet e ON e.ID = t.Enhet
                WHERE t.ProsjektID = @id
                  AND t.Dato > ISNULL(p.[Siste faktura], '19000101')
                  AND t.Dato >= @fra
                ORDER BY Enhet, t.Dato
                """, [new SqlParameter("@id", prosjektId.Value), new SqlParameter("@fra", FaktureringFra)], ct: ct);

            var timepris = Convert.ToDouble(prosjekt[0]["Timepris"] ?? 0);
            var perEnhet = foringer
                .GroupBy(f => (string)f["Enhet"]!)
                .Select(g =>
                {
                    var timer = Math.Round(g.Sum(f => Convert.ToDouble(f["Fakturerbart"] ?? 0.0)), 2);
                    return new
                    {
                        enhet = g.Key,
                        fakturerbare_timer = timer,
                        estimert_belop = Math.Round(timer * timepris, 0),
                        km = g.Sum(f => Convert.ToInt32(f["Km"] ?? 0)),
                        foringer = g.Select(f => new { Dato = f["Dato"], Ansatt = f["Ansatt"], Timer = f["Fakturerbart"], Beskrivelse = f["Beskrivelse"], Km = f["Km"] }),
                    };
                })
                .ToList();

            var totalt = Math.Round(perEnhet.Sum(e => e.fakturerbare_timer), 2);
            return Database.ToJson(new
            {
                prosjekt = prosjekt[0],
                merknad = Convert.ToInt32(prosjekt[0]["AvtaletypeID"] ?? 0) != AvtaletypeFortlopende
                    ? "Prosjektet har ikke avtaletype «Faktureres fortløpende» (1) og vises ikke i DokuWebs Til fakturering."
                    : null,
                sum_fakturerbare_timer = totalt,
                sum_estimert_belop = Math.Round(totalt * timepris, 0),
                per_enhet = perEnhet,
            });
        }
        catch (DatabaseException ex)
        {
            return ex.Message;
        }
    }

    [McpServerTool(Name = "forhandsfakturerte", Title = "Forhåndsfakturerte prosjekter", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("""
        Status for forhåndsfakturerte prosjekter (avtaletype 2), samme som DokuWebs side
        «Forhåndsfakturerte prosjekter»: avtalte timer, timer brukt totalt, gjenstående timer og
        verdi av gjenstående timer (rest × timepris). Negativ rest betyr at avtalte timer er overskredet.
        Viser også timer siden siste faktura og i inneværende måned.
        """)]
    public static async Task<string> Forhandsfakturerte(
        Database db,
        [Description("Kun prosjekter for dette kundenummeret.")]
        int? kundenummer = null,
        [Description("Kun prosjekter for denne ansvarlige: kortform, fornavn eller fullt navn.")]
        string? ansvarlig = null,
        CancellationToken ct = default)
    {
        try
        {
            // Same basis as the view [Status timeforbruk driftsavtale].
            var rows = await db.QueryAsync("""
                SELECT k.Kundenummer, k.Kundenavn, p.ProsjektID, p.Prosjektnavn, p.Ansvarlig,
                       CAST(p.[Siste faktura] AS date) AS SisteFaktura,
                       p.[Timepris avtale] AS Timepris, p.[Antall timer avtalt] AS TimerAvtalt,
                       ROUND(SUM(t.Varighet_kunde), 2) AS TimerBrukt,
                       ROUND(p.[Antall timer avtalt] - SUM(t.Varighet_kunde), 2) AS Rest,
                       ROUND(p.[Timepris avtale] * (p.[Antall timer avtalt] - SUM(t.Varighet_kunde)), 0) AS Tilgode,
                       ROUND(ISNULL(SUM(CASE WHEN t.Dato > ISNULL(p.[Siste faktura], '19000101') THEN t.Varighet_kunde END), 0), 2) AS TimerSidenSisteFaktura,
                       ROUND(ISNULL(SUM(CASE WHEN t.Dato >= DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1) THEN t.Varighet_kunde END), 0), 2) AS TimerDenneManed
                FROM dbo.Prosjekt p
                JOIN dbo.Timereg t ON t.ProsjektID = p.ProsjektID
                JOIN dbo.Kunde k ON k.Kundenummer = p.Kundenummer
                WHERE p.Avtaletype = @avtaletype AND p.Avsluttet = 0
                  AND (@kundenr IS NULL OR p.Kundenummer = @kundenr)
                  AND (@ansvarlig IS NULL OR p.Ansvarlig = @ansvarlig OR p.Ansvarlig LIKE @ansvarlig + N' %' OR p.Ansvarlig IN (SELECT Navn FROM dbo.Ansatt WHERE kortform = @ansvarlig))
                GROUP BY k.Kundenummer, k.Kundenavn, p.ProsjektID, p.Prosjektnavn, p.Ansvarlig,
                         p.[Siste faktura], p.[Timepris avtale], p.[Antall timer avtalt]
                ORDER BY k.Kundenavn
                """,
                [
                    new SqlParameter("@avtaletype", AvtaletypeForhandsfakturert),
                    new SqlParameter("@kundenr", (object?)kundenummer ?? DBNull.Value),
                    new SqlParameter("@ansvarlig", string.IsNullOrWhiteSpace(ansvarlig) ? DBNull.Value : ansvarlig.Trim()),
                ], ct: ct);

            if (rows.Count == 0)
                return "Ingen åpne forhåndsfakturerte prosjekter med timeføringer funnet.";
            return Database.ToJson(new { antall = rows.Count, prosjekter = rows });
        }
        catch (DatabaseException ex)
        {
            return ex.Message;
        }
    }
}
