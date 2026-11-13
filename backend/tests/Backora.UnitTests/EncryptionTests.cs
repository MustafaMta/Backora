using System.Text;
using Backora.Application.Common;
using Backora.Application.Encryption;
using Backora.Application.Execution;
using Backora.Application.Operations;
using Backora.Application.Validation;
using Backora.Contracts.V1;
using Backora.Domain;
using Backora.Domain.Backups;
using Backora.Domain.Infrastructure;
using FluentAssertions;

namespace Backora.UnitTests;

public class Bech32Tests
{
    // BIP-173 valid test vectors whose data part is byte-aligned (Backora decodes to 8-bit data).
    [Theory]
    [InlineData("A12UEL5L", "a", 0)]
    [InlineData("a12uel5l", "a", 0)]
    [InlineData("abcdef1qpzry9x8gf2tvdw0s3jn54khce6mua7lmqqqxw", "abcdef", 20)]
    [InlineData("11qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqc8247j", "1", 51)]
    public void Decodes_bip173_valid_vectors(string input, string hrp, int bytes)
    {
        Bech32.TryDecode(input, out var h, out var data, out var error).Should().BeTrue(error);
        h.Should().Be(hrp);
        data.Should().HaveCount(bytes);
    }

    // BIP-173 invalid test vectors (+ mixed case).
    [Theory]
    [InlineData("pzry9x0s0muk")]          // no separator
    [InlineData("1pzry9x0s0muk")]         // empty HRP
    [InlineData("x1b4n0q5v")]             // invalid data character
    [InlineData("li1dgmt3")]              // checksum too short
    [InlineData("A1G7SGD8")]              // checksum calculated with uppercase HRP
    [InlineData("10a06t8")]               // empty HRP
    [InlineData("1qzzfhee")]              // empty HRP
    [InlineData("a12UEL5L")]              // mixed case
    [InlineData("de1lg7wtÿ")]        // invalid character
    public void Rejects_bip173_invalid_vectors(string input)
    {
        Bech32.TryDecode(input, out _, out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Encode_round_trips()
    {
        var data = Enumerable.Range(0, 32).Select(i => (byte)(i * 7)).ToArray();
        var s = Bech32.Encode("age", data);
        s.Should().StartWith("age1").And.Be(s.ToLowerInvariant());
        Bech32.TryDecode(s, out var hrp, out var back, out _).Should().BeTrue();
        hrp.Should().Be("age");
        back.Should().Equal(data);
        Bech32.TryDecode(s.ToUpperInvariant(), out _, out var upper, out _).Should().BeTrue("all-uppercase is valid bech32");
        upper.Should().Equal(data);
    }

    [Fact]
    public void Every_single_character_substitution_breaks_the_checksum()
    {
        var s = Bech32.Encode("age", new byte[32]);
        for (var i = 4; i < s.Length; i++)
        {
            var c = s[i] == 'q' ? 'p' : 'q';
            var mutated = s[..i] + c + s[(i + 1)..];
            Bech32.TryDecode(mutated, out _, out _, out _).Should().BeFalse($"position {i}");
        }
    }
}

public class AgeKeyTests
{
    /// <summary>Vectors produced by the real age-keygen 1.2.1 (`docker exec backora-lab-worker-1 age-keygen`). Test-only keys.</summary>
    public static TheoryData<string, string> AgeKeygenVectors => new()
    {
        { "AGE-SECRET-KEY-1RFVW4Q8J826C3E0N9SNGHSXWDS6R52C59C8G28CAV9KAA5GN5AASH3AM2C", "age1hu2hz460rykkw50cvjrd92rdeynwyd02f4hmr3fwsgeq5j7vageqvr6ekp" },
        { "AGE-SECRET-KEY-1T37NKJGSDPJDNKFAYJJ78VDDACR62957Q0MJ3U6077JGC8ZHZ9PSP4XMWD", "age1s93zuq47x7y29jrckpcs46rc9htpz2g5s4cr6d82x54tpgks84zq0ktsv6" },
        { "AGE-SECRET-KEY-1LR63XYJUXL7Q53XKR57U6N4F778VFG6JRMV09SUPT57MMJ4GR58Q3E2Z7P", "age1yhy5ya4mcqjgy036zr4ew8pnef235yuhxapdaxvvjkx263mgx9yswxpl5y" },
    };

    [Theory]
    [MemberData(nameof(AgeKeygenVectors))]
    public void Derives_the_same_recipient_as_age_keygen(string identity, string recipient)
    {
        AgeKeys.TryDerivePublicKey(identity, out var pk, out var error).Should().BeTrue(error);
        Bech32.Encode(AgeKeys.RecipientHrp, pk).Should().Be(recipient);
        AgeKeys.Matches(identity, recipient, out var fp, out _).Should().BeTrue();
        fp.Should().Be(AgeKeys.RecipientFingerprint(recipient));
        // Lowercase identities and a pasted age-keygen key file are accepted too.
        AgeKeys.Matches(identity.ToLowerInvariant(), recipient, out _, out _).Should().BeTrue();
        AgeKeys.Matches($"# created: 2026-11-08T00:00:00Z\n# public key: {recipient}\n{identity}\n", recipient, out _, out _).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(AgeKeygenVectors))]
    public void A_different_key_does_not_match(string identity, string recipient)
    {
        var other = AgeKeygenVectors.Select(v => (string)v[1]).First(r => r != recipient);
        AgeKeys.Matches(identity, other, out var fp, out var error).Should().BeFalse();
        error.Should().BeNull();
        fp.Should().Be(AgeKeys.RecipientFingerprint(recipient), "the fingerprint reported is the one the supplied key belongs to");
    }

    [Fact]
    public void Fingerprint_is_sha256_of_the_public_key_unpadded_base64()
    {
        var fp = AgeKeys.RecipientFingerprint("age1hu2hz460rykkw50cvjrd92rdeynwyd02f4hmr3fwsgeq5j7vageqvr6ekp");
        fp.Should().StartWith("SHA256:").And.NotContain("=");
        fp.Length.Should().Be("SHA256:".Length + 43);
        AgeKeys.TryParseRecipient("age1hu2hz460rykkw50cvjrd92rdeynwyd02f4hmr3fwsgeq5j7vageqvr6ekp", out var pk, out _);
        var expected = "SHA256:" + Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(pk)).TrimEnd('=');
        fp.Should().Be(expected);
    }

