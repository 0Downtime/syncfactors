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
            new StubDirectoryGateway(),
            new StubIdentityMatcher(),
            new StubAttributeDiffService(),
            new StubWorkerPreviewLogWriter(),
            NullLogger<WorkerPreviewPlanner>.Instance);

        var preview = await planner.PreviewAsync("44522", CancellationToken.None);

        Assert.Contains(preview.SourceAttributes, attribute => attribute.Attribute == "company" && attribute.Value == "Example Services, Inc.");
        Assert.Contains(preview.SourceAttributes, attribute => attribute.Attribute == "department" && attribute.Value == "Infrastructure & Security");
        Assert.Contains(preview.SourceAttributes, attribute => attribute.Attribute == "employmentNav[0].jobInfoNav[0].companyNav.name_localized" && attribute.Value == "Example Services, Inc.");
        Assert.Contains(preview.SourceAttributes, attribute => attribute.Attribute == "employmentNav[0].jobInfoNav[0].locationNav.name" && attribute.Value == "STL - 700 Market");
        Assert.DoesNotContain(preview.SourceAttributes, attribute => attribute.Attribute == "emptyValue");
    }

    private sealed class StubWorkerSource(WorkerSnapshot worker) : IWorkerSource
    {
        public Task<WorkerSnapshot?> GetWorkerAsync(string workerId, CancellationToken cancellationToken)
        {
            _ = workerId;
            _ = cancellationToken;
            return Task.FromResult<WorkerSnapshot?>(worker);
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
            string? logPath,
            CancellationToken cancellationToken)
        {
            _ = worker;
            _ = directoryUser;
            _ = logPath;
            _ = cancellationToken;
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
}
