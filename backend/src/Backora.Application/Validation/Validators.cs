using System.Net;
using System.Text.RegularExpressions;
using Backora.Contracts.Security;
using Backora.Contracts.V1;
using Backora.Contracts.V1.Auth;
using Backora.Application.Encryption;
using Backora.Domain.Backups;
using Backora.Domain.Execution;
using Backora.Domain.Scheduling;
using FluentValidation;

namespace Backora.Application.Validation;

public static partial class Rules
{
    /// <summary>Hostname (RFC 1123) or IPv4/IPv6 literal. No whitespace, shell metacharacters or schemes.</summary>
    public static bool IsHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 253) return false;
        if (IPAddress.TryParse(value, out _)) return true;
        return HostRegex().IsMatch(value);
    }

    public static bool IsPosixUser(string? value) => !string.IsNullOrWhiteSpace(value) && UserRegex().IsMatch(value);

    public static bool LooksLikePrivateKey(string? value) =>
        value is not null && value.Contains("PRIVATE KEY-----", StringComparison.Ordinal) && value.Length < 20000;

    public static bool IsHttpUrl(string? value, bool requireHttps = false) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (!requireHttps && uri.Scheme == Uri.UriSchemeHttp))
        && string.IsNullOrEmpty(uri.UserInfo);

    public static bool IsBucketName(string? value) => value is not null && BucketRegex().IsMatch(value);

    public static bool IsVariableName(string? value) => value is not null && VariableRegex().IsMatch(value);

    public static readonly HashSet<string> ReservedPlaceholders = new(StringComparer.Ordinal)
        { "JOB_NAME", "CATEGORY", "SCRIPT_NAME", "RETENTION_DAYS", "NFS_HOST", "NFS_PATH" };

    [GeneratedRegex(@"^(?=.{1,253}$)([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$")]
    private static partial Regex HostRegex();

    [GeneratedRegex(@"^[a-z_][a-z0-9_.-]{0,31}\$?$", RegexOptions.IgnoreCase)]
    private static partial Regex UserRegex();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9.\-]{1,61}[a-z0-9]$")]
    private static partial Regex BucketRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,99}$")]
    private static partial Regex VariableRegex();
}

public sealed class ServerUpsertValidator : AbstractValidator<ServerUpsertRequest>
{
    public ServerUpsertValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Host).Must(Rules.IsHost).WithMessage("Enter a valid hostname or IP address.");
        RuleFor(x => x.SshPort).InclusiveBetween(1, 65535);
        RuleFor(x => x.SshUser).Must(Rules.IsPosixUser).WithMessage("Enter a valid POSIX user name.");
        RuleFor(x => x.PrivateKey).Must(Rules.LooksLikePrivateKey!).When(x => !string.IsNullOrWhiteSpace(x.PrivateKey))
            .WithMessage("Paste an OpenSSH or PEM private key.");
        RuleFor(x => x.Description).MaximumLength(1000);
    }
}

public sealed class ConnectionTestValidator : AbstractValidator<ConnectionTestRequest>
{
    public ConnectionTestValidator()
    {
        RuleFor(x => x.Host).Must(Rules.IsHost).WithMessage("Enter a valid hostname or IP address.");
        RuleFor(x => x.SshPort).InclusiveBetween(1, 65535);
        RuleFor(x => x.SshUser).Must(Rules.IsPosixUser).WithMessage("Enter a valid POSIX user name.");
        RuleFor(x => x.PrivateKey).Must(Rules.LooksLikePrivateKey!).When(x => !string.IsNullOrWhiteSpace(x.PrivateKey));
    }
}

public sealed class WorkerNodeValidator : AbstractValidator<WorkerNodeUpdateRequest>
{
    public WorkerNodeValidator()
    {
        RuleFor(x => x.Host).Must(Rules.IsHost).WithMessage("Enter a valid hostname or IP address.");
        RuleFor(x => x.SshPort).InclusiveBetween(1, 65535);
        RuleFor(x => x.SshUser).Must(Rules.IsPosixUser).WithMessage("Enter a valid POSIX user name.");
        RuleFor(x => x.PrivateKey).Must(Rules.LooksLikePrivateKey!).When(x => !string.IsNullOrWhiteSpace(x.PrivateKey));
        RuleFor(x => x.RclonePath).NotEmpty().Must(p => p == "rclone" || ShellQuote.IsSafeAbsolutePath(p))
            .WithMessage("Use 'rclone' or an absolute path to the rclone binary.");
        RuleFor(x => x.WorkDirectory).Must(ShellQuote.IsSafeAbsolutePath).WithMessage("Use an absolute path without '..'.")
            .Must(p => p.Count(c => c == '/') >= 2 || p.Length > 4).WithMessage("Choose a dedicated directory, not a filesystem root.");
    }
}