    [Theory]
    [InlineData("", "Enter an age recipient")]
    [InlineData("AGE-SECRET-KEY-1RFVW4Q8J826C3E0N9SNGHSXWDS6R52C59C8G28CAV9KAA5GN5AASH3AM2C", "PRIVATE key")]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIG", "Only native age X25519")]
    [InlineData("age1hu2hz460rykkw50cvjrd92rdeynwyd02f4hmr3fwsgeq5j7vageqvr6ekq", "checksum")]   // last char changed
    [InlineData("age1hu2hz460rykkw50cvjrd92rdeynwyd02f4hmr3fwsgeq5j7vAGEqvr6ekp", "mixes upper and lower")]
    public void Rejects_invalid_recipients_with_a_useful_reason(string value, string reason)
    {
        AgeKeys.TryParseRecipient(value, out _, out var error).Should().BeFalse();
        error.Should().Contain(reason);
        AgeKeys.Inspect(value).Valid.Should().BeFalse();
    }

    [Fact]
    public void Rejects_a_bech32_value_with_another_prefix()
    {
        AgeKeys.TryParseRecipient(Bech32.Encode("abc", new byte[32]), out _, out var error).Should().BeFalse();
        error.Should().Contain("must start with");
    }

    [Fact]
    public void Rejects_a_recipient_of_the_wrong_length()
    {
        var shortKey = Bech32.Encode("age", new byte[31]);
        AgeKeys.TryParseRecipient(shortKey, out _, out var error).Should().BeFalse();
        error.Should().Contain("32-byte");
    }

    [Fact]
    public void Normalizes_uppercase_recipients_to_lowercase()
    {
        const string r = "age1hu2hz460rykkw50cvjrd92rdeynwyd02f4hmr3fwsgeq5j7vageqvr6ekp";
        AgeKeys.NormalizeRecipient(r.ToUpperInvariant()).Should().Be(r);
        AgeKeys.Inspect(" " + r + " ").Should().BeEquivalentTo(new RecipientInspectionDto(true, AgeKeys.RecipientFingerprint(r), null, "age-x25519"));
    }

