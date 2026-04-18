using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Domain.Tests;

public sealed class FullSyncRunServiceTests
{
    [Fact]
    public async Task LaunchAsync_DryRun_PersistsEntriesWithoutExecutingDirectoryMutations()
    {
        var worker = CreateWorker("10001", managerId: "90001");
        var service = CreateService(
            workers: [worker],
            directoryGateway: new StubDirectoryGateway(managerDistinguishedName: "CN=Manager,OU=LabUsers,DC=example,DC=com"),
            directoryCommandGateway: new CapturingDirectoryCommandGateway(),
            runRepository: out var runRepository,
            runtimeStatusStore: out _);

        var result = await service.LaunchAsync(
            new LaunchFullRunRequest(DryRun: true, AcknowledgeRealSync: false),
            CancellationToken.None);

        Assert.Equal("Succeeded", result.Status);
        Assert.Equal("FullSync", runRepository.SavedRuns[^1].ArtifactType);
        Assert.Equal("Succeeded", runRepository.SavedRuns[^1].Status);
        Assert.Single(runRepository.ReplacedEntries);
        Assert.Equal("creates", runRepository.ReplacedEntries[0].entries.Single().Bucket);
    }

    [Fact]
    public async Task LaunchAsync_LiveRun_ExecutesDirectoryMutations()
    {
        var worker = CreateWorker("10001", managerId: "90001");
        var directoryCommandGateway = new CapturingDirectoryCommandGateway();
        var service = CreateService(
            workers: [worker],
            directoryGateway: new StubDirectoryGateway(managerDistinguishedName: "CN=Manager,OU=LabUsers,DC=example,DC=com"),
            directoryCommandGateway: directoryCommandGateway,
            runRepository: out var runRepository,
            runtimeStatusStore: out _);

        var result = await service.LaunchAsync(
            new LaunchFullRunRequest(DryRun: false, AcknowledgeRealSync: true),
            CancellationToken.None);

        Assert.Equal("Succeeded", result.Status);
        Assert.NotNull(directoryCommandGateway.LastCommand);
        Assert.Equal("CreateUser", directoryCommandGateway.LastCommand!.Action);
        Assert.Equal("Succeeded", runRepository.SavedRuns[^1].Status);
    }

    [Fact]
    public async Task LaunchAsync_LiveRun_RejectsWhenRealSyncIsDisabled()
    {
        var worker = CreateWorker("10001", managerId: "90001");
        var service = CreateService(
            workers: [worker],
            directoryGateway: new StubDirectoryGateway(managerDistinguishedName: "CN=Manager,OU=LabUsers,DC=example,DC=com"),
            directoryCommandGateway: new CapturingDirectoryCommandGateway(),
            realSyncSettings: new RealSyncSettings(Enabled: false),
            runRepository: out _,
            runtimeStatusStore: out _);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.LaunchAsync(
            new LaunchFullRunRequest(DryRun: false, AcknowledgeRealSync: true),
            CancellationToken.None));

