using Backora.Domain.Execution;
using FluentAssertions;
using Xunit;

namespace Backora.UnitTests;

/// <summary>The shipped backup templates (backend/database/templates) render cleanly the way Backora runs them.</summary>
public class BackupTemplateFilesTests
{
    private static string TemplatesDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "database", "templates");
            if (File.Exists(Path.Combine(candidate, "_common.inc"))) return candidate;
        }
        throw new DirectoryNotFoundException("backend/database/templates not found");
    }

    public static TheoryData<string> Templates()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.GetFiles(TemplatesDir(), "*.sh").Order(StringComparer.Ordinal)) data.Add(Path.GetFileName(f));
        return data;
    }

    private static string Inline(string file)
    {
        var dir = TemplatesDir();
        return File.ReadAllText(Path.Combine(dir, file)).Replace("#@include _common.inc\n", File.ReadAllText(Path.Combine(dir, "_common.inc")), StringComparison.Ordinal);
    }

    [Theory, MemberData(nameof(Templates))]
    public void Template_renders_without_unresolved_placeholders(string file)
    {
        var script = Inline(file);
        script.Should().NotContain("#@include", "the shared block must be inlined");
        var rendered = ScriptRenderer.Render(script, new ScriptRenderContext("sso-db", "MariaDB", file, 7, "nfs:/exports/backora", "/mnt/backora", new Dictionary<string, string>()));
        rendered.UnresolvedPlaceholders.Should().BeEmpty();
        rendered.Content.Should().Contain("JOB=\"sso-db\"").And.Contain("NFS_SOURCE=\"nfs:/exports/backora/sso-db\"");
        rendered.Content.Should().Contain("set -euo pipefail").And.Contain("trap cleanup EXIT");
    }

    [Theory, MemberData(nameof(Templates))]
    public void Template_declares_its_connection_and_extra_settings(string file)
    {
        var prefix = file.StartsWith("mariadb", StringComparison.Ordinal) ? "MYSQL" : "PG";
        var specs = TemplateSettings.Parse(Inline(file)).Select(s => s.Name).ToList();
        specs.Should().Contain([$"{prefix}_HOST", $"{prefix}_PORT", $"{prefix}_USER", $"{prefix}_PASSWORD", "CONTAINER", "CONTAINER_ENGINE", "PRE_COMMAND", "EXTRA_PATHS"]);
        specs.Should().NotContain(s => s.StartsWith("DB_", StringComparison.Ordinal), "settings are per engine so one all-jobs value never crosses engines");
        TemplateSettings.Parse(Inline(file)).Single(s => s.Name == $"{prefix}_PASSWORD").Secret.Should().BeTrue();
    }

    // A locked-down postgres account (shell /sbin/nologin, no home) is common on hardened servers. `su - postgres`
    // runs the login shell there, which refuses ("This account is currently not available.") and the backup fails.
    [Theory, MemberData(nameof(Templates))]
    public void Templates_never_depend_on_the_postgres_login_shell(string file)
    {
        var script = string.Join('\n', Inline(file).Split('\n').Where(l => !l.TrimStart().StartsWith('#')));   // code, not comments
        script.Should().Contain("as_postgres");
        script.Should().NotMatchRegex(@"\bsu\s+(-|-l|--login)\s+postgres\b");
        script.Should().NotMatchRegex(@"\bsu\s+postgres\b");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void As_postgres_runs_the_command_without_the_login_shell(bool hasRunuser)
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/bash")) return;   // needs bash
        var common = File.ReadAllText(Path.Combine(TemplatesDir(), "_common.inc"));
        var fn = System.Text.RegularExpressions.Regex.Match(common, @"^as_postgres\(\) \{\n.*?^\}\n",
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.Multiline).Value;
        fn.Should().NotBeEmpty();

        var bin = Directory.CreateTempSubdirectory("bko-aspg-").FullName;
        try
        {
            var calls = Path.Combine(bin, "calls.log");
            void Tool(string name, string body)
            {
                var path = Path.Combine(bin, name);
                File.WriteAllText(path, "#!/bin/bash\n" + body + "\n");
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            // The account exactly as on the failing server: nologin shell, home folder that does not exist.
            Tool("getent", "echo 'postgres:x:26:26:PostgreSQL Server:/home/postgres:/sbin/nologin'");
            if (hasRunuser)
                Tool("runuser", $"echo \"runuser $*\" >> {calls}\n[[ \"$1 $2 $3\" == '-u postgres --' ]] || exit 9\nshift 3\nexec \"$@\"");
            Tool("su", $"echo \"su $*\" >> {calls}\n[[ \"$1 $2 $3 $4\" == '-s /bin/bash postgres -c' ]] || exit 9\nexec \"$2\" -c \"$5\"");

            var test = Path.Combine(bin, "test.sh");
            File.WriteAllText(test, "set -euo pipefail\n" + fn + "as_postgres 'echo \"ran in $PWD home=$HOME\"; printf \"%s\\n\" \"$0\"'\n");
            var psi = new System.Diagnostics.ProcessStartInfo("/bin/bash", [test]) { RedirectStandardOutput = true, RedirectStandardError = true };
            psi.Environment["PATH"] = bin;   // only the fakes: deterministic whether or not this machine has runuser
            using var p = System.Diagnostics.Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();

            p.ExitCode.Should().Be(0, stderr);
            stdout.Should().Contain("ran in / home=/").And.NotContain("nologin");
            File.ReadAllText(calls).Should().StartWith(hasRunuser ? "runuser -u postgres -- /bin/bash -c" : "su -s /bin/bash postgres -c");
        }
        finally { Directory.Delete(bin, recursive: true); }
    }
}
