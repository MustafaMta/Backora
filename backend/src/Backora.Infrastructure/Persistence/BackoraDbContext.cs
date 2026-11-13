using Backora.Application.Abstractions;
using Backora.Domain;
using Backora.Domain.Backups;
using Backora.Domain.Common;
using Backora.Domain.Infrastructure;
using Backora.Domain.Platform;
using Backora.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Backora.Infrastructure.Persistence;

public sealed class BackoraDbContext(DbContextOptions<BackoraDbContext> options, IClock? clock = null)
    : IdentityDbContext<ApplicationUser, ApplicationRole, long>(options), IAppDbContext
{
    private readonly IClock _clock = clock ?? new SystemClock();

    public DbSet<Server> Servers => Set<Server>();
    public DbSet<WorkerNode> WorkerNodes => Set<WorkerNode>();
    public DbSet<StorageSettings> StorageSettings => Set<StorageSettings>();
    public DbSet<SshSettings> SshSettings => Set<SshSettings>();
    public DbSet<Variable> Variables => Set<Variable>();
    public DbSet<S3Destination> S3Destinations => Set<S3Destination>();
    public DbSet<S3DestinationEncryptionKey> S3DestinationEncryptionKeys => Set<S3DestinationEncryptionKey>();
    public DbSet<ScriptTemplate> ScriptTemplates => Set<ScriptTemplate>();
    public DbSet<BackupJob> BackupJobs => Set<BackupJob>();
    public DbSet<BackupRun> BackupRuns => Set<BackupRun>();
    public DbSet<BackupArtifact> BackupArtifacts => Set<BackupArtifact>();
    public DbSet<OffloadRun> OffloadRuns => Set<OffloadRun>();
    public DbSet<BackupJobReplicationTarget> BackupJobReplicationTargets => Set<BackupJobReplicationTarget>();
    public DbSet<BackupJobSetting> BackupJobSettings => Set<BackupJobSetting>();
    public DbSet<RestoreRun> RestoreRuns => Set<RestoreRun>();
    public DbSet<SmtpSettings> SmtpSettings => Set<SmtpSettings>();
    public DbSet<TeamsSettings> TeamsSettings => Set<TeamsSettings>();
    public DbSet<NotificationSettings> NotificationSettings => Set<NotificationSettings>();
    public DbSet<NotificationMessage> NotificationMessages => Set<NotificationMessage>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<WorkerHeartbeat> WorkerHeartbeats => Set<WorkerHeartbeat>();
    public DbSet<ClusterLock> ClusterLocks => Set<ClusterLock>();
    public DbSet<OperationLease> OperationLeases => Set<OperationLease>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampTimestamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void StampTimestamps()
    {
        var now = _clock.UtcNow;
        foreach (var entry in ChangeTracker.Entries<Entity>())
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.CreatedAt == default) entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }
        foreach (var entry in ChangeTracker.Entries<ApplicationUser>())
        {
            if (entry.State == EntityState.Added && entry.Entity.CreatedAt == default) entry.Entity.CreatedAt = now;
            if (entry.State is EntityState.Added or EntityState.Modified) entry.Entity.UpdatedAt = now;
        }
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        // Identity tables with clean names.
        b.Entity<ApplicationUser>(e =>
        {
            e.ToTable("users");
            e.Property(u => u.Name).HasMaxLength(200).IsRequired();
            e.Property(u => u.LegacyUserCode).HasMaxLength(100);
            e.Property(u => u.Version).IsRowVersion();
            e.HasIndex(u => u.LegacyId).IsUnique().HasFilter("legacy_id IS NOT NULL");
        });
        b.Entity<ApplicationRole>().ToTable("roles");
        b.Entity<IdentityUserRole<long>>().ToTable("user_roles");
        b.Entity<IdentityUserClaim<long>>().ToTable("user_claims");
        b.Entity<IdentityUserLogin<long>>().ToTable("user_logins");
        b.Entity<IdentityUserToken<long>>().ToTable("user_tokens");
        b.Entity<IdentityRoleClaim<long>>().ToTable("role_claims");

        b.Entity<Server>(e =>
        {
            ConfigureEntity(e, "servers");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Host).HasMaxLength(255).IsRequired();
            e.Property(x => x.SshUser).HasMaxLength(100).IsRequired();
            e.Property(x => x.HostKeyFingerprint).HasMaxLength(128);
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.LastConnectionTestMessage).HasMaxLength(2000);
            e.HasIndex(x => x.Name).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("ck_servers_port", "ssh_port BETWEEN 1 AND 65535"));
        });

        b.Entity<WorkerNode>(e =>
        {
            ConfigureEntity(e, "worker_nodes");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Host).HasMaxLength(255).IsRequired();
            e.Property(x => x.SshUser).HasMaxLength(100).IsRequired();
            e.Property(x => x.HostKeyFingerprint).HasMaxLength(128);
            e.Property(x => x.RclonePath).HasMaxLength(500).IsRequired();
            e.Property(x => x.WorkDirectory).HasMaxLength(500).IsRequired();
            e.Property(x => x.LastConnectionTestMessage).HasMaxLength(2000);
            e.ToTable(t => t.HasCheckConstraint("ck_worker_nodes_singleton", "id = 1"));
        });

        b.Entity<StorageSettings>(e =>
        {
            ConfigureEntity(e, "storage_settings");
            e.Property(x => x.NfsHost).HasMaxLength(500).IsRequired();
            e.Property(x => x.NfsPath).HasMaxLength(500).IsRequired();
            e.ToTable(t => t.HasCheckConstraint("ck_storage_settings_singleton", "id = 1"));
        });

        b.Entity<SshSettings>(e =>
        {
            ConfigureEntity(e, "ssh_settings");
            e.ToTable(t => t.HasCheckConstraint("ck_ssh_settings_singleton", "id = 1"));
        });

        b.Entity<Variable>(e =>
        {
            ConfigureEntity(e, "variables");
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Description).HasMaxLength(500);
            e.HasIndex(x => x.Name).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("ck_variables_secret_storage",
                "(is_secret AND value IS NULL) OR (NOT is_secret AND value_ciphertext IS NULL)"));
        });

        b.Entity<S3Destination>(e =>
        {
            ConfigureEntity(e, "s3_destinations");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.LocalPath).HasMaxLength(1024);
            e.Property(x => x.Endpoint).HasMaxLength(500).IsRequired();
            e.Property(x => x.Region).HasMaxLength(100).IsRequired();
            e.Property(x => x.Bucket).HasMaxLength(255).IsRequired();
            e.Property(x => x.AccessKeyId).HasMaxLength(255).IsRequired();
            e.Property(x => x.SecretKeyCiphertext).IsRequired();
            e.Property(x => x.PathPrefix).HasMaxLength(255);
            e.Property(x => x.LastConnectionTestMessage).HasMaxLength(2000);
            // Public key material only. There is deliberately no column for a private key.
            e.Property(x => x.EncryptionRecipient).HasMaxLength(200);
            e.Property(x => x.EncryptionKeyFingerprint).HasMaxLength(100);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasMany(x => x.EncryptionKeys).WithOne().HasForeignKey(k => k.S3DestinationId).OnDelete(DeleteBehavior.Cascade);
            e.ToTable(t => t.HasCheckConstraint("ck_s3_destinations_encryption",
                "encryption_mode = 0 OR (encryption_mode = 1 AND encryption_recipient IS NOT NULL AND encryption_key_fingerprint IS NOT NULL)"));
        });

        b.Entity<S3DestinationEncryptionKey>(e =>
        {
            ConfigureEntity(e, "s3_destination_encryption_keys");
            e.Property(x => x.S3DestinationId).HasColumnName("s3_destination_id");
            e.Property(x => x.Recipient).HasMaxLength(200).IsRequired();
            e.Property(x => x.Fingerprint).HasMaxLength(100).IsRequired();
            e.Property(x => x.IdentityCiphertext).HasColumnName("identity_ciphertext");
            e.HasIndex(x => new { x.S3DestinationId, x.ActivatedAt });
            // At most one active (not retired) key per destination.
            e.HasIndex(x => x.S3DestinationId).IsUnique().HasFilter("retired_at IS NULL").HasDatabaseName("ux_s3_destination_encryption_keys_one_active");
        });

        b.Entity<ScriptTemplate>(e =>
        {
            ConfigureEntity(e, "script_templates");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Category).HasMaxLength(100).IsRequired();
            e.Property(x => x.Script).IsRequired();
            e.Property(x => x.Description).HasMaxLength(1000);
            e.HasIndex(x => new { x.Name, x.Category }).IsUnique();
        });

        b.Entity<BackupJob>(e =>
        {
            ConfigureEntity(e, "backup_jobs");
            e.Property(x => x.Name).HasMaxLength(BackupJob.MaxNameLength).IsRequired();
            e.Property(x => x.Category).HasMaxLength(100).IsRequired();
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.CronExpression).HasMaxLength(120);
            e.Property(x => x.TimeZone).HasMaxLength(64).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.MissedRunPolicy).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.RetentionDays).HasPrecision(10, 4);
            e.Property(x => x.S3DestinationId).HasColumnName("s3_destination_id");
            e.Property(x => x.S3DestinationAssignedAt).HasColumnName("s3_destination_assigned_at");
            e.Ignore(x => x.IsDeleted);
            e.Ignore(x => x.IsScheduled);
            // Unique among non-deleted jobs: the name is the NFS directory and S3 prefix.
            e.HasIndex(x => x.Name).IsUnique().HasFilter("deleted_at IS NULL");
            e.HasIndex(x => x.NextRunAt).HasFilter("status = 'Active' AND deleted_at IS NULL");
            e.HasOne(x => x.ScriptTemplate).WithMany().HasForeignKey(x => x.ScriptTemplateId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Server).WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.S3Destination).WithMany().HasForeignKey(x => x.S3DestinationId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.ReplicationTargets).WithOne().HasForeignKey(t => t.BackupJobId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Settings).WithOne().HasForeignKey(t => t.BackupJobId).OnDelete(DeleteBehavior.Cascade);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_backup_jobs_retention", "retention_days >= 0");
                t.HasCheckConstraint("ck_backup_jobs_timeout", "timeout_minutes BETWEEN 1 AND 10080");
                t.HasCheckConstraint("ck_backup_jobs_attempts", "max_attempts BETWEEN 1 AND 10");
                t.HasCheckConstraint("ck_backup_jobs_min_keep", "min_recovery_points_to_keep BETWEEN 1 AND 1000");
            });
        });

        b.Entity<BackupJobSetting>(e =>
        {
            ConfigureEntity(e, "backup_job_settings");
            e.Property(x => x.Name).HasMaxLength(64).IsRequired();
            e.Property(x => x.Value).HasMaxLength(4000);
            e.HasIndex(x => new { x.BackupJobId, x.Name }).IsUnique();
        });

        b.Entity<BackupJobReplicationTarget>(e =>
        {
            ConfigureEntity(e, "backup_job_replication_targets");
            e.Property(x => x.S3DestinationId).HasColumnName("s3_destination_id");
            e.HasOne(x => x.S3Destination).WithMany().HasForeignKey(x => x.S3DestinationId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.BackupJobId, x.S3DestinationId }).IsUnique();
        });

        b.Entity<BackupRun>(e =>
        {
            ConfigureEntity(e, "backup_runs");
            ConfigureLeased(e);
            e.Property(x => x.DismissedBy).HasMaxLength(256);
            e.Property(x => x.JobName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Category).HasMaxLength(100).IsRequired();
            e.Property(x => x.TemplateName).HasMaxLength(200).IsRequired();
            e.Property(x => x.ScriptSha256).HasMaxLength(64);
            e.Property(x => x.RequestedByName).HasMaxLength(200);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Trigger).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.VerificationStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ProgressStage).HasMaxLength(40);
            e.Ignore(x => x.IsTerminal);
            e.HasOne(x => x.BackupJob).WithMany().HasForeignKey(x => x.BackupJobId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Server).WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Artifacts).WithOne(a => a.BackupRun).HasForeignKey(a => a.BackupRunId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Offloads).WithOne(o => o.BackupRun).HasForeignKey(o => o.BackupRunId).OnDelete(DeleteBehavior.Restrict);

            // At most ONE active run per job, enforced by the database (prevents duplicate execution).
            e.HasIndex(x => x.BackupJobId).IsUnique()
                .HasFilter("status IN ('Pending','Starting','Running','Verifying','Cancelling')")
                .HasDatabaseName("ux_backup_runs_one_active_per_job");
            // A scheduled occurrence can only produce one run (scheduler idempotency).
            e.HasIndex(x => new { x.BackupJobId, x.ScheduledFor }).IsUnique()
                .HasFilter("scheduled_for IS NOT NULL")
                .HasDatabaseName("ux_backup_runs_job_occurrence");
            e.HasIndex(x => new { x.BackupJobId, x.CreatedAt }).IsDescending(false, true);
            e.HasIndex(x => x.CreatedAt).IsDescending();
            e.HasIndex(x => new { x.ServerId, x.CreatedAt }).IsDescending(false, true);
            e.HasIndex(x => new { x.Status, x.NextAttemptAt })
                .HasFilter("status IN ('Pending','Starting','Running','Verifying','Cancelling')")
                .HasDatabaseName("ix_backup_runs_claimable");
        });

        b.Entity<BackupArtifact>(e =>
        {
            e.Property(x => x.Md5).HasMaxLength(32);
            ConfigureEntity(e, "backup_artifacts");
            e.Property(x => x.RelativePath).HasMaxLength(1024).IsRequired();
            e.Property(x => x.Sha256).HasMaxLength(64).IsFixedLength().IsRequired();
            e.HasIndex(x => new { x.BackupRunId, x.RelativePath }).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("ck_backup_artifacts_size", "size_bytes >= 0"));
        });

        b.Entity<OffloadRun>(e =>
        {
            ConfigureEntity(e, "offload_runs");
            ConfigureLeased(e);
            e.Property(x => x.ProgressStage).HasMaxLength(40);
            e.Property(x => x.JobName).HasMaxLength(200).IsRequired();
            e.Property(x => x.SourcePath).HasMaxLength(1024).IsRequired();
            e.Property(x => x.RemotePrefix).HasMaxLength(1024).IsRequired();
            e.Property(x => x.LegacyTotalSize).HasMaxLength(50);
            e.Property(x => x.DismissedBy).HasMaxLength(256);
            e.Property(x => x.S3DestinationId).HasColumnName("s3_destination_id");
            e.Property(x => x.CleanupMessage).HasMaxLength(4000);
            e.Property(x => x.ProtectionReason).HasMaxLength(1000);
            e.Property(x => x.DeletionReason).HasMaxLength(1000);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.VerificationStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.CleanupStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.RetentionState).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.EncryptionFormat).HasMaxLength(20);
            e.Property(x => x.EncryptionRecipient).HasMaxLength(200);
            e.Property(x => x.EncryptionKeyFingerprint).HasMaxLength(100);
            e.Property(x => x.EncryptedObjectsJson).HasColumnType("jsonb");
            e.Property(x => x.Compression).HasMaxLength(20);
            e.Ignore(x => x.ObjectSuffix);
            e.Ignore(x => x.Transformed);
            e.Ignore(x => x.EncryptedObjects);
            e.ToTable(t => t.HasCheckConstraint("ck_offload_runs_encryption",
                "NOT encrypted OR (encryption_format IS NOT NULL AND encryption_recipient IS NOT NULL AND encryption_key_fingerprint IS NOT NULL)"));
            e.Ignore(x => x.IsRecoveryPoint);
            e.Ignore(x => x.IsTerminal);
            e.HasOne(x => x.S3Destination).WithMany().HasForeignKey(x => x.S3DestinationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Server).WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<BackupJob>().WithMany().HasForeignKey(x => x.BackupJobId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.SourceOffloadRun).WithMany().HasForeignKey(x => x.SourceOffloadRunId).OnDelete(DeleteBehavior.Restrict);
            e.Ignore(x => x.IsReplica);
            // At most one active transfer per (backup run, destination): one primary upload plus one per DR copy.
            e.HasIndex(x => new { x.BackupRunId, x.S3DestinationId }).IsUnique()
                .HasFilter("status IN ('Pending','Running','Verifying')")
                .HasDatabaseName("ux_offload_runs_one_active_per_run_destination");
            e.HasIndex(x => x.SourceOffloadRunId).HasFilter("source_offload_run_id IS NOT NULL");
            e.HasIndex(x => new { x.BackupJobId, x.CompletedAt }).IsDescending(false, true);
            e.HasIndex(x => new { x.ServerId, x.CreatedAt }).IsDescending(false, true);
            e.HasIndex(x => new { x.Status, x.RetentionState });
            e.HasIndex(x => new { x.Status, x.NextAttemptAt })
                .HasFilter("status IN ('Pending','Running','Verifying')")
                .HasDatabaseName("ix_offload_runs_claimable");
        });

        b.Entity<RestoreRun>(e =>
        {
            ConfigureEntity(e, "restore_runs");
            ConfigureLeased(e);
            e.Property(x => x.JobName).HasMaxLength(200).IsRequired();
            e.Property(x => x.TargetPath).HasMaxLength(1024).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(2000);
            e.Property(x => x.RequestedByName).HasMaxLength(200);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.VerificationStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.DecryptionKeyFingerprint).HasMaxLength(100);
            e.Ignore(x => x.IsTerminal);
            e.HasOne(x => x.OffloadRun).WithMany().HasForeignKey(x => x.OffloadRunId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Server).WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.TargetPath).IsUnique();
            e.HasIndex(x => x.OffloadRunId).IsUnique()
                .HasFilter("status IN ('Pending','Running','Verifying','Cancelling','AwaitingKey')")
                .HasDatabaseName("ux_restore_runs_one_active_per_point");
            e.HasIndex(x => x.CreatedAt).IsDescending();
            e.HasIndex(x => new { x.Status, x.NextAttemptAt })
                .HasFilter("status IN ('Pending','Running','Verifying','Cancelling','AwaitingKey')")
                .HasDatabaseName("ix_restore_runs_claimable");
        });

        b.Entity<SmtpSettings>(e =>
        {
            ConfigureEntity(e, "smtp_settings");
            e.Property(x => x.Host).HasMaxLength(255).IsRequired();
            e.Property(x => x.Username).HasMaxLength(255);
            e.Property(x => x.FromAddress).HasMaxLength(320).IsRequired();
            e.Property(x => x.FromName).HasMaxLength(200);
            e.Property(x => x.Security).HasMaxLength(20).IsRequired();
            e.ToTable(t => t.HasCheckConstraint("ck_smtp_settings_singleton", "id = 1"));
        });

        b.Entity<TeamsSettings>(e =>
        {
            ConfigureEntity(e, "teams_settings");
            e.ToTable(t => t.HasCheckConstraint("ck_teams_settings_singleton", "id = 1"));
        });

        b.Entity<NotificationSettings>(e =>
        {
            ConfigureEntity(e, "notification_settings");
            e.ToTable(t => t.HasCheckConstraint("ck_notification_settings_singleton", "id = 1"));
        });

        b.Entity<NotificationMessage>(e =>
        {
            ConfigureEntity(e, "notification_messages");
            e.Property(x => x.Channel).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.EventType).HasMaxLength(100).IsRequired();
            e.Property(x => x.Subject).HasMaxLength(500);
            e.Property(x => x.PayloadJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.LastError).HasMaxLength(4000);
            e.Property(x => x.RelatedEntityType).HasMaxLength(100);
            e.Property(x => x.CorrelationId).HasMaxLength(100);
            e.HasIndex(x => new { x.Status, x.NextAttemptAt }).HasFilter("status = 'Pending'");
        });

        b.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Action).HasMaxLength(100).IsRequired();
            e.Property(x => x.ActorName).HasMaxLength(200).IsRequired();
            e.Property(x => x.ActorIp).HasMaxLength(64);
            e.Property(x => x.ResourceType).HasMaxLength(100);
            e.Property(x => x.ResourceId).HasMaxLength(100);
            e.Property(x => x.CorrelationId).HasMaxLength(100);
            e.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.DetailsJson).HasColumnType("jsonb");
            e.HasIndex(x => x.OccurredAt).IsDescending();
            e.HasIndex(x => new { x.ResourceType, x.ResourceId });
            e.HasIndex(x => new { x.ActorUserId, x.OccurredAt }).IsDescending(false, true);
            e.HasIndex(x => x.Action);
        });

        b.Entity<ApiToken>(e =>
        {
            ConfigureEntity(e, "api_tokens");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.TokenHash).HasMaxLength(64).IsFixedLength().IsRequired();
            e.Property(x => x.Prefix).HasMaxLength(16).IsRequired();
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.Property(x => x.TokenHash).HasMaxLength(64).IsFixedLength().IsRequired();
            e.Property(x => x.FamilyId).HasMaxLength(64).IsRequired();
            e.Property(x => x.ReplacedByHash).HasMaxLength(64);
            e.Property(x => x.CreatedByIp).HasMaxLength(64);
            e.Property(x => x.UserAgent).HasMaxLength(500);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.UserId, x.FamilyId });
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<IdempotencyRecord>(e =>
        {
            e.ToTable("idempotency_records");
            e.Property(x => x.Scope).HasMaxLength(100).IsRequired();
            e.Property(x => x.Key).HasMaxLength(200).IsRequired();
            e.Property(x => x.ResourceType).HasMaxLength(100);
            e.HasIndex(x => new { x.UserId, x.Scope, x.Key }).IsUnique();
            e.HasIndex(x => x.CreatedAt);
        });

        b.Entity<WorkerHeartbeat>(e =>
        {
            e.ToTable("worker_heartbeats");
            e.HasKey(x => x.WorkerId);
            e.Property(x => x.WorkerId).HasMaxLength(200);
            e.Property(x => x.HostName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Version).HasMaxLength(50).IsRequired();
        });

        b.Entity<OperationLease>(e =>
        {
            e.ToTable("operation_leases");
            e.HasKey(x => new { x.Kind, x.OperationId });
            e.Property(x => x.Kind).HasMaxLength(20);
            e.Property(x => x.Owner).HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.ExpiresAt);
        });

        b.Entity<ClusterLock>(e =>
        {
            e.ToTable("cluster_locks");
            e.HasKey(x => x.Name);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Owner).HasMaxLength(200);
        });
    }

    private static void ConfigureEntity<T>(EntityTypeBuilder<T> e, string table) where T : Entity
    {
        e.ToTable(table);
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).UseIdentityByDefaultColumn();
        e.Property(x => x.Version).IsRowVersion();
    }

    private static void ConfigureLeased<T>(EntityTypeBuilder<T> e) where T : Domain.Operations.LeasedOperation
    {
        e.Property(x => x.ProcessedBy).HasMaxLength(200);
        e.Property(x => x.FailureClass).HasConversion<string>().HasMaxLength(30);
        e.Property(x => x.ErrorMessage).HasMaxLength(4000);
        e.Property(x => x.RemoteDirectory).HasMaxLength(1024);
        e.Property(x => x.CorrelationId).HasMaxLength(100);
        e.Ignore(x => x.Duration);
    }
}