public sealed class NfsSettingsValidator : AbstractValidator<NfsSettingsUpdateRequest>
{
    public NfsSettingsValidator()
    {
        RuleFor(x => x.NfsHost).NotEmpty().MaximumLength(500).Must(h => !h.Any(char.IsWhiteSpace) && !h.Any(c => "\"'`$;&|<>\\".Contains(c)))
            .WithMessage("Use the form host:/export without spaces or shell characters.");
        RuleFor(x => x.NfsPath).Must(ShellQuote.IsSafeAbsolutePath).WithMessage("Mount path must be absolute and contain no '..'.")
            .Must(p => p.Trim('/').Length > 0).WithMessage("The mount path cannot be '/'.");
    }
}

public sealed class SshSettingsValidator : AbstractValidator<SshSettingsUpdateRequest>
{
    public SshSettingsValidator()
    {
        RuleFor(x => x.PrivateKey).Must(Rules.LooksLikePrivateKey).WithMessage("Paste an OpenSSH or PEM private key.");
        RuleFor(x => x.PublicKey).MaximumLength(5000);
    }
}

public sealed class VariableValidator : AbstractValidator<VariableUpsertRequest>
{
    public VariableValidator()
    {
        RuleFor(x => x.Name).Must(Rules.IsVariableName).WithMessage("Use letters, digits and underscores; start with a letter or underscore.")
            .Must(n => !Rules.ReservedPlaceholders.Contains(n)).WithMessage("This name is a built-in placeholder.");
        RuleFor(x => x.Value).MaximumLength(10000);
        RuleFor(x => x.Description).MaximumLength(500);
    }
}

public sealed class TemplateValidator : AbstractValidator<TemplateUpsertRequest>
{
    public TemplateValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Category).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Script).NotEmpty().MaximumLength(200_000)
            .Must(s => !s.Contains('\0', StringComparison.Ordinal)).WithMessage("Script contains NUL bytes.");
        RuleFor(x => x.Description).MaximumLength(1000);
    }
}

public sealed class S3DestinationValidator : AbstractValidator<S3DestinationUpsertRequest>
{
    public S3DestinationValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Kind).IsInEnum();
        // A folder site (a mounted NFS share on the execution host) has no endpoint, region, bucket or keys; its folder
        // is validated by the storage service (it must not be a system folder or overlap the staging share).
        When(x => x.Kind == StorageKindDto.S3, () =>
        {
            RuleFor(x => x.Endpoint).Must(e => Rules.IsHttpUrl(e)).WithMessage("Enter an http(s) URL without credentials.");
            RuleFor(x => x.Region).NotEmpty().MaximumLength(100).Matches("^[A-Za-z0-9-_]+$");
            RuleFor(x => x.Bucket).Must(Rules.IsBucketName).WithMessage("Enter a valid S3 bucket name.");
        });
        RuleFor(x => x.LocalPath).NotEmpty().MaximumLength(1024).When(x => x.Kind == StorageKindDto.Filesystem)
            .WithMessage("Enter the folder on the execution host, for example /mnt/dr-site.");
        // Empty on update = keep the stored access key id (the API only returns it masked).
        RuleFor(x => x.AccessKeyId).MaximumLength(255).Must(v => v is null || !v.Any(char.IsWhiteSpace));
        RuleFor(x => x.SecretKey).MaximumLength(500).Must(v => v is null || !v.Contains('\n', StringComparison.Ordinal));
        RuleFor(x => x.PathPrefix).MaximumLength(200).Matches("^[A-Za-z0-9._/-]*$").When(x => x.PathPrefix is not null)
            .Must(p => p is null || !p.Split('/').Contains("..")).WithMessage("Prefix cannot contain '..'.");
        RuleFor(x => x.EncryptionMode).IsInEnum();
        // The recipient is public, but messages still never echo submitted values.
        RuleFor(x => x.EncryptionRecipient)
            .Must(r => AgeKeys.IsValidRecipient(r)).When(x => !string.IsNullOrWhiteSpace(x.EncryptionRecipient))
            .WithMessage(x => AgeKeys.Inspect(x.EncryptionRecipient).Error ?? "Invalid age recipient.");
    }
}

