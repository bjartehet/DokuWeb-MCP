using System.ComponentModel;
using System.Net;
using System.Text.RegularExpressions;
using DokuWebMcp.Db;
using Microsoft.Data.SqlClient;
using ModelContextProtocol.Server;

namespace DokuWebMcp.Tools;

[McpServerToolType]
public static partial class IpTools
{
    // An address counts as "i bruk" when it is linked to an active server or has a non-empty
    // IP note (IP_merknad) - same basis as DokuWeb's ip_adresser.aspx. IP_merknad contains
    // many rows with NULL/blank notes; those don't count.

    [GeneratedRegex(@"\d{1,3}(\.\d{1,3}){3}")]
    private static partial Regex Ipv4InText();

    [McpServerTool(Name = "ip_oppslag", Title = "Slå opp IP-adresse", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("""
        Slår opp en IPv4-adresse i DokuWeb: hvilket segment den tilhører, hvilke servere (og kunder)
        den er knyttet til, merknad, og om den er i bruk eller ledig.
        Sjekker både IP-registeret og de frie IP-tekstfeltene på serverne.
        """)]
    public static async Task<string> IpOppslag(
        Database db,
        [Description("IPv4-adresse, f.eks. 193.93.252.70.")]
        string ip,
        CancellationToken ct = default)
    {
        if (!Database.TryParseIpv4(ip, out var bytes))
            return $"«{ip}» er ikke en gyldig IPv4-adresse.";
        var ipText = new IPAddress(bytes).ToString();

        try
        {
            var p = () => new[] { Database.IpParameter("@ip", bytes) };

            var registrert = await db.QueryAsync("""
                SELECT ipa.IP_adresse, seg.ID AS SegmentID, seg.Segmentnavn, seg.VLAN, seg.Kommentar AS Segmentkommentar
                FROM dbo.IP_adresse ipa
                LEFT JOIN dbo.IP_segment seg ON seg.ID = ipa.IP_segment
                WHERE ipa.IP_adresse = @ip
                """, p(), ct: ct);

            var koblinger = await db.QueryAsync("""
                SELECT s.ServerID, s.Server, s.Kunde, s.kundeid AS Kundenummer, s.Aktiv,
                       sip.hoved AS Hoved, sip.merknad AS Merknad
                FROM dbo.server_ip sip
                LEFT JOIN dbo.servere s ON s.ServerID = sip.serverid
                WHERE sip.IP_adresse = @ip
                ORDER BY s.Aktiv DESC
                """, p(), ct: ct);

            var merknad = await db.QueryAsync(
                "SELECT Merknad FROM dbo.IP_merknad WHERE IP_adresse = @ip AND LTRIM(RTRIM(ISNULL(Merknad, ''))) <> ''", p(), ct: ct);

            // Legacy free-text IP fields on servere; LIKE narrows, exact match filters.
            var tekstTreff = (await db.QueryAsync("""
                SELECT ServerID, Server, Kunde, kundeid AS Kundenummer, Aktiv, IPadresse, AltIP
                FROM dbo.servere
                WHERE IPadresse LIKE @like OR AltIP LIKE @like
                """, [new SqlParameter("@like", $"%{ipText}%")], ct: ct))
                .Where(r => ContainsExactIp(r["IPadresse"] as string, ipText) || ContainsExactIp(r["AltIP"] as string, ipText))
                .ToList();

            // Segment by range when the address isn't in the register.
            object? segment = registrert.FirstOrDefault();
            if (segment is null)
                segment = await FindSegmentByRangeAsync(db, bytes, ct);

            var aktiveKoblinger = koblinger.Count(k => k["Aktiv"] is true);
            var aktiveTekst = tekstTreff.Count(t => t["Aktiv"] is true);
            string status =
                aktiveKoblinger > 0 || merknad.Count > 0 ? "I bruk"
                : aktiveTekst > 0 ? "I bruk ifølge serverens IP-felt, men ikke koblet i IP-registeret"
                : registrert.Count > 0 ? "Ledig"
                : "Ikke registrert i DokuWebs IP-register";

            return Database.ToJson(new
            {
                ip = ipText,
                status,
                segment,
                merknad = merknad.FirstOrDefault()?["Merknad"],
                servere = koblinger,
                servere_med_ip_i_tekstfelt = tekstTreff,
            });
        }
        catch (DatabaseException ex)
        {
            return ex.Message;
        }
    }

    [McpServerTool(Name = "ip_segment", Title = "IP-segmenter og ledige adresser", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("""
        Uten segment: lister alle IP-segmenter i DokuWeb med CIDR, VLAN, antall adresser, antall i bruk og ledige.
        Med segment (ID eller del av navn): viser adressene i segmentet med status, server, kunde og merknad.
        Sett bareLedige=true for å finne ledige adresser. En adresse er i bruk når den er knyttet til en aktiv
        server eller har en merknad.
        """)]
    public static async Task<string> IpSegment(
        Database db,
        [Description("Segment-ID eller del av segmentnavn, f.eks. \"Serverpark\" eller 3. Utelat for oversikt.")]
        string? segment = null,
        [Description("Vis bare ledige adresser. Standard false.")]
        bool bareLedige = false,
        [Description("Maks antall adresser (1-500). Standard 300.")]
        int maks = 300,
        CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                var oversikt = await db.QueryAsync("""
                    SELECT seg.ID, seg.Segmentnavn, seg.Start_IP, seg.Mask, seg.VLAN, seg.Kommentar,
                           COUNT(ipa.IP_adresse) AS Adresser,
                           SUM(CASE WHEN bruk.IP_adresse IS NOT NULL THEN 1 ELSE 0 END) AS IBruk
                    FROM dbo.IP_segment seg
                    LEFT JOIN dbo.IP_adresse ipa ON ipa.IP_segment = seg.ID
                    LEFT JOIN (
                        SELECT sip.IP_adresse FROM dbo.server_ip sip JOIN dbo.servere s ON s.ServerID = sip.serverid WHERE s.Aktiv = 1
                        UNION
                        SELECT IP_adresse FROM dbo.IP_merknad WHERE LTRIM(RTRIM(ISNULL(Merknad, ''))) <> ''
                    ) bruk ON bruk.IP_adresse = ipa.IP_adresse
                    GROUP BY seg.ID, seg.Segmentnavn, seg.Start_IP, seg.Mask, seg.VLAN, seg.Kommentar
                    ORDER BY seg.Segmentnavn
                    """, ct: ct);

                foreach (var r in oversikt)
                {
                    r["CIDR"] = $"{r["Start_IP"]}/{r["Mask"]}";
                    r["Ledige"] = Convert.ToInt32(r["Adresser"]) - Convert.ToInt32(r["IBruk"]);
                }
                return Database.ToJson(new { segmenter = oversikt });
            }

            var seg = int.TryParse(segment.Trim(), out var segId)
                ? await db.QueryAsync("SELECT ID, Segmentnavn, Start_IP, Mask, VLAN, Kommentar FROM dbo.IP_segment WHERE ID = @id",
                    [new SqlParameter("@id", segId)], ct: ct)
                : await db.QueryAsync("SELECT ID, Segmentnavn, Start_IP, Mask, VLAN, Kommentar FROM dbo.IP_segment WHERE Segmentnavn LIKE @n ORDER BY Segmentnavn",
                    [new SqlParameter("@n", $"%{segment.Trim()}%")], ct: ct);

            if (seg.Count == 0)
                return $"Fant ikke segmentet «{segment}». Kall ip_segment uten argument for å se alle segmenter.";
            if (seg.Count > 1)
            {
                // Prefer an exact name match ("Serverpark" vs "Serverpark1 (gammelt)").
                var eksakt = seg.Where(s => string.Equals(s["Segmentnavn"] as string, segment.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
                if (eksakt.Count != 1)
                    return Database.ToJson(new { melding = "Flere segmenter passer - oppgi ID.", segmenter = seg });
                seg = eksakt;
            }

            var id = (int)seg[0]["ID"]!;
            var rader = await db.QueryAsync("""
                SELECT ipa.IP_adresse, s.ServerID, s.Server, s.Kunde, s.Aktiv, sip.merknad AS Koblingsmerknad, m.Merknad
                FROM dbo.IP_adresse ipa
                LEFT JOIN dbo.server_ip sip ON sip.IP_adresse = ipa.IP_adresse
                LEFT JOIN dbo.servere s ON s.ServerID = sip.serverid
                LEFT JOIN dbo.IP_merknad m ON m.IP_adresse = ipa.IP_adresse
                WHERE ipa.IP_segment = @id
                ORDER BY ipa.IP_adresse
                """, [new SqlParameter("@id", id)], maxRows: Database.MaxRows, ct: ct);

            // One address can be linked to several servers - group per address.
            var adresser = rader
                .GroupBy(r => (string)r["IP_adresse"]!)
                .Select(g =>
                {
                    var aktive = g.Where(r => r["Aktiv"] is true).ToList();
                    var tidligere = g.Where(r => r["ServerID"] is not null && r["Aktiv"] is not true).ToList();
                    var merknad = g.First()["Merknad"] as string;
                    var iBruk = aktive.Count > 0 || !string.IsNullOrEmpty(merknad);
                    return new
                    {
                        ip = g.Key,
                        status = iBruk ? "I bruk" : "Ledig",
                        servere = aktive.Select(r => new { ServerID = r["ServerID"], Server = r["Server"], Kunde = r["Kunde"], Merknad = r["Koblingsmerknad"] }).ToList(),
                        merknad,
                        tidligere_brukt_av = tidligere.Count > 0 ? string.Join(", ", tidligere.Select(r => r["Server"])) : null,
                    };
                })
                .Where(a => !bareLedige || a.status == "Ledig")
                .ToList();

            maks = Math.Clamp(maks, 1, Database.MaxRows);
            return Database.ToJson(new
            {
                segment = seg[0],
                cidr = $"{seg[0]["Start_IP"]}/{seg[0]["Mask"]}",
                antall_vist = Math.Min(adresser.Count, maks),
                antall_totalt = adresser.Count,
                adresser = adresser.Take(maks),
            });
        }
        catch (DatabaseException ex)
        {
            return ex.Message;
        }
    }

    private static bool ContainsExactIp(string? text, string ip) =>
        text is not null && Ipv4InText().Matches(text).Any(m => m.Value == ip);

    private static async Task<object?> FindSegmentByRangeAsync(Database db, byte[] ip, CancellationToken ct)
    {
        var segmenter = await db.QueryAsync("SELECT ID AS SegmentID, Segmentnavn, Start_IP, Mask, VLAN FROM dbo.IP_segment", ct: ct);
        var ipNum = ToUInt(ip);
        foreach (var s in segmenter)
        {
            if (s["Start_IP"] is not string start || !Database.TryParseIpv4(start, out var startBytes) || s["Mask"] is not byte mask)
                continue;
            var netmask = mask == 0 ? 0u : uint.MaxValue << (32 - mask);
            if ((ipNum & netmask) == (ToUInt(startBytes) & netmask))
            {
                s["merknad"] = "Adressen er innenfor segmentet, men ikke registrert i IP-registeret.";
                return s;
            }
        }
        return null;
    }

    private static uint ToUInt(byte[] b) => (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
}
