using Backora.Infrastructure.Persistence;
using Backora.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Backora.IntegrationTests.Database;

/// <summary>
/// The "enabled" state added for multi-site DR must not silently stop copies to destinations that existed before it:
/// a database migrated from the previous schema keeps every destination enabled.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DestinationEnabledMigrationTests(PostgresContainer pg)
{
    private const string PreviousMigration = "20261108170249_ExecutionHostCapabilities";

    [Fact]
    public async Task Existing_destinations_stay_enabled_after_the_migration()
    {
        var cs = await pg.CreateDatabaseAsync("migr");
        var options = new DbContextOptionsBuilder<BackoraDbContext>()
            .UseNpgsql(cs, o => o.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention()
            .Options;

        await using (var db = new BackoraDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        }

        // Insert two destinations exactly as the previous schema stored them (no is_enabled column yet).
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await using (var check = new NpgsqlCommand("select count(*) from information_schema.columns where table_name = 's3_destinations' and column_name = 'is_enabled'", conn))
            {
                ((long)(await check.ExecuteScalarAsync())!).Should().Be(0, "the test must start from the schema before the column existed");
            }
            var required = new List<(string Name, string Type)>();
            await using (var cols = new NpgsqlCommand("""
                select column_name, data_type from information_schema.columns
                where table_name = 's3_destinations' and is_nullable = 'NO' and column_default is null and is_identity = 'NO' and column_name <> 'xmin'
                """, conn))
            await using (var r = await cols.ExecuteReaderAsync())
            {
                while (await r.ReadAsync()) required.Add((r.GetString(0), r.GetString(1)));
            }
            foreach (var name in new[] { "site-a", "site-b" })
            {
                var values = required.Select(c => c.Name == "name" ? $"'{name}'" : c.Type switch
                {
                    "boolean" => "true",
                    "integer" or "bigint" or "smallint" => "0",
                    var t when t.StartsWith("timestamp", StringComparison.Ordinal) => "now()",
                    _ => c.Name == "encryption_mode" ? "'None'" : "'x'",
                });
                await using var insert = new NpgsqlCommand($"insert into s3_destinations ({string.Join(", ", required.Select(c => c.Name))}) values ({string.Join(", ", values)})", conn);
                await insert.ExecuteNonQueryAsync();
            }
        }

        await using (var db = new BackoraDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync();
            var destinations = await db.S3Destinations.AsNoTracking().OrderBy(d => d.Name).ToListAsync();
            destinations.Select(d => d.Name).Should().Equal("site-a", "site-b");
            destinations.Should().OnlyContain(d => d.IsEnabled, "destinations created before the column existed must keep receiving copies");

            // New destinations default to enabled too, and can be disabled explicitly.
            var site = destinations[0];
            var tracked = await db.S3Destinations.FirstAsync(d => d.Id == site.Id);
            tracked.IsEnabled = false;
            await db.SaveChangesAsync();
        }
        await using (var db = new BackoraDbContext(options))
        {
            (await db.S3Destinations.AsNoTracking().Where(d => d.Name == "site-a").Select(d => d.IsEnabled).SingleAsync()).Should().BeFalse();
        }
    }
}