public sealed class InspectRecipientValidator : AbstractValidator<InspectRecipientRequest>
{
    public InspectRecipientValidator() => RuleFor(x => x.Recipient).NotEmpty().MaximumLength(AgeKeys.MaxInputLength);
}

/// <summary>Private keys: format only; error messages describe the problem without ever echoing the value.</summary>
public sealed class SubmitDecryptionKeyValidator : AbstractValidator<SubmitDecryptionKeyRequest>
{
    public SubmitDecryptionKeyValidator() =>
        RuleFor(x => x.PrivateKey).Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Enter the age private key (\"AGE-SECRET-KEY-1…\").")
            .MaximumLength(AgeKeys.MaxInputLength * 4).WithMessage("The private key is too long.")
            .Must(AgeKeys.IsValidIdentity).WithMessage(x => AgeKeys.IdentityError(x.PrivateKey) ?? "Invalid age private key.");
}

public sealed class VerifyDecryptionKeyValidator : AbstractValidator<VerifyDecryptionKeyRequest>
{
    public VerifyDecryptionKeyValidator() =>
        RuleFor(x => x.PrivateKey).Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Enter the age private key (\"AGE-SECRET-KEY-1…\").")
            .MaximumLength(AgeKeys.MaxInputLength * 4).WithMessage("The private key is too long.")
            .Must(AgeKeys.IsValidIdentity).WithMessage(x => AgeKeys.IdentityError(x.PrivateKey) ?? "Invalid age private key.");
}

public sealed class S3TestValidator : AbstractValidator<S3TestRequest>
{
    public S3TestValidator()
    {
        When(x => x.Kind == StorageKindDto.S3, () =>
        {
            RuleFor(x => x.Endpoint).Must(e => Rules.IsHttpUrl(e)).WithMessage("Enter an http(s) URL without credentials.");
            RuleFor(x => x.Region).NotEmpty();
            RuleFor(x => x.Bucket).Must(Rules.IsBucketName).WithMessage("Enter a valid S3 bucket name.");
            RuleFor(x => x.AccessKeyId).NotEmpty().When(x => x.DestinationId is null);
            RuleFor(x => x.SecretKey).NotEmpty().When(x => x.DestinationId is null);
        });
        RuleFor(x => x.LocalPath).NotEmpty().MaximumLength(1024).When(x => x.Kind == StorageKindDto.Filesystem)
            .WithMessage("Enter the folder on the execution host, for example /mnt/dr-site.");
    }
}

public sealed class BackupJobValidator : AbstractValidator<BackupJobUpsertRequest>
{
    public BackupJobValidator()
    {
        RuleFor(x => x.Name).Must(BackupJob.IsValidName)
            .WithMessage($"Use 1–{BackupJob.MaxNameLength} characters: letters, digits, '.', '_' or '-' (the name is used as the NFS directory and S3 prefix).");
        RuleFor(x => x.Description).MaximumLength(1000);
        RuleFor(x => x.TemplateId).GreaterThan(0);
        RuleFor(x => x.ServerId).GreaterThan(0);
        RuleFor(x => x.CronExpression).Must(c => SchedulePlanner.TryParse(c, out _, out _))
            .When(x => !string.IsNullOrWhiteSpace(x.CronExpression))
            .WithMessage(x => { SchedulePlanner.TryParse(x.CronExpression, out _, out var e); return e ?? "Invalid cron expression."; });
        RuleFor(x => x.CronExpression).NotEmpty().When(x => x.Status == JobStatusDto.Active && x.CronExpression is not null && x.CronExpression.Length > 0);
        RuleFor(x => x.TimeZone).Must(tz => tz is null || SchedulePlanner.TryGetTimeZone(tz, out _)).WithMessage("Unknown IANA time zone.");
        RuleFor(x => x.MissedRunGraceMinutes).InclusiveBetween(1, 1440);
        RuleFor(x => x.RetentionDays).InclusiveBetween(0m, 36500m).PrecisionScale(10, 4, true);
        RuleFor(x => x.MinRecoveryPointsToKeep).InclusiveBetween(1, 1000);
        RuleFor(x => x.TimeoutMinutes).InclusiveBetween(1, 10080);
        RuleFor(x => x.MaxAttempts).InclusiveBetween(1, 10);
    }
}

