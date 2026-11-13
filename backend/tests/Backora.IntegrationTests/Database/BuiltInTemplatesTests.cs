using Backora.Domain.Backups;
using Backora.Infrastructure.Persistence;
using Backora.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backora.IntegrationTests.Database;

/// <summary>
/// Fixes to the built-in templates must reach the copies stored in the database at startup, and must never overwrite
/// a template an administrator edited.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class BuiltInTemplatesTests(PostgresContainer pg) : IAsyncLifetime
{
    private BackoraApiFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new BackoraApiFactory(await pg.CreateDatabaseAsync("builtin"));
        _ = _factory.Server;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private static string TemplatesDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "database", "templates");
            if (File.Exists(Path.Combine(candidate, "_common.inc"))) return candidate;
        }
        throw new DirectoryNotFoundException("backend/database/templates not found");
    }

    [Fact]
    public void The_embedded_templates_are_the_files_in_the_repository()
    {
        var dir = TemplatesDir();
        var common = File.ReadAllText(Path.Combine(dir, "_common.inc"));
        var files = Directory.GetFiles(dir, "*.sh").Select(f => BuiltInTemplates.Inline(File.ReadAllText(f), common)).ToHashSet();
        var embedded = BuiltInTemplates.Load();
        embedded.Select(t => t.Name).Should().BeEquivalentTo(["MariaDB · Logical", "MariaDB · Physical", "PostgreSQL · Logical", "PostgreSQL · Physical"]);
        embedded.Select(t => t.Script).Should().BeEquivalentTo(files);
    }

    [Fact]
    public void Every_current_template_is_listed_as_shipped()
    {
        var shipped = BuiltInTemplates.ShippedVersions();
        foreach (var t in BuiltInTemplates.Load())
        {
            shipped.Should().Contain(BuiltInTemplates.Hash(t.Script),
                $"'{t.Name}' changed: add '{BuiltInTemplates.Hash(t.Script)}  {t.Name}' to backend/database/templates/shipped-versions.txt so the next change can replace it");
        }
    }

    [Fact]
    public async Task Startup_updates_shipped_versions_and_leaves_edited_templates_alone()
    {
        var current = BuiltInTemplates.Load()[0];
        var older = current.Script.Replace("set -euo pipefail", "set -eu", StringComparison.Ordinal);   // an earlier shipped version
        var edited = current.Script + "\n# local change\n";
        var shipped = new HashSet<string>(StringComparer.Ordinal) { BuiltInTemplates.Hash(older) };
        var renamed = current with { Name = current.Name + " " + Guid.NewGuid().ToString("N")[..6] };
        var other = current with { Name = current.Name + " edited " + Guid.NewGuid().ToString("N")[..6] };

        await _factory.WithDbAsync(async db =>
        {
            var a = new ScriptTemplate { Name = renamed.Name, Category = current.Category, Script = older, Revision = 5 };
            var b = new ScriptTemplate { Name = other.Name, Category = current.Category, Script = edited, Revision = 3 };
            db.AddRange(a, b);
            await db.SaveChangesAsync();

            (await BuiltInTemplates.SyncAsync(db, NullLogger.Instance, CancellationToken.None, [renamed, other], shipped)).Should().Be(1);
            (await BuiltInTemplates.SyncAsync(db, NullLogger.Instance, CancellationToken.None, [renamed, other], shipped)).Should().Be(0, "idempotent");

            db.ChangeTracker.Clear();
            var after = await db.ScriptTemplates.AsNoTracking().SingleAsync(t => t.Id == a.Id);
            after.Script.Should().Be(current.Script);
            after.Revision.Should().Be(6);
            var untouched = await db.ScriptTemplates.AsNoTracking().SingleAsync(t => t.Id == b.Id);
            untouched.Script.Should().Be(edited);
            untouched.Revision.Should().Be(3);
        });
    }
}
