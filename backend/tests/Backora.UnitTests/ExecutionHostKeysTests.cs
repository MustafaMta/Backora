using Backora.Application.Abstractions;
using Backora.Application.Execution;
using Backora.Domain.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Backora.UnitTests;

public class ExecutionHostKeysTests
{
    private static ExecutionHostKeys Keys(string? file, ISecretProtector? secrets = null) =>
        new(Options.Create(new ExecutionHostOptions { PrivateKeyFile = file, TargetKeyFile = file }), secrets ?? Substitute.For<ISecretProtector>());

    [Fact]
    public void Mounted_secret_file_is_the_key_source_and_the_database_value_is_ignored()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bko-key-{Guid.NewGuid():N}");
        File.WriteAllText(path, "-----BEGIN OPENSSH PRIVATE KEY-----\nfake\n-----END OPENSSH PRIVATE KEY-----\n");
        try
        {
            var secrets = Substitute.For<ISecretProtector>();
            var keys = Keys(path, secrets);
            var node = new WorkerNode { PrivateKeyCiphertext = "db-ciphertext" };

            keys.KeyFromFile.Should().BeTrue();
            keys.Source.Should().Be("secret-file");
            keys.HasWorkerKey(node).Should().BeTrue();
            keys.WorkerKey(node).Should().StartWith("-----BEGIN OPENSSH PRIVATE KEY-----");
            keys.DefaultTargetKey(null).Should().StartWith("-----BEGIN");
            secrets.DidNotReceive().Unprotect(Arg.Any<string>());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Missing_secret_file_fails_closed_instead_of_falling_back_to_the_database()
    {
        var keys = Keys("/run/secrets/does-not-exist");
        var node = new WorkerNode { PrivateKeyCiphertext = "db-ciphertext" };
        keys.HasWorkerKey(node).Should().BeFalse();
        var act = () => keys.WorkerKey(node);
        act.Should().Throw<RemoteOperationException>().WithMessage("*not mounted*");
    }

    [Fact]
    public void Without_a_file_the_legacy_encrypted_database_key_is_used()
    {
        var secrets = Substitute.For<ISecretProtector>();
        secrets.Unprotect("db-ciphertext").Returns("plain-key");
        var keys = Keys(null, secrets);
        keys.Source.Should().Be("database");
        keys.WorkerKey(new WorkerNode { PrivateKeyCiphertext = "db-ciphertext" }).Should().Be("plain-key");
    }
}
