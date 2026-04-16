using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Api.Pages.Runs;
using SyncFactors.Contracts;
using SyncFactors.Domain;
using System.Text.Json;

namespace SyncFactors.Api.Tests;

public sealed class RunDetailModelTests
{
    [Fact]
    public async Task OnGetAsync_LoadsRequestedPageOfEntries()
    {
        var repository = new StubRunRepository();
        var model = new DetailModel(new RunEntriesQueryService(repository))
        {
            RunId = "bulk-1",
            PageNumber = 2
        };

        var result = await model.OnGetAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal(15, repository.LastSkip);
        Assert.Equal(15, repository.LastTake);
        Assert.Equal(120, model.TotalEntries);
        Assert.Equal(8, model.TotalPages);
        Assert.Equal(2, model.PageNumber);
        Assert.True(model.HasPreviousPage);
        Assert.True(model.HasNextPage);
        Assert.Equal(15, model.Entries.Count);
    }

    [Fact]
    public async Task OnGetAsync_LoadsAttributeTotalsForFilteredEntries()
    {
        var repository = new StubRunRepository();
        var model = new DetailModel(new RunEntriesQueryService(repository))
        {
            RunId = "bulk-1",
            Bucket = "updates",
            WorkerId = "worker-1",
            Filter = "email"
        };

        var result = await model.OnGetAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal("bulk-1", repository.LastTotalsRunId);
        Assert.Equal("updates", repository.LastTotalsBucket);
        Assert.Equal("worker-1", repository.LastTotalsWorkerId);
        Assert.Equal("email", repository.LastTotalsFilter);
        Assert.Collection(
            model.AttributeTotals,
            total =>
            {
                Assert.Equal("email", total.Attribute);
                Assert.Equal(12, total.Count);
            },
            total =>
            {
                Assert.Equal("cn", total.Attribute);
                Assert.Equal(4, total.Count);
            });
        Assert.Collection(
            model.EmploymentStatusTotals,
            total =>
            {
                Assert.Equal("64300", total.Code);
                Assert.Equal(80, total.Count);
            },
            total =>
            {
                Assert.Equal("64304", total.Code);
                Assert.Equal(40, total.Count);
            });
    }

    [Fact]
    public async Task GetFailureDiagnostics_ParsesEntryReason()
    {
        var repository = new StubRunRepository();
        var model = new DetailModel(new RunEntriesQueryService(repository))
        {
            RunId = "bulk-1"
        };

        await model.OnGetAsync(CancellationToken.None);

        var diagnostics = model.GetFailureDiagnostics(new RunEntry(
            EntryId: "entry-conflict",
            RunId: "bulk-1",
            ArtifactType: "BulkRun",
            Mode: "BulkSync",
            Bucket: "conflicts",
            BucketLabel: "Conflicts",
            WorkerId: "10001",
            SamAccountName: "winnie",
            Reason: "Active Directory command 'UpdateUser' failed against LDAP server 'localhost'. The server cannot handle directory requests. Details: Step=ModifyAttributes WorkerId=10001 SamAccountName=winnie DistinguishedName=CN=Sample101\\, Winnie,OU=LabUsers,DC=example,DC=com Attributes=displayName,department,company,streetAddress ManagerId=90001 Next check: Check the target OU, manager resolution, and whether the account already exists with unexpected state.",
            ReviewCategory: null,
            ReviewCaseType: null,
            StartedAt: DateTimeOffset.UtcNow,
            ChangeCount: 4,
            OperationSummary: null,
            FailureSummary: null,
            PrimarySummary: null,
            TopChangedAttributes: [],
            DiffRows: [],
            Item: JsonDocument.Parse("""{}""").RootElement.Clone()));

        Assert.NotNull(diagnostics);
        Assert.Contains(diagnostics!.Details, item => item.Label == "Step" && item.Value == "ModifyAttributes");
        Assert.Contains(diagnostics.Details, item => item.Label == "SAM" && item.Value == "winnie");
    }

