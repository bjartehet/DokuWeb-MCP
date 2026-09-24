using System.ComponentModel;
using DokuWebMcp.Db;
using Microsoft.Data.SqlClient;
using ModelContextProtocol.Server;

namespace DokuWebMcp.Tools;

/// <summary>
/// Projects and time entries. Timereg.Varighet_ansatt = hours worked, Varighet_kunde = hours billable
/// to the customer (what DokuWeb's invoicing pages sum). Timereg.Varighet is legacy, Fakturering is always 0.
/// Timereg.Ansvarlig and Prosjekt.Ansvarlig hold full names; filters also accept kortform and first name.
/// </summary>
[McpServerToolType]
public static class TimeregTools
{
    // Description column is ntext with a different collation than Kommentar; fall back to Kommentar like DokuWeb's prosjekt.aspx.
    internal const string BeskrivelseSql =
        "COALESCE(NULLIF(CAST(t.Beskrivelse AS nvarchar(max)) COLLATE DATABASE_DEFAULT, ''), t.Kommentar COLLATE DATABASE_DEFAULT)";

    [McpServerTool(Name = "sok_prosjekt", Title = "Søk etter prosjekt", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("""
        Søker etter prosjekter i DokuWebs timeregistrering (på prosjektnavn, kundenavn eller prosjekt-ID).
        Viser avtaletype, ansvarlig, timepris, avtalte timer og dato for siste faktura.
        Kun åpne prosjekter med mindre inkluderAvsluttede=true. Bruk ProsjektID videre i hent_prosjekt,
        timer, timeforinger og til_fakturering.
        """)]
    public static async Task<string> SokProsjekt(
        Database db,
        [Description("Fritekst: del av prosjektnavn eller kundenavn, eller eksakt prosjekt-ID.")]
        string? sok = null,
        [Description("Kun prosjekter for dette kundenummeret.")]
        int? kundenummer = null,
        [Description("Del av avtaletype (se hent_prosjekt / Avtaletype-tabellen).")]
        string? avtaletype = null,
        [Description("Ta med avsluttede prosjekter. Standard false.")]
        bool inkluderAvsluttede = false,
        [Description("Maks antall treff (1-200). Standard 50.")]
        int maks = 50,
        CancellationToken ct = default)
    {
        maks = Math.Clamp(maks, 1, 200);
        try
        {
            var (rows, truncated) = await db.QueryWithTruncationAsync("""
                SELECT TOP (@maks)
                    p.ProsjektID, p.Prosjektnavn, p.Kundenummer, k.Kundenavn,
                    p.Avtaletype AS AvtaletypeID, a.Avtaletype, p.Ansvarlig, p.Avdeling,
                    p.[Antall timer avtalt] AS TimerAvtalt, p.[Timepris avtale] AS Timepris,
                    p.[Timepris utover avtale] AS TimeprisUtover,
                    CAST(p.[Siste faktura] AS date) AS SisteFaktura, p.Avsluttet
                FROM dbo.Prosjekt p
                LEFT JOIN dbo.Kunde k ON k.Kundenummer = p.Kundenummer
                LEFT JOIN dbo.Avtaletype a ON a.ID = p.Avtaletype
                WHERE (@inkl = 1 OR p.Avsluttet = 0)
                  AND (@sok IS NULL OR p.Prosjektnavn LIKE @sok OR k.Kundenavn LIKE @sok
                       OR CAST(p.ProsjektID AS nvarchar(20)) = @sokEksakt)
                  AND (@kundenr IS NULL OR p.Kundenummer = @kundenr)
                  AND (@avtale IS NULL OR a.Avtaletype LIKE @avtale)
                ORDER BY k.Kundenavn, p.Prosjektnavn
                """,
                [
                    new SqlParameter("@maks", maks),
                    new SqlParameter("@inkl", inkluderAvsluttede),
                    Like("@sok", sok),
                    new SqlParameter("@sokEksakt", (object?)sok?.Trim() ?? DBNull.Value),
                    new SqlParameter("@kundenr", (object?)kundenummer ?? DBNull.Value),
                    Like("@avtale", avtaletype),
                ], maks, ct);

            if (rows.Count == 0)
                return "Ingen prosjekter funnet med disse filtrene.";
            return Database.ToJson(new
            {
                antall = rows.Count,
                avkortet = truncated ? "Ja - flere treff finnes. Snevre inn søket eller øk maks." : null,
                prosjekter = rows,
            });
        }
        catch (DatabaseException ex)
        {
            return ex.Message;
        }
    }

    [McpServerTool(Name = "hent_prosjekt", Title = "Hent prosjekt", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("""
        Henter ett prosjekt fra DokuWebs timeregistrering: avtale (type, timepris, avtalte timer),
        siste faktura, timer siden siste faktura, timer per måned siste 12 måneder, enheter det er ført på,
        og de siste timeføringene.
        """)]
    public static async Task<string> HentProsjekt(
        Database db,
        [Description("ProsjektID fra sok_prosjekt.")]
        int prosjektId,
        [Description("Antall siste timeføringer som tas med (0-50). Standard 10.")]
        int sisteForinger = 10,
        CancellationToken ct = default)
    {
        var p = () => new[] { new SqlParameter("@id", prosjektId) };
        try
        {
            var prosjekt = await db.QueryAsync("""
                SELECT p.ProsjektID, p.Prosjektnavn, p.Kundenummer, k.Kundenavn, k.FinagoID,
                       p.Avtaletype AS AvtaletypeID, a.Avtaletype, p.Ansvarlig, p.Avdeling,
                       p.[Antall timer avtalt] AS TimerAvtalt, p.[Timepris avtale] AS Timepris,
                       p.[Timepris utover avtale] AS TimeprisUtover,
                       CAST(p.[Siste faktura] AS date) AS SisteFaktura, p.Avsluttet,
                       CAST(p.Beskrivelse AS nvarchar(max)) AS Beskrivelse
                FROM dbo.Prosjekt p
                LEFT JOIN dbo.Kunde k ON k.Kundenummer = p.Kundenummer
                LEFT JOIN dbo.Avtaletype a ON a.ID = p.Avtaletype
                WHERE p.ProsjektID = @id
                """, p(), ct: ct);

            if (prosjekt.Count == 0)
                return $"Fant ikke prosjekt {prosjektId}.";

            var sum = await db.QueryAsync("""
                SELECT SUM(t.Varighet_ansatt) AS TimerTotalt, SUM(t.Varighet_kunde) AS FakturerbartTotalt,
                       SUM(CASE WHEN t.Dato > ISNULL(p.[Siste faktura], '19000101') THEN t.Varighet_ansatt END) AS TimerSidenSisteFaktura,
                       SUM(CASE WHEN t.Dato > ISNULL(p.[Siste faktura], '19000101') THEN t.Varighet_kunde END) AS FakturerbartSidenSisteFaktura,
                       CAST(MIN(t.Dato) AS date) AS ForsteForing, CAST(MAX(t.Dato) AS date) AS SisteForing,
                       COUNT(*) AS AntallForinger
                FROM dbo.Timereg t
                JOIN dbo.Prosjekt p ON p.ProsjektID = t.ProsjektID
                WHERE t.ProsjektID = @id
                """, p(), ct: ct);

            var perManed = await db.QueryAsync("""
                SELECT FORMAT(t.Dato, 'yyyy-MM') AS Maned, SUM(t.Varighet_ansatt) AS Timer, SUM(t.Varighet_kunde) AS Fakturerbart
                FROM dbo.Timereg t
                WHERE t.ProsjektID = @id AND t.Dato >= DATEADD(month, -11, DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1))
                GROUP BY FORMAT(t.Dato, 'yyyy-MM')
                ORDER BY Maned
                """, p(), ct: ct);

            var enheter = await db.QueryAsync("""
                SELECT e.ID AS EnhetID, e.Enhetsnavn, SUM(t.Varighet_kunde) AS Fakturerbart, CAST(MAX(t.Dato) AS date) AS SisteForing
                FROM dbo.Timereg t
                JOIN dbo.Enhet e ON e.ID = t.Enhet
                WHERE t.ProsjektID = @id
                GROUP BY e.ID, e.Enhetsnavn
                ORDER BY e.Enhetsnavn
                """, p(), ct: ct);

            var siste = await db.QueryAsync($"""
                SELECT TOP (@n) t.TimeregID, CAST(t.Dato AS date) AS Dato, t.Ansvarlig, e.Enhetsnavn,
                       t.Varighet_ansatt AS Timer, t.Varighet_kunde AS Fakturerbart, {BeskrivelseSql} AS Beskrivelse
                FROM dbo.Timereg t
                LEFT JOIN dbo.Enhet e ON e.ID = t.Enhet
                WHERE t.ProsjektID = @id
                ORDER BY t.Dato DESC, t.TimeregID DESC
                """, [new SqlParameter("@id", prosjektId), new SqlParameter("@n", Math.Clamp(sisteForinger, 0, 50))], ct: ct);

            return Database.ToJson(new
            {
                prosjekt = prosjekt[0],
                timer = sum[0],
                per_maned_siste_12 = perManed,
                enheter,
                siste_foringer = siste,
            });
        }
        catch (DatabaseException ex)
        {
            return ex.Message;
        }
    }

    // Whitelisted grouping expressions - never built from user input.
    private static readonly Dictionary<string, (string Select, string GroupBy, string OrderBy)> Grupperinger =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["kunde"] = ("k.Kundenummer, ISNULL(k.Kundenavn, '(ingen kunde)') AS Kunde", "k.Kundenummer, k.Kundenavn", "Fakturerbart DESC"),
            ["prosjekt"] = ("p.ProsjektID, ISNULL(p.Prosjektnavn, '(ingen prosjekt)') AS Prosjekt, k.Kundenavn AS Kunde, a.Avtaletype",
                            "p.ProsjektID, p.Prosjektnavn, k.Kundenavn, a.Avtaletype", "Fakturerbart DESC"),
            ["ansatt"] = ("t.Ansvarlig AS Ansatt", "t.Ansvarlig", "t.Ansvarlig"),
            ["enhet"] = ("e.ID AS EnhetID, ISNULL(e.Enhetsnavn, '(ingen enhet)') AS Enhet, CASE WHEN e.Kundenummer = 0 THEN 1 ELSE 0 END AS Intern",
                         "e.ID, e.Enhetsnavn, e.Kundenummer", "Timer DESC"),
            ["avtaletype"] = ("ISNULL(a.Avtaletype, '(ingen)') AS Avtaletype", "a.Avtaletype", "Fakturerbart DESC"),
            ["maned"] = ("FORMAT(t.Dato, 'yyyy-MM') AS Maned", "FORMAT(t.Dato, 'yyyy-MM')", "Maned"),
            // ISO year + week: the Thursday of the week decides the year.
            ["uke"] = ("CONCAT(YEAR(DATEADD(day, 4 - ((DATEPART(weekday, t.Dato) + @@DATEFIRST - 2) % 7 + 1), t.Dato)), '-U', FORMAT(DATEPART(iso_week, t.Dato), '00')) AS Uke",
                       "CONCAT(YEAR(DATEADD(day, 4 - ((DATEPART(weekday, t.Dato) + @@DATEFIRST - 2) % 7 + 1), t.Dato)), '-U', FORMAT(DATEPART(iso_week, t.Dato), '00'))", "Uke"),
            ["dag"] = ("CAST(t.Dato AS date) AS Dato", "CAST(t.Dato AS date)", "Dato"),
        };

    [McpServerTool(Name = "timer", Title = "Timeoversikt", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("""
        Summerer timer fra DokuWebs timeregistrering for en periode, gruppert på kunde, prosjekt, ansatt,
        enhet, avtaletype, maned, uke eller dag. Filtre (kunde, prosjekt, ansatt, enhet) kombineres.
        Timer = arbeidede timer (Varighet_ansatt). Fakturerbart = Varighet_kunde, slik DokuWeb regner -
        inkluderer også intern tid. FakturerbartEksternt = Fakturerbart uten intern tid (avtaletype Internt/
        Internt prosjekt og de interne kundene Next Systems AS, Next Systems - Intern, EnterNett - Intern);
        bruk dette når spørsmålet gjelder hva som kan faktureres kunder.
        Gruppert på ansatt vises utfaktureringsgrad (som DokuWebs ukesoversikt) og UtfaktureringsgradEksternt,
        samt timeføringsgrad (timer mot 7,5 t per virkedag i perioden, helligdager er ikke trukket fra).
        Standard periode: inneværende måned til i dag.
        """)]
    public static async Task<string> Timer(
        Database db,
        [Description("Fra-dato, yyyy-MM-dd eller yyyy-MM. Standard: første dag i inneværende måned.")]
        string? fra = null,
        [Description("Til-dato (inklusiv), yyyy-MM-dd eller yyyy-MM (= ut måneden). Standard: i dag.")]
        string? til = null,
        [Description("Gruppering: kunde, prosjekt, ansatt, enhet, avtaletype, maned, uke eller dag. Standard kunde.")]
        string grupperPa = "kunde",
        [Description("Kun timer for dette kundenummeret.")]
        int? kundenummer = null,
        [Description("Kun timer for dette prosjektet.")]
        int? prosjektId = null,
        [Description("Kun timer for denne ansatte: kortform (\"TerjeB\"), fornavn (\"Terje\") eller fullt navn.")]
        string? ansatt = null,
        [Description("Kun timer for denne enheten (EnhetID).")]
        int? enhetId = null,
        CancellationToken ct = default)
    {
        if (!Periode.TryParse(fra, til, out var periode, out var feil))
            return feil;
        if (!Grupperinger.TryGetValue(grupperPa?.Trim() ?? "", out var g))
            return $"Ugyldig gruppering «{grupperPa}». Bruk: {string.Join(", ", Grupperinger.Keys)}.";

        var sql = $"""
            SELECT {g.Select},
                   ROUND(SUM(t.Varighet_ansatt), 2) AS Timer,
                   ROUND(SUM(t.Varighet_kunde), 2) AS Fakturerbart,
                   ROUND(SUM(CASE WHEN {Intern.ErInternSql} THEN 0 ELSE t.Varighet_kunde END), 2) AS FakturerbartEksternt,
                   COUNT(*) AS Foringer
            FROM dbo.Timereg t
            LEFT JOIN dbo.Prosjekt p ON p.ProsjektID = t.ProsjektID
            LEFT JOIN dbo.Kunde k ON k.Kundenummer = p.Kundenummer
            LEFT JOIN dbo.Avtaletype a ON a.ID = p.Avtaletype
            LEFT JOIN dbo.Enhet e ON e.ID = t.Enhet
            WHERE {FilterSql}
            GROUP BY {g.GroupBy}
            ORDER BY {g.OrderBy}
            """;

        try
        {
            var (rows, truncated) = await db.QueryWithTruncationAsync(sql, FilterParams(periode, kundenummer, prosjektId, ansatt, enhetId), ct: ct);

            double timer = rows.Sum(r => Convert.ToDouble(r["Timer"] ?? 0.0));
            double fakt = rows.Sum(r => Convert.ToDouble(r["Fakturerbart"] ?? 0.0));
            double eksternt = rows.Sum(r => Convert.ToDouble(r["FakturerbartEksternt"] ?? 0.0));

            object? forventet = null;
            if (string.Equals(grupperPa, "ansatt", StringComparison.OrdinalIgnoreCase))
            {
                var forventetTimer = periode.Virkedager() * 7.5;
                forventet = new { virkedager = periode.Virkedager(), timer_per_ansatt = forventetTimer };
                foreach (var r in rows)
                {
                    var t = Convert.ToDouble(r["Timer"] ?? 0.0);
                    var f = Convert.ToDouble(r["Fakturerbart"] ?? 0.0);
                    var fe = Convert.ToDouble(r["FakturerbartEksternt"] ?? 0.0);
                    r["Utfaktureringsgrad"] = t > 0 ? Math.Round(f / t * 100, 1) : 0;
                    r["UtfaktureringsgradEksternt"] = t > 0 ? Math.Round(fe / t * 100, 1) : 0;
                    r["Timeforingsgrad"] = forventetTimer > 0 ? Math.Round(t / forventetTimer * 100, 1) : 0;
                }
            }

            return Database.ToJson(new
            {
                periode = periode.ToString(),
                gruppert_pa = grupperPa,
                sum = new { timer = Math.Round(timer, 2), fakturerbart = Math.Round(fakt, 2), fakturerbart_eksternt = Math.Round(eksternt, 2) },
                forventet,
                avkortet = truncated ? "Ja - for mange grupper. Snevre inn periode eller filtre." : null,
                rader = rows,
            });
        }
        catch (DatabaseException ex)
        {
            return ex.Message;
        }
    }

    [McpServerTool(Name = "timeforinger", Title = "Timeføringer", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("""
        Lister enkeltstående timeføringer fra DokuWeb for en periode med dato, kunde, prosjekt, enhet,
        ansatt, timer, fakturerbart og beskrivelse. Filtre kombineres; fritekst søker i beskrivelsen.
        Nyeste først. Bruk timer-verktøyet for summer.
        """)]
    public static async Task<string> Timeforinger(
        Database db,
        [Description("Fra-dato, yyyy-MM-dd eller yyyy-MM. Standard: første dag i inneværende måned.")]
        string? fra = null,
        [Description("Til-dato (inklusiv), yyyy-MM-dd eller yyyy-MM. Standard: i dag.")]
        string? til = null,
        [Description("Kun føringer for dette kundenummeret.")]
        int? kundenummer = null,
        [Description("Kun føringer for dette prosjektet.")]
        int? prosjektId = null,
        [Description("Kun føringer for denne ansatte: kortform, fornavn eller fullt navn.")]
        string? ansatt = null,
        [Description("Kun føringer for denne enheten (EnhetID).")]
        int? enhetId = null,
        [Description("Fritekst i beskrivelsen.")]
        string? sok = null,
        [Description("Maks antall føringer (1-500). Standard 100.")]
        int maks = 100,
        CancellationToken ct = default)
    {
        if (!Periode.TryParse(fra, til, out var periode, out var feil))
            return feil;
        maks = Math.Clamp(maks, 1, Database.MaxRows);

        var sql = $"""
            SELECT t.TimeregID, CAST(t.Dato AS date) AS Dato, k.Kundenummer, k.Kundenavn AS Kunde,
                   p.ProsjektID, p.Prosjektnavn AS Prosjekt, e.Enhetsnavn AS Enhet, t.Ansvarlig AS Ansatt,
                   t.Varighet_ansatt AS Timer, t.Varighet_kunde AS Fakturerbart,
                   {BeskrivelseSql} AS Beskrivelse,
                   NULLIF(t.kilometer, 0) AS Km, NULLIF(t.ticket_id, 0) AS Sak
            FROM dbo.Timereg t
            LEFT JOIN dbo.Prosjekt p ON p.ProsjektID = t.ProsjektID
            LEFT JOIN dbo.Kunde k ON k.Kundenummer = p.Kundenummer
            LEFT JOIN dbo.Enhet e ON e.ID = t.Enhet
            WHERE {FilterSql}
              AND (@sok IS NULL OR t.Beskrivelse LIKE @sok OR t.Kommentar LIKE @sok)
            ORDER BY t.Dato DESC, t.TimeregID DESC
            """;

        var parameters = FilterParams(periode, kundenummer, prosjektId, ansatt, enhetId).Append(Like("@sok", sok));

        try
        {
            var (rows, truncated) = await db.QueryWithTruncationAsync(sql, parameters, maks, ct);
            if (rows.Count == 0)
                return $"Ingen timeføringer funnet i perioden {periode} med disse filtrene.";
            return Database.ToJson(new
            {
                periode = periode.ToString(),
                antall = rows.Count,
                avkortet = truncated ? "Ja - flere føringer finnes. Snevre inn eller øk maks." : null,
                foringer = rows,
            });
        }
        catch (DatabaseException ex)
        {
            return ex.Message;
        }
    }

    private const string FilterSql = """
        t.Dato >= @fra AND t.Dato < @til
          AND (@kundenr IS NULL OR p.Kundenummer = @kundenr)
          AND (@prosjekt IS NULL OR t.ProsjektID = @prosjekt)
          AND (@ansatt IS NULL OR t.Ansvarlig = @ansatt OR t.Ansvarlig LIKE @ansatt + N' %' OR t.Ansvarlig IN (SELECT Navn FROM dbo.Ansatt WHERE kortform = @ansatt))
          AND (@enhet IS NULL OR t.Enhet = @enhet)
        """;

    private static SqlParameter[] FilterParams(Periode periode, int? kundenummer, int? prosjektId, string? ansatt, int? enhetId) =>
    [
        new SqlParameter("@fra", periode.Fra),
        new SqlParameter("@til", periode.TilEksklusiv),
        new SqlParameter("@kundenr", (object?)kundenummer ?? DBNull.Value),
        new SqlParameter("@prosjekt", (object?)prosjektId ?? DBNull.Value),
        new SqlParameter("@ansatt", string.IsNullOrWhiteSpace(ansatt) ? DBNull.Value : ansatt.Trim()),
        new SqlParameter("@enhet", (object?)enhetId ?? DBNull.Value),
    ];

    internal static SqlParameter Like(string name, string? value) =>
        new(name, string.IsNullOrWhiteSpace(value) ? DBNull.Value : $"%{value.Trim()}%");
}
