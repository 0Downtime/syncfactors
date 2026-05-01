using SyncFactors.Api.Pages;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Api.Tests;

public sealed class Worker360ModelTests
{
    [Fact]
    public async Task OnGetAsync_LoadsPreviewAndHistoryForTrimmedWorker()
    {
        var preview = CreatePreview("10001");
        var history = new[]
        {
            new WorkerPreviewHistoryItem(
                RunId: "preview-previous",
                WorkerId: "10001",
                SamAccountName: "10001",
                Bucket: "updates",
                Status: "Succeeded",
                StartedAt: DateTimeOffset.Parse("2026-03-27T12:00:00Z"),
                ChangeCount: 2,
                Action: "UpdateUser",
                Reason: null,
                Fingerprint: "previous-fingerprint")
        };
        var planner = new CapturingWorkerPreviewPlanner(preview);
        var repository = new CapturingRunRepository(history);
        var model = new Worker360Model(planner, repository)
        {
            WorkerId = " 10001 "
        };

        await model.OnGetAsync(CancellationToken.None);

        Assert.Equal("10001", model.WorkerId);
        Assert.Equal("10001", planner.LastWorkerId);
        Assert.Equal("10001", repository.LastHistoryWorkerId);
        Assert.Same(preview, model.Preview);
        Assert.Same(history, model.PreviewHistory);
        Assert.Null(model.ErrorMessage);
    }

    [Fact]
    public async Task OnGetAsync_CapturesPreviewErrors()
    {
        var model = new Worker360Model(
            new ThrowingWorkerPreviewPlanner(new InvalidOperationException("Preview unavailable.")),
            new CapturingRunRepository([]))
        {
            WorkerId = "10001"
        };

        await model.OnGetAsync(CancellationToken.None);

        Assert.Equal("Preview unavailable.", model.ErrorMessage);
        Assert.Null(model.Preview);
        Assert.Empty(model.PreviewHistory);
    }

    [Fact]
    public void VisibleDiffRows_DefaultsToChangedRowsOnly()
    {
        var preview = CreatePreview("10001");
        var model = new Worker360Model(new CapturingWorkerPreviewPlanner(preview), new CapturingRunRepository([]));
        SetPreview(model, preview);

        Assert.Equal(1, model.ChangedAttributeCount);
        Assert.Single(model.VisibleDiffRows);
        Assert.Equal("manager", model.VisibleDiffRows[0].Attribute);
        Assert.Equal(1, model.HighRiskChangeCount);
        Assert.Equal("64303 - Unpaid Leave", model.EmploymentStatusDisplay);
    }

    [Fact]
    public void VisibleDiffRows_CanShowAllRows()
    {
        var preview = CreatePreview("10001");
        var model = new Worker360Model(new CapturingWorkerPreviewPlanner(preview), new CapturingRunRepository([]))
        {
            ShowAllAttributes = true
        };
        SetPreview(model, preview);

        Assert.Equal(2, model.VisibleDiffRows.Count);
    }

    private static WorkerPreviewResult CreatePreview(string workerId)
    {
        return new WorkerPreviewResult(
            ReportPath: "/tmp/preview.jsonl",
            RunId: $"preview-{workerId}",
            PreviousRunId: null,
            Fingerprint: $"fingerprint-{workerId}",
            Mode: "Preview",
            Status: "Planned",
            ErrorMessage: null,
            ArtifactType: "WorkerPreview",
            SuccessFactorsAuth: "NativeScaffold",
            WorkerId: workerId,
            Buckets: ["updates"],
            MatchedExistingUser: true,
            ReviewCategory: null,
            ReviewCaseType: null,
            Reason: null,
            OperatorActionSummary: null,
            SamAccountName: workerId,
            ManagerDistinguishedName: "CN=Manager,OU=LabUsers,DC=example,DC=com",
            TargetOu: "OU=LabUsers,DC=example,DC=com",
            CurrentDistinguishedName: "CN=Sample101\\, Winnie,OU=LabUsers,DC=example,DC=com",
            CurrentEnabled: true,
            ProposedEnable: true,
            OperationSummary: new OperationSummary(
                Action: $"Update attributes for {workerId}",
                Effect: "1 attribute change.",
                TargetOu: "OU=LabUsers,DC=example,DC=com",
                FromOu: null,
                ToOu: "OU=LabUsers,DC=example,DC=com"),
            DiffRows:
            [
                new DiffRow("manager", "managerId", "old-manager", "new-manager", true),
                new DiffRow("department", "department", "IT", "IT", false)
            ],
            SourceAttributes:
            [
                new SourceAttributeRow("emplStatus", "64303"),
                new SourceAttributeRow("managerId", "90001")
            ],
            UsedSourceAttributes: [],
            UnusedSourceAttributes: [],
            MissingSourceAttributes: [],
            Entries: [],
            DecisionSteps:
            [
                new ProvisioningDecisionStep("Source Worker", "Loaded", "Worker loaded.")
            ]);
    }

