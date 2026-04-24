using System.Text.Json;
using System.Text.Json.Serialization;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Infrastructure;

public sealed class MockRuntimeFixtureReader(MockRuntimeFixturePathResolver pathResolver)
{
    public IReadOnlyList<MockRuntimeFixtureWorkerRecord> ListWorkers()
    {
        var path = pathResolver.Resolve();
        if (!File.Exists(path))
        {
            return [];
        }

        var json = File.ReadAllText(path);
        var document = JsonSerializer.Deserialize<MockRuntimeFixtureDocument>(json, JsonOptions.Default);
        return document?.Workers ?? [];
    }
}

public sealed record MockRuntimeFixtureDocument(
    [property: JsonPropertyName("workers")] IReadOnlyList<MockRuntimeFixtureWorkerRecord>? Workers);

public sealed record MockRuntimeFixtureWorkerRecord(
    [property: JsonPropertyName("personIdExternal")] string? PersonIdExternal,
    [property: JsonPropertyName("userName")] string? UserName,
    [property: JsonPropertyName("userId")] string? UserId,
    [property: JsonPropertyName("firstName")] string? FirstName,
    [property: JsonPropertyName("lastName")] string? LastName,
    [property: JsonPropertyName("preferredName")] string? PreferredName,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("startDate")] string? StartDate,
    [property: JsonPropertyName("employmentStatus")] string? EmploymentStatus,
    [property: JsonPropertyName("lifecycleState")] string? LifecycleState,
    [property: JsonPropertyName("endDate")] string? EndDate,
    [property: JsonPropertyName("lastDateWorked")] string? LastDateWorked,
    [property: JsonPropertyName("latestTerminationDate")] string? LatestTerminationDate,
    [property: JsonPropertyName("department")] string? Department,
    [property: JsonPropertyName("managerId")] string? ManagerId,
    [property: JsonPropertyName("lastModifiedDateTime")] string? LastModifiedDateTime);

internal static class MockRuntimeFixtureProjection
{
    public static bool MatchesWorkerId(MockRuntimeFixtureWorkerRecord worker, string? workerId)
    {
        if (string.IsNullOrWhiteSpace(workerId))
        {
            return false;
        }

        return string.Equals(ResolveWorkerId(worker), workerId, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(worker.PersonIdExternal, workerId, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(worker.UserId, workerId, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(worker.UserName, workerId, StringComparison.OrdinalIgnoreCase);
    }

    public static string ResolveWorkerId(MockRuntimeFixtureWorkerRecord worker)
    {
        return FirstNonEmpty(worker.UserId, worker.UserName, worker.PersonIdExternal) ?? string.Empty;
    }

    public static string ResolveSamAccountName(MockRuntimeFixtureWorkerRecord worker)
    {
        return FirstNonEmpty(worker.UserId, worker.UserName, worker.PersonIdExternal) ?? string.Empty;
    }

    public static string ResolveDisplayName(MockRuntimeFixtureWorkerRecord worker)
    {
        var displayName = FirstNonEmpty(worker.DisplayName, BuildPreferredDisplayName(worker));
        return string.IsNullOrWhiteSpace(displayName)
            ? ResolveSamAccountName(worker)
            : displayName;
    }

    public static string ResolveTargetOu(
        MockRuntimeFixtureWorkerRecord worker,
        LifecyclePolicySettings lifecycleSettings,
        DateTimeOffset now)
    {
        if (IsGraveyard(worker, lifecycleSettings))
        {
            return lifecycleSettings.GraveyardOu;
        }

        if (IsLeave(worker, lifecycleSettings))
        {
            return string.IsNullOrWhiteSpace(lifecycleSettings.LeaveOu)
                ? lifecycleSettings.ActiveOu
                : lifecycleSettings.LeaveOu;
        }

        if (IsPrehire(worker, now))
        {
            return lifecycleSettings.PrehireOu;
        }

        return lifecycleSettings.ActiveOu;
    }

    public static bool ResolveEnabled(
        MockRuntimeFixtureWorkerRecord worker,
        LifecyclePolicySettings lifecycleSettings,
        DateTimeOffset now)
    {
        _ = now;
        return !IsGraveyard(worker, lifecycleSettings) &&
               !IsLeave(worker, lifecycleSettings);
    }

    public static bool IsGraveyard(
        MockRuntimeFixtureWorkerRecord worker,
        LifecyclePolicySettings lifecycleSettings)
    {
        var lifecycleState = NormalizeLifecycleState(worker.LifecycleState);
        if (lifecycleState is "retired" or "terminated")
        {
            return true;
        }

        var status = worker.EmploymentStatus?.Trim();
        return !string.IsNullOrWhiteSpace(status) &&
               lifecycleSettings.InactiveStatusValues.Any(value =>
                   string.Equals(value, status, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsLeave(
        MockRuntimeFixtureWorkerRecord worker,
        LifecyclePolicySettings lifecycleSettings)
    {
        var lifecycleState = NormalizeLifecycleState(worker.LifecycleState);
        if (lifecycleState is "paidleave" or "unpaidleave")
        {
            return true;
        }

        var leaveValues = lifecycleSettings.LeaveStatusValues ?? [];
        var status = worker.EmploymentStatus?.Trim();
        return !string.IsNullOrWhiteSpace(status) &&
               leaveValues.Any(value => string.Equals(value, status, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsPrehire(MockRuntimeFixtureWorkerRecord worker, DateTimeOffset now)
    {
        var lifecycleState = NormalizeLifecycleState(worker.LifecycleState);
        if (lifecycleState == "preboarding")
        {
            return true;
        }

        return SourceDateParser.TryParse(worker.StartDate, out var startDate) &&
               startDate.Date > now.Date;
    }

    public static DateTimeOffset? ResolveAnchorDate(MockRuntimeFixtureWorkerRecord worker)
    {
        if (SourceDateParser.TryParse(worker.EndDate, out var endDate))
        {
            return endDate;
        }

        if (SourceDateParser.TryParse(worker.LatestTerminationDate, out var terminationDate))
        {
            return terminationDate;
        }

        if (SourceDateParser.TryParse(worker.LastDateWorked, out var lastWorkedDate))
        {
            return lastWorkedDate;
        }

        if (DateTimeOffset.TryParse(worker.LastModifiedDateTime, out var lastModified))
        {
            return lastModified;
        }

        return null;
    }

    public static string BuildDistinguishedName(
        MockRuntimeFixtureWorkerRecord worker,
        LifecyclePolicySettings lifecycleSettings,
        DateTimeOffset now)
    {
        var commonName = FirstNonEmpty(ResolveDisplayName(worker), ResolveSamAccountName(worker), ResolveWorkerId(worker), "Worker");
        return $"CN={commonName},{ResolveTargetOu(worker, lifecycleSettings, now)}";
    }

    private static string? BuildPreferredDisplayName(MockRuntimeFixtureWorkerRecord worker)
    {
        var preferredName = FirstNonEmpty(worker.PreferredName, worker.FirstName);
        var fullName = string.Join(" ", new[] { preferredName, worker.LastName }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(fullName) ? null : fullName;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string NormalizeLifecycleState(string? lifecycleState)
    {
        if (string.IsNullOrWhiteSpace(lifecycleState))
        {
            return string.Empty;
        }

        return new string(lifecycleState
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }
}
