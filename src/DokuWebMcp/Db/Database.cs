using System.Data;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace DokuWebMcp.Db;

/// <summary>
/// Read-only access to timeregSQL. All queries are parameterized and capped
/// by row count and command timeout.
/// </summary>
public sealed class Database
{
    public const int MaxRows = 500;
    private const int CommandTimeoutSeconds = 30;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Keep æøå readable instead of \uXXXX escapes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string? _connectionString;

    public Database(IConfiguration config)
    {
        var raw = config.GetConnectionString("DokuWeb");
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var csb = new SqlConnectionStringBuilder(raw)
            {
                ConnectTimeout = 10,
                ApplicationName = "DokuWeb-MCP",
            };
            _connectionString = csb.ConnectionString;
        }
    }

    public async Task<List<Dictionary<string, object?>>> QueryAsync(
        string sql,
        IEnumerable<SqlParameter>? parameters = null,
        int maxRows = MaxRows,
        CancellationToken ct = default)
    {
        var (rows, _) = await QueryWithTruncationAsync(sql, parameters, maxRows, ct);
        return rows;
    }

    /// <summary>
    /// Runs a query and reports whether more rows existed than <paramref name="maxRows"/>.
    /// </summary>
    public async Task<(List<Dictionary<string, object?>> Rows, bool Truncated)> QueryWithTruncationAsync(
        string sql,
        IEnumerable<SqlParameter>? parameters = null,
        int maxRows = MaxRows,
        CancellationToken ct = default)
    {
        if (_connectionString is null)
            throw new DatabaseException(
                "Tilkoblingsstreng mangler. Sett den med: dotnet user-secrets set \"ConnectionStrings:DokuWeb\" \"<streng>\" --project <sti til DokuWebMcp>");

        maxRows = Math.Clamp(maxRows, 1, MaxRows);
        var rows = new List<Dictionary<string, object?>>();
        var truncated = false;

        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = CommandTimeoutSeconds };
            if (parameters is not null)
                cmd.Parameters.AddRange(parameters.ToArray());

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (rows.Count == maxRows)
                {
                    truncated = true;
                    break;
                }

                var row = new Dictionary<string, object?>(reader.FieldCount);
                for (var i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : ConvertValue(reader.GetValue(i));
                rows.Add(row);
            }
        }
        catch (SqlException ex) when (IsConnectivityError(ex))
        {
            throw new DatabaseException(
                "Får ikke kontakt med databaseserveren (web01.enternett.no). Er VPN tilkoblet?", ex);
        }
        catch (SqlException ex) when (ex.Number == 229)
        {
            throw new DatabaseException(
                $"Mangler lesetilgang: {ex.Message} Tabellen må legges til i et GRANT-skript i sql/.", ex);
        }
        catch (SqlException ex)
        {
            throw new DatabaseException($"Databasefeil: {ex.Message}", ex);
        }

        return (rows, truncated);
    }

    public static string ToJson(object value) => JsonSerializer.Serialize(value, JsonOptions);

    /// <summary>binary(4) columns in timeregSQL are IPv4 addresses (network byte order).</summary>
    public static SqlParameter IpParameter(string name, byte[] ip) =>
        new(name, SqlDbType.Binary, 4) { Value = ip };

    public static bool TryParseIpv4(string? text, out byte[] bytes)
    {
        bytes = [];
        if (text is null || !IPAddress.TryParse(text.Trim(), out var ip)
            || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || text.Trim().Count(c => c == '.') != 3)
            return false;
        bytes = ip.GetAddressBytes();
        return true;
    }

    private static object? ConvertValue(object value) => value switch
    {
        byte[] { Length: 4 } ip => new IPAddress(ip).ToString(),
        byte[] other => $"<binær, {other.Length} byte>",
        // Legacy data has stray tabs/spaces; blank strings carry no information.
        string str => str.Trim() is { Length: > 0 } t ? t : null,
        _ => value,
    };

    // -2 timeout, 53/40 network path, 10060/10061 TCP, 11001 DNS, -1 generic connect failure.
    private static bool IsConnectivityError(SqlException ex) =>
        ex.Number is -2 or -1 or 2 or 40 or 53 or 10060 or 10061 or 11001;
}

public sealed class DatabaseException(string message, Exception? inner = null) : Exception(message, inner);
