using Backora.Application.Abstractions;
using Backora.Contracts.Security;
using Backora.Infrastructure;
using Backora.Infrastructure.Auditing;
using Backora.Infrastructure.Identity;
using Backora.Infrastructure.Persistence;
using Backora.Tools.Legacy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backora.Tools;

/// <summary>
/// Operational CLI: migrate, create-admin, rotate-keys, import-legacy, verify-import.
/// Configuration comes from environment variables exactly like the API (ConnectionStrings__Backora,
/// Encryption__ActiveKeyId, Encryption__Keys__&lt;id&gt;). Exit codes: 0 success, 1 failure/FAIL, 2 usage.
/// </summary>
public static class ToolsProgram
{
    private const string Usage = """
        Backora.Tools <command> [options]

          migrate                                   Apply database migrations and seed roles
          create-admin --email <e> --name <n>       Create a Platform Administrator (password from BACKORA_ADMIN_PASSWORD or prompt)
          rotate-keys [--dry-run]                   Re-encrypt secrets that are not under the active encryption key
          import-legacy --mysql "<conn>" [--dry-run] [--report <file.json|file.md>] [--allow-non-empty] [--preserve-job-status]
                                                    Import a legacy Laravel/MariaDB Backora database (read-only on the source)
          verify-import --mysql "<conn>" [--report <file>]
                                                    Compare legacy and target without writing
          s3-site --name <n> --endpoint <url> --region <r> --bucket <b> [--prefix <p>] [--path-style] [--backora-key] [--link-jobs a,b,c]
                                                    Create/update a storage site (keys from BACKORA_S3_ACCESS_KEY/_SECRET_KEY) and link jobs as DR copies
          consolidate-templates --templates <dir> [--dry-run] [--templates-only]
                                                    Merge imported templates into the unified set; credentials move into encrypted job settings
          import-script-variables --env-file <path>
                                                    Store BACKORA__NAME[__JOB]='value' lines as Backora variables NAME[__JOB] (secrets encrypted)

        Environment: ConnectionStrings__Backora, Encryption__ActiveKeyId, Encryption__Keys__<id>
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        var command = args[0];
        var options = ParseOptions(args.Skip(1).ToArray());
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            using var host = BuildHost();
            await using var scope = host.Services.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            return command switch
            {
                "migrate" => await MigrateAsync(sp, cts.Token),
                "create-admin" => await CreateAdminAsync(sp, options, cts.Token),
                "rotate-keys" => await RotateKeysAsync(sp, options.ContainsKey("dry-run"), cts.Token),
                "import-legacy" => await ImportAsync(sp, options, cts.Token),
                "verify-import" => await VerifyAsync(sp, options, cts.Token),
                "s3-site" => await new S3SiteCommand(sp.GetRequiredService<BackoraDbContext>(), sp.GetRequiredService<ISecretProtector>()).RunAsync(options, cts.Token),
                "import-script-variables" => await new ScriptVariablesImport(sp.GetRequiredService<BackoraDbContext>(), sp.GetRequiredService<ISecretProtector>()).RunAsync(options, cts.Token),
                "consolidate-templates" => await new TemplateConsolidation(sp.GetRequiredService<BackoraDbContext>(), sp.GetRequiredService<ISecretProtector>())
                    .RunAsync(options.GetValueOrDefault("templates") ?? throw new UsageException("--templates <dir> is required."), options.ContainsKey("dry-run"), cts.Token, options.ContainsKey("templates-only")),
                _ => UsageError($"Unknown command '{command}'."),
            };
        }
        catch (UsageException ex)
        {
            return UsageError(ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Messages only (no stack traces with possible connection details) unless BACKORA_TOOLS_DEBUG=1.
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            if (ex.InnerException is not null) Console.Error.WriteLine($"       {ex.InnerException.Message}");
            if (Environment.GetEnvironmentVariable("BACKORA_TOOLS_DEBUG") == "1") Console.Error.WriteLine(ex);
            return 1;
        }
    }

    public static IHost BuildHost(Action<IConfigurationBuilder>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        // appsettings.json and appsettings.{Environment}.json next to the binary, then environment variables (highest).
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
        builder.Configuration.SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true);
        builder.Configuration.AddEnvironmentVariables();
        configure?.Invoke(builder.Configuration);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddFilter("Backora", LogLevel.Information);
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Error);
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.None);
        builder.Services.AddBackoraCore(builder.Configuration);
        builder.Services.AddBackoraIdentity();
        builder.Services.AddDataProtection().SetApplicationName("Backora.Tools");
        builder.Services.AddSingleton<ICurrentUser, SystemUser>();
        builder.Services.AddScoped<LegacyImporter>();
        return builder.Build();
    }

    private static async Task<int> MigrateAsync(IServiceProvider sp, CancellationToken ct)
    {
        var init = sp.GetRequiredService<DatabaseInitializer>();
        await init.MigrateAsync(ct);
        await init.SeedAsync(ct);
        Console.WriteLine("Database is up to date; roles seeded.");
        return 0;
    }

    private static async Task<int> CreateAdminAsync(IServiceProvider sp, Dictionary<string, string?> o, CancellationToken ct)
    {
        var email = Required(o, "email");
        var name = o.GetValueOrDefault("name") ?? "Platform Administrator";
        var password = Environment.GetEnvironmentVariable("BACKORA_ADMIN_PASSWORD") ?? ReadSecret("Password: ");
        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = sp.GetRequiredService<RoleManager<ApplicationRole>>();
        if (!await roles.RoleExistsAsync(Roles.PlatformAdministrator)) await roles.CreateAsync(new ApplicationRole(Roles.PlatformAdministrator));
        if (await users.FindByEmailAsync(email) is not null)
        {
            Console.Error.WriteLine($"A user with email {email} already exists.");
            return 1;
        }
        var user = new ApplicationUser { UserName = email, Email = email, Name = name, EmailConfirmed = true, IsActive = true, MustChangePassword = true };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors) Console.Error.WriteLine($"- {e.Description}");
            return 1;
        }
        await users.AddToRoleAsync(user, Roles.PlatformAdministrator);
        var audit = sp.GetRequiredService<IAuditLog>();
        await audit.RecordNowAsync("user.created", "User", user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), Domain.AuditOutcome.Success, new { user.Email, via = "Backora.Tools create-admin" }, ct);
        Console.WriteLine($"Platform Administrator {email} created (id {user.Id}); a password change is required at first sign-in.");
        return 0;
    }

    private static async Task<int> RotateKeysAsync(IServiceProvider sp, bool dryRun, CancellationToken ct)
    {
        var rotation = new KeyRotation(sp.GetRequiredService<BackoraDbContext>(), sp.GetRequiredService<ISecretProtector>());
        var counts = await rotation.RotateAsync(dryRun, ct);
        foreach (var (column, n) in counts) Console.WriteLine($"{column,-45} {n,6} re-encrypted{(dryRun ? " (dry run)" : string.Empty)}");
        Console.WriteLine($"Total: {counts.Values.Sum()}{(dryRun ? " — dry run, nothing written" : string.Empty)}");
        return 0;
    }

    private static async Task<int> ImportAsync(IServiceProvider sp, Dictionary<string, string?> o, CancellationToken ct)
    {
        var conn = Required(o, "mysql");
        var snapshot = await LegacySnapshot.ReadAsync(conn, ct);
        var importer = sp.GetRequiredService<LegacyImporter>();
        var report = await importer.ImportAsync(snapshot, new ImportOptions(o.ContainsKey("dry-run"), o.ContainsKey("allow-non-empty"), o.ContainsKey("preserve-job-status")), ct);
        Emit(report, o.GetValueOrDefault("report"));
        return report.Passed ? 0 : 1;
    }

    private static async Task<int> VerifyAsync(IServiceProvider sp, Dictionary<string, string?> o, CancellationToken ct)
    {
        var snapshot = await LegacySnapshot.ReadAsync(Required(o, "mysql"), ct);
        var report = await sp.GetRequiredService<LegacyImporter>().VerifyAsync(snapshot, ct);
        Emit(report, o.GetValueOrDefault("report"));
        return report.Passed ? 0 : 1;
    }

    private static void Emit(ImportReport report, string? path)
    {
        Console.WriteLine(report.ToMarkdown());
        if (string.IsNullOrWhiteSpace(path)) return;
        var full = Path.GetFullPath(path);
        if (full.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(full, report.ToJson());
            File.WriteAllText(Path.ChangeExtension(full, ".md"), report.ToMarkdown());
        }
        else
        {
            File.WriteAllText(full, report.ToMarkdown());
            File.WriteAllText(Path.ChangeExtension(full, ".json"), report.ToJson());
        }
        Console.WriteLine($"Report written to {full} (+ {(full.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? ".md" : ".json")})");
    }

    internal static Dictionary<string, string?> ParseOptions(string[] args)
    {
        var o = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new UsageException($"Unexpected argument '{args[i]}'.");
            var key = args[i][2..];
            var eq = key.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0) { o[key[..eq]] = key[(eq + 1)..]; continue; }
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) o[key] = args[++i];
            else o[key] = null;
        }
        return o;
    }

    private static string Required(Dictionary<string, string?> o, string key) =>
        o.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : throw new UsageException($"--{key} is required.");

    private static string ReadSecret(string prompt)
    {
        if (Console.IsInputRedirected) return Console.ReadLine() ?? string.Empty;
        Console.Write(prompt);
        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (buffer.Length > 0) buffer.Length--; continue; }
            buffer.Append(key.KeyChar);
        }
        Console.WriteLine();
        return buffer.ToString();
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(Usage);
        return 2;
    }

    internal sealed class UsageException(string message) : Exception(message);
}

/// <summary>Re-encrypts every secret column whose envelope is not under the active key, in one transaction.</summary>
public sealed class KeyRotation(BackoraDbContext db, ISecretProtector protector)
{
    public async Task<SortedDictionary<string, int>> RotateAsync(bool dryRun, CancellationToken ct)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            counts.Clear();
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            string? Re(string? c, string column)
            {
                if (c is null || !protector.NeedsReEncryption(c)) return c;
                counts[column] = counts.GetValueOrDefault(column) + 1;
                return protector.Protect(protector.Unprotect(c));
            }
            foreach (var s in await db.Servers.ToListAsync(ct)) s.PrivateKeyCiphertext = Re(s.PrivateKeyCiphertext, "servers.private_key_ciphertext");
            foreach (var w in await db.WorkerNodes.ToListAsync(ct)) w.PrivateKeyCiphertext = Re(w.PrivateKeyCiphertext, "worker_nodes.private_key_ciphertext")!;
            foreach (var s in await db.SshSettings.ToListAsync(ct)) s.DefaultPrivateKeyCiphertext = Re(s.DefaultPrivateKeyCiphertext, "ssh_settings.default_private_key_ciphertext");
            foreach (var v in await db.Variables.ToListAsync(ct)) v.ValueCiphertext = Re(v.ValueCiphertext, "variables.value_ciphertext");
            foreach (var d in await db.S3Destinations.ToListAsync(ct)) d.SecretKeyCiphertext = Re(d.SecretKeyCiphertext, "s3_destinations.secret_key_ciphertext")!;
            foreach (var s in await db.SmtpSettings.ToListAsync(ct)) s.PasswordCiphertext = Re(s.PasswordCiphertext, "smtp_settings.password_ciphertext");
            foreach (var t in await db.TeamsSettings.ToListAsync(ct)) t.WebhookUrlCiphertext = Re(t.WebhookUrlCiphertext, "teams_settings.webhook_url_ciphertext");
            foreach (var k in await db.S3DestinationEncryptionKeys.Where(k => k.IdentityCiphertext != null).ToListAsync(ct)) k.IdentityCiphertext = Re(k.IdentityCiphertext, "s3_destination_encryption_keys.identity_ciphertext");
            foreach (var j in await db.Set<Domain.Backups.BackupJobSetting>().Where(x => x.ValueCiphertext != null).ToListAsync(ct)) j.ValueCiphertext = Re(j.ValueCiphertext, "backup_job_settings.value_ciphertext");
            if (dryRun)
            {
                await tx.RollbackAsync(ct);
                return;
            }
            await db.SaveChangesAsync(ct);
            db.AuditEvents.Add(new Domain.Platform.AuditEvent
            {
                OccurredAt = DateTimeOffset.UtcNow, ActorName = "Backora.Tools", Action = "security.keys_rotated", ResourceType = "Encryption",
                Outcome = Domain.AuditOutcome.Success, DetailsJson = System.Text.Json.JsonSerializer.Serialize(counts),
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
        return counts;
    }
}
