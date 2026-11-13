using Backora.Application.Abstractions;
using Npgsql;

namespace Backora.Infrastructure.Persistence;

/// <summary>
/// Transactional job leasing on PostgreSQL. The candidate operation row is locked with
/// <c>FOR UPDATE SKIP LOCKED</c> so concurrent workers never pick the same row, and the lease row is
/// upserted only if absent or expired. All time comparisons use the database clock (<c>now()</c>), so
/// worker clock skew cannot cause double ownership.
/// </summary>
public sealed class PostgresLeaseStore(NpgsqlDataSource dataSource) : ILeaseStore
{
    private static readonly Dictionary<string, (string Table, string Statuses)> Kinds = new()
    {
        [OperationKinds.Backup] = ("backup_runs", "'Pending','Starting','Running','Verifying','Cancelling'"),
        [OperationKinds.Offload] = ("offload_runs", "'Pending','Running','Verifying'"),
        [OperationKinds.Restore] = ("restore_runs", "'Pending','Running','Verifying','Cancelling','AwaitingKey'"),
    };

    public async Task<long?> ClaimNextAsync(string kind, string owner, TimeSpan duration, CancellationToken ct)
    {
        var (table, statuses) = Kinds[kind];
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Table and status list come from the fixed dictionary above, never from input.
        var select = $"""
            SELECT r.id FROM {table} r
            LEFT JOIN operation_leases l ON l.kind = @kind AND l.operation_id = r.id
            WHERE r.status IN ({statuses})
              AND r.next_attempt_at <= now()
              AND (l.owner IS NULL OR l.expires_at < now())
            ORDER BY r.next_attempt_at, r.id
            FOR UPDATE OF r SKIP LOCKED
            LIMIT 1
            """;
        long? id;
        await using (var cmd = new NpgsqlCommand(select, conn, tx))
        {
            cmd.Parameters.AddWithValue("kind", kind);
            var result = await cmd.ExecuteScalarAsync(ct);
            id = result is long l ? l : null;
        }

        if (id is null)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        const string upsert = """
            INSERT INTO operation_leases (kind, operation_id, owner, acquired_at, expires_at, heartbeat_at)
            VALUES (@kind, @id, @owner, now(), now() + @duration, now())
            ON CONFLICT (kind, operation_id) DO UPDATE
                SET owner = EXCLUDED.owner, acquired_at = now(), expires_at = EXCLUDED.expires_at, heartbeat_at = now()
                WHERE operation_leases.expires_at < now() OR operation_leases.owner = EXCLUDED.owner
            RETURNING operation_id
            """;
        await using (var cmd = new NpgsqlCommand(upsert, conn, tx))
        {
            cmd.Parameters.AddWithValue("kind", kind);
            cmd.Parameters.AddWithValue("id", id.Value);
            cmd.Parameters.AddWithValue("owner", owner);
            cmd.Parameters.AddWithValue("duration", duration);
            var claimed = await cmd.ExecuteScalarAsync(ct);
            if (claimed is null)
            {
                await tx.RollbackAsync(ct);
                return null;
            }
        }

        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<bool> RenewAsync(string kind, long operationId, string owner, TimeSpan duration, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            UPDATE operation_leases SET expires_at = now() + @duration, heartbeat_at = now()
            WHERE kind = @kind AND operation_id = @id AND owner = @owner
            """);
        cmd.Parameters.AddWithValue("duration", duration);
        cmd.Parameters.AddWithValue("kind", kind);
        cmd.Parameters.AddWithValue("id", operationId);
        cmd.Parameters.AddWithValue("owner", owner);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task ReleaseAsync(string kind, long operationId, string owner, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("DELETE FROM operation_leases WHERE kind = @kind AND operation_id = @id AND owner = @owner");
        cmd.Parameters.AddWithValue("kind", kind);
        cmd.Parameters.AddWithValue("id", operationId);
        cmd.Parameters.AddWithValue("owner", owner);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> TryAcquireClusterLockAsync(string name, string owner, TimeSpan duration, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO cluster_locks (name, owner, expires_at, last_run_at)
            VALUES (@name, @owner, now() + @duration, now())
            ON CONFLICT (name) DO UPDATE SET owner = EXCLUDED.owner, expires_at = EXCLUDED.expires_at, last_run_at = now()
                WHERE cluster_locks.owner = EXCLUDED.owner OR cluster_locks.expires_at IS NULL OR cluster_locks.expires_at < now()
            RETURNING name
            """);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("owner", owner);
        cmd.Parameters.AddWithValue("duration", duration);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }
}
