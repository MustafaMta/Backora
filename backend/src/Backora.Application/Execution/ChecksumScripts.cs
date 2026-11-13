using System.Globalization;
using Backora.Domain.Execution;

namespace Backora.Application.Execution;

/// <summary>
/// The long checksum steps (verifying a backup's files, checking a copy against storage) run detached on the worker,
/// like the backup itself, and report live progress in <c>./progress</c> ("done total stage", the same format the
/// templates use).
/// <para>
/// Speed: a single sequential reader gets ~260 MB/s from the NFS share while 8 parallel readers get ~1 GB/s, and the CPU
/// hashes SHA-256 at ~1 GB/s (SHA-NI). So files are hashed by a small helper that reads each file with parallel
/// read-ahead and feeds the hashes in order — the same SHA-256 as <c>sha256sum</c>, plus the MD5 in the same pass so a
/// copy can be checked against the hash object storage keeps instead of reading the source again. Without python3
/// on the worker the scripts fall back to <c>sha256sum</c> (SHA-256 only).
/// </para>
/// </summary>
public static class ChecksumScripts
{
    /// <summary>Files smaller than this are hashed inline by the fallback (no polling for thousands of small files).</summary>
    public const long PolledHashBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Worker helper (python3), four modes:
    /// <list type="bullet">
    /// <item><c>hash PROGRESS STAGE TOTAL LIST OUT [AGE_HEADER]</c> — LIST: "size TAB path TAB name" lines; OUT:
    /// "sha256 TAB size mtime TAB base64(name) TAB md5" lines then END ("BADHEADER" instead of the hash when AGE_HEADER
    /// is given and the file does not start with it).</item>
    /// <item><c>cat FILE PROGRESS STAGE TOTAL DONE</c> — FILE to stdout with parallel read-ahead; progress DONE + bytes.</item>
    /// <item><c>tee OUT</c> — stdin to OUT (fsynced); prints "sha256 md5 size" of what it wrote.</item>
    /// <item><c>tarcat BASE MEMBERS PROGRESS STAGE TOTAL DONE</c> — the files listed in MEMBERS (relative to BASE) as one
    /// PAX tar stream to stdout, each read with parallel read-ahead; progress DONE + bytes.</item>
    /// </list>
    /// </summary>
    public const string Hasher = """
        import base64, collections, concurrent.futures as cf, hashlib, os, sys, time
        CHUNK = 16 << 20; AHEAD = 12
        readers = cf.ThreadPoolExecutor(8)
        class Progress:
            def __init__(self, path, stage, total, done=0):
                self.path, self.stage, self.total, self.done, self.last = path, stage, total, done, 0.0
            def add(self, n, force=False):
                self.done += n
                now = time.monotonic()
                if force or now - self.last >= 1:
                    with open(self.path + ".tmp", "w") as f: f.write(f"{self.done} {self.total} {self.stage}\n")
                    os.replace(self.path + ".tmp", self.path); self.last = now
        def chunks(path):
            # The file in order, read by parallel readers ahead of the consumer (one reader gets a fraction of NFS speed).
            fd = os.open(path, os.O_RDONLY)
            try:
                size, off, pending = os.fstat(fd).st_size, 0, collections.deque()
                while off < size or pending:
                    while off < size and len(pending) < AHEAD:
                        pending.append((off, readers.submit(os.pread, fd, CHUNK, off))); off += CHUNK
                    start, fut = pending.popleft(); b = fut.result()
                    yield b
                    if len(b) < min(CHUNK, size - start):  # the file shrank while it was read
                        for _, f in pending: f.cancel()
                        pending.clear(); off = start + len(b); break
                while True:  # and anything appended meanwhile
                    b = os.pread(fd, CHUNK, off)
                    if not b: break
                    yield b; off += len(b)
            finally:
                os.close(fd)
        def hash_mode(progress, stage, total, list_file, out_file, header=None):
            p = Progress(progress, stage, int(total)); p.add(0, True)
            second = cf.ThreadPoolExecutor(1)
            header = header.encode() if header else None
            def b64(name): return base64.b64encode(name.encode("utf-8", "surrogateescape")).decode()
            with open(list_file, encoding="utf-8", errors="surrogateescape") as lst, open(out_file + ".tmp", "w") as out:
                for line in lst:
                    parts = line.rstrip("\n").split("\t")
                    if len(parts) != 3: continue
                    size, path, name = int(parts[0]), parts[1], parts[2]
                    try:
                        if header is not None:
                            with open(path, "rb") as f: ok = f.read(len(header)) == header
                            if not ok:
                                st = os.stat(path); p.add(size)
                                out.write(f"BADHEADER\t{st.st_size} {int(st.st_mtime)}\t{b64(name)}\t\n"); continue
                        sha, md5 = hashlib.sha256(), hashlib.md5()
                        for b in chunks(path):
                            m = second.submit(md5.update, b); sha.update(b); m.result(); p.add(len(b))
                        st = os.stat(path)
                    except OSError:
                        continue  # vanished or unreadable: left out, as sha256sum would
                    out.write(f"{sha.hexdigest()}\t{st.st_size} {int(st.st_mtime)}\t{b64(name)}\t{md5.hexdigest()}\n")
                out.write("END\n")
            os.replace(out_file + ".tmp", out_file); p.add(0, True)
        def cat_mode(path, progress, stage, total, done):
            p = Progress(progress, stage, int(total), int(done))
            out = sys.stdout.buffer
            for b in chunks(path):
                out.write(b); p.add(len(b))
            out.flush(); p.add(0, True)
        def tee_mode(out_file):
            sha, md5, n = hashlib.sha256(), hashlib.md5(), 0
            second = cf.ThreadPoolExecutor(1)
            with open(out_file, "wb") as out:
                while True:
                    b = sys.stdin.buffer.read(4 << 20)
                    if not b: break
                    m = second.submit(md5.update, b); out.write(b); sha.update(b); m.result(); n += len(b)
                out.flush(); os.fsync(out.fileno())  # on stable storage before the caller removes the original
            print(sha.hexdigest(), md5.hexdigest(), n)
        def tarcat_mode(base, members_file, progress, stage, total, done):
            # A whole backup folder as one tar stream (PAX headers: no size or name limits), each file read with parallel
            # read-ahead. Headers come from tarfile; the data goes straight to stdout. (tarfile's own stream mode re-slices
            # a 10 KB record buffer for every record, which makes large writes quadratic: ~15 MB/s instead of ~1 GB/s.)
            import tarfile
            p = Progress(progress, stage, int(total), int(done))
            out, written = sys.stdout.buffer, 0
            def put(b):
                nonlocal written
                out.write(b); written += len(b)
            with open(members_file, encoding="utf-8", errors="surrogateescape") as f:
                members = [m for m in f.read().split("\n") if m]
            for rel in members:
                path = os.path.join(base, rel)
                st = os.stat(path)
                info = tarfile.TarInfo(rel)
                info.size, info.mtime, info.mode = st.st_size, int(st.st_mtime), st.st_mode & 0o777
                info.uid = info.gid = 0; info.uname = info.gname = ""
                put(info.tobuf(tarfile.PAX_FORMAT, "utf-8", "surrogateescape"))
                left = st.st_size
                for b in chunks(path):
                    if len(b) > left: b = b[:left]  # grew while it was read: the header has the size it had
                    put(b); left -= len(b); p.add(len(b))
                    if left == 0: break
                if left:
                    sys.exit(f"{rel} shrank while it was read")
                if st.st_size % 512: put(b"\0" * (512 - st.st_size % 512))
            put(b"\0" * 1024)  # end of archive
            if written % tarfile.RECORDSIZE: put(b"\0" * (tarfile.RECORDSIZE - written % tarfile.RECORDSIZE))
            out.flush(); p.add(0, True)
        mode, args = sys.argv[1], sys.argv[2:]
        {"hash": hash_mode, "cat": cat_mode, "tee": tee_mode, "tarcat": tarcat_mode}[mode](*args)
        """;

