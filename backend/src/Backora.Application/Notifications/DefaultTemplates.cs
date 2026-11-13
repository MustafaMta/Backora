namespace Backora.Application.Notifications;

/// <summary>Built-in notification templates (legacy placeholders are supported unchanged).</summary>
public static class DefaultTemplates
{
    public const string Email = """
        <!DOCTYPE html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>Backora notification</title></head>
        <body style="margin:0;padding:0;background:#f3f5f8;font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;color:#0f1b2d;">
          <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#f3f5f8;padding:32px 12px;">
            <tr><td align="center">
              <table role="presentation" width="600" cellpadding="0" cellspacing="0" style="max-width:600px;background:#ffffff;border:1px solid #dde3ea;border-radius:10px;overflow:hidden;">
                <tr><td style="background:#0b1626;padding:18px 24px;color:#e6f6f8;font-size:15px;font-weight:600;letter-spacing:.3px;">
                  <span style="display:inline-block;width:10px;height:10px;border-radius:3px;background:#14b8c4;margin-right:8px;"></span>Backora
                </td></tr>
                <tr><td style="padding:24px 24px 8px;">
                  <p style="margin:0 0 6px;font-size:12px;letter-spacing:.08em;text-transform:uppercase;color:#b42318;font-weight:700;">{{event}}</p>
                  <h1 style="margin:0 0 4px;font-size:20px;line-height:28px;">{{job_name}}</h1>
                  <p style="margin:0;color:#4b5a6e;font-size:14px;">{{server_name}} · {{server_ip}} · status {{status}}</p>
                </td></tr>
                <tr><td style="padding:16px 24px;">
                  <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="font-size:13px;border-collapse:collapse;">
                    <tr><td style="padding:6px 0;color:#4b5a6e;width:150px;">Execution</td><td style="padding:6px 0;">#{{execution_id}}</td></tr>
                    <tr><td style="padding:6px 0;color:#4b5a6e;">Category</td><td style="padding:6px 0;">{{job_category}}</td></tr>
                    <tr><td style="padding:6px 0;color:#4b5a6e;">Template</td><td style="padding:6px 0;">{{script_name}}</td></tr>
                    <tr><td style="padding:6px 0;color:#4b5a6e;">Started</td><td style="padding:6px 0;">{{started_at}}</td></tr>
                    <tr><td style="padding:6px 0;color:#4b5a6e;">Finished</td><td style="padding:6px 0;">{{failure_time}}</td></tr>
                    <tr><td style="padding:6px 0;color:#4b5a6e;">Duration</td><td style="padding:6px 0;">{{total_time}}</td></tr>
                    <tr><td style="padding:6px 0;color:#4b5a6e;">Failure class</td><td style="padding:6px 0;">{{failure_class}}</td></tr>
                  </table>
                </td></tr>
                <tr><td style="padding:0 24px 16px;">
                  <p style="margin:0 0 6px;font-size:13px;font-weight:600;">Message</p>
                  <pre style="margin:0;white-space:pre-wrap;word-break:break-word;background:#fff6f5;border:1px solid #f4c7c3;border-radius:6px;padding:12px;font-size:12px;line-height:18px;color:#7a271a;">{{error_message}}</pre>
                </td></tr>
                <tr><td style="padding:0 24px 24px;">
                  <a href="{{admin_url}}" style="display:inline-block;background:#0e7c86;color:#ffffff;text-decoration:none;font-size:14px;font-weight:600;padding:10px 16px;border-radius:6px;">Open in Backora</a>
                </td></tr>
              </table>
              <p style="color:#7a8799;font-size:12px;margin:16px 0 0;">Automated message from Backora backup management.</p>
            </td></tr>
          </table>
        </body></html>
        """;

    public const string Teams = """
        {
          "@type": "MessageCard",
          "@context": "http://schema.org/extensions",
          "summary": "Backora {{event}}: {{job_name}}",
          "themeColor": "B42318",
          "title": "Backora · {{event}} · {{job_name}}",
          "sections": [
            {
              "activityTitle": "{{server_name}} ({{server_ip}})",
              "activitySubtitle": "Execution #{{execution_id}} · {{status}}",
              "facts": [
                { "name": "Category", "value": "{{job_category}}" },
                { "name": "Template", "value": "{{script_name}}" },
                { "name": "Started", "value": "{{started_at}}" },
                { "name": "Finished", "value": "{{failure_time}}" },
                { "name": "Duration", "value": "{{total_time}}" },
                { "name": "Failure class", "value": "{{failure_class}}" },
                { "name": "Message", "value": "{{error_message}}" }
              ],
              "markdown": false
            }
          ],
          "potentialAction": [
            { "@type": "OpenUri", "name": "Open in Backora", "targets": [ { "os": "default", "uri": "{{admin_url}}" } ] }
          ]
        }
        """;

    public const string PasswordResetEmail = """
        <!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><title>Reset your Backora password</title></head>
        <body style="margin:0;padding:32px 12px;background:#f3f5f8;font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;color:#0f1b2d;">
          <table role="presentation" width="100%"><tr><td align="center">
            <table role="presentation" width="560" style="max-width:560px;background:#fff;border:1px solid #dde3ea;border-radius:10px;padding:28px;">
              <tr><td>
                <h1 style="font-size:20px;margin:0 0 12px;">Reset your password</h1>
                <p style="font-size:14px;line-height:22px;margin:0 0 20px;">Hello {{name}}, a password reset was requested for your Backora account. The link expires in 60 minutes. If you did not request it, ignore this email.</p>
                <a href="{{reset_url}}" style="display:inline-block;background:#0e7c86;color:#fff;text-decoration:none;font-weight:600;padding:10px 16px;border-radius:6px;">Choose a new password</a>
              </td></tr>
            </table>
          </td></tr></table>
        </body></html>
        """;

    /// <summary>Sample values used for previews and test notifications.</summary>
    public static IReadOnlyDictionary<string, string?> SampleValues(string publicUrl) => new Dictionary<string, string?>
    {
        ["event"] = "backup.failed (test)",
        ["status"] = "Failed",
        ["job_name"] = "orders-db-nightly",
        ["job_category"] = "PostgreSQL",
        ["script_name"] = "pg_dumpall",
        ["server_name"] = "db-prod-01",
        ["server_ip"] = "10.0.0.15",
        ["ssh_user"] = "root",
        ["ssh_port"] = "22",
        ["execution_id"] = "1024",
        ["started_at"] = "2025-01-01 02:00:00 UTC",
        ["completed_at"] = "2025-01-01 02:03:12 UTC",
        ["failure_time"] = "2025-01-01 02:03:12 UTC",
        ["total_time"] = "3m 12s",
        ["error_message"] = "pg_dumpall: error: connection to server failed (this is a test notification)",
        ["failure_class"] = "ScriptError",
        ["output"] = "…",
        ["admin_url"] = $"{publicUrl.TrimEnd('/')}/runs",
    };
}
