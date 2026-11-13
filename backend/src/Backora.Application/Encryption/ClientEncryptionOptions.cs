namespace Backora.Application.Encryption;

/// <summary>Settings for customer-key (age) client-side encryption of S3 destinations.</summary>
public sealed class ClientEncryptionOptions
{
    public const string Section = "ClientEncryption";

    /// <summary>Path of the official <c>age</c> binary on the worker host (≥ 1.1). Never auto-installed.</summary>
    public string AgePath { get; set; } = "age";

    /// <summary>How long a restore waits in AwaitingKey for the operator's private key before it fails.</summary>
    public int KeyRequestTimeoutSeconds { get; set; } = 1800;

    /// <summary>How often the API-side key courier checks whether the restore is ready to receive the key.</summary>
    public int CourierPollMilliseconds { get; set; } = 1000;

    /// <summary>Upper bound for one attempt to write the key into the worker-host FIFO.</summary>
    public int DeliveryTimeoutSeconds { get; set; } = 30;

    public TimeSpan KeyRequestTimeout => TimeSpan.FromSeconds(Math.Clamp(KeyRequestTimeoutSeconds, 30, 24 * 3600));
}