    /// <summary>Bash: progress writer, and the fallback sha256sum that reports how far into a large file it is.</summary>
    /// <remarks>Expects W (the script's own working folder, absolute) to be set first.</remarks>
    private static string Helpers() => $$"""
        PROGRESS="$W/progress"
        progress() { printf '%s %s %s\n' "$1" "$2" "$3" > "$PROGRESS.tmp" && mv -f "$PROGRESS.tmp" "$PROGRESS"; }
        fast_hasher() { [ -z "${BACKORA_NO_PYTHON:-}" ] && command -v python3 >/dev/null 2>&1 && [ -f "$W/hasher.py" ]; }
        # hash_file FILE SIZE DONE TOTAL STAGE — prints the SHA-256 of FILE; reports DONE + bytes read so far meanwhile.
        hash_file() {
            if [ "$2" -lt {{PolledHashBytes.ToString(CultureInfo.InvariantCulture)}} ]; then sha256sum "$1" | cut -d' ' -f1; return; fi
            sha256sum "$1" > "$PROGRESS.hash" & local h=$! r
            while kill -0 "$h" 2>/dev/null; do
                r=$(awk '/^rchar:/{print $2}' "/proc/$h/io" 2>/dev/null); progress $(( $3 + ${r:-0} )) "$4" "$5"; sleep 2
            done
            wait "$h" || return 1
            cut -d' ' -f1 "$PROGRESS.hash"
        }
        # hash_list LIST OUT STAGE [AGE_HEADER] — LIST: "size TAB path TAB name"; OUT as the python hasher writes it.
        hash_list() {
            local total; total=$(awk -F'\t' '{ s += $1 } END { printf "%.0f", s }' "$1")
            if fast_hasher; then python3 "$W/hasher.py" hash "$PROGRESS" "$3" "$total" "$1" "$2" ${4:+"$4"}; return; fi
            local done=0 size f name st h
            progress 0 "$total" "$3"; : > "$2.tmp"
            while IFS=$'\t' read -r size f name; do
                [ -f "$f" ] || continue
                st=$(stat -c '%s %Y' "$f") || continue
                if [ -n "${4:-}" ] && [ "$(head -c ${#4} "$f")" != "$4" ]; then h=BADHEADER
                else h=$(hash_file "$f" "$size" "$done" "$total" "$3") || continue; fi
                printf '%s\t%s\t%s\t\n' "$h" "$st" "$(printf '%s' "$name" | base64 | tr -d '\n')" >> "$2.tmp"
                done=$(( done + size )); progress "$done" "$total" "$3"
            done < "$1"
            echo END >> "$2.tmp" && mv -f "$2.tmp" "$2"
        }
        """;