    [Fact]
    public void GetPrimarySummaryDisplay_SuppressesDuplicateFailureSummary()
    {
        var model = new DetailModel(new RunEntriesQueryService(new StubRunRepository()));
        var entry = CreateConflictEntry(
            reason: "Active Directory command 'CreateUser' failed against LDAP server '192.0.2.10'. A value in the request is invalid. 000021C8: AtrErr: DSID-03200E96, #1: 0: 000021C8: DSID-03200E96, problem 1005 (CONSTRAINT_ATT_TYPE), data 0, Att 90290 (userPrincipalName) Next check: Check the target OU, manager resolution, and whether the account already exists with unexpected state.",
            failureSummary: "Active Directory command 'CreateUser' failed against LDAP server '192.0.2.10'. A value in the request is invalid. 000021C8: AtrErr: DSID-03200E96, #1: 0: 000021C8: DSID-03200E96, problem 1005 (CONSTRAINT_ATT_TYPE), data 0, Att 90290 (userPrincipalName) Next check: Check the target OU, manager resolution, and whether the account already exists with unexpected state.",
            primarySummary: "Active Directory command 'CreateUser' failed against LDAP server '192.0.2.10'. A value in the request is invalid. 000021C8: AtrErr: DSID-03200E96, #1: 0: 000021C8: DSID-03200E96, problem 1005 (CONSTRAINT_ATT_TYPE), data 0, Att 90290 (userPrincipalName) Next check: Check the target OU, manager resolution, and whether the account already exists with unexpected state.");

        Assert.Null(model.GetPrimarySummaryDisplay(entry));
        Assert.Equal(
            "Active Directory command 'CreateUser' failed against LDAP server '192.0.2.10'. A value in the request is invalid. 000021C8: AtrErr: DSID-03200E96, #1: 0: 000021C8: DSID-03200E96, problem 1005 (CONSTRAINT_ATT_TYPE), data 0, Att 90290 (userPrincipalName)",
            model.GetFailureSummaryDisplay(entry));
    }

    [Fact]
    public void ShouldShowReason_HidesRawReasonWhenDiagnosticsAreAvailable()
    {
        var model = new DetailModel(new RunEntriesQueryService(new StubRunRepository()));
        var entry = CreateConflictEntry(
            reason: "Active Directory command 'CreateUser' failed against LDAP server '192.0.2.10'. A value in the request is invalid. 000021C8: AtrErr: DSID-03200E96, #1: 0: 000021C8: DSID-03200E96, problem 1005 (CONSTRAINT_ATT_TYPE), data 0, Att 90290 (userPrincipalName) Next check: Check the target OU, manager resolution, and whether the account already exists with unexpected state.",
            failureSummary: "Active Directory command 'CreateUser' failed against LDAP server '192.0.2.10'. A value in the request is invalid. 000021C8: AtrErr: DSID-03200E96, #1: 0: 000021C8: DSID-03200E96, problem 1005 (CONSTRAINT_ATT_TYPE), data 0, Att 90290 (userPrincipalName) Next check: Check the target OU, manager resolution, and whether the account already exists with unexpected state.",
            primarySummary: "Active Directory command 'CreateUser' failed against LDAP server '192.0.2.10'. A value in the request is invalid. 000021C8: AtrErr: DSID-03200E96, #1: 0: 000021C8: DSID-03200E96, problem 1005 (CONSTRAINT_ATT_TYPE), data 0, Att 90290 (userPrincipalName) Next check: Check the target OU, manager resolution, and whether the account already exists with unexpected state.");

        Assert.False(model.ShouldShowReason(entry));
    }

