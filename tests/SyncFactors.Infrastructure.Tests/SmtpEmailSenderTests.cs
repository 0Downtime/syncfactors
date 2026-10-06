using System.Net.Mail;
using SyncFactors.Infrastructure;

namespace SyncFactors.Infrastructure.Tests;

public sealed class SmtpEmailSenderTests
{
    private static readonly SmtpConfig Config = new(
        Host: "smtp.example.com",
        Port: 587,
        UseSsl: true,
        From: "syncfactors@example.com",
        To: ["ops@example.com"]);

    [Fact]
    public async Task SendAsync_PassesMessageToCancellableTransport()
    {
        MailMessage? capturedMessage = null;
        CancellationToken capturedToken = default;
        var sender = new SmtpEmailSender(
            Config,
            (config, message, cancellationToken) =>
            {
                Assert.Same(Config, config);
                capturedMessage = message;
                capturedToken = cancellationToken;
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(1));

        await sender.SendAsync(
            "Retention report",
            "Report body",
            ["first@example.com", "second@example.com"],
            CancellationToken.None);

        Assert.NotNull(capturedMessage);
        Assert.Equal("syncfactors@example.com", capturedMessage.From?.Address);
        Assert.Equal("Retention report", capturedMessage.Subject);
        Assert.Equal("Report body", capturedMessage.Body);
        Assert.Equal(
            ["first@example.com", "second@example.com"],
            capturedMessage.To.Select(address => address.Address).ToArray());
        Assert.True(capturedToken.CanBeCanceled);
    }

    [Fact]
    public async Task SendAsync_PropagatesCallerCancellation()
    {
        var transportStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = new SmtpEmailSender(
            Config,
            async (_, _, cancellationToken) =>
            {
                transportStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
            TimeSpan.FromSeconds(5));
        using var callerCts = new CancellationTokenSource();

        var sendTask = sender.SendAsync("subject", "body", ["ops@example.com"], callerCts.Token);
        await transportStarted.Task;
        await callerCts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sendTask);
        Assert.True(callerCts.IsCancellationRequested);
    }

    [Fact]
    public async Task SendAsync_ThrowsTimeoutExceptionWhenTransportExceedsBound()
    {
        var sender = new SmtpEmailSender(
            Config,
            (_, _, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
            TimeSpan.FromMilliseconds(25));

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            sender.SendAsync("subject", "body", ["ops@example.com"], CancellationToken.None));

        Assert.Contains("timed out", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
    }

    [Fact]
    public async Task SendAsync_PropagatesTransportFailure()
    {
        var expected = new SmtpException("SMTP rejected the message.");
        var sender = new SmtpEmailSender(
            Config,
            (_, _, _) => Task.FromException(expected),
            TimeSpan.FromSeconds(1));

        var actual = await Assert.ThrowsAsync<SmtpException>(() =>
            sender.SendAsync("subject", "body", ["ops@example.com"], CancellationToken.None));

        Assert.Same(expected, actual);
    }
}