    /// <summary>Compress-step record (./compress.log): "COMPRESSED name out raw-bytes out-bytes seconds", "SKIPPED name reason" or "FAILED name reason".</summary>
    public const string CompressLog = "compress.log";
    /// <summary>Suffix of a compressed file being written; removed when the step fails, is stopped or starts again.</summary>
    public const string CompressPartialSuffix = ".backora-partial";
    /// <summary>A raw backup whose compressed file is complete is renamed with this suffix, then removed.</summary>
    public const string RawDeleteSuffix = ".backora-delete";

    /// <summary>Names that are compressed already (or encrypted, so incompressible): kept as they are.</summary>
    private const string Incompressible = "*.zst|*.gz|*.tgz|*.bz2|*.xz|*.lz4|*.zip|*.7z|*.gpg|*.age|*.pgp|*.enc";

    /// <summary>
    /// The compress step, run in the job folder before the checksums: every output of this run becomes ONE file on the
    /// share — a file F becomes F.zst, a backup folder D (all of whose files this run wrote) one D.tar.zst — read with
    /// parallel read-ahead (<see cref="Hasher"/> cat / tarcat), compressed by zstd on all of the worker's cores and written
    /// through the hasher's tee, so the SHA-256 and MD5 of the compressed file are known without reading it again (they
    /// go to ./known.txt). The raw output is removed only once the compressed file is complete and on stable storage
    /// (written as NAME.backora-partial, fsynced, size checked, renamed; the raw output is renamed to NAME.backora-delete
    /// before it is deleted, so a step that starts again after a crash finishes that delete and never compresses twice).
    /// A file that cannot be compressed (no zstd, not enough space, an error) stays as it is: the backup is still good.
    /// Progress: stage "compress", bytes read of the raw output against its total.
    /// </summary>
    private static string CompressPhase(int sinceMinutes, long? notBeforeEpoch) => $$"""
        # ---- compress: each output of this run becomes ONE .zst file (a folder: one .tar.zst) ----
        : > "$W/{{CompressLog}}"
        clog() { printf '%s\t%s\t%s\n' "$1" "$2" "$3" >> "$W/{{CompressLog}}"; }
        # Left by an earlier attempt of this step (a crash or a restart): finish removing raw output whose compressed file
        # is complete, and drop compressed files that were never completed.
        find . -maxdepth 1 -name '*{{RawDeleteSuffix}}' -exec rm -rf -- {} + 2>/dev/null
        find . -maxdepth 1 -name '*{{CompressPartialSuffix}}' -exec rm -f -- {} + 2>/dev/null
        if ! command -v zstd >/dev/null 2>&1; then
            clog SKIPPED '*' "zstd is not installed on the worker"
        else
            # Candidates: files written since the run started.
            find . -path './restore-*' -prune -o -type f {{(notBeforeEpoch is { } nb ? "-newermt @" + nb.ToString(CultureInfo.InvariantCulture) : "-mmin -" + sinceMinutes.ToString(CultureInfo.InvariantCulture))}} -printf '%s\t%P\n' > "$W/c.files"
            # sig ITEM — sizes and times of everything in ITEM: compared after compressing, to catch a file still being written.
            sig() { if [ -d "./$1" ]; then find "./$1" -type f -printf '%s %T@ %P\n' | LC_ALL=C sort | md5sum; else stat -c '%s %Y' "./$1"; fi; }
            # One item per output: a file directly in the job folder, or a folder (the first part of a nested path).
            awk -F'\t' '{ i = index($2, "/"); k = i ? substr($2, 1, i) : $2; s[k] += $1; n[k]++ }
                END { for (k in s) printf "%.0f\t%d\t%s\n", s[k], n[k], k }' "$W/c.files" | LC_ALL=C sort -t "$(printf '\t')" -k3 > "$W/c.all"
            : > "$W/c.items"
            while IFS=$'\t' read -r size count item; do
                name="${item%/}"
                case "$name" in .*|*{{CompressPartialSuffix}}|*{{RawDeleteSuffix}}) continue ;; {{Incompressible}}) continue ;; esac
                # Only this run's own outputs (recorded by the template, ./outputs.txt) are compressed; without a record, only
                # files written since the run started. Anything an earlier backup recorded as its own (./exclude.txt) is left.
                if [ -s "$W/outputs.txt" ] && ! grep -qxF -- "$name" "$W/outputs.txt"; then clog SKIPPED "$name" "not an output of this backup"; continue; fi
                if [ -s "$W/exclude.txt" ] && awk -F'\t' -v n="$name" 'NR == FNR { x[$0] = 1; next } ($2 == n || index($2, n "/") == 1) && ($2 in x) { f = 1 } END { exit !f }' "$W/exclude.txt" "$W/c.files"; then
                    clog SKIPPED "$name" "it belongs to an earlier backup"; continue
                fi
                if [ "$item" != "$name" ]; then
                    # A folder is packed only when this run wrote every file in it: a folder that also holds older files
                    # is not this run's output alone, and is left as it is.
                    all=$(find "./$name" -type f | wc -l)
                    if [ "$all" -ne "$count" ]; then clog SKIPPED "$name" "the folder also holds files older than this run"; continue; fi
                fi
                printf '%s\t%s\n' "$size" "$item" >> "$W/c.items"
            done < "$W/c.all"
            total=$(awk -F'\t' '{ s += $1 } END { printf "%.0f", s }' "$W/c.items"); done=0
            [ -s "$W/c.items" ] && progress 0 "$total" compress
            # (fd 3: nothing in the loop can swallow the list)
            while IFS=$'\t' read -r size item <&3; do
                name="${item%/}"; started=$SECONDS
                if [ "$item" != "$name" ]; then out="$name.tar.zst"; else out="$name.zst"; fi
                tmp="$out{{CompressPartialSuffix}}"
                if [ -e "./$out" ]; then clog SKIPPED "$name" "$out already exists"; done=$(( done + size )); continue; fi
                # Worst case (incompressible data) the compressed file is as large as the original, which is removed only after.
                free=$(df -P -B1 . 2>/dev/null | awk 'NR == 2 { print $4 }')
                if [ -n "$free" ] && [ "$free" -lt $(( size + 1073741824 )) ]; then
                    clog SKIPPED "$name" "not enough free space on the share to compress it"; done=$(( done + size )); continue
                fi
                if [ "$item" != "$name" ]; then find "$name" -type f | LC_ALL=C sort > "$W/c.members"; fi
                before=$(sig "$name")
                {
                    if fast_hasher; then
                        if [ "$item" != "$name" ]; then python3 "$W/hasher.py" tarcat . "$W/c.members" "$PROGRESS" compress "$total" "$done"
                        else python3 "$W/hasher.py" cat "./$name" "$PROGRESS" compress "$total" "$done"; fi
                    elif [ "$item" != "$name" ]; then tar -cf - -T "$W/c.members"
                    else cat -- "./$name"; fi
                } | zstd -T0 -1 -q -c | {
                    if fast_hasher; then python3 "$W/hasher.py" tee "./$tmp"
                    else cat > "./$tmp" && sync 2>/dev/null; echo "- - $(stat -c '%s' "./$tmp")"; fi
                } > "$W/c.sum"
                st=("${PIPESTATUS[@]}")
                sha=; md5=; n=; read -r sha md5 n < "$W/c.sum"
                if [ "${st[0]}" -ne 0 ] || [ "${st[1]}" -ne 0 ] || [ "${st[2]}" -ne 0 ] || [ -z "$n" ] || [ "$(stat -c '%s' "./$tmp" 2>/dev/null)" != "$n" ]; then
                    rm -f -- "./$tmp"
                    clog FAILED "$name" "compression failed (read ${st[0]}, zstd ${st[1]}, write ${st[2]}); the backup is kept uncompressed"
                    done=$(( done + size )); progress "$done" "$total" compress; continue
                fi
                if [ "$(sig "$name")" != "$before" ]; then
                    rm -f -- "./$tmp"
                    clog FAILED "$name" "it changed while it was being compressed (still being written by something else?); kept as it is"
                    done=$(( done + size )); progress "$done" "$total" compress; continue
                fi
                if ! mv -f -- "./$tmp" "./$out"; then rm -f -- "./$tmp"; clog FAILED "$name" "could not rename the compressed file"; done=$(( done + size )); continue; fi
                if [ ${#sha} -eq 64 ]; then printf '%s\t%s\t%s\t%s\n' "$sha" "$md5" "$n" "$out" >> "$W/known.txt"; fi
                # The compressed file is complete and on stable storage: only now does the raw output go.
                mv -f -- "./$name" "./$name{{RawDeleteSuffix}}" && rm -rf -- "./$name{{RawDeleteSuffix}}"
                done=$(( done + size )); progress "$done" "$total" compress
                printf 'COMPRESSED\t%s\t%s\t%s\t%s\t%s\n' "$name" "$out" "$size" "$n" $(( SECONDS - started )) >> "$W/{{CompressLog}}"
            done 3< "$W/c.items"
        fi
        """;

