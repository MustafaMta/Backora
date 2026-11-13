using System.Globalization;
using Backora.Application.Abstractions;
using Backora.Domain;
using Backora.Application.Execution;
using Backora.Domain.Execution;

namespace Backora.Application.Encryption;

/// <summary>
/// Shell scripts executed (detached) on the worker host for encrypted transfers. They contain only public
/// data (paths, the recipient file name); credentials live in the 0600 rclone.conf of the 0700 work dir and
/// the restore key arrives at runtime through a FIFO. Scripts never enable xtrace, so no variable is echoed.
///
/// Exit codes: 0 success · 40 no key received · 41 storage/download/upload error · 42 decryption or
/// authentication failure (corrupted/tampered ciphertext or wrong key) · 43 encryption failure.
/// </summary>
public static class AgeScripts
{
    public const int ExitNoKey = 40;
    public const int ExitStorage = 41;
    public const int ExitDecrypt = 42;
    public const int ExitEncrypt = 43;

    public const string FilesList = "files.txt";
    public const string EncryptedFilesList = "files.enc.txt";
    public const string RecipientsFile = "recipients.txt";
    public const string StageDirectory = "stage";
    public const string KeyFifo = "key.fifo";
    public const string KeyReceivedMarker = "key_received";

    /// <summary>Rejects relative paths that cannot be carried safely in a newline-separated list.</summary>
    public static void EnsureListable(IEnumerable<string> relativePaths)
    {
        foreach (var p in relativePaths)
        {
            if (p.Length == 0 || p.Contains('\n', StringComparison.Ordinal) || p.Contains('\r', StringComparison.Ordinal) || p.Contains('\0', StringComparison.Ordinal)
                || p.StartsWith('/') || p.Split('/').Any(s => s is ".." or "."))
            {
                throw new RemoteOperationException(FailureClass.Configuration, $"Artifact path '{p.ReplaceLineEndings(" ")}' cannot be processed safely for encryption.");
            }
        }
    }

    /// <summary>Restore loop of an encrypted point: one &lt;rel&gt;.age object per artifact, decrypted into place.</summary>
    private static string PlainDecryptLoop => $$"""
        while IFS= read -r rel <&3; do
          mkdir -p -- "$(dirname -- "$TARGET/$rel")" || { rc={{ExitStorage}}; break; }
          "$RCLONE" cat --config ./rclone.conf --contimeout 30s --timeout 300s --low-level-retries 10 -- "$SRC$rel.age" | "$AGE" -d -i <(printf '%s\n' "$KEY") -o "$TARGET/$rel.partial"
          st=("${PIPESTATUS[@]}")
          if [ "${st[0]}" -ne 0 ] && [ "${st[0]}" -ne 141 ]; then echo "BKO_ERROR could not download $rel.age (rclone exit ${st[0]})"; rc={{ExitStorage}}; break; fi
          if [ "${st[1]}" -ne 0 ]; then echo "BKO_ERROR decryption of $rel.age failed: the ciphertext could not be authenticated with the supplied key (corrupted or tampered object)"; rc={{ExitDecrypt}}; break; fi
          mv -f -- "$TARGET/$rel.partial" "$TARGET/$rel" || { rc={{ExitStorage}}; break; }
          echo "BKO_FILE_DONE $rel"
        done 3< ./{{FilesList}}
        """;