    [Fact]
    public void GetEmploymentStatusDisplay_FormatsKnownCode()
    {
        var model = new DetailModel(new RunEntriesQueryService(new StubRunRepository()));
        var entry = new RunEntry(
            EntryId: "entry-status",
            RunId: "bulk-1",
            ArtifactType: "BulkRun",
            Mode: "BulkSync",
            Bucket: "updates",
            BucketLabel: "Updates",
            WorkerId: "10001",
            SamAccountName: "winnie",
            Reason: null,
            ReviewCategory: null,
            ReviewCaseType: null,
            StartedAt: DateTimeOffset.UtcNow,
            ChangeCount: 0,
            OperationSummary: null,
            FailureSummary: null,
            PrimarySummary: null,
            TopChangedAttributes: [],
            DiffRows: [],
            Item: JsonDocument.Parse("""{"emplStatus":"64304"}""").RootElement.Clone());

        Assert.Equal("64304 - Paid Leave", model.GetEmploymentStatusDisplay(entry));
    }

    [Fact]
    public void GetEmploymentStatus_ReturnsToneAndPillCopy()
    {
        var model = new DetailModel(new RunEntriesQueryService(new StubRunRepository()));
        var entry = new RunEntry(
            EntryId: "entry-status",
            RunId: "bulk-1",
            ArtifactType: "BulkRun",
            Mode: "BulkSync",
            Bucket: "updates",
            BucketLabel: "Updates",
            WorkerId: "10001",
            SamAccountName: "winnie",
            Reason: null,
            ReviewCategory: null,
            ReviewCaseType: null,
            StartedAt: DateTimeOffset.UtcNow,
            ChangeCount: 0,
            OperationSummary: null,
            FailureSummary: null,
            PrimarySummary: null,
            TopChangedAttributes: [],
            DiffRows: [],
            Item: JsonDocument.Parse("""{"emplStatus":"64308"}""").RootElement.Clone());

        var status = model.GetEmploymentStatus(entry);

        Assert.NotNull(status);
        Assert.Equal("Terminated", status!.Label);
        Assert.Equal("bad", status.ToneCssClass);
        Assert.Equal("Employment: Terminated", status.PillText);
        Assert.Equal("Code 64308", status.DetailText);
    }

    [Fact]
    public async Task DescribeEntryExecution_RealSyncDisableWithoutWrite_ShowsAlreadyCompliant()
    {
        var model = new DetailModel(new RunEntriesQueryService(new StubRunRepository(CreateRunDetail(dryRun: false))))
        {
            RunId = "bulk-1"
        };

        await model.OnGetAsync(CancellationToken.None);

        var entry = new RunEntry(
            EntryId: "entry-disable",
            RunId: "bulk-1",
            ArtifactType: "BulkRun",
            Mode: "BulkSync",
            Bucket: "disables",
            BucketLabel: "Disables",
            WorkerId: "40774",
            SamAccountName: "40774",
            Reason: "Inactive worker should be disabled and placed in the graveyard OU.",
            ReviewCategory: null,
            ReviewCaseType: null,
            StartedAt: DateTimeOffset.UtcNow,
            ChangeCount: 0,
            OperationSummary: null,
            FailureSummary: null,
            PrimarySummary: null,
            TopChangedAttributes: [],
            DiffRows: [],
            Item: JsonDocument.Parse("""{"currentEnabled":false,"proposedEnable":false,"operations":[]}""").RootElement.Clone());

        var status = model.DescribeEntryExecution(entry);

        Assert.Equal("Already Compliant", status.Label);
        Assert.Equal("neutral", status.ToneCssClass);
        Assert.Contains(status.Facts, fact => fact.Label == "Run Type" && fact.Value == "Real Sync");
        Assert.Contains(status.Facts, fact => fact.Label == "Execution" && fact.Value == "No AD write required");
        Assert.Contains(status.Facts, fact => fact.Label == "Planned Action" && fact.Value == "Keep account disabled");
    }

