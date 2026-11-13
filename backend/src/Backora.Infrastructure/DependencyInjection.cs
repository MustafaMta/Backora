using Backora.Application.Abstractions;
using Backora.Application.Backups;
using Backora.Application.Encryption;
using Backora.Application.Execution;
using Backora.Application.Insights;
using Backora.Application.Inventory;
using Backora.Application.Notifications;
using Backora.Application.Operations;
using Backora.Infrastructure.Auditing;
using Backora.Infrastructure.Identity;
using Backora.Infrastructure.Notifications;
using Backora.Infrastructure.Persistence;
using Backora.Infrastructure.Remote;
using Backora.Infrastructure.Security;
using Backora.Infrastructure.Storage;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Backora.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers persistence, security, remote execution and all application services.</summary>
    public static IServiceCollection AddBackoraCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(sp =>
        {
            // Resolved lazily so hosts/tests can supply configuration after service registration.
            var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString("Backora");
            if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("ConnectionStrings:Backora is not configured.");
            var builder = new NpgsqlDataSourceBuilder(connectionString);
            builder.ConnectionStringBuilder.ApplicationName ??= "backora";
            builder.EnableDynamicJson();
            return builder.Build();
        });
        services.AddDbContext<BackoraDbContext>((sp, o) =>
        {
            o.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npgsql =>
            {
                npgsql.MigrationsHistoryTable("__ef_migrations_history");
                npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null);
            });
            o.UseSnakeCaseNamingConvention();
        });
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<BackoraDbContext>());

        services.AddOptions<EncryptionOptions>().Bind(configuration.GetSection(EncryptionOptions.Section));
        services.AddOptions<ClientEncryptionOptions>().Bind(configuration.GetSection(ClientEncryptionOptions.Section));
        services.AddOptions<SshOptions>().Bind(configuration.GetSection(SshOptions.Section));
        services.AddOptions<WorkerOptions>().Bind(configuration.GetSection(WorkerOptions.Section));
        services.AddOptions<PlatformOptions>().Bind(configuration.GetSection(PlatformOptions.Section));
        services.AddOptions<JwtOptions>().Bind(configuration.GetSection(JwtOptions.Section));

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();
        services.AddSingleton<ISshConnector, SshConnector>();
        // S3 buckets through the S3 API; folder sites (a mounted NFS share) on the execution host.
        services.AddSingleton<S3ObjectStorage>();
        services.AddSingleton<WorkerFolderStorage>();
        services.AddSingleton<IObjectStorage, RoutingObjectStorage>();
        services.AddSingleton<IEmailSender, MailKitEmailSender>();
        services.AddSingleton<ITeamsSender, TeamsWebhookSender>();
        services.AddSingleton<ILeaseStore, PostgresLeaseStore>();
        services.AddSingleton<DecryptionKeyCourier>();
        services.AddHttpClient(TeamsWebhookSender.ClientName, c => c.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

        services.AddScoped<IAuditLog, AuditLog>();
        services.AddScoped<ExecutionEnvironmentFactory>();
        services.AddScoped<NotificationPublisher>();

        // Use cases
        services.AddScoped<ServerService>();
        services.AddOptions<ExecutionHostOptions>().Bind(configuration.GetSection(ExecutionHostOptions.Section));
        services.AddSingleton<ExecutionHostKeys>();
        services.AddScoped<SettingsService>();
        services.AddScoped<ExecutionHostService>();
        services.AddScoped<VariableService>();
        services.AddScoped<TemplateService>();
        services.AddScoped<StorageService>();
        services.AddScoped<BackupJobService>();
        services.AddScoped<RunService>();
        services.AddScoped<RecoveryService>();
        services.AddScoped<DashboardService>();
        services.AddScoped<AuditQueryService>();
        services.AddScoped<SearchService>();
        services.AddScoped<SystemService>();

        // Background operations
        services.AddScoped<SchedulerService>();
        services.AddScoped<RetentionService>();
        services.AddScoped<NotificationDispatcher>();
        services.AddSingleton<BackupRunProcessor>();
        services.AddSingleton<OffloadRunProcessor>();
        services.AddSingleton<RestoreRunProcessor>();

        services.AddScoped<DatabaseInitializer>();
        return services;
    }

    /// <summary>ASP.NET Core Identity with the hardened password and lockout policy.</summary>
    public static IdentityBuilder AddBackoraIdentity(this IServiceCollection services)
    {
        services.AddSingleton<IPasswordHasher<ApplicationUser>, CompatPasswordHasher>();
        services.Configure<PasswordHasherOptions>(o => o.IterationCount = 600_000);
        var builder = services.AddIdentityCore<ApplicationUser>(o =>
            {
                o.User.RequireUniqueEmail = true;
                o.Password.RequiredLength = 12;
                o.Password.RequireDigit = true;
                o.Password.RequireLowercase = true;
                o.Password.RequireUppercase = true;
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequiredUniqueChars = 5;
                o.Lockout.AllowedForNewUsers = true;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                o.Tokens.AuthenticatorTokenProvider = TokenOptions.DefaultAuthenticatorProvider;
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<BackoraDbContext>()
            .AddDefaultTokenProviders();
        services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromMinutes(60));
        services.AddScoped<TokenService>();
        services.AddScoped<AuthService>();
        services.AddScoped<UserAdminService>();
        return builder;
    }
}