    [Theory]
    [InlineData("age1hu2hz460rykkw50cvjrd92rdeynwyd02f4hmr3fwsgeq5j7vageqvr6ekp", "public recipient")]
    [InlineData("AGE-SECRET-KEY-1RFVW4Q8J826C3E0N9SNGHSXWDS6R52C59C8G28CAV9KAA5GN5AASH3AM2D", "checksum")]
    [InlineData("hello", "AGE-SECRET-KEY-1")]
    [InlineData("AGE-SECRET-KEY-1RFVW4Q8J826C3E0N9SNGHSXWDS6R52C59C8G28CAV9KAA5GN5AASH3AM2C\nAGE-SECRET-KEY-1T37NKJGSDPJDNKFAYJJ78VDDACR62957Q0MJ3U6077JGC8ZHZ9PSP4XMWD", "exactly one")]
    public void Rejects_invalid_identities_without_echoing_them(string value, string reason)
    {
        var error = AgeKeys.IdentityError(value);
        error.Should().Contain(reason);
        foreach (var line in value.Split('\n').Where(l => l.Length > 16)) error.Should().NotContain(line[16..]);
        error.Should().NotContain(value.Trim());
    }

    [Fact]
    public void Rejects_an_identity_that_encodes_the_wrong_length()
    {
        var bad = Bech32.Encode("age-secret-key-", new byte[16]).ToUpperInvariant();
        AgeKeys.IdentityError(bad).Should().Contain("32-byte");
    }

    [Fact]
    public void Generated_key_pairs_are_valid_and_unique()
    {
        var a = AgeKeys.Generate();
        var b = AgeKeys.Generate();
        a.Identity.Should().StartWith("AGE-SECRET-KEY-1").And.Be(a.Identity.ToUpperInvariant());
        a.Recipient.Should().StartWith("age1");
        a.Fingerprint.Should().Be(AgeKeys.RecipientFingerprint(a.Recipient));
        AgeKeys.Matches(a.Identity, a.Recipient, out _, out _).Should().BeTrue();
        AgeKeys.Matches(a.Identity, b.Recipient, out _, out _).Should().BeFalse();
        a.Identity.Should().NotBe(b.Identity);
        a.Fingerprint.Should().NotContain(a.Identity[16..]);
    }

    [Fact]
    public void Identity_line_for_the_worker_is_uppercase_ascii_with_newline()
    {
        var bytes = AgeKeys.IdentityLineBytes("# comment\nage-secret-key-1rfvw4q8j826c3e0n9snghsxwds6r52c59c8g28cav9kaa5gn5aash3am2c\n");
        Encoding.ASCII.GetString(bytes).Should().Be("AGE-SECRET-KEY-1RFVW4Q8J826C3E0N9SNGHSXWDS6R52C59C8G28CAV9KAA5GN5AASH3AM2C\n");
    }
}

public class EncryptionValidatorTests
{
    private const string Key = "AGE-SECRET-KEY-1RFVW4Q8J826C3E0N9SNGHSXWDS6R52C59C8G28CAV9KAA5GN5AASH3AM2C";
    // Valid shape, broken checksum: the most likely "almost right" paste.
    private const string Typo = "AGE-SECRET-KEY-1RFVW4Q8J826C3E0N9SNGHSXWDS6R52C59C8G28CAV9KAA5GN5AASH3AM2D";

    [Theory]
    [InlineData(Typo)]
    [InlineData("AGE-SECRET-KEY-1-not-a-key-but-contains-SECRET-MATERIAL-XYZ")]
    [InlineData("  my super secret passphrase 123456  ")]
    public void Key_validators_never_echo_the_submitted_value(string value)
    {
        var results = new[]
        {
            new SubmitDecryptionKeyValidator().Validate(new SubmitDecryptionKeyRequest(value)),
            new VerifyDecryptionKeyValidator().Validate(new VerifyDecryptionKeyRequest(value)),
            new CreateRestoreValidator().Validate(new CreateRestoreRequest(1, "Disaster recovery drill", "restore x", value)),
        };
        foreach (var r in results)
        {
            r.IsValid.Should().BeFalse();
            foreach (var e in r.Errors)
            {
                var significant = value.Trim();
                e.ErrorMessage.Should().NotContain(significant);
                e.ErrorMessage.Should().NotContain(significant[Math.Min(16, significant.Length - 1)..]);
                // FluentValidation exposes the attempted value on the failure object; the API never serialises it
                // (ProblemExceptionHandler maps only ErrorMessage). Make sure placeholders did not inline it either.
                e.ErrorMessage.Should().NotContain("{PropertyValue}");
            }
        }
    }

