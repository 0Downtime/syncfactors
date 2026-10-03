using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Api;
using SyncFactors.Api.Pages;
using SyncFactors.Contracts;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;

namespace SyncFactors.Api.Tests;

public sealed class PreviewModelTests
{
    [Fact]
    public async Task Worker360_OnGetAsync_WithoutWorkerId_DoesNotCallPreviewPlanner()
    {
        var preview = CreatePreview("ignored");
        var planner = new CapturingWorkerPreviewPlanner(preview);
        var model = new Worker360Model(planner, new StubApplyPreviewService(), new StubRunRepository(preview));

        await model.OnGetAsync(CancellationToken.None);

        Assert.Equal(0, planner.CallCount);
        Assert.Null(model.Preview);
        Assert.Empty(model.RunHistory);
    }

    [Fact]
    public async Task OnGetAsync_LoadsPreviewForRequestedWorker()
    {
        var preview = CreatePreview(workerId: "10001");
        var planner = new CapturingWorkerPreviewPlanner(preview);
        var model = new Worker360Model(planner, new StubApplyPreviewService(), new StubRunRepository(preview))
        {
            WorkerId = "10001"
        };

        await model.OnGetAsync(CancellationToken.None);

        Assert.Equal("10001", planner.LastWorkerId);
        Assert.Same(preview, model.Preview);
        Assert.Equal("preview-10001", model.RunHistory.Single().RunId);
        Assert.Null(model.ErrorMessage);
    }

    [Fact]
    public async Task Worker360_OnGetAsync_PreviewFailureKeepsRunHistory()
    {
        var preview = CreatePreview(workerId: "10001");
        var model = new Worker360Model(
            new ThrowingWorkerPreviewPlanner(new InvalidOperationException("preview failed")),
            new StubApplyPreviewService(),
            new StubRunRepository(preview))
        {
            WorkerId = "10001"
        };

        await model.OnGetAsync(CancellationToken.None);

        Assert.Null(model.Preview);
        Assert.Equal("preview failed", model.ErrorMessage);
        Assert.Equal("preview-10001", model.RunHistory.Single().RunId);
    }