    [Fact]
    public async Task DescribeEntryExecution_RealSyncAppliedDisable_ShowsApplied()
    {
        var model = new DetailModel(new RunEntriesQueryService(new StubRunRepository(CreateRunDetail(dryRun: false))))
        {
            RunId = "bulk-1"
        };

        await model.OnGetAsync(CancellationToken.None);

        var entry = new RunEntry(
            EntryId: "entry-disable-applied",
            RunId: "bulk-1",
            ArtifactType: "BulkRun",
            Mode: "BulkSync",
            Bucket: "disables",
            BucketLabel: "Disables",
            WorkerId: "40774",
            SamAccountName: "40774",
            Reason: null,
            ReviewCategory: null,
            ReviewCaseType: null,
            StartedAt: DateTimeOffset.UtcNow,
            ChangeCount: 1,
            OperationSummary: null,
            FailureSummary: null,
            PrimarySummary: null,
            TopChangedAttributes: ["enabled"],
            DiffRows: [new DiffRow("enabled", null, "true", "false", true)],
            Item: JsonDocument.Parse("""{"action":"DisableUser","applied":true,"succeeded":true,"operations":[{"kind":"DisableUser"}],"currentEnabled":true,"proposedEnable":false}""").RootElement.Clone());

        var status = model.DescribeEntryExecution(entry);

        Assert.Equal("Applied", status.Label);
        Assert.Equal("good", status.ToneCssClass);
        Assert.Contains(status.Facts, fact => fact.Label == "Execution" && fact.Value == "Executed");
        Assert.Contains(status.Facts, fact => fact.Label == "Result" && fact.Value == "AD write succeeded");
        Assert.Contains(status.Facts, fact => fact.Label == "Planned Action" && fact.Value == "Disable account");
    }

    [Fact]
    public async Task DescribeEntryExecution_PreviewDisable_ShowsPreviewOnly()
    {
        var model = new DetailModel(new RunEntriesQueryService(new StubRunRepository(CreateRunDetail(
            artifactType: "WorkerPreview",
            mode: "Preview",
            dryRun: true))))
        {
            RunId = "preview-1"
        };

        await model.OnGetAsync(CancellationToken.None);

        var entry = new RunEntry(
            EntryId: "entry-preview-disable",
            RunId: "preview-1",
            ArtifactType: "WorkerPreview",
            Mode: "Preview",
            Bucket: "disables",
            BucketLabel: "Disables",
            WorkerId: "40774",
            SamAccountName: "40774",
            Reason: "Inactive worker should be disabled and placed in the graveyard OU.",
            ReviewCategory: null,
            ReviewCaseType: null,
            StartedAt: DateTimeOffset.UtcNow,
            ChangeCount: 0,
            OperationSummary: null,
            FailureSummary: null,
            PrimarySummary: null,
            TopChangedAttributes: [],
            DiffRows: [],
            Item: JsonDocument.Parse("""{"operations":[]}""").RootElement.Clone());

        var status = model.DescribeEntryExecution(entry);

        Assert.Equal("Preview No Action", status.Label);
        Assert.Equal("neutral", status.ToneCssClass);
        Assert.Contains(status.Facts, fact => fact.Label == "Run Type" && fact.Value == "Preview Snapshot");
        Assert.Contains(status.Facts, fact => fact.Label == "Execution" && fact.Value == "Not executed");
        Assert.Contains(status.Facts, fact => fact.Label == "Result" && fact.Value == "No AD write would be required");
    }

    private static RunEntry CreateConflictEntry(string reason, string? failureSummary, string? primarySummary, string? reviewCaseType = null)
    {
        return new RunEntry(
            EntryId: "entry-conflict",
            RunId: "bulk-1",
            ArtifactType: "BulkRun",
            Mode: "BulkSync",
            Bucket: "conflicts",
            BucketLabel: "Conflicts",
            WorkerId: "10001",
            SamAccountName: "winnie",
            Reason: reason,
            ReviewCategory: null,
            ReviewCaseType: reviewCaseType,
            StartedAt: DateTimeOffset.UtcNow,
            ChangeCount: 4,
            OperationSummary: null,
            FailureSummary: failureSummary,
            PrimarySummary: primarySummary,
            TopChangedAttributes: [],
            DiffRows: [],
            Item: JsonDocument.Parse("""{}""").RootElement.Clone());
    }