    [Fact]
    public void Valid_keys_pass_validation()
    {
        new SubmitDecryptionKeyValidator().Validate(new SubmitDecryptionKeyRequest(Key)).IsValid.Should().BeTrue();
        new CreateRestoreValidator().Validate(new CreateRestoreRequest(1, "Disaster recovery drill", "restore x", Key)).IsValid.Should().BeTrue();
        new CreateRestoreValidator().Validate(new CreateRestoreRequest(1, "Disaster recovery drill", "restore x")).IsValid.Should().BeTrue("the key is checked against the point by the service");
    }

    [Fact]
    public void Destination_validator_checks_the_recipient()
    {
        var v = new S3DestinationValidator();
        S3DestinationUpsertRequest Req(string? recipient) => new("d", "http://s3.test", "us-east-1", "bucket-a", "ak", "sk", true, null, null, EncryptionModeDto.CustomerKey, recipient);
        v.Validate(Req("age1hu2hz460rykkw50cvjrd92rdeynwyd02f4hmr3fwsgeq5j7vageqvr6ekp")).IsValid.Should().BeTrue();
        var bad = v.Validate(Req("age1hu2hz460rykkw50cvjrd92rdeynwyd02f4hmr3fwsgeq5j7vageqvr6ekq"));
        bad.IsValid.Should().BeFalse();
        bad.Errors.Should().ContainSingle(e => e.PropertyName == "EncryptionRecipient").Which.ErrorMessage.Should().Contain("checksum");
        var priv = v.Validate(Req(Key));
        priv.IsValid.Should().BeFalse();
        priv.Errors.Single().ErrorMessage.Should().Contain("PRIVATE key").And.NotContain(Key[16..]);
    }
}

