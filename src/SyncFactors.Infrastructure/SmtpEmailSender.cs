using System.Net.Mail;
using SyncFactors.Domain;

namespace SyncFactors.Infrastructure;

public sealed class SmtpEmailSender : IEmailSender
{
    internal static readonly TimeSpan DefaultSendTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<SmtpConfig?> smtpConfigProvider;
    private readonly Func<SmtpConfig, MailMessage, CancellationToken, Task> sendMailAsync;
    private readonly TimeSpan sendTimeout;

    public SmtpEmailSender(SyncFactorsConfigurationLoader configLoader)
        : this(
            () => configLoader.GetSyncConfig().Alerts.Smtp,
            SendMailAsync,
            DefaultSendTimeout)
    {
    }

    internal SmtpEmailSender(
        SmtpConfig? smtpConfig,
        Func<SmtpConfig, MailMessage, CancellationToken, Task> sendMailAsync,
        TimeSpan sendTimeout)
        : this(() => smtpConfig, sendMailAsync, sendTimeout)
    {
    }

    private SmtpEmailSender(
        Func<SmtpConfig?> smtpConfigProvider,
        Func<SmtpConfig, MailMessage, CancellationToken, Task> sendMailAsync,
        TimeSpan sendTimeout)
    {
        ArgumentNullException.ThrowIfNull(smtpConfigProvider);
        ArgumentNullException.ThrowIfNull(sendMailAsync);
        if (sendTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(sendTimeout), "SMTP send timeout must be positive.");
        }

        this.smtpConfigProvider = smtpConfigProvider;
        this.sendMailAsync = sendMailAsync;
        this.sendTimeout = sendTimeout;
    }

    public async Task SendAsync(
        string subject,
        string body,
        IReadOnlyList<string> recipients,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var smtpConfig = smtpConfigProvider()
            ?? throw new InvalidOperationException("SMTP settings are not configured.");
        if (!smtpConfig.UseSsl)
        {
            throw new InvalidOperationException("SMTP SSL must be enabled before sending alert email.");
        }

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

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(sendTimeout);
        try
        {
            await sendMailAsync(smtpConfig, message, timeoutCts.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"SMTP send timed out after {sendTimeout.TotalSeconds:0} seconds.", ex);
        }
    }

    private static async Task SendMailAsync(
        SmtpConfig smtpConfig,
        MailMessage message,
        CancellationToken cancellationToken)
    {
        using var client = new SmtpClient(smtpConfig.Host, smtpConfig.Port)
        {
            EnableSsl = true
        };
        await client.SendMailAsync(message, cancellationToken);
    }
}
