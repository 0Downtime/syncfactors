using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Domain.Tests;

public sealed class WorkerPreviewPlannerTests
{
    [Fact]
    public async Task PreviewAsync_IncludesPopulatedSourceAttributes()
    {
        var worker = new WorkerSnapshot(
            WorkerId: "44522",
            PreferredName: "Christopher",
            LastName: "Brien",
            Department: "Infrastructure & Security",
            TargetOu: "OU=Employees,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["company"] = "Example Services, Inc.",
                ["department"] = "Infrastructure & Security",
                ["employmentNav[0].jobInfoNav[0].companyNav.name_localized"] = "Example Services, Inc.",
                ["employmentNav[0].jobInfoNav[0].locationNav.name"] = "STL - 700 Market",
                ["emptyValue"] = null
            });

        var planner = new WorkerPreviewPlanner(
            new StubWorkerSource(worker),
            new WorkerPlanningService(
                new StubDirectoryGateway(),
                new StubIdentityMatcher(),
                new StubAttributeDiffService(),
                new StubAttributeMappingProvider(),
                NullLogger<WorkerPlanningService>.Instance),
            new StubAttributeMappingProvider(),
            new StubWorkerPreviewLogWriter(),
            new StubRunRepository(),
            NullLogger<WorkerPreviewPlanner>.Instance);

        var preview = await planner.PreviewAsync("44522", CancellationToken.None);