public class DestinationEncryptionTests
{
    private const string RecipientA = "age1hu2hz460rykkw50cvjrd92rdeynwyd02f4hmr3fwsgeq5j7vageqvr6ekp";
    private const string RecipientB = "age1s93zuq47x7y29jrckpcs46rc9htpz2g5s4cr6d82x54tpgks84zq0ktsv6";
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);

    private static S3DestinationUpsertRequest Req(EncryptionModeDto mode, string? recipient = null, bool confirm = false) =>
        new("d", "http://s3.test", "us-east-1", "bucket-a", "ak", null, true, null, 1, mode, recipient, confirm);

    [Fact]
    public void Enable_rotate_and_disable_keep_a_public_history()
    {
        var d = new S3Destination();
        DestinationEncryption.Apply(d, Req(EncryptionModeDto.CustomerKey, RecipientA.ToUpperInvariant()), isNew: true, T0)!.Kind.Should().Be("enabled");
        d.EncryptionMode.Should().Be(EncryptionMode.CustomerKey);
        d.EncryptionRecipient.Should().Be(RecipientA, "recipients are stored in canonical lowercase");
        d.EncryptionKeyFingerprint.Should().Be(AgeKeys.RecipientFingerprint(RecipientA));
        d.EncryptionKeys.Should().ContainSingle(k => k.RetiredAt == null);

        // Same recipient again: no change, no new history row. Empty recipient on update keeps the active key.
        DestinationEncryption.Apply(d, Req(EncryptionModeDto.CustomerKey, RecipientA), isNew: false, T0.AddMinutes(1)).Should().BeNull();
        DestinationEncryption.Apply(d, Req(EncryptionModeDto.CustomerKey, null), isNew: false, T0.AddMinutes(1)).Should().BeNull();

        var rotated = DestinationEncryption.Apply(d, Req(EncryptionModeDto.CustomerKey, RecipientB), isNew: false, T0.AddHours(1))!;
        rotated.Kind.Should().Be("rotated");
        rotated.OldFingerprint.Should().Be(AgeKeys.RecipientFingerprint(RecipientA));
        rotated.NewFingerprint.Should().Be(AgeKeys.RecipientFingerprint(RecipientB));
        d.EncryptionKeys.Should().HaveCount(2);
        d.EncryptionKeys.Single(k => k.Recipient == RecipientA).RetiredAt.Should().Be(T0.AddHours(1));
        d.EncryptionKeys.Single(k => k.Recipient == RecipientB).RetiredAt.Should().BeNull();
        d.EncryptionKeyActivatedAt.Should().Be(T0.AddHours(1));
    }

    [Fact]
    public void Disabling_requires_explicit_confirmation()
    {
        var d = new S3Destination();
        DestinationEncryption.Apply(d, Req(EncryptionModeDto.CustomerKey, RecipientA), isNew: true, T0);

        var act = () => DestinationEncryption.Apply(d, Req(EncryptionModeDto.None), isNew: false, T0.AddDays(1));
        act.Should().Throw<UnprocessableRequestException>().Which.Errors.Should().ContainKey("confirmDisableEncryption");
        d.EncryptionMode.Should().Be(EncryptionMode.CustomerKey, "nothing changes without confirmation");

        DestinationEncryption.Apply(d, Req(EncryptionModeDto.None, confirm: true), isNew: false, T0.AddDays(1))!.Kind.Should().Be("disabled");
        d.EncryptionMode.Should().Be(EncryptionMode.None);
        d.EncryptionRecipient.Should().BeNull();
        d.EncryptionKeys.Should().OnlyContain(k => k.RetiredAt != null);
    }

    [Fact]
    public void Customer_key_without_a_recipient_is_a_validation_error()
    {
        var act = () => DestinationEncryption.Apply(new S3Destination(), Req(EncryptionModeDto.CustomerKey, null), isNew: true, T0);
        act.Should().Throw<RequestValidationException>().Which.Errors.Should().ContainKey("encryptionRecipient");
        var act2 = () => DestinationEncryption.Apply(new S3Destination(), Req(EncryptionModeDto.CustomerKey, "age1nope"), isNew: true, T0);
        act2.Should().Throw<RequestValidationException>();
    }
}

public class EncryptedOffloadPlanningTests
{
    private const string RecipientA = "age1hu2hz460rykkw50cvjrd92rdeynwyd02f4hmr3fwsgeq5j7vageqvr6ekp";
    private const string RecipientB = "age1s93zuq47x7y29jrckpcs46rc9htpz2g5s4cr6d82x54tpgks84zq0ktsv6";

    private static S3Destination Dest(string? recipient) => recipient is null
        ? new S3Destination { Name = "plain" }
        : new S3Destination { Name = "enc", EncryptionMode = EncryptionMode.CustomerKey, EncryptionRecipient = recipient, EncryptionKeyFingerprint = AgeKeys.RecipientFingerprint(recipient) };

    private static OffloadRun EncryptedSource(string recipient) => new()
    {
        Id = 1, S3Destination = Dest(null), Encrypted = true, EncryptionFormat = "age-v1", EncryptionRecipient = recipient,
        EncryptionKeyFingerprint = AgeKeys.RecipientFingerprint(recipient), EncryptedObjectsJson = "[{\"RelativePath\":\"a.tar.gz\",\"CiphertextSize\":123,\"CiphertextSha256\":\"ab\"}]",
    };

    [Fact]
    public void Primary_upload_to_a_customer_key_destination_snapshots_the_recipient()
    {
        var o = new OffloadRun { S3Destination = Dest(RecipientA) };
        OffloadRunProcessor.SnapshotEncryption(o);
        o.Encrypted.Should().BeTrue();
        o.EncryptionFormat.Should().Be("age-v1");
        o.EncryptionRecipient.Should().Be(RecipientA);
        OffloadRunProcessor.ModeOf(o).Should().Be(OffloadRunProcessor.TransferMode.StageFiles);
        o.ObjectKeyFor("x/y.tar.gz").Should().Be("x/y.tar.gz.age");
    }