    /// <summary>
    /// Hashes the files a backup produced in <paramref name="jobDirectory"/> (modified within the last
    /// <paramref name="sinceMinutes"/> minutes, restore staging excluded). Writes ./result in the format
    /// <see cref="RemoteFiles.Parse"/> reads, or NODIR. Needs <see cref="Hasher"/> as ./hasher.py for the fast path.
    /// With <paramref name="compress"/>, the outputs are first compressed into one file each (<see cref="CompressPhase"/>):
    /// those listed in ./outputs.txt when the template recorded them, otherwise files written since
    /// <paramref name="notBeforeEpoch"/>; never one listed in ./exclude.txt (recorded by an earlier backup).
    /// </summary>
    public static string BackupFiles(string jobDirectory, int sinceMinutes, bool compress = false, long? notBeforeEpoch = null) => $$"""
        #!/bin/bash
        W="$PWD"
        {{Helpers()}}
        cd {{ShellQuote.Quote(jobDirectory)}} 2>/dev/null || { echo NODIR > "$W/result"; exit 0; }
        {{(compress ? CompressPhase(Math.Max(1, sinceMinutes), notBeforeEpoch) : "")}}
        find . -path './restore-*' -prune -o -type f -mmin -{{Math.Max(1, sinceMinutes).ToString(CultureInfo.InvariantCulture)}} -printf '%s\t%p\t%P\n' > "$W/list" || exit 3
        : > "$W/known.hit"; : > "$W/list.rest"
        if [ -s "$W/known.txt" ]; then
            # Checksums the compress step computed as it wrote each compressed file ("sha256 md5 size path"): a file with
            # the same name and size is not read again over NFS; anything else is hashed here. (Every file goes to exactly
            # one of the two lists; both start empty, since awk only creates a file it writes to.)
            awk -F'\t' -v W="$W" 'NR == FNR { k[$4 "\t" $3] = $1 "\t" $2; next }
                { key = $3 "\t" $1; if (key in k) print $3 "\t" k[key] >> (W "/known.hit"); else print >> (W "/list.rest") }' "$W/known.txt" "$W/list"
        else
            cp "$W/list" "$W/list.rest"
        fi
        : > "$W/result.known"
        while IFS=$'\t' read -r name sha md5; do
            st=$(stat -c '%s %Y' "./$name") || continue
            printf '%s\t%s\t%s\t%s\n' "$sha" "$st" "$(printf '%s' "$name" | base64 | tr -d '\n')" "$md5" >> "$W/result.known"
        done < "$W/known.hit"
        hash_list "$W/list.rest" "$W/result.rest" verify || exit 3
        { cat "$W/result.known"; grep -v '^END$' "$W/result.rest"; echo END; } > "$W/result.tmp" && mv -f "$W/result.tmp" "$W/result"
        """;

