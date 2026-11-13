using Backora.Application.Abstractions;
using Backora.Domain.Infrastructure;
using Microsoft.Extensions.Options;

namespace Backora.Application.Execution;

/// <summary>
/// Where SSH private keys come from. In production the execution-host key is a secret file mounted read-only into
/// the backend container (Docker/Kubernetes secret), so it never enters PostgreSQL, the API or the UI.
/// The legacy database field (envelope-encrypted) is only used when no file is configured.
/// </summary>
public sealed class ExecutionHostOptions
{
    public const string Section = "ExecutionHost";

    /// <summary>Absolute path of the execution host's private key (e.g. /run/secrets/execution_host_key).</summary>
    public string? PrivateKeyFile { get; set; }

    /// <summary>Optional key used for managed servers that have no server-specific key (reached through the execution host).</summary>
    public string? TargetKeyFile { get; set; }
}

public sealed class ExecutionHostKeys(IOptions<ExecutionHostOptions> options, ISecretProtector secrets)
{
    private const int MaxKeyBytes = 16 * 1024;

    public bool KeyFromFile => !string.IsNullOrWhiteSpace(options.Value.PrivateKeyFile);

    /// <summary>"secret-file" or "database" — never the key itself.</summary>
    public string Source => KeyFromFile ? "secret-file" : "database";

    public bool HasWorkerKey(WorkerNode w) => KeyFromFile ? FileExists(options.Value.PrivateKeyFile!) : !string.IsNullOrEmpty(w.PrivateKeyCiphertext);

    public string WorkerKey(WorkerNode w) => KeyFromFile
        ? ReadKey(options.Value.PrivateKeyFile!, "execution host")
        : !string.IsNullOrEmpty(w.PrivateKeyCiphertext) ? secrets.Unprotect(w.PrivateKeyCiphertext)
        : throw new RemoteOperationException(Domain.FailureClass.Configuration, "No SSH key is configured for the execution host.");

    public string? DefaultTargetKey(SshSettings? ssh) =>
        !string.IsNullOrWhiteSpace(options.Value.TargetKeyFile) ? ReadKey(options.Value.TargetKeyFile!, "managed servers")
        : ssh?.DefaultPrivateKeyCiphertext is { } c ? secrets.Unprotect(c) : null;

    private static bool FileExists(string path) => Path.IsPathRooted(path) && File.Exists(path);

    private static string ReadKey(string path, string purpose)
    {
        if (!Path.IsPathRooted(path) || !File.Exists(path))
        {
            throw new RemoteOperationException(Domain.FailureClass.Configuration, $"The SSH key file for the {purpose} is not mounted.");
        }
        var info = new FileInfo(path);
        if (info.Length is 0 or > MaxKeyBytes)
        {
            throw new RemoteOperationException(Domain.FailureClass.Configuration, $"The SSH key file for the {purpose} is empty or too large.");
        }
        return File.ReadAllText(path).Trim();
    }
}
