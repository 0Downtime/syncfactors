using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Api;
using SyncFactors.Api.Pages;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Api.Tests;

public sealed class PreviewModelTests
{
    [Fact]
    public async Task OnGetAsync_LoadsPreviewForRequestedWorker()
    {
        var preview = CreatePreview(workerId: "10001");
        var planner = new CapturingWorkerPreviewPlanner(preview);
        var model = new PreviewModel(planner, new StubApplyPreviewService(), new StubRunRepository(preview))
        {
            WorkerId = "10001"
        };

        await model.OnGetAsync(CancellationToken.None);

        Assert.Equal("10001", planner.LastWorkerId);
        Assert.Same(preview, model.Preview);
        Assert.Null(model.ErrorMessage);
    }

    [Fact]
    public async Task OnPostApplyAsync_UsesSameWorkerIdAndReloadsPreview()
    {
        var preview = CreatePreview(workerId: "10001");
        var planner = new CapturingWorkerPreviewPlanner(preview);
        var applyService = new CapturingApplyPreviewService(
            new DirectoryCommandResult(
                Succeeded: true,
                Action: "UpdateUser",
                SamAccountName: "10001",
                DistinguishedName: "CN=Sample101\\, Winnie,OU=LabUsers,DC=example,DC=com",
                Message: "Updated AD user 10001.",
                RunId: "apply-10001-20260327120000"));
        var model = new PreviewModel(planner, applyService, new StubRunRepository(preview))
        {
            WorkerId = "10001",
            PreviewRunId = preview.RunId!,
            PreviewFingerprint = preview.Fingerprint,
            AcknowledgeRealSync = true
        };

        var result = await model.OnPostApplyAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(applyService.LastRequest);
        Assert.Equal("10001", applyService.LastRequest!.WorkerId);
        Assert.True(applyService.LastRequest.AcknowledgeRealSync);
        Assert.Equal(0, planner.CallCount);
        Assert.Same(preview, model.Preview);
        Assert.NotNull(model.ApplyResult);
        Assert.Equal("apply-10001-20260327120000", model.ApplyResult!.RunId);
        Assert.Null(model.ErrorMessage);
    }

