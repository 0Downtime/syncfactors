using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Api.Pages;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Api.Tests;

public sealed class PreviewModelTests
{
    [Fact]
    public async Task OnGetAsync_LoadsPreviewForRequestedWorker()
    {
        var preview = CreatePreview(workerId: "mock-10001");
        var planner = new CapturingWorkerPreviewPlanner(preview);
        var model = new PreviewModel(planner, new StubApplyPreviewService(), new StubRunRepository(preview))
        {
            WorkerId = "mock-10001"
        };

        await model.OnGetAsync(CancellationToken.None);

        Assert.Equal("mock-10001", planner.LastWorkerId);
        Assert.Same(preview, model.Preview);
        Assert.Null(model.ErrorMessage);
    }

    [Fact]
    public async Task OnPostApplyAsync_UsesSameWorkerIdAndReloadsPreview()
    {
        var preview = CreatePreview(workerId: "mock-10001");
        var planner = new CapturingWorkerPreviewPlanner(preview);
        var applyService = new CapturingApplyPreviewService(
            new DirectoryCommandResult(
                Succeeded: true,
                Action: "UpdateUser",
                SamAccountName: "mock-10001",
                DistinguishedName: "CN=Sample101\\, Winnie,OU=LabUsers,DC=example,DC=com",
                Message: "Updated AD user mock-10001.",
                RunId: "apply-mock-10001-20260327120000"));
        var model = new PreviewModel(planner, applyService, new StubRunRepository(preview))
        {
            WorkerId = "mock-10001",
            PreviewRunId = preview.RunId!,
            PreviewFingerprint = preview.Fingerprint,
            ConfirmationText = ApplyPreviewService.BuildConfirmationText(preview)
        };

        var result = await model.OnPostApplyAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(applyService.LastRequest);
        Assert.Equal("mock-10001", applyService.LastRequest!.WorkerId);
        Assert.Equal(0, planner.CallCount);
        Assert.Same(preview, model.Preview);
        Assert.NotNull(model.ApplyResult);
        Assert.Equal("apply-mock-10001-20260327120000", model.ApplyResult!.RunId);
        Assert.Null(model.ErrorMessage);
    }

    [Fact]
    public async Task OnGetAsync_LoadsSavedPreviewWhenRunIdIsProvided()
    {
        var preview = CreatePreview(workerId: "mock-10001");
        var planner = new CapturingWorkerPreviewPlanner(preview);
        var model = new PreviewModel(planner, new StubApplyPreviewService(), new StubRunRepository(preview))
        {
            SavedRunId = preview.RunId!
        };

        await model.OnGetAsync(CancellationToken.None);

        Assert.Equal(0, planner.CallCount);
        Assert.Same(preview, model.Preview);
        Assert.Equal(preview.WorkerId, model.WorkerId);
        Assert.Equal(preview.RunId, model.PreviewRunId);
        Assert.Null(model.ErrorMessage);
    }

    [Fact]
    public async Task OnPostApplyAsync_RequiresWorkerId()
    {
        var preview = CreatePreview("ignored");
        var model = new PreviewModel(new CapturingWorkerPreviewPlanner(preview), new StubApplyPreviewService(), new StubRunRepository(preview));

        var result = await model.OnPostApplyAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal("Worker ID is required.", model.ErrorMessage);
        Assert.Null(model.ApplyResult);
        Assert.Null(model.Preview);
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
                Effect: "3 attribute changes.",
                TargetOu: "OU=LabUsers,DC=example,DC=com",
                FromOu: null,
                ToOu: "OU=LabUsers,DC=example,DC=com"),
            DiffRows:
            [
                new DiffRow("displayName", "sAMAccountName", "Old Name", workerId, true),
                new DiffRow("UserPrincipalName", "resolved email local-part", "old.email@Exampleenergy.com", "preview.email@Exampleenergy.com", true),
                new DiffRow("mail", "resolved email local-part", "old.email@Exampleenergy.com", "preview.email@Exampleenergy.com", true)
            ],
            SourceAttributes: [],
            UsedSourceAttributes: [],
            UnusedSourceAttributes: [],
            MissingSourceAttributes: [],
            Entries: []);
    }

    private sealed class CapturingWorkerPreviewPlanner(WorkerPreviewResult preview) : IWorkerPreviewPlanner
    {
        public string? LastWorkerId { get; private set; }

        public int CallCount { get; private set; }

        public Task<WorkerPreviewResult> PreviewAsync(string workerId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            LastWorkerId = workerId;
            CallCount++;
            return Task.FromResult(preview);
        }
    }

    private sealed class CapturingApplyPreviewService(DirectoryCommandResult result) : IApplyPreviewService
    {
        public ApplyPreviewRequest? LastRequest { get; private set; }

        public Task<DirectoryCommandResult> ApplyAsync(ApplyPreviewRequest request, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            LastRequest = request;
            return Task.FromResult(result);
        }
    }

    private sealed class StubApplyPreviewService : IApplyPreviewService
    {
        public Task<DirectoryCommandResult> ApplyAsync(ApplyPreviewRequest request, CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            throw new InvalidOperationException("Apply should not be called in this test.");
        }
    }

    private sealed class StubRunRepository(WorkerPreviewResult preview) : IRunRepository
    {
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
            return Task.FromResult<WorkerPreviewResult?>(preview);
        }

        public Task<IReadOnlyList<WorkerPreviewHistoryItem>> ListWorkerPreviewHistoryAsync(string workerId, int take, CancellationToken cancellationToken)
        {
            _ = workerId;
            _ = take;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<WorkerPreviewHistoryItem>>([]);
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

        public Task<IReadOnlyList<RunEntry>> GetRunEntriesAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? entryId, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = bucket;
            _ = workerId;
            _ = reason;
            _ = filter;
            _ = entryId;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<RunEntry>>([]);
        }
    }
}
