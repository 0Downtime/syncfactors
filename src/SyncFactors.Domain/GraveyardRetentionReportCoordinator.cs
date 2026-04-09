using Microsoft.Extensions.Logging;
using SyncFactors.Contracts;

namespace SyncFactors.Domain;

public sealed class GraveyardRetentionReportCoordinator(
    IGraveyardRetentionStore retentionStore,
    IDirectoryGateway directoryGateway,
    IEmailSender emailSender,
    GraveyardRetentionNotificationSettings settings,
    LifecyclePolicySettings lifecycleSettings,
    TimeProvider timeProvider,
    ILogger<GraveyardRetentionReportCoordinator> logger)
{
    public async Task<bool> TrySendDueReportAsync(CancellationToken cancellationToken)
    {
        if (!settings.Enabled || settings.Recipients.Count == 0)
        {
            return false;
        }

        var status = await retentionStore.GetReportStatusAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (status.LastSentAtUtc is not null &&
            now - status.LastSentAtUtc.Value < TimeSpan.FromDays(settings.IntervalDays))
        {
            return false;
        }

        var activeRecords = await retentionStore.ListActiveAsync(cancellationToken);
        if (activeRecords.Count == 0)
        {
            await retentionStore.RecordReportAttemptAsync(now, null, now, cancellationToken);
            return false;
        }

        var graveyardUsers = await directoryGateway.ListUsersInOuAsync(lifecycleSettings.GraveyardOu, cancellationToken);
        var identitiesInGraveyard = graveyardUsers
            .Select(user => user.Attributes.TryGetValue(lifecycleSettings.DirectoryIdentityAttribute, out var workerId) ? workerId : null)
            .Where(workerId => !string.IsNullOrWhiteSpace(workerId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var overdueUsers = activeRecords
            .Where(record => identitiesInGraveyard.Contains(record.WorkerId))
            .Select(record => new OverdueUser(record, CalculateOverdueDays(record, settings.RetentionDays, now)))
            .Where(item => item.OverdueDays > 0)
            .OrderByDescending(item => item.OverdueDays)
            .ThenBy(item => item.Record.WorkerId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (overdueUsers.Length == 0)
        {
            await retentionStore.RecordReportAttemptAsync(now, null, now, cancellationToken);
            return false;
        }

        var subject = $"{settings.SubjectPrefix} Graveyard Users Past Retention ({overdueUsers.Length})";
        var body = BuildEmailBody(overdueUsers, settings.RetentionDays, now);

        try
        {
            await emailSender.SendAsync(subject, body, settings.Recipients, cancellationToken);
            await retentionStore.RecordReportAttemptAsync(now, null, now, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send graveyard retention report email.");
            await retentionStore.RecordReportAttemptAsync(now, ex.Message, null, cancellationToken);
            return false;
        }
    }

    internal static int CalculateOverdueDays(GraveyardRetentionRecord record, int retentionDays, DateTimeOffset now)
    {
        var anchor = record.EndDateUtc ?? record.LastObservedAtUtc;
        var overdue = now.Date - anchor.Date.AddDays(retentionDays);
        return overdue.Days;
    }

    private static string BuildEmailBody(IEnumerable<OverdueUser> overdueUsers, int retentionDays, DateTimeOffset now)
    {
        var lines = new List<string>
        {
            $"Generated: {now:O}",
            $"Retention days: {retentionDays}",
            string.Empty,
            "Users still in the graveyard OU past retention:",
            string.Empty
        };

        foreach (var item in overdueUsers)
        {
            GraveyardRetentionRecord record = item.Record;
            int overdueDays = item.OverdueDays;
            lines.Add(
                $"- WorkerId: {record.WorkerId}, SAM: {record.SamAccountName ?? "(unknown)"}, Name: {record.DisplayName ?? "(unknown)"}, Status: {record.Status}, EndDate: {(record.EndDateUtc?.ToString("yyyy-MM-dd") ?? "(unknown)")}, DaysPastRetention: {overdueDays}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private sealed record OverdueUser(GraveyardRetentionRecord Record, int OverdueDays);
}
