using Backora.Application.Abstractions;
using Backora.Application.Operations;
using Backora.Infrastructure;
using Backora.Infrastructure.Auditing;

namespace Backora.Api.Execution;

/// <summary>
/// Durable background execution runs inside the backend process — there is no separate worker application.
/// The backend only orchestrates: every backup, transfer, encryption and restore command runs on the configured
/// execution host over SSH, as a detached process tracked through PostgreSQL leases and on-host state files, so a
/// backend restart re-adopts in-flight operations instead of failing them.
/// </summary>
public static class ExecutionRegistration
{
    public static IServiceCollection AddBackgroundExecution(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<WorkerIdentity>();

        // Let in-flight steps finish and leases be released before the process stops.
        services.Configure<HostOptions>(o =>
        {
            o.ShutdownTimeout = TimeSpan.FromSeconds(configuration.GetValue("Worker:ShutdownTimeoutSeconds", 45));
            o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
        });

        if (configuration.GetValue("Worker:EnableOperations", true))
        {
            services.AddHostedService<OperationDispatcher<BackupRunProcessor>>();
            services.AddHostedService<OperationDispatcher<OffloadRunProcessor>>();
            services.AddHostedService<OperationDispatcher<RestoreRunProcessor>>();
        }
        if (configuration.GetValue("Worker:EnableScheduler", true)) services.AddHostedService<SchedulerHostedService>();
        if (configuration.GetValue("Worker:EnableRetention", true)) services.AddHostedService<RetentionHostedService>();
        if (configuration.GetValue("Worker:EnableNotifications", true)) services.AddHostedService<NotificationHostedService>();
        services.AddHostedService<HeartbeatHostedService>();
        return services;
    }
}

/// <summary>
/// Hosts only the background execution part of the backend (no HTTP API). Used by integration tests to stop and
/// restart execution independently of the API, which is how "backend restart during a backup" is exercised.
/// Not a separate deployable: production runs execution inside the API process (Execution:Enabled=true).
/// </summary>
public static class BackgroundExecutionHost
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        builder.Services.AddBackoraCore(builder.Configuration);
        builder.Services.AddSingleton<ICurrentUser, SystemUser>();
        builder.Services.AddBackgroundExecution(builder.Configuration);
        var app = builder.Build();
        _ = app.Services.GetRequiredService<ISecretProtector>();
        return app;
    }
}