    [Fact]
    public void The_worker_compresses_primary_uploads_and_replicas_keep_their_sources_format()
    {
        var primary = new OffloadRun { S3Destination = Dest(null) };
        OffloadRunProcessor.SnapshotEncryption(primary, "zstd");
        primary.Compression.Should().Be("zstd");
        OffloadRunProcessor.ModeOf(primary).Should().Be(OffloadRunProcessor.TransferMode.StageFiles);
        primary.ObjectKeyFor("db.xbstream").Should().Be("db.xbstream.zst");

        var both = new OffloadRun { S3Destination = Dest(RecipientA) };
        OffloadRunProcessor.SnapshotEncryption(both, "zstd");
        both.ObjectKeyFor("db.xbstream").Should().Be("db.xbstream.zst.age", "compressed first, then encrypted");

        var off = new OffloadRun { S3Destination = Dest(null) };
        OffloadRunProcessor.SnapshotEncryption(off, "none");
        off.Compression.Should().BeNull();
        OffloadRunProcessor.ModeOf(off).Should().Be(OffloadRunProcessor.TransferMode.Plain);

        // A DR copy of a compressed point copies the stored .zst objects unchanged...
        var source = new OffloadRun { Id = 1, S3Destination = Dest(null), Compression = "zstd" };
        var replica = new OffloadRun { SourceOffloadRunId = 1, SourceOffloadRun = source, S3Destination = Dest(null) };
        OffloadRunProcessor.SnapshotEncryption(replica, "none");
        replica.Compression.Should().Be("zstd", "a replica keeps its source's format, whatever the worker setting");
        OffloadRunProcessor.ModeOf(replica).Should().Be(OffloadRunProcessor.TransferMode.CopyStored);

        // ...and to an encrypted site it encrypts those .zst objects as they stream.
        var encrypted = new OffloadRun { SourceOffloadRunId = 1, SourceOffloadRun = source, S3Destination = Dest(RecipientA) };
        OffloadRunProcessor.SnapshotEncryption(encrypted, "zstd");
        OffloadRunProcessor.ModeOf(encrypted).Should().Be(OffloadRunProcessor.TransferMode.EncryptStream);
        encrypted.ObjectKeyFor("db.xbstream").Should().Be("db.xbstream.zst.age");
    }

    [Fact]
    public void Plain_destination_stays_plain()
    {
        var o = new OffloadRun { S3Destination = Dest(null) };
        OffloadRunProcessor.SnapshotEncryption(o);
        o.Encrypted.Should().BeFalse();
        OffloadRunProcessor.ModeOf(o).Should().Be(OffloadRunProcessor.TransferMode.Plain);
    }

    [Fact]
    public void Replica_rules()
    {
        // plaintext source → encrypted target: stream through age
        var a = new OffloadRun { SourceOffloadRunId = 1, SourceOffloadRun = new OffloadRun { Id = 1, S3Destination = Dest(null) }, S3Destination = Dest(RecipientA) };
        OffloadRunProcessor.SnapshotEncryption(a);
        OffloadRunProcessor.ModeOf(a).Should().Be(OffloadRunProcessor.TransferMode.EncryptStream);

        // encrypted source → same recipient: copy ciphertext unchanged
        var b = new OffloadRun { SourceOffloadRunId = 1, SourceOffloadRun = EncryptedSource(RecipientA), S3Destination = Dest(RecipientA) };
        OffloadRunProcessor.SnapshotEncryption(b);
        OffloadRunProcessor.ModeOf(b).Should().Be(OffloadRunProcessor.TransferMode.CopyStored);
        b.EncryptedObjects.Should().ContainSingle(e => e.CiphertextSize == 123);

        // encrypted source → unencrypted target: copy ciphertext, keep the source's metadata
        var c = new OffloadRun { SourceOffloadRunId = 1, SourceOffloadRun = EncryptedSource(RecipientA), S3Destination = Dest(null) };
        OffloadRunProcessor.SnapshotEncryption(c);
        c.Encrypted.Should().BeTrue();
        c.EncryptionRecipient.Should().Be(RecipientA);
        OffloadRunProcessor.ModeOf(c).Should().Be(OffloadRunProcessor.TransferMode.CopyStored);

        // encrypted source → different recipient: refused (re-encryption would need the private key)
        var d = new OffloadRun { SourceOffloadRunId = 1, SourceOffloadRun = EncryptedSource(RecipientA), S3Destination = Dest(RecipientB) };
        var act = () => OffloadRunProcessor.SnapshotEncryption(d);
        act.Should().Throw<Backora.Application.Abstractions.RemoteOperationException>()
            .Where(e => e.FailureClass == FailureClass.Configuration && e.Message.Contains("different key"));
    }