    [Fact]
    public async Task Preview_OnGetAsync_WithWorkerId_RedirectsToWorker360()
    {
        var preview = CreatePreview(workerId: "10001");
        var model = new PreviewModel(new StubRunRepository(preview))
        {
            WorkerId = "10001"
        };

        var result = await model.OnGetAsync(CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Workers", redirect.PageName);
        Assert.Equal("10001", redirect.RouteValues!["workerId"]);
    }

    [Fact]
    public async Task Preview_OnGetAsync_WithRunId_RedirectsToWorker360Worker()
    {
        var preview = CreatePreview(workerId: "10001");
        var model = new PreviewModel(new StubRunRepository(preview))
        {
            SavedRunId = preview.RunId,
            ShowAllAttributes = true
        };

        var result = await model.OnGetAsync(CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Workers", redirect.PageName);
        Assert.Equal("10001", redirect.RouteValues!["workerId"]);
        Assert.Equal(preview.RunId, redirect.RouteValues["runId"]);
        Assert.True((bool)redirect.RouteValues["showAllAttributes"]!);
    }

    [Fact]
    public async Task Preview_OnGetAsync_WithUnknownRunId_RedirectsToWorker360Run()
    {
        var preview = CreatePreview(workerId: "10001");
        var model = new PreviewModel(new StubRunRepository(preview, resolvePreview: false))
        {
            SavedRunId = "missing-preview"
        };

        var result = await model.OnGetAsync(CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Workers", redirect.PageName);
        Assert.Equal("missing-preview", redirect.RouteValues!["runId"]);
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
        var model = new Worker360Model(planner, applyService, new StubRunRepository(preview))
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
    public async Task OnPostApplyAsync_WritesEquivalentAuditEvent()
    {
        var preview = CreatePreview(workerId: "10001");
        var audit = new CapturingSecurityAuditService();
        var model = new Worker360Model(
            new CapturingWorkerPreviewPlanner(preview),
            new CapturingApplyPreviewService(new DirectoryCommandResult(true, "UpdateUser", "10001", null, "Updated.", "apply-10001")),
            new StubRunRepository(preview),
            audit: audit)
        {
            WorkerId = "10001",
            PreviewRunId = preview.RunId!,
            PreviewFingerprint = preview.Fingerprint,
            AcknowledgeRealSync = true
        };

        await model.OnPostApplyAsync(CancellationToken.None);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal("PreviewApplied", entry.EventType);
        Assert.Equal("Success", entry.Outcome);
        Assert.Equal("10001", entry.Fields["WorkerId"]);
        Assert.Equal(preview.RunId, entry.Fields["PreviewRunId"]);
    }

    [Fact]
    public async Task OnPostApplyAsync_PreservesApplyOutcomeWhenAuditWriteFailsWithoutExposingDetails()
    {
        var preview = CreatePreview(workerId: "10001");
        var model = new Worker360Model(
            new CapturingWorkerPreviewPlanner(preview),
            new CapturingApplyPreviewService(new DirectoryCommandResult(true, "UpdateUser", "10001", null, "Updated.", "apply-10001")),
            new StubRunRepository(preview),
            audit: new ThrowingSecurityAuditService(new UnauthorizedAccessException("/secret/audit-path")))
        {
            WorkerId = "10001",
            PreviewRunId = preview.RunId!,
            PreviewFingerprint = preview.Fingerprint,
            AcknowledgeRealSync = true
        };

        var result = await model.OnPostApplyAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(model.ApplyResult);
        Assert.True(model.ApplyResult!.Succeeded);
        Assert.Equal("The action completed, but security audit recording failed.", model.ErrorMessage);
        Assert.DoesNotContain("/secret/audit-path", model.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnPostApplyAsync_DoesNotCallApplyServiceWhenDryRunOnlyIsEnabled()
    {
        var preview = CreatePreview(workerId: "10001");
        var planner = new CapturingWorkerPreviewPlanner(preview);
        var applyService = new StubApplyPreviewService();
        var model = new Worker360Model(
            planner,
            applyService,
            new StubRunRepository(preview),
            new RealSyncSettings(Enabled: true, DryRunOnly: true))
        {
            WorkerId = "10001",
            PreviewRunId = preview.RunId!,
            PreviewFingerprint = preview.Fingerprint,
            AcknowledgeRealSync = true
        };

        var result = await model.OnPostApplyAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.False(model.CanApplyPreview);
        Assert.Equal("Dry-run-only mode is enabled. Live AD writes are disabled for this environment.", model.ErrorMessage);
        Assert.Same(preview, model.Preview);
        Assert.Null(model.ApplyResult);
    }

    [Fact]
    public async Task PreviewApplyCapability_IsDisabledWhenAtomicGatewayIsUnavailable()
    {
        var preview = CreatePreview(workerId: "10001");
        var applyService = new StubApplyPreviewService(canApplyPreview: false);
        var model = new Worker360Model(
            new CapturingWorkerPreviewPlanner(preview),
            applyService,
            new StubRunRepository(preview),
            new RealSyncSettings(Enabled: true, DryRunOnly: false))
        {
            WorkerId = "10001",
            PreviewRunId = preview.RunId!,
            PreviewFingerprint = preview.Fingerprint,
            AcknowledgeRealSync = true
        };

        var result = await model.OnPostApplyAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.False(model.CanApplyPreview);
        Assert.Equal(applyService.CapabilityUnavailableMessage, model.ErrorMessage);
        Assert.Null(model.ApplyResult);
    }

    [Fact]
    public async Task OnGetAsync_LoadsSavedPreviewWhenRunIdIsProvided()
    {
        var preview = CreatePreview(workerId: "10001");
        var planner = new CapturingWorkerPreviewPlanner(preview);
        var model = new Worker360Model(planner, new StubApplyPreviewService(), new StubRunRepository(preview))
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
    public async Task OnGetAsync_WithUnknownSavedPreviewShowsError()
    {
        var preview = CreatePreview(workerId: "10001");
        var model = new Worker360Model(
            new CapturingWorkerPreviewPlanner(preview),
            new StubApplyPreviewService(),
            new StubRunRepository(preview, resolvePreview: false))
        {
            SavedRunId = "missing-preview"
        };

        await model.OnGetAsync(CancellationToken.None);

        Assert.Null(model.Preview);
        Assert.Equal("Preview run missing-preview could not be resolved.", model.ErrorMessage);
    }

    [Fact]
    public async Task OnPostApplyAsync_RequiresPreviewRunAndFingerprint()
    {
        var preview = CreatePreview(workerId: "10001");
        var model = new Worker360Model(new CapturingWorkerPreviewPlanner(preview), new StubApplyPreviewService(), new StubRunRepository(preview))
        {
            WorkerId = "10001"
        };

        var result = await model.OnPostApplyAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal("Refresh preview before applying.", model.ErrorMessage);
        Assert.Same(preview, model.Preview);
    }

    [Fact]
    public async Task OnPostApplyAsync_RequiresWorkerId()
    {
        var preview = CreatePreview("ignored");
        var model = new Worker360Model(new CapturingWorkerPreviewPlanner(preview), new StubApplyPreviewService(), new StubRunRepository(preview));

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
        var model = new Worker360Model(new CapturingWorkerPreviewPlanner(preview), applyService, new StubRunRepository(preview))
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
            "Active Directory command 'CreateUser' failed against LDAP server '192.0.2.35'. A value in the request is invalid. LDAP error code 19. Server detail: 000021C8: AtrErr: DSID-03200E96, problem 1005 (CONSTRAINT_ATT_TYPE), Att 90290 (userPrincipalName) Details: Step=CreateUserAddRequest WorkerId=45086 SamAccountName=45086 DistinguishedName=CN=45086,OU=Users,DC=example,DC=com TargetOu=OU=Users,DC=example,DC=com UserPrincipalName=45086@example.com Mail=45086@example.com IdentityAttribute=employeeID IdentityValue=45086 CreateAttributes=objectClass,cn,displayName,sAMAccountName,userPrincipalName,mail,userAccountControl,employeeID ManagerId=90001 ManagerDistinguishedName=CN=Manager,OU=Users,DC=example,DC=com Next check: Check the target OU, manager resolution, and whether the account already exists with unexpected state.");

        Assert.NotNull(diagnostics);
        Assert.Contains(diagnostics!.Details, item => item.Label == "Step" && item.Value == "CreateUserAddRequest");
        Assert.Contains(diagnostics.Details, item => item.Label == "Target OU" && item.Value == "OU=Users,DC=example,DC=com");
        Assert.Contains(diagnostics.Details, item => item.Label == "UPN" && item.Value == "45086@example.com");
        Assert.Contains(diagnostics.Details, item => item.Label == "Mail" && item.Value == "45086@example.com");
        Assert.Contains(diagnostics.Details, item => item.Label == "Identity Attribute" && item.Value == "employeeID");
        Assert.Contains(diagnostics.Details, item => item.Label == "Identity Value" && item.Value == "45086");
        Assert.Contains(diagnostics.Details, item => item.Label == "Create Attributes" && item.Value == "objectClass,cn,displayName,sAMAccountName,userPrincipalName,mail,userAccountControl,employeeID");
        Assert.Contains(diagnostics.Details, item => item.Label == "Manager Distinguished Name" && item.Value == "CN=Manager,OU=Users,DC=example,DC=com");
    }

    [Fact]
    public void ActiveDirectoryFailureDiagnostics_Parse_HandlesPreflightIdentityConflict()
    {
        var diagnostics = ActiveDirectoryFailureDiagnostics.Parse(
            "Active Directory command 'CreateUser' failed against LDAP server '192.0.2.35'. A different AD account already uses userPrincipalName 'brian.oliver@example.test' for create worker 45086. Details: Step=PreflightIdentityConflict WorkerId=45086 SamAccountName=45086 DistinguishedName=CN=45086,OU=Active,OU=SyncFactors-Users,DC=example,DC=test TargetOu=OU=Active,OU=SyncFactors-Users,DC=example,DC=test UserPrincipalName=brian.oliver@example.test Mail=brian.oliver@example.test IdentityAttribute=sAMAccountName IdentityValue=45086 ConflictingAttribute=userPrincipalName ConflictingValue=brian.oliver@example.test ExistingSamAccountName=boliver ExistingDisplayName=Oliver, Brian ExistingDistinguishedName=CN=Brian Oliver,OU=Active,OU=SyncFactors-Users,DC=example,DC=test ExistingUserPrincipalName=brian.oliver@example.test ExistingMail=brian.oliver@example.test ManagerId=43114 ManagerDistinguishedName=CN=43114,OU=Active,OU=SyncFactors-Users,DC=example,DC=test Next check: Resolve the existing AD account that already owns this SAM, UPN, or mail value before retrying.");

        Assert.NotNull(diagnostics);
        Assert.Contains(diagnostics!.Details, item => item.Label == "Conflicting Attribute" && item.Value == "userPrincipalName");
        Assert.Contains(diagnostics.Details, item => item.Label == "Conflicting Value" && item.Value == "brian.oliver@example.test");
        Assert.Contains(diagnostics.Details, item => item.Label == "Existing SAM" && item.Value == "boliver");
        Assert.Contains(diagnostics.Details, item => item.Label == "Existing Display Name" && item.Value == "Oliver, Brian");
        Assert.Contains(diagnostics.Details, item => item.Label == "Existing Distinguished Name" && item.Value == "CN=Brian Oliver,OU=Active,OU=SyncFactors-Users,DC=example,DC=test");
        Assert.Contains(diagnostics.Details, item => item.Label == "Existing UPN" && item.Value == "brian.oliver@example.test");
        Assert.Contains(diagnostics.Details, item => item.Label == "Existing Mail" && item.Value == "brian.oliver@example.test");
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
        Assert.Equal("64303 - Unpaid Leave", Worker360Model.GetEmploymentStatusDisplay(preview));
    }

    [Fact]
    public async Task Worker360_GetSourceSummary_PrefersKnownFieldsThenUsedFields()
    {
        var preview = CreatePreview(
            workerId: "10001",
            sourceAttributes:
            [
                new SourceAttributeRow("department", "Engineering"),
                new SourceAttributeRow("personIdExternal", "10001"),
                new SourceAttributeRow("unlistedSource", "ignored")
            ],
            usedSourceAttributes:
            [
                new SourceAttributeRow("customRouting", "A"),
                new SourceAttributeRow("department", "duplicate")
            ]);
        var model = new Worker360Model(new CapturingWorkerPreviewPlanner(preview), new StubApplyPreviewService(), new StubRunRepository(preview))
        {
            WorkerId = "10001"
        };

        await model.OnGetAsync(CancellationToken.None);
        var rows = model.GetSourceSummary();

        Assert.Equal(["personIdExternal", "department", "customRouting"], rows.Select(row => row.Attribute).ToArray());
    }

    [Fact]
    public async Task Worker360_GetVisibleDiffRows_FiltersChangedRowsUnlessShowingAll()
    {
        var preview = CreatePreview(workerId: "10001", includeUnchangedDiff: true);
        var model = new Worker360Model(new CapturingWorkerPreviewPlanner(preview), new StubApplyPreviewService(), new StubRunRepository(preview))
        {
            WorkerId = "10001"
        };

        await model.OnGetAsync(CancellationToken.None);
        Assert.All(model.GetVisibleDiffRows(), row => Assert.True(row.Changed));

        model.ShowAllAttributes = true;
        Assert.Contains(model.GetVisibleDiffRows(), row => !row.Changed);
    }

    [Fact]
    public void Worker360_GetDiffGroup_MapsKnownAttributeFamilies()
    {
        Assert.Equal("Identity", Worker360Model.GetDiffGroup(new DiffRow("userPrincipalName", "", "", "", true)));
        Assert.Equal("Organization", Worker360Model.GetDiffGroup(new DiffRow("costCenter", "", "", "", true)));
        Assert.Equal("Lifecycle", Worker360Model.GetDiffGroup(new DiffRow("emplStatus", "", "", "", true)));
        Assert.Equal("Routing", Worker360Model.GetDiffGroup(new DiffRow("targetOu", "", "", "", true)));
        Assert.Equal("Access", Worker360Model.GetDiffGroup(new DiffRow("memberOf", "", "", "", true)));
        Assert.Equal("Other", Worker360Model.GetDiffGroup(new DiffRow("favoriteColor", "", "", "", true)));
    }

    private static WorkerPreviewResult CreatePreview(
        string workerId,
        IReadOnlyList<SourceAttributeRow>? sourceAttributes = null,
        IReadOnlyList<SourceAttributeRow>? usedSourceAttributes = null,
        bool includeUnchangedDiff = false)
    {
        var diffRows = new List<DiffRow>
        {
            new("displayName", "sAMAccountName", "Old Name", workerId, true),
            new("UserPrincipalName", "resolved email local-part", "old.email@example.test", "preview.email@example.test", true),
            new("mail", "resolved email local-part", "old.email@example.test", "preview.email@example.test", true)
        };
        if (includeUnchangedDiff)
        {
            diffRows.Add(new DiffRow("department", "department", "Engineering", "Engineering", false));
        }

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
            DiffRows: diffRows,
            SourceAttributes: sourceAttributes ?? [],
            UsedSourceAttributes: usedSourceAttributes ?? [],
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

    private sealed class ThrowingWorkerPreviewPlanner(Exception exception) : IWorkerPreviewPlanner
    {
        public Task<WorkerPreviewResult> PreviewAsync(string workerId, CancellationToken cancellationToken)
        {
            _ = workerId;
            _ = cancellationToken;
            return Task.FromException<WorkerPreviewResult>(exception);
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

    private sealed class StubApplyPreviewService(bool canApplyPreview = true) : IApplyPreviewService
    {
        public bool CanApplyPreview { get; } = canApplyPreview;

        public string CapabilityUnavailableMessage =>
            "The configured directory gateway cannot atomically apply a reviewed preview.";

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

    private sealed class CapturingSecurityAuditService : ISecurityAuditService
    {
        public List<(string EventType, string Outcome, IReadOnlyDictionary<string, object?> Fields)> Entries { get; } = [];

        public void Write(string eventType, string outcome, params (string Key, object? Value)[] fields) =>
            Entries.Add((eventType, outcome, fields.ToDictionary(field => field.Key, field => field.Value)));
    }

    private sealed class ThrowingSecurityAuditService(Exception exception) : ISecurityAuditService
    {
        public void Write(string eventType, string outcome, params (string Key, object? Value)[] fields)
        {
            _ = eventType;
            _ = outcome;
            _ = fields;
            throw exception;
        }
    }

    private sealed class StubRunRepository(WorkerPreviewResult preview, bool resolvePreview = true) : IRunRepository
    {
        private readonly IReadOnlyList<WorkerRunHistoryItem> workerHistory =
        [
            new WorkerRunHistoryItem(
                RunId: preview.RunId ?? "preview-run",
                EntryId: $"{preview.RunId ?? "preview-run"}:updates:0",
                ArtifactType: preview.ArtifactType ?? "WorkerPreview",
                Mode: preview.Mode ?? "Preview",
                DryRun: true,
                RunStatus: preview.Status ?? "Planned",
                RunTrigger: "Preview",
                StartedAt: DateTimeOffset.Parse("2026-04-01T12:00:00Z"),
                CompletedAt: null,
                Bucket: preview.Buckets.Count > 0 ? preview.Buckets[0] : "updates",
                BucketLabel: "Updates",
                WorkerId: preview.WorkerId,
                SamAccountName: preview.SamAccountName,
                Reason: preview.Reason,
                ReviewCaseType: preview.ReviewCaseType,
                ChangeCount: preview.DiffRows.Count(row => row.Changed),
                TopChangedAttributes: preview.DiffRows.Where(row => row.Changed).Select(row => row.Attribute).ToArray(),
                OperationSummary: preview.OperationSummary)
        ];

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
            return Task.FromResult(resolvePreview ? preview : null);
        }

        public Task<IReadOnlyList<WorkerPreviewHistoryItem>> ListWorkerPreviewHistoryAsync(string workerId, int take, CancellationToken cancellationToken)
        {
            _ = workerId;
            _ = take;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<WorkerPreviewHistoryItem>>([]);
        }

        public Task<int> CountWorkerRunHistoryAsync(string workerId, CancellationToken cancellationToken)
        {
            _ = workerId;
            _ = cancellationToken;
            return Task.FromResult(workerHistory.Count);
        }

        public Task<IReadOnlyList<WorkerRunHistoryItem>> ListWorkerRunHistoryAsync(string workerId, int skip, int take, CancellationToken cancellationToken)
        {
            _ = workerId;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<WorkerRunHistoryItem>>(workerHistory.Skip(skip).Take(take).ToArray());
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
        public Task<int> PruneTerminalRunsStartedBeforeAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<bool> VacuumIfNeededAsync(DateTimeOffset nowUtc, long minimumFreeBytes, TimeSpan minimumInterval, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<IReadOnlyList<EmploymentStatusTotal>> GetRunEntryEmploymentStatusTotalsAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? employmentStatus, string? entryId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EmploymentStatusTotal>>([]);

    }
}
