using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Api.Pages;

public sealed class Worker360Model(
    IWorkerPreviewPlanner previewPlanner,
    IRunRepository runRepository) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string WorkerId { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public bool ShowAllAttributes { get; set; }

    public WorkerPreviewResult? Preview { get; private set; }

    public IReadOnlyList<WorkerPreviewHistoryItem> PreviewHistory { get; private set; } = [];

    public string? ErrorMessage { get; private set; }

    public int ChangedAttributeCount => Preview?.DiffRows.Count(row => row.Changed) ?? 0;

    public int HighRiskChangeCount => Preview?.DiffRows.Count(row => row.Changed && IsHighRiskAttribute(row.Attribute)) ?? 0;

    public IReadOnlyList<DiffRow> VisibleDiffRows =>
        Preview is null
            ? []
            : (ShowAllAttributes ? Preview.DiffRows : Preview.DiffRows.Where(row => row.Changed).ToArray());

    public string? EmploymentStatusDisplay =>
        Preview is null
            ? null
            : EmploymentStatusDisplayFormatter(ResolveSourceAttribute("emplStatus"));

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(WorkerId))
        {
            return;
        }

        var workerId = WorkerId.Trim();
        WorkerId = workerId;

        try
        {
            Preview = await previewPlanner.PreviewAsync(workerId, cancellationToken);
            PreviewHistory = await runRepository.ListWorkerPreviewHistoryAsync(workerId, 8, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or HttpRequestException)
        {
            ErrorMessage = ex.Message;
        }
    }

    public string? ResolveSourceAttribute(string attribute)
    {
        if (Preview is null)
        {
            return null;
        }

        return Preview.SourceAttributes
            .FirstOrDefault(row => string.Equals(row.Attribute, attribute, StringComparison.OrdinalIgnoreCase))
            ?.Value;
    }

    public static string DiffTone(DiffRow row)
    {
        if (!row.Changed)
        {
            return "dim";
        }

        return IsHighRiskAttribute(row.Attribute) ? "warn" : "info";
    }

    public static bool IsHighRiskAttribute(string attribute) =>
        attribute.Contains("manager", StringComparison.OrdinalIgnoreCase) ||
        attribute.Contains("distinguished", StringComparison.OrdinalIgnoreCase) ||
        attribute.Contains("enabled", StringComparison.OrdinalIgnoreCase) ||
        attribute.Contains("useraccountcontrol", StringComparison.OrdinalIgnoreCase) ||
        attribute.Contains("ou", StringComparison.OrdinalIgnoreCase);

    private static string? EmploymentStatusDisplayFormatter(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : SyncFactors.Domain.EmploymentStatusDisplay.Format(code);
}
