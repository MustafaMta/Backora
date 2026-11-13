using Backora.Domain.Execution;
using FluentAssertions;
using Xunit;

namespace Backora.UnitTests;

public class TemplateSettingsTests
{
    private const string Script = """
        #!/bin/bash
        # @setting DB_HOST "Database host" help="Leave empty for the local socket."
        # @setting DB_PASSWORD "Database password" secret required
        # @setting DUMP_OPTIONS "Options" default="--single-transaction --quick"
        # @setting PORT "Port" default=5432
        echo "{JOB_NAME}"
        """;

    [Fact]
    public void Parses_declared_settings()
    {
        var specs = TemplateSettings.Parse(Script);
        specs.Select(s => s.Name).Should().Equal("DB_HOST", "DB_PASSWORD", "DUMP_OPTIONS", "PORT");
        specs[0].Help.Should().Be("Leave empty for the local socket.");
        specs[1].Secret.Should().BeTrue(); specs[1].Required.Should().BeTrue();
        specs[2].Default.Should().Be("--single-transaction --quick");
        specs[3].Default.Should().Be("5432");
    }

    [Fact]
    public void Exports_values_after_the_shebang_with_safe_quoting()
    {
        var rendered = ScriptRenderer.Render(Script, new ScriptRenderContext("job1", "c", "s", 7, "nfs", "/n", new Dictionary<string, string>()));
        var hostile = "p'a\"s$s`whoami`\n;rm -rf /";
        var result = TemplateSettings.Apply(rendered, TemplateSettings.Parse(Script), new Dictionary<string, string> { ["DB_PASSWORD"] = hostile });

        result.Content.Should().StartWith("#!/bin/bash\n# --- Backora job settings");
        result.Content.Should().Contain("export DB_PASSWORD='p'\"'\"'a\"s$s`whoami`\n;rm -rf /'");
        result.Content.Should().Contain("export DB_HOST=''");
        result.Content.Should().Contain("export DUMP_OPTIONS='--single-transaction --quick'");
        result.Content.Should().Contain("echo \"job1\"");
        result.Sha256.Should().NotBe(rendered.Sha256);
    }

    [Fact]
    public void Templates_without_settings_are_unchanged()
    {
        var rendered = ScriptRenderer.Render("#!/bin/bash\necho hi\n", new ScriptRenderContext("j", "c", "s", 1, "h", "/p", new Dictionary<string, string>()));
        TemplateSettings.Apply(rendered, [], new Dictionary<string, string>()).Should().Be(rendered);
    }

    [Fact]
    public void Settings_come_from_variables_job_specific_first()
    {
        var vars = new Dictionary<string, string>
        {
            ["DB_USER"] = "backup_usr",
            ["DB_USER__SSO_DB"] = "sso_user",
            ["DB_PASSWORD__ZABBIX"] = "zbx",
            ["DB_HOST"] = "",
        };
        var specs = TemplateSettings.Parse(Script + "\n# @setting DB_USER \"User\"\n");

        var sso = TemplateSettings.Resolve(specs, "sso-db", k => vars.GetValueOrDefault(k));
        sso["DB_USER"].Should().Be("sso_user");
        sso.Should().NotContainKey("DB_HOST", "empty variables count as unset");

        var other = TemplateSettings.Resolve(specs, "zabbix", k => vars.GetValueOrDefault(k));
        other["DB_USER"].Should().Be("backup_usr");
        other["DB_PASSWORD"].Should().Be("zbx");
        TemplateSettings.JobKey("CloudStack-Prod01").Should().Be("CLOUDSTACK_PROD01");
        TemplateSettings.VariableName("DB_USER", "sso-db").Should().Be("DB_USER__SSO_DB");
        TemplateSettings.SplitVariableName("DUMP_OPTIONS__ZABBIX_MAIN_DB").Should().Be(("DUMP_OPTIONS", "ZABBIX_MAIN_DB"));
        TemplateSettings.SplitVariableName("DB_USER").Should().Be(("DB_USER", (string?)null));

        TemplateSettings.Resolve(specs, "zabbix", k => vars.GetValueOrDefault(k), out var all);
        all.Should().BeEquivalentTo(["DB_USER"], "only the all-jobs value is marked; the job's own password is not");
    }
}