    /// <summary>
    /// Restore loop of a compressed and encrypted point (objects listed in objects.txt): each object decrypted, then
    /// decompressed; a packed backup folder (&lt;folder&gt;.tar.zst.age) is unpacked into the target.
    /// </summary>
    private static string CompressedDecryptLoop => $$"""
        while IFS=$'\t' read -r kind key name <&3; do
          if [ "$kind" = packed ]; then
            "$RCLONE" cat --config ./rclone.conf --contimeout 30s --timeout 300s --low-level-retries 10 -- "$SRC$key" | "$AGE" -d -i <(printf '%s\n' "$KEY") | zstd -dc -q | tar -x -C "$TARGET" --no-same-owner --no-same-permissions
          else
            mkdir -p -- "$(dirname -- "$TARGET/$name")" || { rc={{ExitStorage}}; break; }
            "$RCLONE" cat --config ./rclone.conf --contimeout 30s --timeout 300s --low-level-retries 10 -- "$SRC$key" | "$AGE" -d -i <(printf '%s\n' "$KEY") | zstd -dc -q > "$TARGET/$name.partial"
          fi
          st=("${PIPESTATUS[@]}")
          if [ "${st[0]}" -ne 0 ] && [ "${st[0]}" -ne 141 ]; then echo "BKO_ERROR could not download $key (rclone exit ${st[0]})"; rc={{ExitStorage}}; break; fi
          if [ "${st[1]}" -ne 0 ]; then echo "BKO_ERROR decryption of $key failed: the ciphertext could not be authenticated with the supplied key (corrupted or tampered object)"; rc={{ExitDecrypt}}; break; fi
          if [ "${st[2]}" -ne 0 ]; then echo "BKO_ERROR $key decrypted but could not be decompressed (corrupted object)"; rc={{TransferScripts.ExitCompress}}; break; fi
          if [ "$kind" = packed ]; then
            if [ "${st[3]}" -ne 0 ]; then echo "BKO_ERROR $key could not be unpacked"; rc={{TransferScripts.ExitCompress}}; break; fi
          else
            mv -f -- "$TARGET/$name.partial" "$TARGET/$name" || { rc={{ExitStorage}}; break; }
          fi
          echo "BKO_FILE_DONE $name"
        done 3< ./{{TransferScripts.RestoreList}}
        """;

    private const string Prologue = """
        #!/bin/bash
        # Generated by Backora. Contains no secrets. Never run with xtrace.
        set +x
        set -u -o pipefail
        umask 077
        cd "$(dirname "$0")" || exit 41

        """;

    /// <summary>
    /// Replica of a plaintext point to an encrypted destination: stream each object from the source through
    /// age into ./stage (no plaintext is written anywhere), then upload the ciphertext.
    /// </summary>
    public static string StreamEncryptAndUpload(string agePath, string rclonePath, string rcloneSource, string rcloneTarget, string rcloneFlags) =>
        Prologue + $$"""
        AGE={{ShellQuote.Quote(agePath)}}
        RCLONE={{ShellQuote.Quote(rclonePath)}}
        SRC={{ShellQuote.Quote(rcloneSource.TrimEnd('/') + "/")}}
        mkdir -p -m 700 ./{{StageDirectory}} || exit {{ExitStorage}}
        {{Ticker("./" + StageDirectory, "BKO_STAGE")}}
        while IFS= read -r rel <&3; do
          mkdir -p -- "./{{StageDirectory}}/$(dirname -- "$rel")" || exit {{ExitStorage}}
          "$RCLONE" cat --config ./rclone.conf --contimeout 30s --timeout 300s --low-level-retries 10 -- "$SRC$rel" | "$AGE" -e -R ./{{RecipientsFile}} -o "./{{StageDirectory}}/$rel.age"
          st=("${PIPESTATUS[@]}")
          if [ "${st[0]}" -ne 0 ]; then echo "BKO_ERROR could not read $rel from the source (rclone exit ${st[0]})"; exit {{ExitStorage}}; fi
          if [ "${st[1]}" -ne 0 ]; then echo "BKO_ERROR age could not encrypt $rel"; exit {{ExitEncrypt}}; fi
          [ "$(head -c 21 "./{{StageDirectory}}/$rel.age")" = "{{AgeKeys.HeaderLine}}" ] || { echo "BKO_ERROR $rel.age does not start with the age v1 header"; exit {{ExitEncrypt}}; }
          echo "BKO_ENCRYPTED $rel"
        done 3< ./{{FilesList}}
        kill "$TICK" 2>/dev/null
        echo "BKO_UPLOAD"
        exec "$RCLONE" copy ./{{StageDirectory}} {{ShellQuote.Quote(rcloneTarget)}} --files-from ./{{EncryptedFilesList}} --checksum {{rcloneFlags}}

        """;

