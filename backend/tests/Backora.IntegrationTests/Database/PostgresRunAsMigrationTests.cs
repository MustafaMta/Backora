using Backora.Domain.Backups;
using Backora.Infrastructure.Persistence.Migrations;
using Backora.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Backora.IntegrationTests.Database;

/// <summary>
/// The stored templates used `su - postgres`, which fails where postgres has a nologin shell. The migration must swap
/// the old lines for as_postgres once and leave custom templates alone.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresRunAsMigrationTests(PostgresContainer pg) : IAsyncLifetime
{
    private BackoraApiFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new BackoraApiFactory(await pg.CreateDatabaseAsync("runas"));
        _ = _factory.Server;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Stored_templates_get_the_new_lines_once_and_custom_ones_are_untouched()
    {
        // A fixed snapshot of the stored scripts as they were (the template files have moved on since; later template
        // changes reach the database through BuiltInTemplates.SyncAsync).
        var lines = PostgresRunAsWithoutLoginShell.Lines;
        var previous = "#!/bin/bash\nset -euo pipefail" + string.Concat(lines.Select(l => "\n" + l.Old.TrimStart('\n'))) + "\n";
        var expected = "#!/bin/bash\nset -euo pipefail" + string.Concat(lines.Select(l => "\n" + l.New.TrimStart('\n'))) + "\n";
        previous.Should().Contain("su - postgres -c").And.NotContain("as_postgres");

        await _factory.WithDbAsync(async db =>
        {
            var suffix = Guid.NewGuid().ToString("N")[..6];
            var builtIn = new ScriptTemplate { Name = "PostgreSQL · Physical " + suffix, Category = "PostgreSQL", Script = previous, Revision = 4 };
            var custom = new ScriptTemplate { Name = "custom-" + suffix, Category = "PostgreSQL", Script = "su - postgres -c 'pg_dumpall' > /tmp/x", Revision = 1 };
            db.AddRange(builtIn, custom);
            await db.SaveChangesAsync();

            // ExecuteSqlRaw treats {…} as format placeholders (migrationBuilder.Sql does not), so escape them here.
            var sql = PostgresRunAsWithoutLoginShell.UpdateSql.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal);
            await db.Database.ExecuteSqlRawAsync(sql);
            await db.Database.ExecuteSqlRawAsync(sql);   // idempotent

            var after = await db.ScriptTemplates.AsNoTracking().SingleAsync(t => t.Id == builtIn.Id);
            after.Script.Should().Be(expected);
            after.Script.Should().NotContain("su - postgres -c");
            after.Revision.Should().Be(5);
            var untouched = await db.ScriptTemplates.AsNoTracking().SingleAsync(t => t.Id == custom.Id);
            untouched.Script.Should().Be(custom.Script);
            untouched.Revision.Should().Be(1);
        });
    }
}
