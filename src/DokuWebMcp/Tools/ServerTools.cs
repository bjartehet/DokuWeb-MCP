using System.ComponentModel;
using DokuWebMcp.Db;
using Microsoft.Data.SqlClient;
using ModelContextProtocol.Server;

namespace DokuWebMcp.Tools;

[McpServerToolType]
public static class ServerTools
{
    [McpServerTool(Name = "sok_server", Title = "Søk etter server", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("""
        Søker etter servere i DokuWeb. Alle filtre er valgfrie og kombineres (OG).
        Fritekst matcher servernavn, beskrivelse, tjenester, kundenavn og IP-feltene.
        Kun aktive servere med mindre inkluderInaktive=true. Bruk ServerID videre i hent_server.
        """)]
    public static async Task<string> SokServer(
        Database db,
        [Description("Fritekst: del av servernavn, beskrivelse, tjenester, kunde eller IP.")]
        string? sok = null,
        [Description("Kun servere for dette kundenummeret.")]
        int? kundenummer = null,
        [Description("Del av operativsystem, f.eks. \"2019\" eller \"Linux\".")]
        string? os = null,
        [Description("Del av datasenternavn: EnterNett, Klikk, Hetzner, Netpower, Upheads, Avvikles.")]
        string? datasenter = null,
        [Description("Ansvarlig tekniker (kortform/navn), f.eks. \"Bjarte\".")]
        string? ansvar = null,
        [Description("Ta med inaktive (avviklede) servere. Standard false.")]
        bool inkluderInaktive = false,
        [Description("Maks antall treff (1-200). Standard 50.")]
        int maks = 50,
        CancellationToken ct = default)
    {
        maks = Math.Clamp(maks, 1, 200);
        const string sql = """
            SELECT TOP (@maks)
                s.ServerID, s.Server, s.Kunde, s.kundeid AS Kundenummer, s.Type, s.OS, s.Beskrivelse,
                s.IPadresse, s.Ansvar, d.Datasenter, s.Aktiv
            FROM dbo.servere s
            LEFT JOIN dbo.Datasenter d ON d.DatasenterID = s.DatasenterID
            WHERE (@inkluderInaktive = 1 OR s.Aktiv = 1)
              AND (@sok IS NULL OR s.Server LIKE @sok OR s.Beskrivelse LIKE @sok OR s.Tjenester LIKE @sok
                   OR s.Kunde LIKE @sok OR s.IPadresse LIKE @sok OR s.AltIP LIKE @sok)
              AND (@kundenr IS NULL OR s.kundeid = @kundenr)
              AND (@os IS NULL OR s.OS LIKE @os OR s.[Guest OS] LIKE @os)
              AND (@datasenter IS NULL OR d.Datasenter LIKE @datasenter)
              AND (@ansvar IS NULL OR s.Ansvar LIKE @ansvar)
            ORDER BY s.Kunde, s.Server
            """;

        var parameters = new[]
        {
            new SqlParameter("@maks", maks),
            new SqlParameter("@inkluderInaktive", inkluderInaktive),
            Like("@sok", sok),
            new SqlParameter("@kundenr", (object?)kundenummer ?? DBNull.Value),
            Like("@os", os),
            Like("@datasenter", datasenter),
            Like("@ansvar", ansvar),
        };

        try
        {
            var (rows, truncated) = await db.QueryWithTruncationAsync(sql, parameters, maks, ct);
            if (rows.Count == 0)
                return "Ingen servere funnet med disse filtrene.";
            return Database.ToJson(new
            {
                antall = rows.Count,
                avkortet = truncated ? "Ja - flere treff finnes. Snevre inn søket eller øk maks." : null,
                servere = rows,
            });
        }
        catch (DatabaseException ex)
        {
            return ex.Message;
        }
    }

    [McpServerTool(Name = "hent_server", Title = "Hent server", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("""
        Henter alle detaljer om én server fra DokuWeb: kunde, type, OS, ressurser, datasenter,
        ansvarlig, passordgruppe (PG1/PG2/KP/Gen2 - ikke selve passordet), lisenser, backup-jobber,
        IP-adresser med segment, tjenester som kjører på serveren, VMware-data og driftsnotat.
        Oppgi serverId (fra sok_server) eller servernavn.
        """)]
    public static async Task<string> HentServer(
        Database db,
        [Description("ServerID fra sok_server.")]
        int? serverId = null,
        [Description("Servernavn (eksakt eller del av navn) hvis ServerID ikke er kjent.")]
        string? servernavn = null,
        CancellationToken ct = default)
    {
        try
        {
            if (serverId is null)
            {
                if (string.IsNullOrWhiteSpace(servernavn))
                    return "Oppgi serverId eller servernavn.";

                var kandidater = await db.QueryAsync("""
                    SELECT TOP 20 ServerID, Server, Kunde, Aktiv
                    FROM dbo.servere
                    WHERE Server = @navn OR Server LIKE @like
                    ORDER BY CASE WHEN Server = @navn THEN 0 ELSE 1 END, Aktiv DESC, Server
                    """, [new SqlParameter("@navn", servernavn.Trim()), new SqlParameter("@like", $"%{servernavn.Trim()}%")], ct: ct);

                if (kandidater.Count == 0)
                    return $"Fant ingen server med navn som inneholder «{servernavn}».";
                var eksakt = kandidater.Where(k => string.Equals(k["Server"] as string, servernavn.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
                if (eksakt.Count == 1)
                    serverId = (int)eksakt[0]["ServerID"]!;
                else if (kandidater.Count == 1)
                    serverId = (int)kandidater[0]["ServerID"]!;
                else
                    return Database.ToJson(new { melding = "Flere servere passer - kall hent_server med serverId.", kandidater });
            }

            var p = () => new[] { new SqlParameter("@id", serverId.Value) };

            var server = await db.QueryAsync("""
                SELECT s.ServerID, s.Server, s.Aktiv, s.Kunde, s.kundeid AS Kundenummer,
                       s.Type, s.Beskrivelse, s.Tjenester, s.Hosting, s.Panel,
                       s.OS, s.[Guest OS] AS GuestOS, s.SP, s.CPU, s.Minne, s.[Disk], s.Trafikk,
                       s.IPadresse, s.AltIP,
                       d.Datasenter, fd.Datasenter AS FramtidigDatasenter, s.future AS Framtid,
                       s.Ansvar, s.Kontaktperson, s.Epost,
                       s.[Admin#bruker] AS Adminbruker, s.Passord AS Passordgruppe,
                       s.Pwd_update AS PassordOppdatert, s.Pwd_update_date AS PassordOppdatertDato,
                       s.Antivirus, s.[Lisens OS] AS LisensOS, s.[Lisens SQL] AS LisensSQL,
                       s.[Backup] AS BackupNotat, s.Windows_update AS WindowsUpdate,
                       s.Enhanced_security AS EnhancedSecurity,
                       s.VMWare_ID, s.VMWareTools, s.[VMWare-check] AS VMWareCheck,
                       s.VPSpris, s.[VPS-pris] AS VPSprisGammel,
                       s.[EHS-link] AS EHSlink, s.[RDP-link?] AS RDPlink, s.Munin, s.[Feil?] AS Feil,
                       s.Kommentar, s.Driftsnotat
                FROM dbo.servere s
                LEFT JOIN dbo.Datasenter d ON d.DatasenterID = s.DatasenterID
                LEFT JOIN dbo.Datasenter fd ON fd.DatasenterID = s.FramtidigDatasenter
                WHERE s.ServerID = @id
                """, p(), ct: ct);

            if (server.Count == 0)
                return $"Fant ingen server med ServerID {serverId}.";

            var navn = server[0]["Server"] as string ?? "";

            var ip = await db.QueryAsync("""
                SELECT sip.IP_adresse, sip.hoved AS Hoved, sip.merknad AS Merknad, seg.Segmentnavn, seg.VLAN
                FROM dbo.server_ip sip
                LEFT JOIN dbo.IP_adresse ipa ON ipa.IP_adresse = sip.IP_adresse
                LEFT JOIN dbo.IP_segment seg ON seg.ID = ipa.IP_segment
                WHERE sip.serverid = @id
                ORDER BY sip.hoved DESC, sip.IP_adresse
                """, p(), ct: ct);

            // DokuWeb joins backup jobs on server name; ServerID is 0 for some rows.
            var backup = await db.QueryAsync("""
                SELECT backup_jobb AS Jobb, server_navn AS Servernavn, oppdatert AS Oppdatert
                FROM dbo.server_backup
                WHERE ServerID = @id OR (server_navn = @navn AND @navn <> '')
                """, [new SqlParameter("@id", serverId.Value), new SqlParameter("@navn", navn)], ct: ct);

            var tjenester = await db.QueryAsync("""
                SELECT t.tjeneste AS Tjeneste, t.beskrivelse AS Beskrivelse, t.domene AS Domene,
                       t.kundenr AS Kundenummer, k.Kundenavn, t.ansvar AS Ansvar
                FROM dbo.kunde_tjeneste t
                LEFT JOIN dbo.Kunde k ON k.Kundenummer = t.kundenr
                WHERE t.serverID = @id
                ORDER BY k.Kundenavn, t.tjeneste
                """, p(), ct: ct);

            // VMware data is optional - don't fail the whole lookup if it's unavailable.
            object? vmware;
            try
            {
                var vm = await db.QueryAsync("""
                    SELECT TOP 1 vm.State, vm.Status, vm.[Host CPU] AS vCPU, vm.[Host Mem] AS MinneMB,
                           vm.[Provisioned Space] AS Tildelt, vm.[Used Space] AS Brukt,
                           vm.[Guest OS] AS GuestOS, vm.[IP Address] AS IP, vm.[DNS Name] AS DNS,
                           vm.[VMware Tools Version Status] AS VMwareTools, vm.Oppdatert, vm.Notat
                    FROM dbo.[VM-import] vm
                    WHERE vm.Name = @navn
                    """, [new SqlParameter("@navn", navn)], ct: ct);
                vmware = vm.Count > 0 ? vm[0] : "Ikke funnet i VMware-importen.";
            }
            catch (DatabaseException ex)
            {
                vmware = $"Ikke tilgjengelig: {ex.Message}";
            }

            return Database.ToJson(new
            {
                server = server[0],
                ip_adresser = ip,
                backup_jobber = backup,
                tjenester_på_serveren = tjenester,
                vmware,
            });
        }
        catch (DatabaseException ex)
        {
            return ex.Message;
        }
    }

    private static SqlParameter Like(string name, string? value) =>
        new(name, string.IsNullOrWhiteSpace(value) ? DBNull.Value : $"%{value.Trim()}%");
}