    private static RunDetail CreateRunDetail(
        string runId = "bulk-1",
        string artifactType = "BulkRun",
        string mode = "BulkSync",
        bool dryRun = true)
    {
        return new RunDetail(
            new RunSummary(
                RunId: runId,
                Path: null,
                ArtifactType: artifactType,
                ConfigPath: null,
                MappingConfigPath: null,
                Mode: mode,
                DryRun: dryRun,
                Status: "Succeeded",
                StartedAt: DateTimeOffset.UtcNow,
                CompletedAt: DateTimeOffset.UtcNow,
                DurationSeconds: 10,
                ProcessedWorkers: 120,
                TotalWorkers: 120,
                Creates: 10,
                Updates: 100,
                Enables: 0,
                Disables: 0,
                GraveyardMoves: 0,
                Deletions: 0,
                Quarantined: 0,
                Conflicts: 10,
                GuardrailFailures: 0,
                ManualReview: 0,
                Unchanged: 0,
                SyncScope: "Delta"),
            JsonDocument.Parse("""{"kind":"bulkRun"}""").RootElement.Clone(),
            new Dictionary<string, int> { ["updates"] = 100, ["conflicts"] = 10, ["creates"] = 10 });
    }

    private sealed class StubRunRepository(RunDetail? runDetail = null) : IRunRepository
    {
        public int LastSkip { get; private set; }

        public int LastTake { get; private set; }

        public string? LastTotalsRunId { get; private set; }

        public string? LastTotalsBucket { get; private set; }

        public string? LastTotalsWorkerId { get; private set; }

        public string? LastTotalsFilter { get; private set; }

        public string? LastEmploymentTotalsRunId { get; private set; }

        public Task<IReadOnlyList<RunSummary>> ListRunsAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<RunSummary>>([]);
        }

        public Task<RunDetail?> GetRunAsync(string runId, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = cancellationToken;
            return Task.FromResult<RunDetail?>(runDetail ?? CreateRunDetail());
        }

        public Task<WorkerPreviewResult?> GetWorkerPreviewAsync(string runId, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = cancellationToken;
            return Task.FromResult<WorkerPreviewResult?>(null);
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
            _ = cancellationToken;
            LastSkip = skip;
            LastTake = take;
            return Task.FromResult<IReadOnlyList<RunEntry>>(Enumerable.Range(1, take).Select(index =>
                new RunEntry(
                    EntryId: $"entry-{skip + index}",
                    RunId: runId,
                    ArtifactType: "BulkRun",
                    Mode: "BulkSync",
                    Bucket: "updates",
                    BucketLabel: "Updates",
                    WorkerId: $"worker-{skip + index}",
                    SamAccountName: null,
                    Reason: null,
                    ReviewCategory: null,
                    ReviewCaseType: null,
                    StartedAt: DateTimeOffset.UtcNow,
                    ChangeCount: 0,
                    OperationSummary: null,
                    FailureSummary: null,
                    PrimarySummary: null,
                    TopChangedAttributes: [],
                    DiffRows: [],
                    Item: JsonDocument.Parse("""{}""").RootElement.Clone())).ToArray());
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
            return Task.FromResult(120);
        }

        public Task<IReadOnlyList<ChangedAttributeTotal>> GetRunEntryAttributeTotalsAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? entryId, CancellationToken cancellationToken)
        {
            LastTotalsRunId = runId;
            LastTotalsBucket = bucket;
            LastTotalsWorkerId = workerId;
            LastTotalsFilter = filter;
            _ = reason;
            _ = entryId;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<ChangedAttributeTotal>>(
            [
                new("email", 12),
                new("cn", 4)
            ]);
        }

        public Task<IReadOnlyList<EmploymentStatusTotal>> GetRunEntryEmploymentStatusTotalsAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? entryId, CancellationToken cancellationToken)
        {
            LastEmploymentTotalsRunId = runId;
            _ = bucket;
            _ = workerId;
            _ = reason;
            _ = filter;
            _ = entryId;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<EmploymentStatusTotal>>(
            [
                new("64300", 80),
                new("64304", 40)
            ]);
        }
    }
}