    /// <summary>
    /// Verifies an upload, run from the copy's ./check folder. For client-side encrypted copies the staged ciphertext is
    /// inspected first (age header + checksums per file into ./stage.tsv, stage "inspect"). Then, when every object has
    /// a known MD5 (./expected.md5, or from the inspection), the objects are compared with the hash storage keeps for
    /// them (<c>rclone md5sum</c>, no second read of the source); otherwise, or when storage has no hash for an object,
    /// <c>rclone check</c> reads the source (stage "check"). Exits 0 when the copy matches; output in ../check.log.
    /// </summary>
    /// <param name="stageDirectory">Folder of staged objects (absolute), or null for a copy of the files as they are.</param>
    /// <param name="ageHeader">The age header every staged object must start with (encrypted copies), else null.</param>
    /// <param name="objectSuffix">What the stored object names add to the listed names (".zst", ".age", ".zst.age").</param>
    public static string CopyCheck(string rclonePath, string source, string target, string filesList, string? stageDirectory, string? stageFilesList, string? ageHeader, string objectSuffix, long checkBytes) => $$"""
        #!/bin/bash
        W="$PWD"
        {{Helpers()}}
        cd .. || exit 3
        {{(stageDirectory is null ? "" : $$"""
        if [ -s ./{{TransferScripts.StageManifest}} ]; then
            # Written by the staging script while it produced the objects: no second read of the staged copy.
            cp ./{{TransferScripts.StageManifest}} "$W/stage.tsv"
        else
            while IFS= read -r rel; do
                f={{ShellQuote.Quote(stageDirectory.TrimEnd('/') + "/")}}"$rel{{objectSuffix}}"; [ -f "$f" ] && printf '%s\t%s\t%s\n' "$(stat -c '%s' "$f")" "$f" "$rel"
            done < {{ShellQuote.Quote(stageFilesList!)}} > "$W/stage.list"
            hash_list "$W/stage.list" "$W/stage.tsv" inspect {{(ageHeader is null ? "" : ShellQuote.Quote(ageHeader))}} || exit 3
        fi
        # The staged objects' MD5s let the upload be checked against storage without reading it again.
        awk -F'\t' '$1 == "END" { next } length($4) != 32 { bad = 1 } { print $4 "\t" $3 } END { exit bad }' "$W/stage.tsv" > "$W/stage.md5" \
            && while IFS=$'\t' read -r m n; do nm="$(printf '%s' "$n" | base64 -d)"; case "$nm" in */) nm="${nm%/}.tar" ;; esac; printf '%s  %s{{objectSuffix}}\n' "$m" "$nm"; done < "$W/stage.md5" > "$W/expected.md5" \
            || rm -f "$W/expected.md5"
        """)}}
        RCLONE={{ShellQuote.Quote(rclonePath)}}
        if [ -s "$W/expected.md5" ]; then
            progress 0 0 check
            if "$RCLONE" md5sum {{ShellQuote.Quote(target)}} --files-from {{ShellQuote.Quote(filesList)}} --config ./rclone.conf > "$W/remote.md5" 2> ./check.log; then
                awk 'NR == FNR { e[substr($0, 35)] = substr($0, 1, 32); n++; next }
                     { k = substr($0, 35); if (!(k in e)) next; h = substr($0, 1, 32); gsub(/ /, "", h); seen++
                       if (h == "") { missing++; next }
                       if (tolower(h) != e[k]) { bad++; print "ERROR : " k ": md5 differs (storage " h ", backup " e[k] ")" } }
                     END { if (bad) exit 1; if (missing || seen < n) exit 2; exit 0 }' "$W/expected.md5" "$W/remote.md5" >> ./check.log
                rc=$?
                if [ "$rc" -eq 0 ]; then echo "Verified $(wc -l < "$W/expected.md5") object(s) against the MD5 recorded when the backup was verified" >> ./check.log; exit 0; fi
                if [ "$rc" -eq 1 ]; then exit 1; fi
            fi
            echo "Storage did not return an MD5 for every object; comparing by reading the source (rclone check)" >> ./check.log
        fi
        progress 0 {{checkBytes.ToString(CultureInfo.InvariantCulture)}} check
        "$RCLONE" check {{ShellQuote.Quote(source)}} {{ShellQuote.Quote(target)}} --files-from {{ShellQuote.Quote(filesList)}} --one-way --config ./rclone.conf >> ./check.log 2>&1 &
        P=$!
        while kill -0 "$P" 2>/dev/null; do
            r=$(awk '/^rchar:/{print $2}' "/proc/$P/io" 2>/dev/null); [ -n "$r" ] && progress "$r" {{checkBytes.ToString(CultureInfo.InvariantCulture)}} check; sleep 3
        done
        wait "$P"
        """;
}