    /// <summary>
    /// Encrypted restore. Blocks reading the identity from ./key.fifo (the key exists only in this bash
    /// process's memory: never exported, never in argv, never in a file), then streams each object
    /// <c>rclone cat | age -d</c> into &lt;target&gt;/&lt;rel&gt;.partial and renames it on success. Any failure removes
    /// every file this script wrote (partial or complete) — there is no plaintext fallback.
    /// </summary>
    public static string DecryptRestore(string agePath, string rclonePath, string rcloneSource, string targetDirectory, bool decompress = false) =>
        Prologue + $$"""
        AGE={{ShellQuote.Quote(agePath)}}
        RCLONE={{ShellQuote.Quote(rclonePath)}}
        SRC={{ShellQuote.Quote(rcloneSource.TrimEnd('/') + "/")}}
        TARGET={{ShellQuote.Quote(targetDirectory.TrimEnd('/'))}}
        cleanup_restored() { while IFS= read -r r <&4; do rm -f -- "$TARGET/$r" "$TARGET/$r.partial"; done 4< ./{{FilesList}}; }
        KEY=
        if ! IFS= read -r KEY < ./{{KeyFifo}}; then KEY=; echo "BKO_ERROR no decryption key was received"; exit {{ExitNoKey}}; fi
        rm -f ./{{KeyFifo}}
        : > ./{{KeyReceivedMarker}}
        case "$KEY" in AGE-SECRET-KEY-1*) ;; *) KEY=; echo "BKO_ERROR the supplied value is not an age identity"; exit {{ExitDecrypt}};; esac
        echo "BKO_KEY_RECEIVED"
        {{Ticker("$TARGET", "BKO_PROGRESS")}}
        rc=0
        {{(decompress ? CompressedDecryptLoop : PlainDecryptLoop)}}
        KEY=
        unset KEY
        kill "$TICK" 2>/dev/null
        if [ "$rc" -ne 0 ]; then cleanup_restored; echo "BKO_ERROR restored files were removed"; exit "$rc"; fi
        echo "BKO_PROGRESS $(( $(du -sk "$TARGET" | cut -f1) * 1024 ))"
        exit 0

        """;

    /// <summary>Background progress ticker: prints the byte size of <paramref name="path"/> whenever it changes.</summary>
    private static string Ticker(string path, string tag) =>
        $"( prev=; while sleep 5; do cur=$(( $(du -sk \"{path}\" 2>/dev/null | cut -f1) * 1024 )); [ \"$cur\" != \"$prev\" ] && echo \"{tag} $cur\"; prev=$cur; done ) & TICK=$!; trap 'kill \"$TICK\" 2>/dev/null' EXIT";

    /// <summary>Parses the latest BKO_PROGRESS / BKO_STAGE byte count in a log chunk.</summary>
    public static long? LatestCounter(string chunk, string tag)
    {
        long? latest = null;
        var prefix = tag + " ";
        foreach (var line in chunk.Split('\n'))
        {
            var l = line.Trim();
            if (l.StartsWith(prefix, StringComparison.Ordinal) && long.TryParse(l[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var v)) latest = v;
        }
        return latest;
    }

    /// <summary>Extracts BKO_ERROR messages (and age's own error lines) from a script log.</summary>
    public static string ExtractErrors(string log, int max = 5)
    {
        var errors = log.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("BKO_ERROR ", StringComparison.Ordinal) || l.StartsWith("age: error:", StringComparison.Ordinal))
            .Select(l => l.StartsWith("BKO_ERROR ", StringComparison.Ordinal) ? l[10..] : l)
            .TakeLast(max);
        return string.Join(" | ", errors);
    }
}