        Assert.Contains(preview.SourceAttributes, attribute => attribute.Attribute == "company" && attribute.Value == "Example Services, Inc.");
        Assert.Contains(preview.SourceAttributes, attribute => attribute.Attribute == "department" && attribute.Value == "Infrastructure & Security");
        Assert.Contains(preview.SourceAttributes, attribute => attribute.Attribute == "employmentNav[0].jobInfoNav[0].companyNav.name_localized" && attribute.Value == "Example Services, Inc.");
        Assert.Contains(preview.SourceAttributes, attribute => attribute.Attribute == "employmentNav[0].jobInfoNav[0].locationNav.name" && attribute.Value == "STL - 700 Market");
        Assert.DoesNotContain(preview.SourceAttributes, attribute => attribute.Attribute == "emptyValue");
    }

    [Fact]
    public async Task PreviewAsync_PassesResolvedEmailAddressIntoAttributeDiffService()
    {
        var worker = new WorkerSnapshot(
            WorkerId: "44522",
            PreferredName: "Christopher",
            LastName: "Brien",
            Department: "Infrastructure & Security",
            TargetOu: "OU=Employees,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

        var diffService = new CapturingAttributeDiffService();
        var planner = new WorkerPreviewPlanner(
            new StubWorkerSource(worker),
            new WorkerPlanningService(
                new StubDirectoryGateway(),
                new StubIdentityMatcher(),
                diffService,
                new StubAttributeMappingProvider(),
                NullLogger<WorkerPlanningService>.Instance),
            new StubAttributeMappingProvider(),
            new StubWorkerPreviewLogWriter(),
            new StubRunRepository(),
            NullLogger<WorkerPreviewPlanner>.Instance);

        await planner.PreviewAsync("44522", CancellationToken.None);

        Assert.Equal("christopher.brien@Exampleenergy.com", diffService.LastProposedEmailAddress);
    }

    [Fact]
    public async Task PreviewAsync_DoesNotRequireReviewWhenRequiredMappingsResolveThroughNormalizedSourcePaths()
    {
        var worker = new WorkerSnapshot(
            WorkerId: "44522",
            PreferredName: "Christopher",
            LastName: "Brien",
            Department: "Infrastructure & Security",
            TargetOu: "OU=Employees,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["personIdExternal"] = "44522",
                ["firstName"] = "Christopher",
                ["lastName"] = "Brien",
                ["email"] = "christopher.brien@example.test"
            });

        var mappingProvider = new RequiredPathMappingProvider();
        var planner = new WorkerPreviewPlanner(
            new StubWorkerSource(worker),
            new WorkerPlanningService(
                new StubDirectoryGateway(),
                new StubIdentityMatcher(),
                new StubAttributeDiffService(),
                mappingProvider,
                NullLogger<WorkerPlanningService>.Instance),
            mappingProvider,
            new StubWorkerPreviewLogWriter(),
            new StubRunRepository(),
            NullLogger<WorkerPreviewPlanner>.Instance);

        var preview = await planner.PreviewAsync("44522", CancellationToken.None);

        Assert.Null(preview.ReviewCaseType);
        Assert.Empty(preview.MissingSourceAttributes);
    }

    [Fact]
    public async Task PreviewAsync_DoesNotRequireReviewWhenManagerCannotBeResolved()
    {
        var worker = new WorkerSnapshot(
            WorkerId: "44522",
            PreferredName: "Christopher",
            LastName: "Brien",
            Department: "Infrastructure & Security",
            TargetOu: "OU=Employees,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["company"] = "Example Services, Inc.",
                ["department"] = "Infrastructure & Security",
                ["managerId"] = "10004"
            });

        var planner = new WorkerPreviewPlanner(
            new StubWorkerSource(worker),
            new WorkerPlanningService(
                new StubDirectoryGateway(),
                new StubIdentityMatcher(),
                new StubAttributeDiffService(),
                new StubAttributeMappingProvider(),
                NullLogger<WorkerPlanningService>.Instance),
            new StubAttributeMappingProvider(),
            new StubWorkerPreviewLogWriter(),
            new StubRunRepository(),
            NullLogger<WorkerPreviewPlanner>.Instance);

        var preview = await planner.PreviewAsync("44522", CancellationToken.None);

        Assert.Null(preview.ReviewCaseType);
        Assert.Null(preview.ReviewCategory);
    }

    private sealed class StubWorkerSource(WorkerSnapshot worker) : IWorkerSource
    {
        public Task<WorkerSnapshot?> GetWorkerAsync(string workerId, CancellationToken cancellationToken)
        {
            _ = workerId;
            _ = cancellationToken;
            return Task.FromResult<WorkerSnapshot?>(worker);
        }

        public async IAsyncEnumerable<WorkerSnapshot> ListWorkersAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return worker;
            await Task.Yield();
        }
    }

    private sealed class StubDirectoryGateway : IDirectoryGateway
    {
        public Task<DirectoryUserSnapshot?> FindByWorkerAsync(WorkerSnapshot worker, CancellationToken cancellationToken)
        {
            _ = worker;
            _ = cancellationToken;
            return Task.FromResult<DirectoryUserSnapshot?>(null);
        }

        public Task<string?> ResolveManagerDistinguishedNameAsync(string managerId, CancellationToken cancellationToken)
        {
            _ = managerId;
            _ = cancellationToken;
            return Task.FromResult<string?>(null);
        }

        public Task<string> ResolveAvailableEmailLocalPartAsync(WorkerSnapshot worker, CancellationToken cancellationToken)
        {
            _ = worker;
            _ = cancellationToken;
            return Task.FromResult("christopher.brien");
        }
    }

    private sealed class StubIdentityMatcher : IIdentityMatcher
    {
        public IdentityMatchResult Match(WorkerSnapshot worker, DirectoryUserSnapshot? directoryUser)
        {
            _ = worker;
            _ = directoryUser;
            return new IdentityMatchResult(
                Bucket: "creates",
                MatchedExistingUser: false,
                SamAccountName: "44522",
                Reason: "Create preview",
                OperatorActionSummary: "Create account preview");
        }
    }

    private sealed class StubAttributeDiffService : IAttributeDiffService
    {
        public Task<IReadOnlyList<AttributeChange>> BuildDiffAsync(
            WorkerSnapshot worker,
            DirectoryUserSnapshot? directoryUser,
            string? proposedEmailAddress,
            string? logPath,
            CancellationToken cancellationToken)
        {
            _ = worker;
            _ = directoryUser;
            _ = proposedEmailAddress;
            _ = logPath;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<AttributeChange>>([]);
        }
    }

    private sealed class CapturingAttributeDiffService : IAttributeDiffService
    {
        public string? LastProposedEmailAddress { get; private set; }

        public Task<IReadOnlyList<AttributeChange>> BuildDiffAsync(
            WorkerSnapshot worker,
            DirectoryUserSnapshot? directoryUser,
            string? proposedEmailAddress,
            string? logPath,
            CancellationToken cancellationToken)
        {
            _ = worker;
            _ = directoryUser;
            _ = logPath;
            _ = cancellationToken;
            LastProposedEmailAddress = proposedEmailAddress;
            return Task.FromResult<IReadOnlyList<AttributeChange>>([]);
        }
    }

    private sealed class StubWorkerPreviewLogWriter : IWorkerPreviewLogWriter
    {
        public string CreateLogPath(string workerId, DateTimeOffset startedAt)
        {
            return $"/tmp/{workerId}-{startedAt:yyyyMMddHHmmss}.json";
        }

        public Task AppendAsync(string logPath, WorkerPreviewLogEntry entry, CancellationToken cancellationToken)
        {
            _ = logPath;
            _ = entry;
            _ = cancellationToken;
            return Task.CompletedTask;
        }
    }

    private sealed class StubAttributeMappingProvider : IAttributeMappingProvider
    {
        public IReadOnlyList<AttributeMapping> GetEnabledMappings() =>
        [
            new AttributeMapping("company", "company", Required: true, Transform: "identity"),
            new AttributeMapping("department", "department", Required: true, Transform: "identity")
        ];
    }

    private sealed class RequiredPathMappingProvider : IAttributeMappingProvider
    {
        public IReadOnlyList<AttributeMapping> GetEnabledMappings() =>
        [
            new AttributeMapping("personIdExternal", "employeeID", Required: true, Transform: "Trim"),
            new AttributeMapping("personalInfoNav[0].firstName", "GivenName", Required: true, Transform: "Trim"),
            new AttributeMapping("personalInfoNav[0].lastName", "Surname", Required: true, Transform: "Trim"),
            new AttributeMapping("emailNav[?(@.isPrimary == true)].emailAddress", "UserPrincipalName", Required: true, Transform: "Lower")
        ];
    }

    private sealed class StubRunRepository : IRunRepository
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
    }
}
