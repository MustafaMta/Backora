using System.Globalization;
using Backora.Application.Encryption;
using Backora.Domain.Execution;

namespace Backora.Application.Execution;

/// <summary>
/// Primary uploads that are compressed (zstd, the default) and/or encrypted (age, customer-key destinations) are
/// produced on the worker host, never on the database servers. Each artifact is read once from the NFS share with
/// parallel read-ahead, piped through <c>zstd -T0 -1</c> and/or <c>age</c>, and written to a staging folder on the share
/// (outside every job folder) while its SHA-256 and MD5 are computed in the same pass; the staged objects are then
/// uploaded with <c>rclone copy</c> (resumable, checksummed) and verified against storage without reading them again.
/// The backup itself on the share is never changed. Plaintext never reaches storage when the destination is encrypted.
/// <para>Exit codes: 41 storage/read/write error · 43 encryption failure · 44 compression failure.</para>
/// </summary>
/// <summary>One object the worker produces for upload: a single artifact, or a backup folder packed into one archive.</summary>
/// <param name="Name">Stored name: the artifact path, or "&lt;folder&gt;/" for a packed folder.</param>
/// <param name="Members">For a packed folder, the artifact paths it holds (relative to the job folder).</param>
public sealed record StageItem(bool Packed, long Size, string Name, string Key, IReadOnlyList<string> Members)
{
    /// <summary>
    /// The objects for a compressed upload: every backup folder (PostgreSQL base.tar + pg_wal.tar + backup_manifest)
    /// becomes one &lt;folder&gt;.tar.zst; a single-file backup (.xbstream, .sql) becomes one &lt;file&gt;.zst.
    /// </summary>
    public static List<StageItem> For(Domain.Backups.OffloadRun offload, IEnumerable<(string RelativePath, long SizeBytes)> artifacts)
    {
        var items = new List<StageItem>();
        foreach (var group in artifacts.GroupBy(a => a.RelativePath.Contains('/') ? a.RelativePath[..a.RelativePath.IndexOf('/')] + "/" : a.RelativePath, StringComparer.Ordinal))
        {
            var packed = Domain.Backups.OffloadRun.IsPackedFolder(group.Key);
            items.Add(new StageItem(packed, group.Sum(a => a.SizeBytes), group.Key, offload.ObjectKeyFor(group.Key),
                packed ? group.Select(a => a.RelativePath).Order(StringComparer.Ordinal).ToList() : []));
        }
        return items.OrderBy(i => i.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>"packed|file TAB size TAB name TAB key TAB members-file" lines for the staging script.</summary>
    public static string ItemsFile(IReadOnlyList<StageItem> items) =>
        string.Concat(items.Select((it, i) => $"{(it.Packed ? "packed" : "file")}\t{it.Size.ToString(CultureInfo.InvariantCulture)}\t{it.Name}\t{it.Key}\t{(it.Packed ? $"./members/{i}.txt" : "-")}\n"));

    /// <summary>"packed|file TAB key TAB name" lines for a restore (what to download and how to unpack it).</summary>
    public static string RestoreFile(IEnumerable<(string Name, string Key)> stored) =>
        string.Concat(stored.Select(s => $"{(Domain.Backups.OffloadRun.IsPackedFolder(s.Name) ? "packed" : "file")}\t{s.Key}\t{s.Name}\n"));
}

public static class TransferScripts
{
    public const int ExitCompress = 44;
    public const string ItemsList = "stage.items";
    public const string RestoreList = "objects.txt";
    /// <summary>Folder on the NFS share (next to the job folders) that holds staged objects during an upload.</summary>
    public const string StagingRoot = ".backora-staging";
    /// <summary>The staged objects' sizes and checksums, written by the staging script into the work folder.</summary>
    public const string StageManifest = "stage.tsv";

    /// <param name="workDirectory">The upload's work folder on the worker (holds hasher.py, files lists, rclone.conf).</param>
    /// <param name="sourceDirectory">The job folder on the share (the backup as written).</param>
    /// <param name="stagingDirectory">Where the staged objects go (on the share; created 0700).</param>
    /// <param name="compress">Pipe through zstd (all cores, level 1).</param>
    /// <param name="agePath">age binary when the destination is encrypted, else null.</param>
    public static string StageAndUpload(string sourceDirectory, string stagingDirectory, bool compress, string? agePath, string rclonePath, string rcloneTarget, string rcloneFlags, long totalBytes)
    {
        var stage = compress ? "compress" : "encrypt";
        string Pipeline(string reader) => reader
            + (compress ? " | zstd -T0 -1 -q -c" : "")
            + (agePath is null ? "" : " | \"$AGE\" -e -R ./" + AgeScripts.RecipientsFile)
            + " | python3 ./hasher.py tee \"$out\"";
        var fileReader = "python3 ./hasher.py cat \"$SRC/$name\" ./progress " + stage + " \"$TOTAL\" \"$DONE\"";
        // A whole backup folder becomes one archive: one tar stream, read with parallel read-ahead, one progress step.
        var folderReader = "python3 ./hasher.py tarcat \"$SRC\" \"$members\" ./progress " + stage + " \"$TOTAL\" \"$DONE\"";
        // The exit status of every stage of the pipeline, in order: read/pack, [zstd], [age], write.
        var checks = new List<string> { $"[ \"${{st[0]}}\" -eq 0 ] || {{ echo \"BKO_ERROR could not read $name from the share\"; exit {AgeScripts.ExitStorage}; }}" };
        var i = 1;
        if (compress) checks.Add($"[ \"${{st[{i++}]}}\" -eq 0 ] || {{ echo \"BKO_ERROR zstd could not compress $name\"; exit {ExitCompress}; }}");
        if (agePath is not null) checks.Add($"[ \"${{st[{i++}]}}\" -eq 0 ] || {{ echo \"BKO_ERROR age could not encrypt $name\"; exit {AgeScripts.ExitEncrypt}; }}");
        checks.Add($"[ \"${{st[{i}]}}\" -eq 0 ] || {{ echo \"BKO_ERROR could not write the staged copy of $name\"; exit {AgeScripts.ExitStorage}; }}");

        return $$"""
            #!/bin/bash
            # Generated by Backora. Contains no secrets. Never run with xtrace.
            set +x
            set -u -o pipefail
            umask 077
            cd "$(dirname "$0")" || exit {{AgeScripts.ExitStorage}}
            SRC={{ShellQuote.Quote(sourceDirectory.TrimEnd('/'))}}
            STAGE={{ShellQuote.Quote(stagingDirectory.TrimEnd('/'))}}
            RCLONE={{ShellQuote.Quote(rclonePath)}}
            {{(agePath is null ? "" : "AGE=" + ShellQuote.Quote(agePath))}}
            TOTAL={{totalBytes.ToString(CultureInfo.InvariantCulture)}}; DONE=0
            mkdir -p -m 700 "$STAGE" || { echo "BKO_ERROR cannot create the staging folder $STAGE"; exit {{AgeScripts.ExitStorage}}; }
            : > ./{{StageManifest}}.tmp
            # One object per line: a single backup file, or a whole backup folder packed into one archive.
            while IFS=$'\t' read -r kind size name key members <&3; do
              out="$STAGE/$key"
              mkdir -p -- "$(dirname -- "$out")" || exit {{AgeScripts.ExitStorage}}
              if [ "$kind" = packed ]; then
                result=$( {{Pipeline(folderReader)}}; echo "RC ${PIPESTATUS[*]}" )
              else
                [ -f "$SRC/$name" ] || { echo "BKO_ERROR $name is missing from the share"; exit {{AgeScripts.ExitStorage}}; }
                result=$( {{Pipeline(fileReader)}}; echo "RC ${PIPESTATUS[*]}" )
              fi
              st=($(printf '%s\n' "$result" | sed -n 's/^RC //p'))
              {{string.Join("\n              ", checks)}}
              {{(agePath is null ? "" : $"[ \"$(head -c 21 \"$out\")\" = {ShellQuote.Quote(AgeKeys.HeaderLine)} ] || {{ echo \"BKO_ERROR $key does not start with the age v1 header\"; exit {AgeScripts.ExitEncrypt}; }}")}}
              read -r sha md5 n <<< "$(printf '%s\n' "$result" | grep -v '^RC ' | tail -n 1)"
              printf '%s\t%s %s\t%s\t%s\n' "$sha" "$n" "$(stat -c '%Y' "$out")" "$(printf '%s' "$name" | base64 | tr -d '\n')" "$md5" >> ./{{StageManifest}}.tmp
              DONE=$(( DONE + size ))
              echo "BKO_STAGED $name"
            done 3< ./{{ItemsList}}
            echo END >> ./{{StageManifest}}.tmp && mv -f ./{{StageManifest}}.tmp ./{{StageManifest}}
            echo "BKO_UPLOAD"
            exec "$RCLONE" copy "$STAGE" {{ShellQuote.Quote(rcloneTarget)}} --files-from ./{{AgeScripts.EncryptedFilesList}} --checksum {{rcloneFlags}}

            """;
    }

    /// <summary>
    /// Restore of compressed and/or encrypted objects: each object is streamed <c>rclone cat | [age -d] | zstd -dc</c>
    /// into &lt;target&gt;/&lt;rel&gt;.partial and renamed on success; any failure removes every file this script wrote.
    /// Without encryption no key is needed; with it, the key arrives through the FIFO exactly as for
    /// <see cref="AgeScripts.DecryptRestore"/>. The result is then verified against the SHA-256 manifest as usual.
    /// </summary>
    public static string DecompressRestore(string rclonePath, string rcloneSource, string targetDirectory) => $$"""
        #!/bin/bash
        # Generated by Backora. Contains no secrets. Never run with xtrace.
        set +x
        set -u -o pipefail
        umask 077
        cd "$(dirname "$0")" || exit {{AgeScripts.ExitStorage}}
        RCLONE={{ShellQuote.Quote(rclonePath)}}
        SRC={{ShellQuote.Quote(rcloneSource.TrimEnd('/') + "/")}}
        TARGET={{ShellQuote.Quote(targetDirectory.TrimEnd('/'))}}
        cleanup_restored() { while IFS= read -r r <&4; do rm -f -- "$TARGET/$r" "$TARGET/$r.partial"; done 4< ./{{AgeScripts.FilesList}}; }
        ( prev=; while sleep 5; do cur=$(( $(du -sk "$TARGET" 2>/dev/null | cut -f1) * 1024 )); [ "$cur" != "$prev" ] && echo "BKO_PROGRESS $cur"; prev=$cur; done ) & TICK=$!
        rc=0
        while IFS=$'\t' read -r kind key name <&3; do
          if [ "$kind" = packed ]; then
            # A backup folder packed into one archive: unpacked into the target (member paths start with the folder).
            "$RCLONE" cat --config ./rclone.conf --contimeout 30s --timeout 300s --low-level-retries 10 -- "$SRC$key" | zstd -dc -q | tar -x -C "$TARGET" --no-same-owner --no-same-permissions
            st=("${PIPESTATUS[@]}")
            if [ "${st[0]}" -ne 0 ] && [ "${st[0]}" -ne 141 ]; then echo "BKO_ERROR could not download $key (rclone exit ${st[0]})"; rc={{AgeScripts.ExitStorage}}; break; fi
            if [ "${st[1]}" -ne 0 ]; then echo "BKO_ERROR $key could not be decompressed (corrupted object)"; rc={{ExitCompress}}; break; fi
            if [ "${st[2]}" -ne 0 ]; then echo "BKO_ERROR $key could not be unpacked"; rc={{ExitCompress}}; break; fi
          else
            mkdir -p -- "$(dirname -- "$TARGET/$name")" || { rc={{AgeScripts.ExitStorage}}; break; }
            "$RCLONE" cat --config ./rclone.conf --contimeout 30s --timeout 300s --low-level-retries 10 -- "$SRC$key" | zstd -dc -q > "$TARGET/$name.partial"
            st=("${PIPESTATUS[@]}")
            if [ "${st[0]}" -ne 0 ] && [ "${st[0]}" -ne 141 ]; then echo "BKO_ERROR could not download $key (rclone exit ${st[0]})"; rc={{AgeScripts.ExitStorage}}; break; fi
            if [ "${st[1]}" -ne 0 ]; then echo "BKO_ERROR $key could not be decompressed (corrupted object)"; rc={{ExitCompress}}; break; fi
            mv -f -- "$TARGET/$name.partial" "$TARGET/$name" || { rc={{AgeScripts.ExitStorage}}; break; }
          fi
          echo "BKO_FILE_DONE $name"
        done 3< ./{{RestoreList}}
        kill "$TICK" 2>/dev/null
        if [ "$rc" -ne 0 ]; then cleanup_restored; echo "BKO_ERROR restored files were removed"; exit "$rc"; fi
        echo "BKO_PROGRESS $(( $(du -sk "$TARGET" | cut -f1) * 1024 ))"
        exit 0

        """;
}
