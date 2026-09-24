using System.Globalization;

namespace DokuWebMcp.Tools;

/// <summary>
/// Date range for time tracking tools. Accepts yyyy-MM-dd or yyyy-MM.
/// <see cref="TilEksklusiv"/> is the day after the last included day, for "Dato &lt; @til" filters.
/// </summary>
public readonly record struct Periode(DateTime Fra, DateTime TilEksklusiv)
{
    public DateTime TilInklusiv => TilEksklusiv.AddDays(-1);

    public override string ToString() => $"{Fra:yyyy-MM-dd} – {TilInklusiv:yyyy-MM-dd}";

    /// <summary>Defaults: fra = first day of current month, til = today.</summary>
    public static bool TryParse(string? fra, string? til, out Periode periode, out string feil)
    {
        periode = default;
        feil = "";
        var idag = DateTime.Today;

        DateTime start;
        if (string.IsNullOrWhiteSpace(fra))
            start = new DateTime(idag.Year, idag.Month, 1);
        else if (!TryParseDate(fra, out start, out _))
        {
            feil = $"Ugyldig fra-dato «{fra}». Bruk yyyy-MM-dd eller yyyy-MM.";
            return false;
        }

        DateTime sluttEks;
        if (string.IsNullOrWhiteSpace(til))
            sluttEks = idag.AddDays(1);
        else if (TryParseDate(til, out var slutt, out var bareManed))
            // "2026-08" as til means through the end of August.
            sluttEks = bareManed ? slutt.AddMonths(1) : slutt.AddDays(1);
        else
        {
            feil = $"Ugyldig til-dato «{til}». Bruk yyyy-MM-dd eller yyyy-MM.";
            return false;
        }

        if (sluttEks <= start)
        {
            feil = "Til-dato må være lik eller etter fra-dato.";
            return false;
        }

        periode = new Periode(start, sluttEks);
        return true;
    }

    /// <summary>Weekdays (Mon–Fri) in the period. Public holidays are not subtracted.</summary>
    public int Virkedager()
    {
        var n = 0;
        for (var d = Fra; d < TilEksklusiv; d = d.AddDays(1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                n++;
        return n;
    }

    private static bool TryParseDate(string text, out DateTime date, out bool bareManed)
    {
        text = text.Trim();
        bareManed = false;
        if (DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return true;
        if (DateTime.TryParseExact(text, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            bareManed = true;
            return true;
        }
        return false;
    }
}