    [Fact]
    public async Task OnGetAsync_LoadsSavedPreviewWhenRunIdIsProvided()
    {
        var preview = CreatePreview(workerId: "10001");
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

    [Fact]
    public async Task OnPostApplyAsync_ParsesActiveDirectoryFailureDiagnostics()
    {
        var preview = CreatePreview(workerId: "10001");
        var applyService = new ThrowingApplyPreviewService(new InvalidOperationException(
            "Active Directory command 'UpdateUser' failed against LDAP server 'localhost'. The server cannot handle directory requests. Details: Step=ModifyAttributes WorkerId=10001 SamAccountName=winnie DistinguishedName=CN=Sample101\\, Winnie,OU=LabUsers,DC=example,DC=com Attributes=displayName,department,company,streetAddress ManagerId=90001 Next check: Check the target OU, manager resolution, and whether the account already exists with unexpected state."));
        var model = new PreviewModel(new CapturingWorkerPreviewPlanner(preview), applyService, new StubRunRepository(preview))
        {
            WorkerId = "10001",
            PreviewRunId = preview.RunId!,
            PreviewFingerprint = preview.Fingerprint,
            AcknowledgeRealSync = true
        };

        var result = await model.OnPostApplyAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(model.ErrorDiagnostics);
        Assert.Equal("Check the target OU, manager resolution, and whether the account already exists with unexpected state.", model.ErrorDiagnostics!.Guidance);
        Assert.Contains(model.ErrorDiagnostics.Details, item => item.Label == "Step" && item.Value == "ModifyAttributes");
        Assert.Contains(model.ErrorDiagnostics.Details, item => item.Label == "Attributes" && item.Value == "displayName,department,company,streetAddress");
        Assert.Contains(model.ErrorDiagnostics.Details, item => item.Label == "Manager ID" && item.Value == "90001");
    }

    [Fact]
    public void ActiveDirectoryFailureDiagnostics_Parse_HandlesCreateFailureContext()
    {
        var diagnostics = ActiveDirectoryFailureDiagnostics.Parse(
            "Active Directory command 'CreateUser' failed against LDAP server '192.0.2.10'. A value in the request is invalid. LDAP error code 19. Server detail: 000021C8: AtrErr: DSID-03200E96, problem 1005 (CONSTRAINT_ATT_TYPE), Att 90290 (userPrincipalName) Details: Step=CreateUser WorkerId=45086 SamAccountName=45086 DistinguishedName=CN=45086,OU=Users,DC=example,DC=com TargetOu=OU=Users,DC=example,DC=com UserPrincipalName=45086@example.com Mail=45086@example.com IdentityAttribute=employeeID IdentityValue=45086 ManagerId=90001 ManagerDistinguishedName=CN=Manager,OU=Users,DC=example,DC=com Next check: Check the target OU, manager resolution, and whether the account already exists with unexpected state.");

        Assert.NotNull(diagnostics);
        Assert.Contains(diagnostics!.Details, item => item.Label == "Step" && item.Value == "CreateUser");
        Assert.Contains(diagnostics.Details, item => item.Label == "Target OU" && item.Value == "OU=Users,DC=example,DC=com");
        Assert.Contains(diagnostics.Details, item => item.Label == "UPN" && item.Value == "45086@example.com");
        Assert.Contains(diagnostics.Details, item => item.Label == "Mail" && item.Value == "45086@example.com");
        Assert.Contains(diagnostics.Details, item => item.Label == "Identity Attribute" && item.Value == "employeeID");
        Assert.Contains(diagnostics.Details, item => item.Label == "Identity Value" && item.Value == "45086");
        Assert.Contains(diagnostics.Details, item => item.Label == "Manager Distinguished Name" && item.Value == "CN=Manager,OU=Users,DC=example,DC=com");
    }

    [Fact]
    public void ActiveDirectoryFailureDiagnostics_Parse_HandlesDisplayNameWithSpaces()
    {
        var diagnostics = ActiveDirectoryFailureDiagnostics.Parse(
            "Active Directory command 'UpdateUser' failed against LDAP server 'localhost'. The server cannot handle directory requests. Details: Step=RenameUser WorkerId=10001 SamAccountName=winnie DistinguishedName=CN=Old\\, Name,OU=LabUsers,DC=example,DC=com CurrentCn=Old\\, Name DesiredCn=Doe, Winnie Attributes=displayName Next check: Check the target OU.");

        Assert.NotNull(diagnostics);
        Assert.Contains(diagnostics!.Details, item => item.Label == "Desired CN" && item.Value == "Doe, Winnie");
        Assert.Contains(diagnostics.Details, item => item.Label == "Current CN" && item.Value == "Old\\, Name");
    }

    [Fact]
    public void GetEmploymentStatusDisplay_FormatsKnownCodeFromSourceAttributes()
    {
        var preview = CreatePreview(
            workerId: "10001",
            sourceAttributes:
            [
                new SourceAttributeRow("emplStatus", "64303")
            ]);
        var model = new PreviewModel(new CapturingWorkerPreviewPlanner(preview), new StubApplyPreviewService(), new StubRunRepository(preview));

        Assert.Equal("64303 - Unpaid Leave", model.GetEmploymentStatusDisplay(preview));
    }

    private static WorkerPreviewResult CreatePreview(string workerId, IReadOnlyList<SourceAttributeRow>? sourceAttributes = null)
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
            SourceAttributes: sourceAttributes ?? [],
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

    private sealed class ThrowingApplyPreviewService(Exception exception) : IApplyPreviewService
    {
        public Task<DirectoryCommandResult> ApplyAsync(ApplyPreviewRequest request, CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            return Task.FromException<DirectoryCommandResult>(exception);
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

        public Task AppendRunEntryAsync(RunEntryRecord entry, CancellationToken cancellationToken)
        {
            _ = entry;
            _ = cancellationToken;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RunEntry>> GetRunEntriesAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? entryId, int skip, int take, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = bucket;
            _ = workerId;
            _ = reason;
            _ = filter;
            _ = entryId;
            _ = skip;
            _ = take;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<RunEntry>>([]);
        }

        public Task<int> CountRunEntriesAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? entryId, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = bucket;
            _ = workerId;
            _ = reason;
            _ = filter;
            _ = entryId;
            _ = cancellationToken;
            return Task.FromResult(0);
        }

        public Task<IReadOnlyList<ChangedAttributeTotal>> GetRunEntryAttributeTotalsAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? entryId, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = bucket;
            _ = workerId;
            _ = reason;
            _ = filter;
            _ = entryId;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<ChangedAttributeTotal>>([]);
        }
    }
}
