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
            Filter = "email",
            EmploymentStatus = "64304"
        };

        var result = await model.OnGetAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal("bulk-1", repository.LastTotalsRunId);
        Assert.Equal("updates", repository.LastTotalsBucket);
        Assert.Equal("worker-1", repository.LastTotalsWorkerId);
        Assert.Equal("email", repository.LastTotalsFilter);
        Assert.Equal("64304", repository.LastTotalsEmploymentStatus);
        Assert.Equal("64304", repository.LastEntriesEmploymentStatus);
        Assert.Null(repository.LastEmploymentTotalsEmploymentStatus);
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
    public async Task OnGetExportAsync_ReturnsFilteredJsonDownload()
    {
        var repository = new StubRunRepository();
        var model = new DetailModel(new RunEntriesQueryService(repository))
        {
            RunId = "bulk-1",
            Bucket = "conflicts",
            WorkerId = "worker-1",
            Filter = "manager"
        };

        var result = await model.OnGetExportAsync(CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/json", file.ContentType);
        Assert.Equal("syncfactors-run-bulk-1-conflicts-worker-filtered-text-filtered-entries.json", file.FileDownloadName);
        Assert.Equal(0, repository.LastSkip);
        Assert.Equal(120, repository.LastTake);

        using var document = JsonDocument.Parse(file.FileContents);
        Assert.Equal("bulk-1", document.RootElement.GetProperty("filters").GetProperty("runId").GetString());
        Assert.Equal("conflicts", document.RootElement.GetProperty("filters").GetProperty("bucket").GetString());
        Assert.Equal("worker-1", document.RootElement.GetProperty("filters").GetProperty("workerId").GetString());
        Assert.Equal("manager", document.RootElement.GetProperty("filters").GetProperty("filter").GetString());
        Assert.Equal(120, document.RootElement.GetProperty("summary").GetProperty("matchingEntries").GetInt32());
        Assert.Equal(120, document.RootElement.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task OnGetExportJsonlAsync_ReturnsMetadataLineThenEntryLines()
    {
        var repository = new StubRunRepository();
        var model = new DetailModel(new RunEntriesQueryService(repository))
        {
            RunId = "bulk-1",
            Bucket = "conflicts",
            WorkerId = "worker-1",
            Filter = "manager"
        };

        var result = await model.OnGetExportJsonlAsync(CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/x-ndjson", file.ContentType);
        Assert.Equal("syncfactors-run-bulk-1-conflicts-worker-filtered-text-filtered-entries.jsonl", file.FileDownloadName);

        var lines = System.Text.Encoding.UTF8.GetString(file.FileContents)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Equal(121, lines.Length);

        using var metadataDocument = JsonDocument.Parse(lines[0]);
        Assert.Equal("metadata", metadataDocument.RootElement.GetProperty("recordType").GetString());
        Assert.Equal("conflicts", metadataDocument.RootElement.GetProperty("filters").GetProperty("bucket").GetString());
        Assert.Equal(120, metadataDocument.RootElement.GetProperty("summary").GetProperty("matchingEntries").GetInt32());

        using var entryDocument = JsonDocument.Parse(lines[1]);
        Assert.Equal("entry", entryDocument.RootElement.GetProperty("recordType").GetString());
        Assert.Equal("conflicts", entryDocument.RootElement.GetProperty("entry").GetProperty("bucket").GetString());
    }

    [Fact]
    public async Task OnGetExportCsvAsync_ReturnsFlattenedRowsWithContextColumns()
    {
        var repository = new StubRunRepository();
        var model = new DetailModel(new RunEntriesQueryService(repository))
        {
            RunId = "bulk-1",
            Bucket = "conflicts",
            WorkerId = "worker-1",
            Filter = "manager"
        };

        var result = await model.OnGetExportCsvAsync(CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/csv", file.ContentType);
        Assert.Equal("syncfactors-run-bulk-1-conflicts-worker-filtered-text-filtered-entries.csv", file.FileDownloadName);

        var lines = System.Text.Encoding.UTF8.GetString(file.FileContents)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Equal(121, lines.Length);
        Assert.StartsWith("exportedAt,runId,runArtifactType,runMode,runStatus,runDryRun,runSyncScope,filterBucket,filterWorkerId,filterReason,filterText,entryId,bucket,bucketLabel,workerId,samAccountName,reason,reviewCategory,reviewCaseType,startedAt,changeCount,operationAction,operationEffect,failureSummary,primarySummary,topChangedAttributes,diffRowsJson,itemJson", lines[0]);
        Assert.Contains(",bulk-1,", lines[1]);
        Assert.Contains(",conflicts,worker-1,,manager,", lines[1]);
        Assert.Contains(",entry-1,conflicts,Conflicts,worker-1,", lines[1]);
        Assert.EndsWith("{}", lines[1]);
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
    public void GetFailureDiagnostics_ParsesExistingCreateConflictDetails()
    {
        var model = new DetailModel(new RunEntriesQueryService(new StubRunRepository()));

        var diagnostics = model.GetFailureDiagnostics(new RunEntry(
            EntryId: "entry-existing-create-conflict",
            RunId: "bulk-1",
            ArtifactType: "BulkRun",
            Mode: "BulkSync",
            Bucket: "conflicts",
            BucketLabel: "Conflicts",
            WorkerId: "30008382",
            SamAccountName: "30008382",
            Reason: "Active Directory command 'CreateUser' failed against LDAP server 'localhost'. The object exists. 00000524: UpdErr: DSID-031A11FA, problem 6005 (ENTRY_EXISTS), data 0 Details: Step=CreateUser WorkerId=30008382 SamAccountName=30008382 DistinguishedName=CN=30008382,OU=POWERSHELL,OU=SpireQA-Users,DC=spireQA,DC=biz TargetOu=OU=POWERSHELL,OU=SpireQA-Users,DC=spireQA,DC=biz UserPrincipalName=david.ramsey@example.com Mail=david.ramsey@example.com IdentityAttribute=sAMAccountName IdentityValue=30008382 LicensingGroups=(none) ExistingSamAccountName=30008382 ExistingDisplayName=Ramsey, David ExistingDistinguishedName=CN=30008382,OU=POWERSHELL,OU=SpireQA-Users,DC=spireQA,DC=biz ExistingUserPrincipalName=david.ramsey@example.com ExistingMail=david.ramsey@example.com ManagerId=38256 ManagerDistinguishedName=(unset) Next check: Check the target OU, manager resolution, and whether the account already exists with unexpected state.",
            ReviewCategory: null,
            ReviewCaseType: null,
            StartedAt: DateTimeOffset.UtcNow,
            ChangeCount: 0,
            OperationSummary: null,
            FailureSummary: null,
            PrimarySummary: null,
            TopChangedAttributes: [],
            DiffRows: [],
            Item: JsonDocument.Parse("""{}""").RootElement.Clone()));

        Assert.NotNull(diagnostics);
        Assert.Contains(diagnostics!.Details, item => item.Label == "Existing SAM" && item.Value == "30008382");
        Assert.Contains(diagnostics.Details, item => item.Label == "Existing Display Name" && item.Value == "Ramsey, David");
        Assert.Contains(diagnostics.Details, item => item.Label == "Existing Distinguished Name" && item.Value == "CN=30008382,OU=POWERSHELL,OU=SpireQA-Users,DC=spireQA,DC=biz");
        Assert.Contains(diagnostics.Details, item => item.Label == "Existing UPN" && item.Value == "david.ramsey@example.com");
        Assert.Contains(diagnostics.Details, item => item.Label == "Existing Mail" && item.Value == "david.ramsey@example.com");
    }

    [Fact]
    public void GetFailureDiagnosticSections_GroupsRequestedExistingAndContextDetails()
    {
        var model = new DetailModel(new RunEntriesQueryService(new StubRunRepository()));
        var diagnostics = model.GetFailureDiagnostics(new RunEntry(
            EntryId: "entry-existing-create-conflict",
            RunId: "bulk-1",
            ArtifactType: "BulkRun",
            Mode: "BulkSync",
            Bucket: "conflicts",
            BucketLabel: "Conflicts",
            WorkerId: "30008382",
            SamAccountName: "30008382",
            Reason: "Active Directory command 'CreateUser' failed against LDAP server 'localhost'. The object exists. 00000524: UpdErr: DSID-031A11FA, problem 6005 (ENTRY_EXISTS), data 0 Details: Step=CreateUser WorkerId=30008382 SamAccountName=30008382 DistinguishedName=CN=30008382,OU=POWERSHELL,OU=SpireQA-Users,DC=spireQA,DC=biz TargetOu=OU=POWERSHELL,OU=SpireQA-Users,DC=spireQA,DC=biz UserPrincipalName=david.ramsey@example.com Mail=david.ramsey@example.com IdentityAttribute=sAMAccountName IdentityValue=30008382 LicensingGroups=(none) ExistingSamAccountName=30008382 ExistingDisplayName=Ramsey, David ExistingDistinguishedName=CN=30008382,OU=POWERSHELL,OU=SpireQA-Users,DC=spireQA,DC=biz ExistingUserPrincipalName=david.ramsey@example.com ExistingMail=david.ramsey@example.com ManagerId=38256 ManagerDistinguishedName=(unset) Next check: Check the target OU, manager resolution, and whether the account already exists with unexpected state.",
            ReviewCategory: null,
            ReviewCaseType: null,
            StartedAt: DateTimeOffset.UtcNow,
            ChangeCount: 0,
            OperationSummary: null,
            FailureSummary: null,
            PrimarySummary: null,
            TopChangedAttributes: [],
            DiffRows: [],
            Item: JsonDocument.Parse("""{}""").RootElement.Clone()));

        Assert.NotNull(diagnostics);

        var sections = model.GetFailureDiagnosticSections(diagnostics!);

        Assert.Equal(3, sections.Count);
        Assert.Equal("Requested Directory State", sections[0].Title);
        Assert.Contains(sections[0].Items, item => item.Label == "UPN" && item.Value == "david.ramsey@example.com");
        Assert.Contains(sections[0].Items, item => item.Label == "Licensing Groups" && item.Value == "(none)");
        Assert.Equal("Existing AD Account", sections[1].Title);
        Assert.Contains(sections[1].Items, item => item.Label == "Existing Display Name" && item.Value == "Ramsey, David");
        Assert.Equal("Failure Context", sections[2].Title);
        Assert.Contains(sections[2].Items, item => item.Label == "Step" && item.Value == "CreateUser");
        Assert.Contains(sections[2].Items, item => item.Label == "Next Check" && item.Value == "Check the target OU, manager resolution, and whether the account already exists with unexpected state.");
    }

    [Fact]
    public void GetManualReviewDiagnosticSections_GroupsMissingRequiredInputs()
    {
        var model = new DetailModel(new RunEntriesQueryService(new StubRunRepository()));
        var entry = new RunEntry(
            EntryId: "entry-manual-missing",
            RunId: "bulk-1",
            ArtifactType: "BulkRun",
            Mode: "BulkSync",
            Bucket: "manualReview",
            BucketLabel: "Manual Review",
            WorkerId: "45086",
            SamAccountName: "45086",
            Reason: "Required mapping for employeeType has no value.",
            ReviewCategory: "RequiredMapping",
            ReviewCaseType: "MissingRequiredSourceAttribute",
            StartedAt: DateTimeOffset.UtcNow,
            ChangeCount: 1,
            OperationSummary: null,
            FailureSummary: null,
            PrimarySummary: null,
            TopChangedAttributes: [],
            DiffRows: [],
            Item: JsonDocument.Parse(
                """
                {
                  "workerId": "45086",
                  "samAccountName": "45086",
                  "reviewCategory": "RequiredMapping",
                  "reviewCaseType": "MissingRequiredSourceAttribute",
                  "targetOu": "OU=Users,DC=example,DC=com",
                  "currentOu": "OU=Prehire,DC=example,DC=com",
                  "managerId": "90001",
                  "managerDistinguishedName": "CN=90001,OU=Users,DC=example,DC=com",
                  "proposedEmailAddress": "45086@example.com",
                  "matchedExistingUser": true,
                  "currentEnabled": false,
                  "proposedEnable": true,
                  "missingSourceAttributes": [
                    {
                      "attribute": "employeeType",
                      "reason": "Required mapping for employeeType has no value."
                    }
                  ],
                  "changedAttributeDetails": [
                    {
                      "targetAttribute": "department",
                      "sourceField": "department",
                      "currentAdValue": "Old",
                      "proposedValue": "New"
                    }
                  ]
                }
                """).RootElement.Clone());

        var sections = model.GetManualReviewDiagnosticSections(entry);

        Assert.Equal(3, sections.Count);
        Assert.Equal("Manual Review Decision", sections[0].Title);
        Assert.Contains(sections[0].Items, item => item.Label == "Review Category" && item.Value == "RequiredMapping");
        Assert.Contains(sections[0].Items, item => item.Label == "Proposed Email" && item.Value == "45086@example.com");
        Assert.Equal("Missing Required Inputs", sections[1].Title);
        Assert.Contains(sections[1].Items, item => item.Label == "employeeType" && item.Value == "Required mapping for employeeType has no value.");
        Assert.Equal("Planned Attribute Changes", sections[2].Title);
        Assert.Contains(sections[2].Items, item => item.Label == "department" && item.Value == "Old -> New (source: department)");
    }

    [Fact]
    public void GetManualReviewDiagnosticSections_GroupsAmbiguousDirectoryMatches()
    {
        var model = new DetailModel(new RunEntriesQueryService(new StubRunRepository()));
        var entry = new RunEntry(
            EntryId: "entry-manual-ambiguous",
            RunId: "bulk-1",
            ArtifactType: "BulkRun",
            Mode: "BulkSync",
            Bucket: "manualReview",
            BucketLabel: "Manual Review",
            WorkerId: "10000",
            SamAccountName: null,
            Reason: "Ambiguous AD manager identity lookup for '10000' via employeeID. Matched entries: CN=10000,OU=Users,DC=example,DC=com, CN=user.10000,OU=Users,DC=example,DC=com.",
            ReviewCategory: "DirectoryIdentity",
            ReviewCaseType: "AmbiguousManagerIdentity",
            StartedAt: DateTimeOffset.UtcNow,
            ChangeCount: 0,
            OperationSummary: null,
            FailureSummary: null,
            PrimarySummary: null,
            TopChangedAttributes: [],
            DiffRows: [],
            Item: JsonDocument.Parse(
                """
                {
                  "workerId": "10000",
                  "reviewCategory": "DirectoryIdentity",
                  "reviewCaseType": "AmbiguousManagerIdentity",
                  "targetOu": "OU=Users,DC=example,DC=com",
                  "managerId": "10000"
                }
                """).RootElement.Clone());

        var sections = model.GetManualReviewDiagnosticSections(entry);

        Assert.Equal(2, sections.Count);
        Assert.Equal("Manual Review Decision", sections[0].Title);
        Assert.Contains(sections[0].Items, item => item.Label == "Review Case" && item.Value == "AmbiguousManagerIdentity");
        Assert.Equal("Ambiguous AD Matches", sections[1].Title);
        Assert.Contains(sections[1].Items, item => item.Label == "Lookup Kind" && item.Value == "manager identity");
        Assert.Contains(sections[1].Items, item => item.Label == "Identity Attribute" && item.Value == "employeeID");
        Assert.Contains(sections[1].Items, item => item.Label == "Matched Entry 1" && item.Value == "CN=10000,OU=Users,DC=example,DC=com");
        Assert.Contains(sections[1].Items, item => item.Label == "Matched Entry 2" && item.Value == "CN=user.10000,OU=Users,DC=example,DC=com");
    }

    [Fact]
    public void GetPrimarySummaryDisplay_SuppressesDuplicateFailureSummary()
    {
        var model = new DetailModel(new RunEntriesQueryService(new StubRunRepository()));
        var entry = CreateConflictEntry(
            reason: "Active Directory command 'CreateUser' failed against LDAP server '10.1.182.35'. A value in the request is invalid. 000021C8: AtrErr: DSID-03200E96, #1: 0: 000021C8: DSID-03200E96, problem 1005 (CONSTRAINT_ATT_TYPE), data 0, Att 90290 (userPrincipalName) Next check: Check the target OU, manager resolution, and whether the account already exists with unexpected state.",
            failureSummary: "Active Directory command 'CreateUser' failed against LDAP server '10.1.182.35'. A value in the request is invalid. 000021C8: AtrErr: DSID-03200E96, #1: 0: 000021C8: DSID-03200E96, problem 1005 (CONSTRAINT_ATT_TYPE), data 0, Att 90290 (userPrincipalName) Next check: Check the target OU, manager resolution, and whether the account already exists with unexpected state.",
            primarySummary: "Active Directory command 'CreateUser' failed against LDAP server '10.1.182.35'. A value in the request is invalid. 000021C8: AtrErr: DSID-03200E96, #1: 0: 000021C8: DSID-03200E96, problem 1005 (CONSTRAINT_ATT_TYPE), data 0, Att 90290 (userPrincipalName) Next check: Check the target OU, manager resolution, and whether the account already exists with unexpected state.");

        Assert.Null(model.GetPrimarySummaryDisplay(entry));
        Assert.Equal(
            "Active Directory command 'CreateUser' failed against LDAP server '10.1.182.35'. A value in the request is invalid. 000021C8: AtrErr: DSID-03200E96, #1: 0: 000021C8: DSID-03200E96, problem 1005 (CONSTRAINT_ATT_TYPE), data 0, Att 90290 (userPrincipalName)",
            model.GetFailureSummaryDisplay(entry));
    }

    [Fact]
    public void ShouldShowReason_HidesRawReasonWhenDiagnosticsAreAvailable()
    {
        var model = new DetailModel(new RunEntriesQueryService(new StubRunRepository()));
        var entry = CreateConflictEntry(
            reason: "Active Directory command 'CreateUser' failed against LDAP server '10.1.182.35'. A value in the request is invalid. 000021C8: AtrErr: DSID-03200E96, #1: 0: 000021C8: DSID-03200E96, problem 1005 (CONSTRAINT_ATT_TYPE), data 0, Att 90290 (userPrincipalName) Next check: Check the target OU, manager resolution, and whether the account already exists with unexpected state.",
            failureSummary: "Active Directory command 'CreateUser' failed against LDAP server '10.1.182.35'. A value in the request is invalid. 000021C8: AtrErr: DSID-03200E96, #1: 0: 000021C8: DSID-03200E96, problem 1005 (CONSTRAINT_ATT_TYPE), data 0, Att 90290 (userPrincipalName) Next check: Check the target OU, manager resolution, and whether the account already exists with unexpected state.",
            primarySummary: "Active Directory command 'CreateUser' failed against LDAP server '10.1.182.35'. A value in the request is invalid. 000021C8: AtrErr: DSID-03200E96, #1: 0: 000021C8: DSID-03200E96, problem 1005 (CONSTRAINT_ATT_TYPE), data 0, Att 90290 (userPrincipalName) Next check: Check the target OU, manager resolution, and whether the account already exists with unexpected state.");

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
    public async Task GetPopulationComparison_ReadsTotalsFromRunReport()
    {
        var runDetail = CreateRunDetail(
            report: JsonDocument.Parse(
                """
                {
                  "kind": "bulkRun",
                  "populationTotals": {
                    "successFactorsActive": 42,
                    "activeDirectoryEnabled": 39,
                    "difference": 3,
                    "activeOu": "OU=LabUsers,DC=example,DC=com"
                  }
                }
                """).RootElement.Clone());
        var model = new DetailModel(new RunEntriesQueryService(new StubRunRepository(runDetail)))
        {
            RunId = "bulk-1"
        };

        await model.OnGetAsync(CancellationToken.None);

        var comparison = model.GetPopulationComparison();

        Assert.NotNull(comparison);
        Assert.Equal(42, comparison!.SuccessFactorsActive);
        Assert.Equal(39, comparison.ActiveDirectoryEnabled);
        Assert.Equal(3, comparison.Difference);
        Assert.Equal("+3", comparison.DifferenceLabel);
        Assert.Equal("SF ahead", comparison.StatusLabel);
        Assert.Equal("warn", comparison.ToneCssClass);
        Assert.Equal("OU=LabUsers,DC=example,DC=com", comparison.ActiveOuLabel);
    }

    [Fact]
    public void GetEndDateDisplay_FormatsParseableDate()
    {
        var model = new DetailModel(new RunEntriesQueryService(new StubRunRepository()));
        var localOffset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 4, 14, 12, 0, 0, DateTimeKind.Unspecified));
        var entry = new RunEntry(
            EntryId: "entry-status",
            RunId: "bulk-1",
            ArtifactType: "BulkRun",
            Mode: "BulkSync",
            Bucket: "graveyardMoves",
            BucketLabel: "Graveyard Moves",
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
            Item: JsonDocument.Parse($$"""{"endDate":"{{new DateTimeOffset(2026, 4, 14, 12, 0, 0, localOffset):O}}"}""").RootElement.Clone());

        Assert.Equal("04/14/2026", model.GetEndDateDisplay(entry));
    }

    [Fact]
    public void GetEndDateDisplay_ReturnsNullForMissingOrNullLikeValue()
    {
        var model = new DetailModel(new RunEntriesQueryService(new StubRunRepository()));
        var missingEntry = new RunEntry(
            EntryId: "entry-missing-end-date",
            RunId: "bulk-1",
            ArtifactType: "BulkRun",
            Mode: "BulkSync",
            Bucket: "graveyardMoves",
            BucketLabel: "Graveyard Moves",
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
            Item: JsonDocument.Parse("""{}""").RootElement.Clone());
        var nullStringEntry = missingEntry with
        {
            EntryId = "entry-null-end-date",
            Item = JsonDocument.Parse("""{"endDate":"null"}""").RootElement.Clone()
        };

        Assert.Null(model.GetEndDateDisplay(missingEntry));
        Assert.Null(model.GetEndDateDisplay(nullStringEntry));
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
        bool dryRun = true,
        JsonElement? report = null)
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
            report ?? JsonDocument.Parse("""{"kind":"bulkRun"}""").RootElement.Clone(),
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

        public string? LastTotalsEmploymentStatus { get; private set; }

        public string? LastEmploymentTotalsRunId { get; private set; }

        public string? LastEmploymentTotalsEmploymentStatus { get; private set; }

        public string? LastEntriesEmploymentStatus { get; private set; }

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

        public Task<IReadOnlyList<RunEntry>> GetRunEntriesAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? employmentStatus, string? entryId, int skip, int take, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = bucket;
            _ = workerId;
            _ = reason;
            _ = filter;
            LastEntriesEmploymentStatus = employmentStatus;
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
                    Bucket: bucket ?? "updates",
                    BucketLabel: string.Equals(bucket, "conflicts", StringComparison.OrdinalIgnoreCase) ? "Conflicts" : "Updates",
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
            return Task.FromResult(120);
        }

        public Task<IReadOnlyList<ChangedAttributeTotal>> GetRunEntryAttributeTotalsAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? employmentStatus, string? entryId, CancellationToken cancellationToken)
        {
            LastTotalsRunId = runId;
            LastTotalsBucket = bucket;
            LastTotalsWorkerId = workerId;
            LastTotalsFilter = filter;
            LastTotalsEmploymentStatus = employmentStatus;
            _ = reason;
            _ = entryId;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<ChangedAttributeTotal>>(
            [
                new("email", 12),
                new("cn", 4)
            ]);
        }

        public Task<IReadOnlyList<EmploymentStatusTotal>> GetRunEntryEmploymentStatusTotalsAsync(string runId, string? bucket, string? workerId, string? reason, string? filter, string? employmentStatus, string? entryId, CancellationToken cancellationToken)
        {
            LastEmploymentTotalsRunId = runId;
            _ = bucket;
            _ = workerId;
            _ = reason;
            _ = filter;
            LastEmploymentTotalsEmploymentStatus = employmentStatus;
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
