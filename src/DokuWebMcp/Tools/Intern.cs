namespace DokuWebMcp.Tools;

/// <summary>
/// What counts as internal time. Varighet_kunde is set on internal projects too, so DokuWeb's
/// "fakturerbart" (and ukesoversikt's utfaktureringsgrad) includes internal hours.
/// </summary>
internal static class Intern
{
    // Avtaletype 3 = Internt, 7 = Internt prosjekt.
    // 30000 = NEXT SYSTEMS - INTERN, 300560 = NEXT SYSTEMS AS, 300629 = ENTERNETT - INTERN.
    // Expects aliases p (Prosjekt).
    public const string ErInternSql =
        "(ISNULL(p.Avtaletype, 0) IN (3, 7) OR ISNULL(p.Kundenummer, 0) IN (30000, 300560, 300629))";
}