public sealed class SchedulePreviewValidator : AbstractValidator<SchedulePreviewRequest>
{
    public SchedulePreviewValidator() => RuleFor(x => x.CronExpression).MaximumLength(120);
}

public sealed class CreateRestoreValidator : AbstractValidator<CreateRestoreRequest>
{
    public CreateRestoreValidator()
    {
        RuleFor(x => x.RecoveryPointId).GreaterThan(0);
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(5).MaximumLength(2000).WithMessage("Describe why this restore is needed (at least 5 characters).");
        RuleFor(x => x.ConfirmationPhrase).NotEmpty();
        RuleFor(x => x.DecryptionKey).Cascade(CascadeMode.Stop)
            .MaximumLength(AgeKeys.MaxInputLength * 4).WithMessage("The decryption key is too long.")
            .Must(AgeKeys.IsValidIdentity).WithMessage(x => AgeKeys.IdentityError(x.DecryptionKey) ?? "Invalid age private key.")
            .When(x => !string.IsNullOrWhiteSpace(x.DecryptionKey));
    }
}

public sealed class ProtectRecoveryPointValidator : AbstractValidator<ProtectRecoveryPointRequest>
{
    public ProtectRecoveryPointValidator() =>
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(1000).When(x => x.Protected).WithMessage("A reason is required to place a hold.");
}

public sealed class SmtpSettingsValidator : AbstractValidator<SmtpSettingsUpdateRequest>
{
    public SmtpSettingsValidator()
    {
        RuleFor(x => x.Host).Must(Rules.IsHost).WithMessage("Enter a valid SMTP host.");
        RuleFor(x => x.Port).InclusiveBetween(1, 65535);
        RuleFor(x => x.FromAddress).NotEmpty().EmailAddress();
        RuleFor(x => x.Security).Must(s => s is "None" or "StartTls" or "SslOnConnect").WithMessage("Security must be None, StartTls or SslOnConnect.");
        RuleFor(x => x.Template).MaximumLength(200_000);
        RuleFor(x => x.FromName).MaximumLength(200);
    }
}

public sealed class TeamsSettingsValidator : AbstractValidator<TeamsSettingsUpdateRequest>
{
    public TeamsSettingsValidator()
    {
        RuleFor(x => x.WebhookUrl).Must(u => Rules.IsHttpUrl(u, requireHttps: true)).When(x => !string.IsNullOrWhiteSpace(x.WebhookUrl))
            .WithMessage("The webhook URL must be an https URL.");
        RuleFor(x => x.Template).MaximumLength(100_000);
    }
}

public sealed class NotificationSettingsValidator : AbstractValidator<NotificationSettingsUpdateRequest>
{
    public NotificationSettingsValidator()
    {
        RuleFor(x => x.Recipients).Must(r => r.Count <= 50).WithMessage("At most 50 recipients.");
        RuleForEach(x => x.Recipients).NotEmpty().EmailAddress().MaximumLength(320);
    }
}

public sealed class CreateUserValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Password).NotEmpty();
        RuleFor(x => x.Roles).NotEmpty().WithMessage("Select at least one role.");
        RuleForEach(x => x.Roles).Must(r => Roles.All.Contains(r)).WithMessage("Unknown role.");
    }
}

public sealed class UpdateUserValidator : AbstractValidator<UpdateUserRequest>
{
    public UpdateUserValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Roles).NotEmpty().WithMessage("Select at least one role.");
        RuleForEach(x => x.Roles).Must(r => Roles.All.Contains(r)).WithMessage("Unknown role.");
    }
}

public sealed class LoginValidator : AbstractValidator<LoginRequest>
{
    public LoginValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(512);
    }
}

public sealed class CreateTokenValidator : AbstractValidator<CreatePersonalAccessTokenRequest>
{
    public CreateTokenValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.ExpiresInDays).InclusiveBetween(1, 365).When(x => x.ExpiresInDays is not null);
    }
}
