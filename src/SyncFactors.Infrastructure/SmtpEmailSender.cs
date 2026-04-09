using System.Net.Mail;
using SyncFactors.Domain;

namespace SyncFactors.Infrastructure;

public sealed class SmtpEmailSender(SyncFactorsConfigurationLoader configLoader) : IEmailSender
{
    public Task SendAsync(string subject, string body, IReadOnlyList<string> recipients, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var smtpConfig = configLoader.GetSyncConfig().Alerts.Smtp
            ?? throw new InvalidOperationException("SMTP settings are not configured.");

        using var client = new SmtpClient(smtpConfig.Host, smtpConfig.Port)
        {
            EnableSsl = smtpConfig.UseSsl
        };
        using var message = new MailMessage
        {
            From = new MailAddress(smtpConfig.From),
            Subject = subject,
            Body = body
        };

        foreach (var recipient in recipients)
        {
            message.To.Add(recipient);
        }

        client.Send(message);
        return Task.CompletedTask;
    }
}
