using System.Net.Http.Headers;
using System.Text;
using Backora.Application.Abstractions;
using Backora.Domain.Platform;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Backora.Infrastructure.Notifications;

/// <summary>SMTP via MailKit. Server certificates are always validated (no bypass option exists).</summary>
public sealed class MailKitEmailSender : IEmailSender
{
    public async Task SendAsync(SmtpSettings settings, string? password, IReadOnlyList<string> recipients, string subject, string htmlBody, CancellationToken ct)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.FromName ?? "Backora", settings.FromAddress));
        foreach (var r in recipients) message.To.Add(MailboxAddress.Parse(r));
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = htmlBody, TextBody = "This notification requires an HTML capable mail client." }.ToMessageBody();

        using var client = new SmtpClient { Timeout = 30000 };
        var security = settings.Security switch
        {
            "SslOnConnect" => SecureSocketOptions.SslOnConnect,
            "None" => SecureSocketOptions.None,
            _ => SecureSocketOptions.StartTls,
        };
        await client.ConnectAsync(settings.Host, settings.Port, security, ct);
        if (!string.IsNullOrEmpty(settings.Username)) await client.AuthenticateAsync(settings.Username, password ?? string.Empty, ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }
}

/// <summary>Posts cards to Microsoft Teams incoming webhooks / workflow URLs (HTTPS only, no redirects).</summary>
public sealed class TeamsWebhookSender(IHttpClientFactory httpClientFactory) : ITeamsSender
{
    public const string ClientName = "teams";

    public async Task SendAsync(string webhookUrl, string jsonPayload, CancellationToken ct)
    {
        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Teams webhook URL must be an absolute https URL.");
        }
        var client = httpClientFactory.CreateClient(ClientName);
        using var content = new StringContent(jsonPayload, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await client.PostAsync(uri, content, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Teams webhook returned {(int)response.StatusCode}: {(body.Length > 300 ? body[..300] : body)}");
        }
    }
}
