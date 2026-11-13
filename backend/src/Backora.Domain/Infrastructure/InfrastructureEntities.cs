using Backora.Domain.Common;

namespace Backora.Domain.Infrastructure;

/// <summary>A Linux host whose data is backed up (legacy: servers).</summary>
public sealed class Server : Entity
{
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int SshPort { get; set; } = 22;
    public string SshUser { get; set; } = "root";

    /// <summary>Encrypted private key (envelope format). Null means "use the global default key".</summary>
    public string? PrivateKeyCiphertext { get; set; }

    /// <summary>Trusted SSH host key fingerprint (SHA256:base64). Execution is refused until set.</summary>
    public string? HostKeyFingerprint { get; set; }
    public DateTimeOffset? HostKeyTrustedAt { get; set; }
    public long? HostKeyTrustedByUserId { get; set; }

    public DateTimeOffset? LastConnectionTestAt { get; set; }
    public bool? LastConnectionTestSucceeded { get; set; }
    public string? LastConnectionTestMessage { get; set; }

    public string? Description { get; set; }
}

/// <summary>
/// The worker/jump host (legacy: worker_settings). It has the NFS share mounted, runs rclone for
/// offload and restore transfers, and forwards SSH connections to target servers.
/// </summary>
public sealed class WorkerNode : Entity
{
    public string Name { get; set; } = "Primary worker";
    public string Host { get; set; } = string.Empty;
    public int SshPort { get; set; } = 22;
    public string SshUser { get; set; } = string.Empty;
    public string PrivateKeyCiphertext { get; set; } = string.Empty;
    public string? HostKeyFingerprint { get; set; }
    public DateTimeOffset? HostKeyTrustedAt { get; set; }

    /// <summary>Last capability probe (JSON of ExecutionHostProbeDto). Contains no secrets: tool versions, disks, services.</summary>
    public string? CapabilitiesJson { get; set; }
    public DateTimeOffset? CapabilitiesProbedAt { get; set; }
    public long? HostKeyTrustedByUserId { get; set; }

    /// <summary>Path of the rclone binary on the worker. Never auto-installed.</summary>
    public string RclonePath { get; set; } = "rclone";

    /// <summary>Directory on the worker used for per-run working files (0700).</summary>
    public string WorkDirectory { get; set; } = "/var/tmp/backora";

    public DateTimeOffset? LastConnectionTestAt { get; set; }
    public bool? LastConnectionTestSucceeded { get; set; }
    public string? LastConnectionTestMessage { get; set; }
}

/// <summary>Singleton NFS staging settings (legacy: nfs_settings).</summary>
public sealed class StorageSettings : Entity
{
    /// <summary>NFS export, e.g. <c>nfs01:/backora</c>. Rendered into scripts as {NFS_HOST}.</summary>
    public string NfsHost { get; set; } = string.Empty;

    /// <summary>Mount point of the export on the worker and on targets. Rendered as {NFS_PATH}.</summary>
    public string NfsPath { get; set; } = string.Empty;
}

/// <summary>Singleton global SSH key (legacy: ssh_settings).</summary>
public sealed class SshSettings : Entity
{
    public string? DefaultPrivateKeyCiphertext { get; set; }
    public string? DefaultPublicKey { get; set; }
}

/// <summary>Template placeholder variable (legacy: variables).</summary>
public sealed class Variable : Entity
{
    public string Name { get; set; } = string.Empty;
    public bool IsSecret { get; set; }

    /// <summary>Plain value for non-secret variables.</summary>
    public string? Value { get; set; }

    /// <summary>Encrypted value for secret variables.</summary>
    public string? ValueCiphertext { get; set; }

    public string? Description { get; set; }
}

/// <summary>S3-compatible object storage destination (legacy: s3_settings).</summary>
public sealed class S3Destination : Entity
{
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// S3 (a bucket) or Filesystem: a folder on the execution host, typically a mounted NFS share (<see cref="LocalPath"/>).
    /// A filesystem site needs no endpoint or keys; copies, checks and restores go through rclone's local backend and
    /// listing, retention and connection tests run on the execution host.
    /// </summary>
    public StorageKind Kind { get; set; } = StorageKind.S3;

    /// <summary>For a filesystem site: the folder on the execution host (absolute path), e.g. /mnt/dr-site.</summary>
    public string? LocalPath { get; set; }

    public string Endpoint { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string Bucket { get; set; } = string.Empty;
    public string AccessKeyId { get; set; } = string.Empty;
    public string SecretKeyCiphertext { get; set; } = string.Empty;
    public bool ForcePathStyle { get; set; } = true;
    public string? PathPrefix { get; set; }

    /// <summary>
    /// When false the destination receives no new uploads or DR copies. Recovery points already stored on it stay
    /// listed and restorable, and retention keeps managing them.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    public DateTimeOffset? LastConnectionTestAt { get; set; }
    public bool? LastConnectionTestSucceeded { get; set; }
    public string? LastConnectionTestMessage { get; set; }

    /// <summary>Client-side encryption applied to new uploads. Existing recovery points keep their own recorded key.</summary>
    public EncryptionMode EncryptionMode { get; set; } = EncryptionMode.None;

    /// <summary>Active age X25519 recipient (public key, "age1…"). The private key is never stored by Backora.</summary>
    public string? EncryptionRecipient { get; set; }
    public string? EncryptionKeyFingerprint { get; set; }
    public DateTimeOffset? EncryptionKeyActivatedAt { get; set; }

    /// <summary>Every recipient ever activated on this destination (rotation history; public material only).</summary>
    public List<S3DestinationEncryptionKey> EncryptionKeys { get; set; } = [];
}

/// <summary>A recipient public key that was active on a destination for a period of time.</summary>
public sealed class S3DestinationEncryptionKey : Entity
{
    public long S3DestinationId { get; set; }
    public string Recipient { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public DateTimeOffset ActivatedAt { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
    /// <summary>The private key (age identity) when Backora holds it, encrypted with the app's encryption key
    /// (<c>Encryption:Keys</c>). Null for customer-held keys, which Backora never sees.</summary>
    public string? IdentityCiphertext { get; set; }
}
