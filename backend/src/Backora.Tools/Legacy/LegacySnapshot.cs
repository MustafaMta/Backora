using System.Globalization;
using MySqlConnector;

namespace Backora.Tools.Legacy;

/// <summary>One row of a legacy table. Tolerates columns that are missing in older dump versions.</summary>
public sealed class LegacyRow(IReadOnlyDictionary<string, object?> values)
{
    public IReadOnlyDictionary<string, object?> Values { get; } = values;

    public long Id => Long("id") ?? throw new InvalidOperationException("Row without id.");

    public bool Has(string column) => Values.ContainsKey(column);

    public object? Raw(string column) => Values.TryGetValue(column, out var v) ? v : null;

    public string? Str(string column) => Raw(column) switch
    {
        null => null,
        string s => s,
        byte[] b => System.Text.Encoding.UTF8.GetString(b),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        var o => o.ToString(),
    };

    public long? Long(string column) => Raw(column) switch
    {
        null => null,
        long l => l,
        int i => i,
        ulong u => (long)u,
        uint u => u,
        short s => s,
        ushort s => s,
        sbyte s => s,
        byte b => b,
        decimal d => (long)d,
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) => p,
        _ => null,
    };

    public int? Int(string column) => Long(column) is { } l ? (int)l : null;

    public decimal? Dec(string column) => Raw(column) switch
    {
        null => null,
        decimal d => d,
        double d => (decimal)d,
        float f => (decimal)f,
        long l => l,
        int i => i,
        string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var p) => p,
        _ => null,
    };

    /// <summary>Legacy timestamps are UTC (dumps are taken with TIME_ZONE='+00:00' and Laravel app timezone UTC).</summary>
    public DateTimeOffset? Ts(string column) => Raw(column) switch
    {
        DateTime dt when dt == DateTime.MinValue => null,
        DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
        DateTimeOffset dto => dto.ToUniversalTime(),
        string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var p)
            => new DateTimeOffset(DateTime.SpecifyKind(p, DateTimeKind.Utc)),
        _ => null,
    };
}

/// <summary>Full in-memory copy of the legacy domain tables (the legacy database is only ever read).</summary>
public sealed class LegacySnapshot
{
    public static readonly string[] DomainTables =
    [
        "users", "servers", "worker_settings", "ssh_settings", "nfs_settings", "variables", "templates", "s3_settings",
        "smtp_settings", "teams_settings", "notification_settings", "backup_jobs", "backup_job_executions",
        "upload_to_s3_job_executions", "restore_to_nfs_job_executions", "api_tokens",
    ];

    public Dictionary<string, List<LegacyRow>> Tables { get; } = new(StringComparer.Ordinal);
    public string? ServerVersion { get; set; }
    public string Database { get; set; } = string.Empty;

    public List<LegacyRow> this[string table] => Tables.TryGetValue(table, out var rows) ? rows : [];

    public static async Task<LegacySnapshot> ReadAsync(string connectionString, CancellationToken ct)
    {
        var csb = new MySqlConnectionStringBuilder(connectionString)
        {
            ConvertZeroDateTime = true,
            AllowZeroDateTime = false,
            TreatTinyAsBoolean = false,
        };
        var snapshot = new LegacySnapshot { Database = csb.Database };
        await using var conn = new MySqlConnection(csb.ConnectionString);
        await conn.OpenAsync(ct);
        snapshot.ServerVersion = conn.ServerVersion;

        // Read-only, consistent snapshot: the legacy database is never modified.
        await using (var ro = new MySqlCommand("SET SESSION TRANSACTION ISOLATION LEVEL REPEATABLE READ; START TRANSACTION READ ONLY, WITH CONSISTENT SNAPSHOT;", conn))
        {
            await ro.ExecuteNonQueryAsync(ct);
        }

        var existing = new HashSet<string>(StringComparer.Ordinal);
        await using (var cmd = new MySqlCommand("SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE()", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) existing.Add(r.GetString(0));
        }

        foreach (var table in DomainTables)
        {
            if (!existing.Contains(table)) continue;
            var rows = new List<LegacyRow>();
            // Table names come from the fixed list above.
            await using var cmd = new MySqlCommand($"SELECT * FROM `{table}` ORDER BY id", conn) { CommandTimeout = 600 };
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var values = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (var i = 0; i < r.FieldCount; i++) values[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
                rows.Add(new LegacyRow(values));
            }
            snapshot.Tables[table] = rows;
        }

        await using (var end = new MySqlCommand("COMMIT", conn)) await end.ExecuteNonQueryAsync(ct);
        return snapshot;
    }
}