        Assert.Equal("Real AD sync is disabled for this environment.", exception.Message);
    }

    [Fact]
    public async Task LaunchAsync_CreateGuardrailExceeded_RebucketsAdditionalCreates()
    {
        var workers = new[]
        {
            CreateWorker("10001", managerId: "90001"),
            CreateWorker("10002", managerId: "90001")
        };
        var directoryCommandGateway = new CapturingDirectoryCommandGateway();
        var service = CreateService(
            workers: workers,
            directoryGateway: new StubDirectoryGateway(managerDistinguishedName: "CN=Manager,OU=LabUsers,DC=example,DC=com"),
            directoryCommandGateway: directoryCommandGateway,
            settings: new WorkerRunSettings(MaxCreatesPerRun: 1),
            planningService: null,
            runRepository: out var runRepository,
            runtimeStatusStore: out _);

        var result = await service.LaunchAsync(
            new LaunchFullRunRequest(DryRun: false, AcknowledgeRealSync: true),
            CancellationToken.None);

        Assert.Equal("Succeeded", result.Status);
        Assert.Equal(1, directoryCommandGateway.ExecuteCount);
        Assert.Equal(1, runRepository.SavedRuns[^1].Creates);
        Assert.Equal(1, runRepository.SavedRuns[^1].GuardrailFailures);
        Assert.Contains(runRepository.ReplacedEntries[0].entries, entry => entry.WorkerId == "10002" && entry.Bucket == "guardrailFailures");
    }

    [Fact]
    public async Task LaunchAsync_UnresolvedManager_DoesNotBlockMutation()
    {
        var worker = CreateWorker("10001", managerId: "90001");
        var directoryCommandGateway = new CapturingDirectoryCommandGateway();
        var service = CreateService(
            workers: [worker],
            directoryGateway: new StubDirectoryGateway(managerDistinguishedName: null),
            directoryCommandGateway: directoryCommandGateway,
            runRepository: out var runRepository,
            runtimeStatusStore: out _);

        var result = await service.LaunchAsync(
            new LaunchFullRunRequest(DryRun: false, AcknowledgeRealSync: true),
            CancellationToken.None);

        Assert.Equal("Succeeded", result.Status);
        Assert.NotNull(directoryCommandGateway.LastCommand);
        Assert.Equal("90001", directoryCommandGateway.LastCommand!.ManagerId);
        Assert.Null(directoryCommandGateway.LastCommand.ManagerDistinguishedName);
        Assert.Equal("creates", runRepository.ReplacedEntries[0].entries.Single().Bucket);
    }

    [Fact]
    public async Task LaunchAsync_WorkerMutationFailure_MarksRunFailed()
    {
        var worker = CreateWorker("10001", managerId: "90001");
        var service = CreateService(
            workers: [worker],
            directoryGateway: new StubDirectoryGateway(managerDistinguishedName: "CN=Manager,OU=LabUsers,DC=example,DC=com"),
            directoryCommandGateway: new ThrowingDirectoryCommandGateway(new InvalidOperationException("LDAP bind failed.")),
            runRepository: out var runRepository,
            runtimeStatusStore: out var runtimeStatusStore);

        var result = await service.LaunchAsync(
            new LaunchFullRunRequest(DryRun: false, AcknowledgeRealSync: true),
            CancellationToken.None);

        Assert.Equal("Failed", result.Status);
        Assert.Equal("Failed", runRepository.SavedRuns[^1].Status);
        Assert.Equal(1, runRepository.SavedRuns[^1].Conflicts);
        Assert.Equal("conflicts", runRepository.ReplacedEntries[0].entries.Single().Bucket);
        Assert.Equal("Failed", runtimeStatusStore.SavedStatuses[^1].Status);
        Assert.Contains("LDAP bind failed.", result.Message);
    }

    [Fact]
    public async Task LaunchAsync_PlanningFailure_IsCapturedAndCompletesRun()
    {
        var worker = CreateWorker("10001", managerId: "90001");
        var service = CreateService(
            workers: [worker],
            directoryGateway: new StubDirectoryGateway(managerDistinguishedName: "CN=Manager,OU=LabUsers,DC=example,DC=com"),
            directoryCommandGateway: new CapturingDirectoryCommandGateway(),
            settings: null,
            planningService: new ThrowingWorkerPlanningService(new InvalidOperationException("AD lookup failed.")),
            runRepository: out var runRepository,
            runtimeStatusStore: out var runtimeStatusStore);

        var result = await service.LaunchAsync(
            new LaunchFullRunRequest(DryRun: false, AcknowledgeRealSync: true),
            CancellationToken.None);

        Assert.Equal("Failed", result.Status);
        Assert.Equal("Failed", runRepository.SavedRuns[^1].Status);
        Assert.Equal("Failed", runtimeStatusStore.SavedStatuses[^1].Status);
        Assert.Equal("Completed", runtimeStatusStore.SavedStatuses[^1].Stage);
        Assert.Equal("conflicts", runRepository.ReplacedEntries[0].entries.Single().Bucket);
        Assert.Contains("AD lookup failed.", result.Message);
    }

    [Fact]
    public async Task LaunchAsync_WhenRunAlreadyActive_DoesNotEnumerateWorkers()
    {
        var workerSource = new CountingWorkerSource([CreateWorker("10001", managerId: "90001")]);
        var runtimeStatusStore = new CapturingRuntimeStatusStore { TryStartResult = false };
        var service = CreateService(
            workerSource: workerSource,
            directoryGateway: new StubDirectoryGateway(managerDistinguishedName: "CN=Manager,OU=LabUsers,DC=example,DC=com"),
            directoryCommandGateway: new CapturingDirectoryCommandGateway(),
            runtimeStatusStore: runtimeStatusStore,
            runRepository: out _);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.LaunchAsync(
            new LaunchFullRunRequest(DryRun: true, AcknowledgeRealSync: false),
            CancellationToken.None));

        Assert.Equal("Another sync run is already in progress.", exception.Message);
        Assert.Equal(0, workerSource.ListCallCount);
    }

    [Fact]
    public async Task LaunchAsync_EnumeratesWorkersWithFullListingMode()
    {
        var workerSource = new CountingWorkerSource([CreateWorker("10001", managerId: "90001")]);
        var service = CreateService(
            workerSource: workerSource,
            directoryGateway: new StubDirectoryGateway(managerDistinguishedName: "CN=Manager,OU=LabUsers,DC=example,DC=com"),
            directoryCommandGateway: new CapturingDirectoryCommandGateway(),
            runtimeStatusStore: new CapturingRuntimeStatusStore(),
            runRepository: out _);

        await service.LaunchAsync(
            new LaunchFullRunRequest(DryRun: true, AcknowledgeRealSync: false),
            CancellationToken.None);

        Assert.Equal(WorkerListingMode.Full, workerSource.LastMode);
    }

    [Fact]
    public async Task LaunchAsync_PersistsEmploymentStatusInEntryItem()
    {
        var worker = CreateWorker("10001", managerId: "90001", emplStatus: "64303");
        var service = CreateService(
            workers: [worker],
            directoryGateway: new StubDirectoryGateway(managerDistinguishedName: "CN=Manager,OU=LabUsers,DC=example,DC=com"),
            directoryCommandGateway: new CapturingDirectoryCommandGateway(),
            runRepository: out var runRepository,
            runtimeStatusStore: out _);

        await service.LaunchAsync(
            new LaunchFullRunRequest(DryRun: true, AcknowledgeRealSync: false),
            CancellationToken.None);

        var entry = runRepository.ReplacedEntries.Single().entries.Single();
        Assert.Equal("64303", entry.Item.GetProperty("emplStatus").GetString());
    }

    [Fact]
    public async Task LaunchAsync_PersistsEndDateInEntryItem()
    {
        var worker = CreateWorker("10001", managerId: "90001", endDate: "2026-04-14T00:00:00Z");
        var service = CreateService(
            workers: [worker],
            directoryGateway: new StubDirectoryGateway(managerDistinguishedName: "CN=Manager,OU=LabUsers,DC=example,DC=com"),
            directoryCommandGateway: new CapturingDirectoryCommandGateway(),
            runRepository: out var runRepository,
            runtimeStatusStore: out _);

        await service.LaunchAsync(
            new LaunchFullRunRequest(DryRun: true, AcknowledgeRealSync: false),
            CancellationToken.None);

        var entry = runRepository.ReplacedEntries.Single().entries.Single();
        Assert.Equal("2026-04-14T00:00:00Z", entry.Item.GetProperty("endDate").GetString());
    }

    [Fact]
    public async Task LaunchAsync_PersistsPopulationTotalsInRunReport()
    {
        var workers = new[]
        {
            CreateWorker("10001", managerId: "90001", emplStatus: "A"),
            CreateWorker("10002", managerId: "90001", emplStatus: "T")
        };
        var service = CreateService(
            workers: workers,
            directoryGateway: new StubDirectoryGateway(
                managerDistinguishedName: "CN=Manager,OU=LabUsers,DC=example,DC=com",
                activeUsers:
                [
                    new DirectoryUserSnapshot("10001", "CN=10001,OU=LabUsers,DC=example,DC=com", true, "Worker 10001", new Dictionary<string, string?>()),
                    new DirectoryUserSnapshot("10005", "CN=10005,OU=LabUsers,DC=example,DC=com", true, "Worker 10005", new Dictionary<string, string?>()),
                    new DirectoryUserSnapshot("10006", "CN=10006,OU=LabUsers,DC=example,DC=com", false, "Worker 10006", new Dictionary<string, string?>())
                ]),
            directoryCommandGateway: new CapturingDirectoryCommandGateway(),
            runRepository: out var runRepository,
            runtimeStatusStore: out _);

        await service.LaunchAsync(
            new LaunchFullRunRequest(DryRun: true, AcknowledgeRealSync: false),
            CancellationToken.None);

        var populationTotals = runRepository.SavedRuns[^1].Report.GetProperty("populationTotals");
        Assert.Equal(1, populationTotals.GetProperty("successFactorsActive").GetInt32());
        Assert.Equal(2, populationTotals.GetProperty("activeDirectoryEnabled").GetInt32());
        Assert.Equal(-1, populationTotals.GetProperty("difference").GetInt32());
    }

    [Fact]
    public async Task LaunchAsync_TerminatedWorkerWithoutExistingUser_UsingConfiguredSuccessFactorsPath_DoesNotExecuteCreate()
    {
        var worker = new WorkerSnapshot(
            WorkerId: "10003",
            PreferredName: "Winnie",
            LastName: "Sample101",
            Department: "IT",
            TargetOu: "OU=LabUsers,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["firstName"] = "Winnie",
                ["lastName"] = "Sample101",
                ["department"] = "IT",
                ["employmentNav[0].jobInfoNav[0].emplStatus"] = "T"
            });

        var runRepository = new CapturingRunRepository();
        var runtimeStatusStore = new CapturingRuntimeStatusStore();
        var directoryCommandGateway = new CapturingDirectoryCommandGateway();
        var service = new FullSyncRunService(
            new StubWorkerSource([worker]),
            new WorkerPlanningService(
                new StubDirectoryGateway(managerDistinguishedName: null),
                new IdentityMatcher(),
                new LifecyclePolicy(CreateConfiguredStatusLifecycleSettings()),
                new StubAttributeDiffService(),
                new StubAttributeMappingProvider(),
                NullLogger<WorkerPlanningService>.Instance),
            new DirectoryMutationCommandBuilder(),
            directoryCommandGateway,
            new StubDirectoryGateway(managerDistinguishedName: null),
            runRepository,
            runtimeStatusStore,
            new RealSyncSettings(),
            new WorkerRunSettings(MaxCreatesPerRun: 10, MaxDisablesPerRun: 10),
            CreateConfiguredStatusLifecycleSettings(),
            NullLogger<FullSyncRunService>.Instance);

        var result = await service.LaunchAsync(
            new LaunchFullRunRequest(DryRun: false, AcknowledgeRealSync: true),
            CancellationToken.None);

        Assert.Equal("Succeeded", result.Status);
        Assert.Equal(0, directoryCommandGateway.ExecuteCount);
        Assert.Equal("unchanged", runRepository.ReplacedEntries.Single().entries.Single().Bucket);
    }

    [Fact]
    public async Task LaunchAsync_TerminatedWorkerWithoutExistingUser_DoesNotPersistManualReviewDiffsForMissingRequiredMappings()
    {
        var worker = new WorkerSnapshot(
            WorkerId: "10003",
            PreferredName: "Winnie",
            LastName: "Sample101",
            Department: "IT",
            TargetOu: "OU=LabUsers,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["lastName"] = "Sample101",
                ["employmentNav[0].jobInfoNav[0].emplStatus"] = "T"
            });

        var runRepository = new CapturingRunRepository();
        var runtimeStatusStore = new CapturingRuntimeStatusStore();
        var directoryCommandGateway = new CapturingDirectoryCommandGateway();
        var mappingProvider = new RequiredGivenNameMappingProvider();
        var service = new FullSyncRunService(
            new StubWorkerSource([worker]),
            new WorkerPlanningService(
                new StubDirectoryGateway(managerDistinguishedName: null),
                new IdentityMatcher(),
                new LifecyclePolicy(CreateConfiguredStatusLifecycleSettings()),
                new StubAttributeDiffService(),
                mappingProvider,
                NullLogger<WorkerPlanningService>.Instance),
            new DirectoryMutationCommandBuilder(),
            directoryCommandGateway,
            new StubDirectoryGateway(managerDistinguishedName: null),
            runRepository,
            runtimeStatusStore,
            new RealSyncSettings(),
            new WorkerRunSettings(MaxCreatesPerRun: 10, MaxDisablesPerRun: 10),
            CreateConfiguredStatusLifecycleSettings(),
            NullLogger<FullSyncRunService>.Instance);

        var result = await service.LaunchAsync(
            new LaunchFullRunRequest(DryRun: true, AcknowledgeRealSync: false),
            CancellationToken.None);

        var entry = runRepository.ReplacedEntries.Single().entries.Single();

        Assert.Equal("Succeeded", result.Status);
        Assert.Equal(0, directoryCommandGateway.ExecuteCount);
        Assert.Equal("unchanged", entry.Bucket);
        Assert.False(entry.Item.TryGetProperty("reviewCaseType", out var reviewCaseType) && !string.IsNullOrWhiteSpace(reviewCaseType.GetString()));
        Assert.Empty(entry.Item.GetProperty("changedAttributeDetails").EnumerateArray());
    }

    private static FullSyncRunService CreateService(
        IReadOnlyList<WorkerSnapshot> workers,
        IDirectoryGateway directoryGateway,
        IDirectoryCommandGateway directoryCommandGateway,
        out CapturingRunRepository runRepository,
        out CapturingRuntimeStatusStore runtimeStatusStore)
    {
        return CreateService(
            workers,
            directoryGateway,
            directoryCommandGateway,
            new RealSyncSettings(),
            out runRepository,
            out runtimeStatusStore);
    }

    private static FullSyncRunService CreateService(
        IReadOnlyList<WorkerSnapshot> workers,
        IDirectoryGateway directoryGateway,
        IDirectoryCommandGateway directoryCommandGateway,
        RealSyncSettings realSyncSettings,
        out CapturingRunRepository runRepository,
        out CapturingRuntimeStatusStore runtimeStatusStore)
    {
        return CreateService(
            workers,
            directoryGateway,
            directoryCommandGateway,
            settings: null,
            planningService: null,
            realSyncSettings,
            out runRepository,
            out runtimeStatusStore);
    }

    private static FullSyncRunService CreateService(
        IReadOnlyList<WorkerSnapshot> workers,
        IDirectoryGateway directoryGateway,
        IDirectoryCommandGateway directoryCommandGateway,
        WorkerRunSettings? settings,
        IWorkerPlanningService? planningService,
        out CapturingRunRepository runRepository,
        out CapturingRuntimeStatusStore runtimeStatusStore)
    {
        return CreateService(
            workers,
            directoryGateway,
            directoryCommandGateway,
            settings,
            planningService,
            new RealSyncSettings(),
            out runRepository,
            out runtimeStatusStore);
    }

    private static FullSyncRunService CreateService(
        IReadOnlyList<WorkerSnapshot> workers,
        IDirectoryGateway directoryGateway,
        IDirectoryCommandGateway directoryCommandGateway,
        WorkerRunSettings? settings,
        IWorkerPlanningService? planningService,
        RealSyncSettings realSyncSettings,
        out CapturingRunRepository runRepository,
        out CapturingRuntimeStatusStore runtimeStatusStore)
    {
        return CreateService(
            new StubWorkerSource(workers),
            directoryGateway,
            directoryCommandGateway,
            settings,
            planningService,
            realSyncSettings,
            new CapturingRuntimeStatusStore(),
            out runRepository,
            out runtimeStatusStore);
    }

    private static FullSyncRunService CreateService(
        IWorkerSource workerSource,
        IDirectoryGateway directoryGateway,
        IDirectoryCommandGateway directoryCommandGateway,
        CapturingRuntimeStatusStore runtimeStatusStore,
        out CapturingRunRepository runRepository)
    {
        return CreateService(
            workerSource,
            directoryGateway,
            directoryCommandGateway,
            new RealSyncSettings(),
            runtimeStatusStore,
            out runRepository);
    }

    private static FullSyncRunService CreateService(
        IWorkerSource workerSource,
        IDirectoryGateway directoryGateway,
        IDirectoryCommandGateway directoryCommandGateway,
        RealSyncSettings realSyncSettings,
        CapturingRuntimeStatusStore runtimeStatusStore,
        out CapturingRunRepository runRepository)
    {
        return CreateService(
            workerSource,
            directoryGateway,
            directoryCommandGateway,
            settings: null,
            planningService: null,
            realSyncSettings,
            runtimeStatusStore,
            out runRepository,
            out _);
    }

    private static FullSyncRunService CreateService(
        IWorkerSource workerSource,
        IDirectoryGateway directoryGateway,
        IDirectoryCommandGateway directoryCommandGateway,
        WorkerRunSettings? settings,
        IWorkerPlanningService? planningService,
        CapturingRuntimeStatusStore runtimeStatusStore,
        out CapturingRunRepository runRepository,
        out CapturingRuntimeStatusStore resolvedRuntimeStatusStore)
    {
        return CreateService(
            workerSource,
            directoryGateway,
            directoryCommandGateway,
            settings,
            planningService,
            new RealSyncSettings(),
            runtimeStatusStore,
            out runRepository,
            out resolvedRuntimeStatusStore);
    }

    private static FullSyncRunService CreateService(
        IWorkerSource workerSource,
        IDirectoryGateway directoryGateway,
        IDirectoryCommandGateway directoryCommandGateway,
        WorkerRunSettings? settings,
        IWorkerPlanningService? planningService,
        RealSyncSettings realSyncSettings,
        CapturingRuntimeStatusStore runtimeStatusStore,
        out CapturingRunRepository runRepository,
        out CapturingRuntimeStatusStore resolvedRuntimeStatusStore)
    {
        runRepository = new CapturingRunRepository();
        resolvedRuntimeStatusStore = runtimeStatusStore;

        return new FullSyncRunService(
            workerSource,
            planningService ?? new WorkerPlanningService(
                directoryGateway,
                new IdentityMatcher(),
                CreateLifecyclePolicy(),
                new StubAttributeDiffService(),
                new StubAttributeMappingProvider(),
                NullLogger<WorkerPlanningService>.Instance),
            new DirectoryMutationCommandBuilder(),
            directoryCommandGateway,
            directoryGateway,
            runRepository,
            resolvedRuntimeStatusStore,
            realSyncSettings,
            settings ?? new WorkerRunSettings(MaxCreatesPerRun: 10, MaxDisablesPerRun: 10),
            CreateLifecycleSettings(),
            NullLogger<FullSyncRunService>.Instance);
    }

    private static WorkerSnapshot CreateWorker(string workerId, string? managerId, string? emplStatus = null, string? endDate = null)
    {
        var attributes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["firstName"] = "Winnie",
            ["lastName"] = "Sample101",
            ["department"] = "IT",
            ["managerId"] = managerId,
            ["emplStatus"] = emplStatus,
            ["endDate"] = endDate
        };

        return new WorkerSnapshot(
            WorkerId: workerId,
            PreferredName: "Winnie",
            LastName: "Sample101",
            Department: "IT",
            TargetOu: "OU=LabUsers,DC=example,DC=com",
            IsPrehire: false,
            Attributes: attributes);
    }

    private static LifecyclePolicySettings CreateLifecycleSettings()
        => new(
            ActiveOu: "OU=LabUsers,DC=example,DC=com",
            PrehireOu: "OU=Prehire,DC=example,DC=com",
            GraveyardOu: "OU=Graveyard,DC=example,DC=com",
            InactiveStatusField: "emplStatus",
            InactiveStatusValues: ["T"]);

    private static LifecyclePolicySettings CreateConfiguredStatusLifecycleSettings()
        => new(
            ActiveOu: "OU=LabUsers,DC=example,DC=com",
            PrehireOu: "OU=Prehire,DC=example,DC=com",
            GraveyardOu: "OU=Graveyard,DC=example,DC=com",
            InactiveStatusField: "employmentNav/jobInfoNav/emplStatus",
            InactiveStatusValues: ["T"]);

    private static LifecyclePolicy CreateLifecyclePolicy()
        => new(CreateLifecycleSettings());

    private sealed class StubWorkerSource(IReadOnlyList<WorkerSnapshot> workers) : IWorkerSource
    {
        public Task<WorkerSnapshot?> GetWorkerAsync(string workerId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(workers.FirstOrDefault(worker => worker.WorkerId == workerId));
        }

        public async IAsyncEnumerable<WorkerSnapshot> ListWorkersAsync(WorkerListingMode mode, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = mode;
            foreach (var worker in workers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return worker;
                await Task.Yield();
            }
        }
    }

    private sealed class CountingWorkerSource(IReadOnlyList<WorkerSnapshot> workers) : IWorkerSource
    {
        public int ListCallCount { get; private set; }

        public Task<WorkerSnapshot?> GetWorkerAsync(string workerId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(workers.FirstOrDefault(worker => worker.WorkerId == workerId));
        }

        public async IAsyncEnumerable<WorkerSnapshot> ListWorkersAsync(WorkerListingMode mode, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastMode = mode;
            ListCallCount++;
            foreach (var worker in workers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return worker;
                await Task.Yield();
            }
        }

        public WorkerListingMode? LastMode { get; private set; }
    }

    private sealed class StubDirectoryGateway(
        string? managerDistinguishedName,
        IReadOnlyList<DirectoryUserSnapshot>? activeUsers = null) : IDirectoryGateway
    {
        public Task<DirectoryUserSnapshot?> FindByWorkerAsync(WorkerSnapshot worker, CancellationToken cancellationToken)
        {
            _ = worker;
            _ = cancellationToken;
            return Task.FromResult<DirectoryUserSnapshot?>(null);
        }

        public Task<IReadOnlyList<DirectoryUserSnapshot>> ListUsersInOuAsync(string ouDistinguishedName, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<DirectoryUserSnapshot>>(
                string.Equals(ouDistinguishedName, "OU=LabUsers,DC=example,DC=com", StringComparison.OrdinalIgnoreCase)
                    ? activeUsers ?? []
                    : []);
        }

        public Task<string?> ResolveManagerDistinguishedNameAsync(string managerId, CancellationToken cancellationToken)
        {
            _ = managerId;
            _ = cancellationToken;
            return Task.FromResult(managerDistinguishedName);
        }

        public Task<string> ResolveAvailableEmailLocalPartAsync(WorkerSnapshot worker, bool isCreate, CancellationToken cancellationToken)
        {
            _ = isCreate;
            _ = cancellationToken;
            return Task.FromResult($"{worker.PreferredName.ToLowerInvariant()}.{worker.LastName.ToLowerInvariant()}");
        }
    }

    private sealed class StubAttributeMappingProvider : IAttributeMappingProvider
    {
        public IReadOnlyList<AttributeMapping> GetEnabledMappings() => [];
    }

    private sealed class RequiredGivenNameMappingProvider : IAttributeMappingProvider
    {
        public IReadOnlyList<AttributeMapping> GetEnabledMappings() =>
        [
            new AttributeMapping("personalInfoNav[0].firstName", "GivenName", Required: true, Transform: "Trim")
        ];
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
            _ = directoryUser;
            _ = logPath;
            _ = cancellationToken;
            return Task.FromResult<IReadOnlyList<AttributeChange>>(
            [
                new AttributeChange("department", "department", "(unset)", worker.Department, true),
                new AttributeChange("UserPrincipalName", "resolved email local-part", "(unset)", proposedEmailAddress ?? "(unset)", true),
                new AttributeChange("mail", "resolved email local-part", "(unset)", proposedEmailAddress ?? "(unset)", true)
            ]);
        }
    }

    private sealed class CapturingDirectoryCommandGateway : IDirectoryCommandGateway
    {
        public DirectoryMutationCommand? LastCommand { get; private set; }
        public int ExecuteCount { get; private set; }

        public Task<DirectoryCommandResult> ExecuteAsync(DirectoryMutationCommand command, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            LastCommand = command;
            ExecuteCount++;
            return Task.FromResult(new DirectoryCommandResult(true, command.Action, command.SamAccountName, "CN=Created,OU=LabUsers,DC=example,DC=com", "Created AD user.", null));
        }
    }

    private sealed class ThrowingDirectoryCommandGateway(Exception exception) : IDirectoryCommandGateway
    {
        public Task<DirectoryCommandResult> ExecuteAsync(DirectoryMutationCommand command, CancellationToken cancellationToken)
        {
            _ = command;
            _ = cancellationToken;
            return Task.FromException<DirectoryCommandResult>(exception);
        }
    }

    private sealed class CapturingRunRepository : IRunRepository
    {
        public List<RunRecord> SavedRuns { get; } = [];
        public List<(string runId, IReadOnlyList<RunEntryRecord> entries)> ReplacedEntries { get; } = [];

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
            _ = cancellationToken;
            SavedRuns.Add(run);
            return Task.CompletedTask;
        }

        public Task ReplaceRunEntriesAsync(string runId, IReadOnlyList<RunEntryRecord> entries, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            ReplacedEntries.Add((runId, entries));
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
    }

    private sealed class CapturingRuntimeStatusStore : IRuntimeStatusStore
    {
        public List<RuntimeStatus> SavedStatuses { get; } = [];
        public bool TryStartResult { get; set; } = true;

        public Task<RuntimeStatus?> GetCurrentAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult<RuntimeStatus?>(null);
        }

        public Task<bool> TryStartAsync(RuntimeStatus status, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            if (TryStartResult)
            {
                SavedStatuses.Add(status);
            }

            return Task.FromResult(TryStartResult);
        }

        public Task SaveAsync(RuntimeStatus status, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            SavedStatuses.Add(status);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingWorkerPlanningService(Exception exception) : IWorkerPlanningService
    {
        public Task<PlannedWorkerAction> PlanAsync(WorkerSnapshot worker, string? logPath, CancellationToken cancellationToken)
        {
            _ = worker;
            _ = logPath;
            _ = cancellationToken;
            return Task.FromException<PlannedWorkerAction>(exception);
        }
    }
}
