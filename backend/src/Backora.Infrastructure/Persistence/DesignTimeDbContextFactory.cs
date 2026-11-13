using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Backora.Infrastructure.Persistence;

/// <summary>Used only by <c>dotnet ef</c> to generate migrations. No connection is opened.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<BackoraDbContext>
{
    public BackoraDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<BackoraDbContext>()
            .UseNpgsql("Host=localhost;Database=backora_design;Username=design", o => o.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention()
            .Options;
        return new BackoraDbContext(options);
    }
}
