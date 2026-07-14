using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Api;
using SyncFactors.Contracts;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;

namespace SyncFactors.Api.Pages;

public sealed class Worker360Model(
    IWorkerPreviewPlanner previewPlanner,
    IApplyPreviewService applyPreviewService,
    IRunRepository runRepository,
    RealSyncSettings? realSyncSettings = null,
    ISecurityAuditService? audit = null) : PageModel
{
    private readonly RealSyncSettings _realSyncSettings = realSyncSettings ?? new RealSyncSettings();

    [BindProperty(SupportsGet = true, Name = "runId")]
    public string SavedRunId { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string WorkerId { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public bool ShowAllAttributes { get; set; }

    [BindProperty]
    public bool AcknowledgeRealSync { get; set; }

    [BindProperty]
    public string PreviewRunId { get; set; } = string.Empty;

    [BindProperty]
    public string PreviewFingerprint { get; set; } = string.Empty;

    public WorkerPreviewResult? Preview { get; private set; }

    public DirectoryCommandResult? ApplyResult { get; private set; }

    public string? ErrorMessage { get; private set; }

    public FailureDiagnostics? ErrorDiagnostics => ActiveDirectoryFailureDiagnostics.Parse(ErrorMessage);

    public FailureDiagnostics? ApplyDiagnostics => ActiveDirectoryFailureDiagnostics.Parse(ApplyResult?.Message);

    public bool CanApplyPreview => _realSyncSettings.EffectiveWriteEnabled;

    public string LiveWriteDisabledMessage => _realSyncSettings.LiveWriteDisabledMessage;

    public WorkerPreviewResult? PreviousPreview { get; private set; }

    public IReadOnlyList<WorkerPreviewHistoryItem> PreviewHistory { get; private set; } = [];

    public IReadOnlyList<WorkerRunHistoryItem> RunHistory { get; private set; } = [];

    public int TotalRunHistory { get; private set; }

    public DateTimeOffset? RefreshedAt { get; private set; }

    public IReadOnlyList<SourceAttributeRow> GetSourceSummary() =>
        Preview is null ? [] : SelectSourceSummary(Preview).ToArray();

    public IReadOnlyList<DiffRow> GetVisibleDiffRows()
    {
        if (Preview is null)
        {
            return [];
        }

        return ShowAllAttributes ? Preview.DiffRows : Preview.DiffRows.Where(row => row.Changed).ToArray();
    }

    public static string? GetEmploymentStatusDisplay(WorkerPreviewResult preview)
        => EmploymentStatusDisplay.Format(
            preview.SourceAttributes
                .FirstOrDefault(attribute => string.Equals(attribute.Attribute, "emplStatus", StringComparison.OrdinalIgnoreCase))
                ?.Value);

    public static string GetDiffGroup(DiffRow row)
    {
        var attribute = row.Attribute;
        if (Matches(attribute, "sam", "account", "userprincipalname", "upn", "mail", "email", "cn", "displayname", "givenname", "sn", "surname", "employeeid", "personid"))
        {
            return "Identity";
        }

        if (Matches(attribute, "department", "company", "division", "businessunit", "costcenter", "location", "office", "street", "city", "state", "postal", "country", "title", "job"))
        {
            return "Organization";
        }

        if (Matches(attribute, "emplstatus", "employment", "startdate", "enddate", "hire", "termination", "enabled", "useraccountcontrol"))
        {
            return "Lifecycle";
        }

        if (Matches(attribute, "ou", "distinguishedname", "manager", "route", "graveyard", "leave"))
        {
            return "Routing";
        }

        if (Matches(attribute, "group", "license", "member", "access"))
        {
            return "Access";
        }

        return "Other";
    }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadPreviewAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostApplyAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(WorkerId))
        {
            ErrorMessage = "Worker ID is required.";
            await LoadHistoryAsync(cancellationToken);
            return Page();
        }

        if (string.IsNullOrWhiteSpace(PreviewRunId) || string.IsNullOrWhiteSpace(PreviewFingerprint))
        {
            ErrorMessage = "Refresh preview before applying.";
            await LoadPreviewAsync(cancellationToken);
            return Page();
        }

        if (!CanApplyPreview)
        {
            ErrorMessage = LiveWriteDisabledMessage;
            await LoadPreviewAsync(cancellationToken);
            return Page();
        }

        try
        {
            ApplyResult = await applyPreviewService.ApplyAsync(
                new ApplyPreviewRequest(
                    WorkerId: WorkerId,
                    PreviewRunId: PreviewRunId,
                    PreviewFingerprint: PreviewFingerprint,
                    AcknowledgeRealSync: AcknowledgeRealSync),
                cancellationToken);
            audit?.Write(
                "PreviewApplied",
                ApplyResult.Succeeded ? "Success" : "Failure",
                ("RequestedBy", PageContext?.HttpContext?.User.Identity?.Name ?? "Workers page"),
                ("WorkerId", WorkerId),
                ("PreviewRunId", PreviewRunId));
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }

        SavedRunId = PreviewRunId;
        await LoadPreviewAsync(cancellationToken);
        return Page();
    }

    private async Task LoadPreviewAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(WorkerId))
        {
            await LoadHistoryAsync(cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(SavedRunId))
        {
            Preview = await runRepository.GetWorkerPreviewAsync(SavedRunId, cancellationToken);
            if (Preview is null)
            {
                ErrorMessage = $"Preview run {SavedRunId} could not be resolved.";
                return;
            }

            WorkerId = Preview.WorkerId;
            PreviewRunId = Preview.RunId ?? string.Empty;
            PreviewFingerprint = Preview.Fingerprint;
            RefreshedAt = DateTimeOffset.UtcNow;
            PreviewHistory = await runRepository.ListWorkerPreviewHistoryAsync(WorkerId, 6, cancellationToken);
            await LoadHistoryAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(Preview.PreviousRunId))
            {
                PreviousPreview = await runRepository.GetWorkerPreviewAsync(Preview.PreviousRunId, cancellationToken);
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(WorkerId))
        {
            return;
        }

        try
        {
            Preview = await previewPlanner.PreviewAsync(WorkerId, cancellationToken);
            PreviewRunId = Preview.RunId ?? string.Empty;
            PreviewFingerprint = Preview.Fingerprint;
            SavedRunId = PreviewRunId;
            RefreshedAt = DateTimeOffset.UtcNow;
            PreviewHistory = await runRepository.ListWorkerPreviewHistoryAsync(WorkerId, 6, cancellationToken);
            await LoadHistoryAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(Preview.PreviousRunId))
            {
                PreviousPreview = await runRepository.GetWorkerPreviewAsync(Preview.PreviousRunId, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task LoadHistoryAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(WorkerId))
        {
            return;
        }

        TotalRunHistory = await runRepository.CountWorkerRunHistoryAsync(WorkerId, cancellationToken);
        RunHistory = await runRepository.ListWorkerRunHistoryAsync(WorkerId, 0, 12, cancellationToken);
    }

    private static IEnumerable<SourceAttributeRow> SelectSourceSummary(WorkerPreviewResult preview)
    {
        var preferred = new[]
        {
            "personIdExternal",
            "userId",
            "employeeId",
            "firstName",
            "lastName",
            "preferredName",
            "displayName",
            "emplStatus",
            "startDate",
            "endDate",
            "department",
            "company",
            "location",
            "businessUnit",
            "jobTitle",
            "managerId"
        };

        foreach (var name in preferred)
        {
            var row = preview.SourceAttributes.FirstOrDefault(attribute => string.Equals(attribute.Attribute, name, StringComparison.OrdinalIgnoreCase));
            if (row is not null)
            {
                yield return row;
            }
        }

        foreach (var row in preview.UsedSourceAttributes
            .Where(row => preferred.All(name => !string.Equals(name, row.Attribute, StringComparison.OrdinalIgnoreCase)))
            .Take(12))
        {
            yield return row;
        }
    }

    private static bool Matches(string value, params string[] tokens)
    {
        var normalized = value.Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        return tokens.Any(token => normalized.Contains(token, StringComparison.OrdinalIgnoreCase));
    }
}
