using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace WorkPlanStudio.Api.Security;

public sealed class EmailDeliveryOptions
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public string SenderAddress { get; set; } = "";
    public string SenderName { get; set; } = "WorkPlan Studio";
    public string PublicBaseUrl { get; set; } = "";

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(Host) && Port is > 0 and <= 65_535 &&
                                MailAddress.TryCreate(SenderAddress, out _) &&
                                Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var uri) &&
                                uri.Scheme is "http" or "https";
}

public interface IEmailDelivery
{
    bool IsConfigured { get; }
    Task SendPasswordResetAsync(string recipient, string resetUrl, CancellationToken cancellationToken);
}

public sealed class SmtpEmailDelivery(IOptions<EmailDeliveryOptions> options) : IEmailDelivery
{
    private readonly EmailDeliveryOptions _options = options.Value;

    public bool IsConfigured => _options.IsConfigured;

    public async Task SendPasswordResetAsync(string recipient, string resetUrl, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("SMTP email delivery is not configured.");

        using var message = new MailMessage
        {
            From = new MailAddress(_options.SenderAddress, _options.SenderName),
            Subject = "Reset your WorkPlan Studio password",
            Body = $"A password reset was requested for your account.\r\n\r\nOpen this one-time link:\r\n{resetUrl}\r\n\r\nIf you did not request this, ignore this message.",
            IsBodyHtml = false
        };
        message.To.Add(new MailAddress(recipient));

        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.UseSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = string.IsNullOrWhiteSpace(_options.UserName),
            Credentials = string.IsNullOrWhiteSpace(_options.UserName)
                ? CredentialCache.DefaultNetworkCredentials
                : new NetworkCredential(_options.UserName, _options.Password),
            Timeout = 15_000
        };
        await client.SendMailAsync(message, cancellationToken);
    }
}