    private static void SetPreview(Worker360Model model, WorkerPreviewResult preview)
    {
        typeof(Worker360Model)
            .GetProperty(nameof(Worker360Model.Preview))!
            .SetValue(model, preview);
    }

    private sealed class CapturingWorkerPreviewPlanner(WorkerPreviewResult preview) : IWorkerPreviewPlanner
    {
        public string? LastWorkerId { get; private set; }

        public Task<WorkerPreviewResult> PreviewAsync(string workerId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            LastWorkerId = workerId;
            return Task.FromResult(preview);
        }
    }

    private sealed class ThrowingWorkerPreviewPlanner(Exception exception) : IWorkerPreviewPlanner
    {
        public Task<WorkerPreviewResult> PreviewAsync(string workerId, CancellationToken cancellationToken)
        {
            _ = workerId;
            _ = cancellationToken;
            return Task.FromException<WorkerPreviewResult>(exception);
        }
    }

    private sealed class CapturingRunRepository(IReadOnlyList<WorkerPreviewHistoryItem> history) : IRunRepository
    {
        public string? LastHistoryWorkerId { get; private set; }

        public Task<IReadOnlyList<RunSummary>> ListRunsAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<RunSummary>>([]);
        }

        public Task<RunDetail?> GetRunAsync(string runId, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = cancellationToken;
            return Task.FromResult<RunDetail?>(null);
        }

        public Task<WorkerPreviewResult?> GetWorkerPreviewAsync(string runId, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = cancellationToken;
            return Task.FromResult<WorkerPreviewResult?>(null);
        }

        public Task<IReadOnlyList<WorkerPreviewHistoryItem>> ListWorkerPreviewHistoryAsync(string workerId, int take, CancellationToken cancellationToken)
        {
            _ = take;
            _ = cancellationToken;
            LastHistoryWorkerId = workerId;
            return Task.FromResult(history);
        }

        public Task SaveRunAsync(RunRecord run, CancellationToken cancellationToken)
        {
            _ = run;
            _ = cancellationToken;
            return Task.CompletedTask;
        }

        public Task ReplaceRunEntriesAsync(string runId, IReadOnlyList<RunEntryRecord> entries, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = entries;
            _ = cancellationToken;
            return Task.CompletedTask;
        }

        public Task AppendRunEntryAsync(RunEntryRecord entry, CancellationToken cancellationToken)
        {
            _ = entry;
            _ = cancellationToken;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RunEntry>> GetRunEntriesAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? employmentStatus, string? entryId, int skip, int take, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = bucket;
            _ = workerId;
            _ = reason;
            _ = filter;
            _ = employmentStatus;
            _ = entryId;
            _ = skip;
            _ = take;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<RunEntry>>([]);
        }

        public Task<IReadOnlyList<ChangedAttributeTotal>> GetRunEntryAttributeTotalsAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? employmentStatus, string? entryId, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = bucket;
            _ = workerId;
            _ = reason;
            _ = filter;
            _ = employmentStatus;
            _ = entryId;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<ChangedAttributeTotal>>([]);
        }

        public Task<int> CountRunEntriesAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? employmentStatus, string? entryId, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = bucket;
            _ = workerId;
            _ = reason;
            _ = filter;
            _ = employmentStatus;
            _ = entryId;
            _ = cancellationToken;
            return Task.FromResult(0);
        }
    }
}