    [Fact]
    public void Restore_state_machine_supports_awaiting_key()
    {
        var now = DateTimeOffset.UtcNow;
        var r = new RestoreRun();
        r.TransitionTo(RestoreStatus.Running, now);
        r.TransitionTo(RestoreStatus.AwaitingKey, now);
        r.IsTerminal.Should().BeFalse();
        r.TransitionTo(RestoreStatus.Running, now);
        r.TransitionTo(RestoreStatus.Verifying, now);
        r.TransitionTo(RestoreStatus.Completed, now);

        var c = new RestoreRun();
        c.TransitionTo(RestoreStatus.Running, now);
        c.TransitionTo(RestoreStatus.AwaitingKey, now);
        c.TransitionTo(RestoreStatus.Cancelling, now);
        c.TransitionTo(RestoreStatus.Cancelled, now);

        var x = new RestoreRun();
        var act = () => x.TransitionTo(RestoreStatus.AwaitingKey, now);
        act.Should().Throw<Backora.Domain.Common.InvalidStateTransitionException>("a key is only requested once the attempt has started");
        ((int)RestoreStatus.AwaitingKey).Should().Be(7);
        Mapping.ToDto(RestoreStatus.AwaitingKey).Should().Be(RestoreStatusDto.AwaitingKey);
    }

    [Fact]
    public void Script_markers_are_summarised_and_counters_dropped()
    {
        var s = OffloadRunProcessor.Summarize("BKO_STAGE 1024\nBKO_ENCRYPTED a/b.tar.gz\nBKO_PROGRESS 5\nBKO_ERROR decryption of x failed\n");
        s.Should().Be("Encrypted a/b.tar.gz → a/b.tar.gz.age (age)\nERROR decryption of x failed\n");
        AgeScripts.LatestCounter("BKO_PROGRESS 10\nnoise\nBKO_PROGRESS 2048\n", "BKO_PROGRESS").Should().Be(2048);
        AgeScripts.ExtractErrors("x\nBKO_ERROR one\nage: error: no identity matched any of the recipients\n").Should().Be("one | age: error: no identity matched any of the recipients");
    }

    [Fact]
    public void Scripts_never_enable_xtrace_and_never_put_the_key_in_argv()
    {
        var restore = AgeScripts.DecryptRestore("age", "rclone", "backora:bucket/p/", "/BackoraDB/job/restore-x");
        restore.Should().Contain("set +x").And.NotContain("set -x").And.NotContain("export KEY");
        restore.Should().Contain("IFS= read -r KEY < ./key.fifo").And.Contain("-i <(printf '%s\\n' \"$KEY\")").And.Contain("unset KEY");
        restore.Should().Contain(".partial").And.Contain("cleanup_restored");
        var offload = TransferScripts.StageAndUpload("/BackoraDB/job", "/BackoraDB/.backora-staging/offload-1-a1", compress: true, agePath: "age", "rclone", "backora:bucket/p/", "--config ./rclone.conf", 100);
        offload.Should().Contain("set +x").And.NotContain("set -x");
        offload.Should().Contain("| zstd -T0 -1 -q -c | \"$AGE\" -e -R ./recipients.txt |").And.Contain("age-encryption.org/v1").And.Contain("copy \"$STAGE\"");
        offload.Should().Contain("tarcat", "a backup folder is packed into one archive");
        var decrypt = AgeScripts.DecryptRestore("age", "rclone", "backora:bucket/p/", "/BackoraDB/job/restore-x", decompress: true);
        decrypt.Should().Contain("objects.txt").And.Contain("| zstd -dc -q | tar -x -C \"$TARGET\"").And.Contain("| zstd -dc -q > \"$TARGET/$name.partial\"");
    }
}
