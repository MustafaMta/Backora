using Backora.Contracts.V1;

namespace Backora.Web.Tests;

public static class TestData
{
    public static OffloadSummaryDto RecoveryPoint(long id = 42, string job = "pg-prod") => new(
        id, 7, 3, job, 5, "db-01", 1, "Primary S3", "backups", $"{job}/2026-11-01-02-r{id}/", false,
        OffloadStatusDto.Completed, VerificationStatusDto.Verified, 1024 * 1024, 1024 * 1024, 2, 2, 100, null,
        CleanupStatusDto.Completed, RetentionStateDto.Active, false, null, DateTimeOffset.UtcNow.AddDays(7),
        DateTimeOffset.UtcNow.AddHours(-3), DateTimeOffset.UtcNow.AddHours(-3), DateTimeOffset.UtcNow.AddHours(-2),
        FailureClassDto.None, null, 1, 3);

    public static RestorePrecheckResponse Precheck(bool canRestore, long id = 42, string phrase = "pg-prod") => new(
        canRestore,
        [
            new CheckResultDto("Objects present in storage", canRestore ? CheckState.Passed : CheckState.Failed, canRestore ? "2 objects, sizes match manifest" : "1 object missing"),
            new CheckResultDto("Target directory free", CheckState.Passed, "/BackoraDB/pg-prod/restore-9 does not exist"),
        ],
        "/BackoraDB/pg-prod/restore-9", 2, 1024 * 1024, phrase, RecoveryPoint(id));

    public static RestoreSummaryDto Restore(long id = 9, RestoreStatusDto status = RestoreStatusDto.Running) => new(
        id, 42, "pg-prod", 5, "db-01", status, VerificationStatusDto.NotVerified, "/BackoraDB/pg-prod/restore-9", 2, 0, 1024 * 1024, 0, null,
        "Test Operator", "INC-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, FailureClassDto.None, null);

    public static RestoreDetailDto RestoreDetail(long id = 9, RestoreStatusDto status = RestoreStatusDto.Running) =>
        new(Restore(id, status), "starting transfer", RecoveryPoint(), []);

    public static BackupJobDto Job(long id = 1, string name = "pg-prod") => new(
        id, name, "PostgreSQL", null, 2, "pg_basebackup", 5, "db-01", "10.0.0.5", JobStatusDto.Active, "0 2 * * *", "UTC", "Daily at 02:00 (UTC)",
        MissedRunPolicyDto.RunOnceWithinGrace, 60, 7m, 1, 1, "Primary S3", DateTimeOffset.UtcNow.AddDays(-10), false, true, 360, 3,
        DateTimeOffset.UtcNow.AddHours(5), null, null, HealthState.Healthy, null, 1, DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow);

    public static PagedResult<T> Page<T>(params T[] items) => new(items, 1, 25, items.Length);
}
