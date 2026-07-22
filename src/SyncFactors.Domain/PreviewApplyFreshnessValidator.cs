using System.Security.Cryptography;
using System.Text;
using SyncFactors.Contracts;

namespace SyncFactors.Domain;

public interface IPreviewApplyFreshnessValidator
{
    Task ValidateAsync(WorkerPreviewResult preview, CancellationToken cancellationToken);
}

public sealed class PreviewApplyFreshnessValidator(
    IWorkerSource workerSource,
    IWorkerPlanningService planningService,
    TimeProvider timeProvider) : IPreviewApplyFreshnessValidator
{
    private static readonly TimeSpan MaximumPreviewAge = TimeSpan.FromMinutes(15);

    public async Task ValidateAsync(WorkerPreviewResult preview, CancellationToken cancellationToken)
    {
        if (!preview.CreatedAtUtc.HasValue ||
            string.IsNullOrWhiteSpace(preview.SourceStateFingerprint) ||
            string.IsNullOrWhiteSpace(preview.DirectoryStateFingerprint))
        {
            throw new InvalidOperationException("The saved preview cannot be safely revalidated. Refresh preview before applying.");
        }

        var now = timeProvider.GetUtcNow();
        if (preview.CreatedAtUtc.Value > now || now - preview.CreatedAtUtc.Value > MaximumPreviewAge)
        {
            throw new InvalidOperationException("The saved preview has expired. Refresh preview before applying.");
        }

        var worker = await workerSource.GetWorkerAsync(preview.WorkerId, cancellationToken);
        if (worker is null)
        {
            throw new InvalidOperationException("The saved preview source worker can no longer be resolved. Refresh preview before applying.");
        }

        if (!string.Equals(preview.SourceStateFingerprint, WorkerPreviewStateFingerprint.ComputeSource(worker), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The saved preview no longer matches the current source state. Refresh preview before applying.");
        }

        var plan = await planningService.PlanAsync(worker, logPath: null, cancellationToken);
        if (!string.Equals(preview.DirectoryStateFingerprint, WorkerPreviewStateFingerprint.ComputeDirectory(plan.DirectoryUser), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The saved preview no longer matches the current Active Directory state. Refresh preview before applying.");
        }
    }
}

public static class WorkerPreviewStateFingerprint
{
    public static string ComputeSource(WorkerSnapshot worker)
    {
        var builder = new StringBuilder();
        Append(builder, "workerId", worker.WorkerId);
        Append(builder, "preferredName", worker.PreferredName);
        Append(builder, "lastName", worker.LastName);
        Append(builder, "department", worker.Department);
        Append(builder, "targetOu", worker.TargetOu);
        Append(builder, "isPrehire", worker.IsPrehire.ToString());
        AppendAttributes(builder, worker.Attributes);
        return Hash(builder);
    }

    public static string ComputeDirectory(DirectoryUserSnapshot directoryUser)
    {
        var builder = new StringBuilder();
        Append(builder, "samAccountName", directoryUser.SamAccountName);
        Append(builder, "distinguishedName", directoryUser.DistinguishedName);
        Append(builder, "enabled", directoryUser.Enabled?.ToString());
        Append(builder, "displayName", directoryUser.DisplayName);
        AppendAttributes(builder, directoryUser.Attributes);
        return Hash(builder);
    }

    private static void AppendAttributes(StringBuilder builder, IReadOnlyDictionary<string, string?> attributes)
    {
        foreach (var attribute in attributes.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            Append(builder, attribute.Key, attribute.Value);
        }
    }

    private static void Append(StringBuilder builder, string key, string? value)
    {
        builder.Append(key.Length).Append(':').Append(key).Append('=');
        builder.Append(value is null ? "null" : $"{value.Length}:{value}");
        builder.Append(';');
    }

    private static string Hash(StringBuilder builder) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
}