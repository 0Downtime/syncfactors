using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Contracts;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;

namespace SyncFactors.MockSuccessFactors;

internal sealed class LifecycleSimulationHarness(
    LifecycleSimulationRequest request,
    LifecycleSimulationScenario scenario,
    MockFixtureDocument fixtures)
{
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;
    private static readonly IReadOnlySet<string> SupportedMutationFields = new HashSet<string>(Comparer)
    {
        "personIdExternal",
        "userName",
        "userId",
        "email",
        "emailType",
        "firstName",
        "lastName",
        "preferredName",
        "displayName",
        "startDate",
        "department",
        "company",
        "jobTitle",
        "businessUnit",
        "division",
        "costCenter",
        "employeeClass",
        "employeeType",
        "managerId",
        "peopleGroup",
        "leadershipLevel",
        "region",
        "geozone",
        "bargainingUnit",
        "unionJobCode",
        "employmentStatus",
        "emplStatus",
        "lifecycleState",
        "endDate",
        "firstDateWorked",
        "lastDateWorked",
        "isContingentWorker",
        "lastModifiedDateTime",
        "locationName",
        "locationAddress",
        "locationCity",
        "locationZipCode",
        "locationCustomString4"
    };

    private readonly LifecycleSimulationRequest _request = request;
    private readonly LifecycleSimulationScenario _scenario = scenario;
    private readonly MockFixtureDocument _fixtures = fixtures;

    public async Task<LifecycleSimulationReport> RunAsync(CancellationToken cancellationToken)
    {
        ValidateScenario(_scenario);

        var startedAt = DateTimeOffset.UtcNow;
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-lifecycle-simulation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var configPath = await WriteSyncConfigAsync(tempRoot, cancellationToken);
            var mappingConfigPath = Path.Combine(tempRoot, "mapping-config.json");
            File.Copy(ResolveSampleMappingConfigPath(), mappingConfigPath, overwrite: true);

            var configLoader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(configPath, mappingConfigPath));
            var workerSource = new MutableWorkerSource(_fixtures.Workers);
            var directoryState = new LifecycleSimulationDirectoryState(_scenario.InitialDirectoryUsers ?? []);
            var directoryGateway = new StatefulDirectoryGateway(directoryState);
            var directoryCommandGateway = new StatefulDirectoryCommandGateway(directoryState);
            var runRepository = new InMemoryRunRepository();
            var runtimeStatusStore = new InMemoryRuntimeStatusStore();
            var mappingProvider = new AttributeMappingProvider(configLoader, NullLogger<AttributeMappingProvider>.Instance);
            var lifecycleSettings = BuildLifecycleSettings(configLoader);
            var planningService = new WorkerPlanningService(
                directoryGateway,
                new IdentityMatcher(),
                new LifecyclePolicy(lifecycleSettings),
                new AttributeDiffService(mappingProvider, new NoopWorkerPreviewLogWriter(), NullLogger<AttributeDiffService>.Instance),
                mappingProvider,
                NullLogger<WorkerPlanningService>.Instance);
            var runService = new FullSyncRunService(
                workerSource,
                planningService,
                new DirectoryMutationCommandBuilder(),
                directoryCommandGateway,
                directoryGateway,
                runRepository,
                runtimeStatusStore,
                new WorkerRunSettings(MaxCreatesPerRun: 50, MaxDisablesPerRun: 50, MaxDeletionsPerRun: 10),
                lifecycleSettings,
                NullLogger<FullSyncRunService>.Instance);

            var reports = new List<LifecycleSimulationIterationReport>();
            var failures = new List<string>();
            var iterations = (_scenario.Iterations ?? [])
                .OrderBy(iteration => iteration.Order)
                .Take(_request.Iterations ?? int.MaxValue)
                .ToArray();

            for (var index = 0; index < iterations.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ApplyMutations(workerSource, iterations[index]);

                var launchResult = await runService.LaunchAsync(
                    new LaunchFullRunRequest(DryRun: false, AcknowledgeRealSync: true),
                    cancellationToken);
                var finalRun = runRepository.GetLastCompletedRun();
                var entries = runRepository.GetLastEntries();

                var iterationFailures = ValidateIteration(iterations[index], launchResult, finalRun, entries);
                failures.AddRange(iterationFailures.Select(failure => $"Iteration {iterations[index].Order}: {failure}"));
                reports.Add(new LifecycleSimulationIterationReport(
                    Order: iterations[index].Order,
                    Name: iterations[index].Name ?? $"Iteration {iterations[index].Order}",
                    RunId: finalRun.RunId,
                    RunStatus: finalRun.Status,
                    BucketCounts: BuildBucketCounts(finalRun),
                    WorkerOperations: BuildWorkerOperations(entries),
                    Failures: iterationFailures));

                if (index < iterations.Length - 1)
                {
                    await WaitForNextRunSecondAsync(cancellationToken);
                }
            }

            var finalUsers = directoryState.ListUsers()
                .Select(ToDirectoryUserState)
                .OrderBy(user => user.WorkerId, Comparer)
                .ToArray();
            failures.AddRange(ValidateFinalExpectation(_scenario.FinalExpectation!, finalUsers));

            var completedAt = DateTimeOffset.UtcNow;
            return new LifecycleSimulationReport(
                ScenarioName: _scenario.Name ?? Path.GetFileNameWithoutExtension(_request.ScenarioPath),
                FixturePath: _request.FixturePath,
                Passed: failures.Count == 0,
                StartedAtUtc: startedAt,
                CompletedAtUtc: completedAt,
                Failures: failures,
                Iterations: reports,
                FinalDirectoryUsers: finalUsers);
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static void ValidateScenario(LifecycleSimulationScenario scenario)
    {
        if (scenario.Iterations is null || scenario.Iterations.Count == 0)
        {
            throw new InvalidOperationException("Lifecycle simulation scenario must contain at least one iteration.");
        }

        if (scenario.FinalExpectation is null)
        {
            throw new InvalidOperationException("Lifecycle simulation scenario must contain a finalExpectation section.");
        }

        var expectedOrder = 1;
        foreach (var iteration in scenario.Iterations)
        {
            if (iteration.Order != expectedOrder)
            {
                throw new InvalidOperationException($"Iteration order is invalid. Expected {expectedOrder} but found {iteration.Order}.");
            }

            if (iteration.Expectation is null)
            {
                throw new InvalidOperationException($"Iteration {iteration.Order} is missing expectation details.");
            }

            foreach (var mutation in iteration.Mutations ?? [])
            {
                if (mutation.RemoveFromSource && (mutation.Worker is not null || mutation.Set?.Count > 0))
                {
                    throw new InvalidOperationException($"Mutation for worker {mutation.WorkerId} cannot combine removeFromSource with worker/set.");
                }

                if (!mutation.RemoveFromSource &&
                    mutation.Worker is null &&
                    (mutation.Set is null || mutation.Set.Count == 0))
                {
                    throw new InvalidOperationException($"Mutation for worker {mutation.WorkerId} must define worker data, field updates, or removeFromSource.");
                }

                if (mutation.Worker is not null &&
                    !string.Equals(mutation.Worker.PersonIdExternal, mutation.WorkerId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Mutation worker payload id '{mutation.Worker.PersonIdExternal}' does not match mutation workerId '{mutation.WorkerId}'.");
                }
            }

            expectedOrder++;
        }
    }

    private static LifecyclePolicySettings BuildLifecycleSettings(SyncFactorsConfigurationLoader configLoader)
    {
        var config = configLoader.GetSyncConfig();
        return new LifecyclePolicySettings(
            ActiveOu: config.Ad.DefaultActiveOu,
            PrehireOu: config.Ad.PrehireOu,
            GraveyardOu: config.Ad.GraveyardOu,
            InactiveStatusField: config.SuccessFactors.Query.InactiveStatusField,
            InactiveStatusValues: config.SuccessFactors.Query.InactiveStatusValues,
            LeaveOu: config.Ad.LeaveOu,
            LeaveStatusValues: config.Sync.LeaveStatusValues,
            DirectoryIdentityAttribute: config.Ad.IdentityAttribute);
    }

    private static async Task<string> WriteSyncConfigAsync(string root, CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, "sync-config.json");
        var reportingDirectory = Path.Combine(root, "reports").Replace("\\", "\\\\", StringComparison.Ordinal);
        var json = $$"""
        {
          "secrets": {
            "adServerEnv": null,
            "adUsernameEnv": null,
            "adBindPasswordEnv": null,
            "successFactorsUsernameEnv": null,
            "successFactorsPasswordEnv": null
          },
          "successFactors": {
            "baseUrl": "http://mock-successfactors.local/odata/v2",
            "auth": {
              "mode": "basic",
              "basic": {
                "username": "mock-user",
                "password": "mock-password"
              }
            },
            "query": {
              "entitySet": "PerPerson",
              "identityField": "personIdExternal",
              "deltaField": "lastModifiedDateTime",
              "inactiveStatusField": "emplStatus",
              "inactiveStatusValues": [ "T", "I", "64308", "64307" ],
              "select": [ "personIdExternal" ],
              "expand": []
            }
          },
          "ad": {
            "server": "ldap.example.test",
            "username": "",
            "bindPassword": "",
            "identityAttribute": "employeeID",
            "defaultActiveOu": "OU=LabUsers,DC=example,DC=com",
            "prehireOu": "OU=Prehire,DC=example,DC=com",
            "graveyardOu": "OU=LabGraveyard,DC=example,DC=com"
          },
          "sync": {
            "enableBeforeStartDays": 7,
            "deletionRetentionDays": 90,
            "leaveStatusValues": [ "U", "64303", "64304" ]
          },
          "safety": {
            "maxCreatesPerRun": 50,
            "maxDisablesPerRun": 50,
            "maxDeletionsPerRun": 10
          },
          "reporting": {
            "outputDirectory": "{{reportingDirectory}}"
          }
        }
        """;
        await File.WriteAllTextAsync(path, json, cancellationToken);
        return path;
    }

    private static string ResolveSampleMappingConfigPath()
    {
        var outputContentPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "config",
            "sample.empjob-confirmed.mapping-config.json"));
        if (File.Exists(outputContentPath))
        {
            return outputContentPath;
        }

        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "config",
            "sample.empjob-confirmed.mapping-config.json"));
    }

    private static void ApplyMutations(MutableWorkerSource workerSource, LifecycleSimulationIteration iteration)
    {
        foreach (var mutation in iteration.Mutations ?? [])
        {
            if (mutation.RemoveFromSource)
            {
                if (!workerSource.RemoveWorker(mutation.WorkerId))
                {
                    throw new InvalidOperationException($"Mutation referenced unknown worker '{mutation.WorkerId}'.");
                }

                continue;
            }

            if (mutation.Worker is not null)
            {
                workerSource.UpsertWorker(mutation.Worker);
            }
            else if (!workerSource.ContainsWorker(mutation.WorkerId))
            {
                throw new InvalidOperationException($"Mutation referenced unknown worker '{mutation.WorkerId}'.");
            }

            if (mutation.Set is not null && mutation.Set.Count > 0)
            {
                workerSource.UpdateWorker(mutation.WorkerId, fixture => ApplyFieldUpdates(fixture, mutation.Set));
            }
        }
    }

    private static MockWorkerFixture ApplyFieldUpdates(MockWorkerFixture worker, IReadOnlyDictionary<string, string?> updates)
    {
        var updated = worker;
        foreach (var pair in updates)
        {
            if (!SupportedMutationFields.Contains(pair.Key))
            {
                throw new InvalidOperationException($"Unsupported mutation field '{pair.Key}'.");
            }

            updated = pair.Key switch
            {
                "personIdExternal" => updated with { PersonIdExternal = RequireValue(pair.Key, pair.Value) },
                "userName" => updated with { UserName = RequireValue(pair.Key, pair.Value) },
                "userId" => updated with { UserId = RequireValue(pair.Key, pair.Value) },
                "email" => updated with { Email = RequireValue(pair.Key, pair.Value) },
                "emailType" => updated with { EmailType = pair.Value },
                "firstName" => updated with { FirstName = RequireValue(pair.Key, pair.Value) },
                "lastName" => updated with { LastName = RequireValue(pair.Key, pair.Value) },
                "preferredName" => updated with { PreferredName = pair.Value },
                "displayName" => updated with { DisplayName = pair.Value },
                "startDate" => updated with { StartDate = RequireValue(pair.Key, pair.Value) },
                "department" => updated with { Department = pair.Value, DepartmentName = pair.Value },
                "company" => updated with { Company = pair.Value },
                "jobTitle" => updated with { JobTitle = pair.Value },
                "businessUnit" => updated with { BusinessUnit = pair.Value },
                "division" => updated with { Division = pair.Value },
                "costCenter" => updated with { CostCenter = pair.Value, CostCenterDescription = pair.Value },
                "employeeClass" => updated with { EmployeeClass = pair.Value },
                "employeeType" => updated with { EmployeeType = pair.Value },
                "managerId" => updated with { ManagerId = pair.Value },
                "peopleGroup" => updated with { PeopleGroup = pair.Value },
                "leadershipLevel" => updated with { LeadershipLevel = pair.Value },
                "region" => updated with { Region = pair.Value },
                "geozone" => updated with { Geozone = pair.Value },
                "bargainingUnit" => updated with { BargainingUnit = pair.Value },
                "unionJobCode" => updated with { UnionJobCode = pair.Value },
                "employmentStatus" or "emplStatus" => updated with { EmploymentStatus = pair.Value },
                "lifecycleState" => updated with { LifecycleState = pair.Value },
                "endDate" => updated with { EndDate = pair.Value },
                "firstDateWorked" => updated with { FirstDateWorked = pair.Value },
                "lastDateWorked" => updated with { LastDateWorked = pair.Value },
                "isContingentWorker" => updated with { IsContingentWorker = pair.Value },
                "lastModifiedDateTime" => updated with { LastModifiedDateTime = pair.Value },
                "locationName" => updated with { Location = UpsertLocation(updated.Location, location => location with { Name = pair.Value }) },
                "locationAddress" => updated with { Location = UpsertLocation(updated.Location, location => location with { Address = pair.Value }) },
                "locationCity" => updated with { Location = UpsertLocation(updated.Location, location => location with { City = pair.Value }) },
                "locationZipCode" => updated with { Location = UpsertLocation(updated.Location, location => location with { ZipCode = pair.Value }) },
                "locationCustomString4" => updated with { Location = UpsertLocation(updated.Location, location => location with { CustomString4 = pair.Value }) },
                _ => updated
            };
        }

        return updated;
    }

    private static MockLocationFixture UpsertLocation(MockLocationFixture? location, Func<MockLocationFixture, MockLocationFixture> update)
    {
        return update(location ?? new MockLocationFixture(Name: null, Address: null, City: null, ZipCode: null));
    }

    private static string RequireValue(string field, string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Mutation field '{field}' requires a non-empty value.")
            : value;
    }

    private static List<string> ValidateIteration(
        LifecycleSimulationIteration iteration,
        RunLaunchResult launchResult,
        RunRecord finalRun,
        IReadOnlyList<RunEntryRecord> entries)
    {
        var failures = new List<string>();
        var expectation = iteration.Expectation!;

        if (!string.IsNullOrWhiteSpace(expectation.RunStatus) &&
            !string.Equals(expectation.RunStatus, launchResult.Status, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"expected run status '{expectation.RunStatus}' but found '{launchResult.Status}'.");
        }

        foreach (var expected in expectation.BucketCounts ?? new Dictionary<string, int>())
        {
            var actual = GetBucketCount(finalRun, expected.Key);
            if (actual != expected.Value)
            {
                failures.Add($"expected bucket '{expected.Key}' to equal {expected.Value} but found {actual}.");
            }
        }

        var actualOperations = BuildWorkerOperations(entries)
            .ToDictionary(operation => operation.WorkerId, operation => operation.Operations, Comparer);
        foreach (var workerOperation in expectation.WorkerOperations ?? [])
        {
            if (!actualOperations.TryGetValue(workerOperation.WorkerId, out var actual))
            {
                failures.Add($"expected worker '{workerOperation.WorkerId}' to produce operations but no run entry was recorded.");
                continue;
            }

            var expectedOperations = workerOperation.Operations ?? [];
            if (!expectedOperations.SequenceEqual(actual, Comparer))
            {
                failures.Add(
                    $"expected worker '{workerOperation.WorkerId}' operations [{string.Join(", ", expectedOperations)}] but found [{string.Join(", ", actual)}].");
            }
        }

        return failures;
    }

    private static IReadOnlyDictionary<string, int> BuildBucketCounts(RunRecord run)
    {
        return new Dictionary<string, int>(Comparer)
        {
            ["creates"] = run.Creates,
            ["updates"] = run.Updates,
            ["enables"] = run.Enables,
            ["disables"] = run.Disables,
            ["graveyardMoves"] = run.GraveyardMoves,
            ["deletions"] = run.Deletions,
            ["manualReview"] = run.ManualReview,
            ["quarantined"] = run.Quarantined,
            ["conflicts"] = run.Conflicts,
            ["guardrailFailures"] = run.GuardrailFailures,
            ["unchanged"] = run.Unchanged
        };
    }

    private static int GetBucketCount(RunRecord run, string bucket)
    {
        return bucket switch
        {
            "creates" => run.Creates,
            "updates" => run.Updates,
            "enables" => run.Enables,
            "disables" => run.Disables,
            "graveyardMoves" => run.GraveyardMoves,
            "deletions" => run.Deletions,
            "manualReview" => run.ManualReview,
            "quarantined" => run.Quarantined,
            "conflicts" => run.Conflicts,
            "guardrailFailures" => run.GuardrailFailures,
            "unchanged" => run.Unchanged,
            _ => 0
        };
    }

    private static IReadOnlyList<LifecycleSimulationWorkerOperationResult> BuildWorkerOperations(IReadOnlyList<RunEntryRecord> entries)
    {
        return entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.WorkerId))
            .Select(entry => new LifecycleSimulationWorkerOperationResult(
                WorkerId: entry.WorkerId!,
                Operations: GetOperationKinds(entry.Item)))
            .OrderBy(entry => entry.WorkerId, Comparer)
            .ToArray();
    }

    private static IReadOnlyList<string> GetOperationKinds(JsonElement item)
    {
        if (!item.TryGetProperty("operations", out var operations) || operations.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return operations.EnumerateArray()
            .Select(operation => operation.TryGetProperty("kind", out var kind) ? kind.GetString() : null)
            .Where(kind => !string.IsNullOrWhiteSpace(kind))
            .Select(kind => kind!)
            .ToArray();
    }

    private static IReadOnlyList<string> ValidateFinalExpectation(
        FinalDirectoryExpectation expectation,
        IReadOnlyList<LifecycleSimulationDirectoryUserState> actualUsers)
    {
        var failures = new List<string>();
        var expectedUsers = expectation.DirectoryUsers ?? [];
        var actualByWorker = actualUsers.ToDictionary(user => user.WorkerId, user => user, Comparer);

        foreach (var expectedUser in expectedUsers)
        {
            if (!actualByWorker.TryGetValue(expectedUser.WorkerId, out var actual))
            {
                failures.Add($"expected final directory user '{expectedUser.WorkerId}' was not present.");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(expectedUser.SamAccountName) &&
                !string.Equals(expectedUser.SamAccountName, actual.SamAccountName, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"expected final user '{expectedUser.WorkerId}' samAccountName '{expectedUser.SamAccountName}' but found '{actual.SamAccountName}'.");
            }

            if (!string.IsNullOrWhiteSpace(expectedUser.ParentOu) &&
                !string.Equals(expectedUser.ParentOu, actual.ParentOu, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"expected final user '{expectedUser.WorkerId}' parentOu '{expectedUser.ParentOu}' but found '{actual.ParentOu}'.");
            }

            if (expectedUser.Enabled is not null && expectedUser.Enabled != actual.Enabled)
            {
                failures.Add($"expected final user '{expectedUser.WorkerId}' enabled={expectedUser.Enabled} but found {actual.Enabled}.");
            }

            if (!string.IsNullOrWhiteSpace(expectedUser.DisplayName) &&
                !string.Equals(expectedUser.DisplayName, actual.DisplayName, StringComparison.Ordinal))
            {
                failures.Add($"expected final user '{expectedUser.WorkerId}' displayName '{expectedUser.DisplayName}' but found '{actual.DisplayName}'.");
            }

            foreach (var attribute in expectedUser.Attributes ?? new Dictionary<string, string?>())
            {
                actual.Attributes.TryGetValue(attribute.Key, out var actualValue);
                if (!string.Equals(attribute.Value, actualValue, StringComparison.Ordinal))
                {
                    failures.Add($"expected final user '{expectedUser.WorkerId}' attribute '{attribute.Key}' to equal '{attribute.Value}' but found '{actualValue}'.");
                }
            }
        }

        if (expectation.RequireExactUserSet)
        {
            var expectedIds = expectedUsers.Select(user => user.WorkerId).ToHashSet(Comparer);
            foreach (var actualUser in actualUsers)
            {
                if (!expectedIds.Contains(actualUser.WorkerId))
                {
                    failures.Add($"final directory contained unexpected user '{actualUser.WorkerId}'.");
                }
            }
        }

        return failures;
    }

    private static LifecycleSimulationDirectoryUserState ToDirectoryUserState(LifecycleSimulationDirectoryUserRecord record)
    {
        return new LifecycleSimulationDirectoryUserState(
            WorkerId: record.WorkerId,
            SamAccountName: record.SamAccountName,
            DistinguishedName: record.DistinguishedName,
            ParentOu: DirectoryDistinguishedName.GetParentOu(record.DistinguishedName),
            Enabled: record.Enabled,
            DisplayName: record.DisplayName,
            Attributes: new Dictionary<string, string?>(record.Attributes, Comparer));
    }

    private static async Task WaitForNextRunSecondAsync(CancellationToken cancellationToken)
    {
        var currentSecond = DateTimeOffset.UtcNow.Second;
        while (DateTimeOffset.UtcNow.Second == currentSecond)
        {
            await Task.Delay(100, cancellationToken);
        }
    }

    internal sealed class MutableWorkerSource : IWorkerSource
    {
        private readonly List<string> _order = [];
        private readonly Dictionary<string, MockWorkerFixture> _workers = new(Comparer);

        public MutableWorkerSource(IEnumerable<MockWorkerFixture> workers)
        {
            foreach (var worker in workers)
            {
                UpsertWorker(worker);
            }
        }

        public bool ContainsWorker(string workerId) => _workers.ContainsKey(workerId);

        public bool RemoveWorker(string workerId)
        {
            if (!_workers.Remove(workerId))
            {
                return false;
            }

            _order.RemoveAll(id => string.Equals(id, workerId, StringComparison.OrdinalIgnoreCase));
            return true;
        }

        public void UpsertWorker(MockWorkerFixture worker)
        {
            if (!_workers.ContainsKey(worker.PersonIdExternal))
            {
                _order.Add(worker.PersonIdExternal);
            }

            _workers[worker.PersonIdExternal] = worker;
        }

        public void UpdateWorker(string workerId, Func<MockWorkerFixture, MockWorkerFixture> update)
        {
            if (!_workers.TryGetValue(workerId, out var current))
            {
                throw new InvalidOperationException($"Worker '{workerId}' could not be resolved.");
            }

            var updated = update(current);
            if (!string.Equals(updated.PersonIdExternal, workerId, StringComparison.OrdinalIgnoreCase))
            {
                _workers.Remove(workerId);
                UpsertWorker(updated);
                _order.RemoveAll(id => string.Equals(id, workerId, StringComparison.OrdinalIgnoreCase));
                if (!_order.Contains(updated.PersonIdExternal, Comparer))
                {
                    _order.Add(updated.PersonIdExternal);
                }
                return;
            }

            _workers[workerId] = updated;
        }

        public Task<WorkerSnapshot?> GetWorkerAsync(string workerId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(_workers.TryGetValue(workerId, out var worker) ? ToWorkerSnapshot(worker) : null);
        }

        public async IAsyncEnumerable<WorkerSnapshot> ListWorkersAsync(WorkerListingMode mode, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = mode;
            foreach (var workerId in _order.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_workers.TryGetValue(workerId, out var worker))
                {
                    yield return ToWorkerSnapshot(worker);
                    await Task.Yield();
                }
            }
        }

        private static WorkerSnapshot ToWorkerSnapshot(MockWorkerFixture worker)
        {
            var attributes = new Dictionary<string, string?>(Comparer)
            {
                ["personIdExternal"] = worker.PersonIdExternal,
                ["personId"] = worker.PersonId ?? worker.PersonIdExternal,
                ["perPersonUuid"] = worker.PerPersonUuid,
                ["displayName"] = worker.DisplayName,
                ["firstName"] = worker.FirstName,
                ["lastName"] = worker.LastName,
                ["preferredName"] = worker.PreferredName ?? worker.FirstName,
                ["email"] = worker.Email,
                ["emailType"] = worker.EmailType,
                ["department"] = worker.DepartmentName ?? worker.Department,
                ["company"] = worker.Company,
                ["location"] = worker.Location?.Name,
                ["officeLocationAddress"] = worker.Location?.Address,
                ["officeLocationCity"] = worker.Location?.City,
                ["officeLocationZipCode"] = worker.Location?.ZipCode,
                ["officeLocationCustomString4"] = worker.Location?.CustomString4,
                ["jobTitle"] = worker.JobTitle,
                ["businessUnit"] = worker.BusinessUnit,
                ["division"] = worker.Division,
                ["costCenter"] = worker.CostCenter,
                ["costCenterDescription"] = worker.CostCenterDescription ?? worker.CostCenter,
                ["costCenterId"] = worker.CostCenterId,
                ["employeeClass"] = worker.EmployeeClass,
                ["employeeType"] = worker.EmployeeType,
                ["emplStatus"] = worker.EmploymentStatus,
                ["managerId"] = worker.ManagerId,
                ["manager"] = worker.ManagerId,
                ["peopleGroup"] = worker.PeopleGroup,
                ["leadershipLevel"] = worker.LeadershipLevel,
                ["region"] = worker.Region,
                ["geozone"] = worker.Geozone,
                ["bargainingUnit"] = worker.BargainingUnit,
                ["unionJobCode"] = worker.UnionJobCode,
                ["startDate"] = worker.StartDate,
                ["endDate"] = worker.EndDate,
                ["firstDateWorked"] = worker.FirstDateWorked,
                ["lastDateWorked"] = worker.LastDateWorked,
                ["isContingentWorker"] = worker.IsContingentWorker,
                ["userId"] = worker.UserId ?? worker.UserName,
                ["employmentNav[0].jobInfoNav[0].companyNav.name_localized"] = worker.Company,
                ["employmentNav[0].jobInfoNav[0].companyNav.company"] = worker.Company,
                ["employmentNav[0].jobInfoNav[0].companyNav.externalCode"] = worker.CompanyId,
                ["employmentNav[0].jobInfoNav[0].departmentNav.name_localized"] = worker.DepartmentName ?? worker.Department,
                ["employmentNav[0].jobInfoNav[0].departmentNav.name"] = worker.DepartmentName ?? worker.Department,
                ["employmentNav[0].jobInfoNav[0].departmentNav.department"] = worker.Department,
                ["employmentNav[0].jobInfoNav[0].departmentNav.externalCode"] = worker.DepartmentId,
                ["employmentNav[0].jobInfoNav[0].departmentNav.costCenter"] = worker.DepartmentCostCenter,
                ["employmentNav[0].jobInfoNav[0].divisionNav.name_localized"] = worker.Division,
                ["employmentNav[0].jobInfoNav[0].divisionNav.division"] = worker.Division,
                ["employmentNav[0].jobInfoNav[0].divisionNav.externalCode"] = worker.DivisionId,
                ["employmentNav[0].jobInfoNav[0].businessUnitNav.name_localized"] = worker.BusinessUnit,
                ["employmentNav[0].jobInfoNav[0].businessUnitNav.businessUnit"] = worker.BusinessUnit,
                ["employmentNav[0].jobInfoNav[0].businessUnitNav.externalCode"] = worker.BusinessUnitId,
                ["employmentNav[0].jobInfoNav[0].costCenterNav.name_localized"] = worker.CostCenter,
                ["employmentNav[0].jobInfoNav[0].costCenterNav.description_localized"] = worker.CostCenterDescription ?? worker.CostCenter,
                ["employmentNav[0].jobInfoNav[0].costCenterNav.externalCode"] = worker.CostCenterId,
                ["employmentNav[0].jobInfoNav[0].locationNav.name"] = worker.Location?.Name,
                ["employmentNav[0].jobInfoNav[0].locationNav.addressNavDEFLT.address1"] = worker.Location?.Address,
                ["employmentNav[0].jobInfoNav[0].locationNav.addressNavDEFLT.city"] = worker.Location?.City,
                ["employmentNav[0].jobInfoNav[0].locationNav.addressNavDEFLT.zipCode"] = worker.Location?.ZipCode,
                ["employmentNav[0].jobInfoNav[0].jobTitle"] = worker.JobTitle,
                ["employmentNav[0].jobInfoNav[0].employeeType"] = worker.EmployeeType,
                ["employmentNav[0].jobInfoNav[0].emplStatus"] = worker.EmploymentStatus,
                ["employmentNav[0].userNav.manager.empInfo.personIdExternal"] = worker.ManagerId,
                ["emailNav[?(@.isPrimary == true)].emailAddress"] = worker.Email,
                ["emailNav[?(@.isPrimary == true)].emailType"] = worker.EmailType
            };

            return new WorkerSnapshot(
                WorkerId: worker.PersonIdExternal,
                PreferredName: worker.PreferredName ?? worker.FirstName,
                LastName: worker.LastName,
                Department: worker.Department ?? worker.DepartmentName ?? "Unknown",
                TargetOu: "OU=LabUsers,DC=example,DC=com",
                IsPrehire: string.Equals(
                    MockFixtureSummaryReporter.ResolveLifecycleState(worker),
                    MockLifecycleState.Preboarding,
                    StringComparison.OrdinalIgnoreCase),
                Attributes: attributes);
        }
    }

    internal sealed class LifecycleSimulationDirectoryState
    {
        private readonly Dictionary<string, LifecycleSimulationDirectoryUserRecord> _users = new(Comparer);

        public LifecycleSimulationDirectoryState(IEnumerable<LifecycleSimulationDirectoryUserInput> users)
        {
            foreach (var user in users)
            {
                var distinguishedName = $"CN={EscapeCn(user.SamAccountName)},{user.ParentOu}";
                _users[user.WorkerId] = new LifecycleSimulationDirectoryUserRecord(
                    WorkerId: user.WorkerId,
                    SamAccountName: user.SamAccountName,
                    DistinguishedName: distinguishedName,
                    Enabled: user.Enabled,
                    DisplayName: user.DisplayName,
                    Attributes: new Dictionary<string, string?>(user.Attributes ?? new Dictionary<string, string?>(), Comparer));
            }
        }

        public DirectoryUserSnapshot? FindByWorker(string workerId)
        {
            return _users.TryGetValue(workerId, out var record)
                ? record.ToSnapshot()
                : null;
        }

        public IReadOnlyList<DirectoryUserSnapshot> ListUsersInOu(string parentOu)
        {
            return _users.Values
                .Where(user => string.Equals(DirectoryDistinguishedName.GetParentOu(user.DistinguishedName), parentOu, StringComparison.OrdinalIgnoreCase))
                .Select(user => user.ToSnapshot())
                .ToArray();
        }

        public string? ResolveManagerDistinguishedName(string managerId)
        {
            return _users.TryGetValue(managerId, out var user) ? user.DistinguishedName : null;
        }

        public string ResolveAvailableEmailLocalPart(WorkerSnapshot worker)
        {
            var baseLocalPart = DirectoryIdentityFormatter.BuildPreferredEmailLocalPart(worker.PreferredName, worker.LastName, worker.WorkerId);
            var localPart = baseLocalPart;
            var existingLocalParts = _users.Values
                .Select(user => user.Attributes.TryGetValue("UserPrincipalName", out var upn) ? upn : null)
                .Where(upn => !string.IsNullOrWhiteSpace(upn))
                .Select(upn => upn!.Split('@')[0])
                .ToHashSet(Comparer);
            if (existingLocalParts.Contains(localPart))
            {
                localPart = $"{baseLocalPart}.{DirectoryIdentityFormatter.NormalizeNamePart(worker.WorkerId)}";
            }

            return localPart;
        }

        public DirectoryCommandResult Apply(DirectoryMutationCommand command)
        {
            var operations = command.Operations.Count == 0
                ? [new DirectoryOperation(command.Action, command.TargetOu)]
                : command.Operations;
            foreach (var operation in operations)
            {
                ApplyOperation(command, operation);
            }

            if (_users.TryGetValue(command.WorkerId, out var updated))
            {
                return new DirectoryCommandResult(
                    Succeeded: true,
                    Action: command.Action,
                    SamAccountName: updated.SamAccountName,
                    DistinguishedName: updated.DistinguishedName,
                    Message: $"Simulated {string.Join(", ", operations.Select(operation => operation.Kind))} for {updated.SamAccountName}.",
                    RunId: null);
            }

            return new DirectoryCommandResult(
                Succeeded: true,
                Action: command.Action,
                SamAccountName: command.SamAccountName,
                DistinguishedName: null,
                Message: $"Simulated {command.Action} for {command.SamAccountName}.",
                RunId: null);
        }

        public IReadOnlyList<LifecycleSimulationDirectoryUserRecord> ListUsers()
        {
            return _users.Values.ToArray();
        }

        private void ApplyOperation(DirectoryMutationCommand command, DirectoryOperation operation)
        {
            switch (operation.Kind)
            {
                case "CreateUser":
                    ApplyCreate(command);
                    break;
                case "UpdateUser":
                    ApplyUpdate(command);
                    break;
                case "MoveUser":
                    ApplyMove(command, operation.TargetOu ?? command.TargetOu);
                    break;
                case "EnableUser":
                    ApplyEnabled(command.WorkerId, enabled: true);
                    break;
                case "DisableUser":
                    ApplyEnabled(command.WorkerId, enabled: false);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported simulated directory operation '{operation.Kind}'.");
            }
        }

        private void ApplyCreate(DirectoryMutationCommand command)
        {
            if (_users.ContainsKey(command.WorkerId))
            {
                throw new InvalidOperationException($"Directory user '{command.WorkerId}' already exists.");
            }

            var attributes = CreateBaseAttributes(command);
            _users[command.WorkerId] = new LifecycleSimulationDirectoryUserRecord(
                WorkerId: command.WorkerId,
                SamAccountName: command.SamAccountName,
                DistinguishedName: BuildDistinguishedName(command.CommonName, command.TargetOu),
                Enabled: command.EnableAccount,
                DisplayName: command.DisplayName,
                Attributes: attributes);
        }

        private void ApplyUpdate(DirectoryMutationCommand command)
        {
            var current = GetRequiredUser(command.WorkerId);
            var attributes = new Dictionary<string, string?>(current.Attributes, Comparer);
            foreach (var pair in CreateBaseAttributes(command))
            {
                attributes[pair.Key] = pair.Value;
            }

            _users[command.WorkerId] = current with
            {
                SamAccountName = command.SamAccountName,
                DisplayName = command.DisplayName,
                Attributes = attributes
            };
        }

        private void ApplyMove(DirectoryMutationCommand command, string targetOu)
        {
            var current = GetRequiredUser(command.WorkerId);
            _users[command.WorkerId] = current with
            {
                DistinguishedName = BuildDistinguishedName(command.CommonName, targetOu)
            };
        }

        private void ApplyEnabled(string workerId, bool enabled)
        {
            var current = GetRequiredUser(workerId);
            _users[workerId] = current with { Enabled = enabled };
        }

        private LifecycleSimulationDirectoryUserRecord GetRequiredUser(string workerId)
        {
            return _users.TryGetValue(workerId, out var user)
                ? user
                : throw new InvalidOperationException($"Directory user '{workerId}' does not exist.");
        }

        private static Dictionary<string, string?> CreateBaseAttributes(DirectoryMutationCommand command)
        {
            var attributes = new Dictionary<string, string?>(command.Attributes, Comparer)
            {
                ["employeeID"] = command.WorkerId,
                ["sAMAccountName"] = command.SamAccountName,
                ["cn"] = command.CommonName,
                ["displayName"] = command.DisplayName,
                ["UserPrincipalName"] = command.UserPrincipalName,
                ["mail"] = command.Mail
            };
            if (!string.IsNullOrWhiteSpace(command.ManagerDistinguishedName))
            {
                attributes["manager"] = command.ManagerDistinguishedName;
            }

            return attributes;
        }

        private static string BuildDistinguishedName(string commonName, string targetOu)
        {
            return $"CN={EscapeCn(commonName)},{targetOu}";
        }

        private static string EscapeCn(string value)
        {
            return value.Replace(",", "\\,", StringComparison.Ordinal);
        }
    }

    internal sealed record LifecycleSimulationDirectoryUserRecord(
        string WorkerId,
        string SamAccountName,
        string DistinguishedName,
        bool Enabled,
        string? DisplayName,
        Dictionary<string, string?> Attributes)
    {
        public DirectoryUserSnapshot ToSnapshot()
        {
            return new DirectoryUserSnapshot(
                SamAccountName,
                DistinguishedName,
                Enabled,
                DisplayName,
                new Dictionary<string, string?>(Attributes, Comparer));
        }
    }

    internal sealed class StatefulDirectoryGateway(LifecycleSimulationDirectoryState state) : IDirectoryGateway
    {
        public Task<DirectoryUserSnapshot?> FindByWorkerAsync(WorkerSnapshot worker, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(state.FindByWorker(worker.WorkerId));
        }

        public Task<IReadOnlyList<DirectoryUserSnapshot>> ListUsersInOuAsync(string ouDistinguishedName, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(state.ListUsersInOu(ouDistinguishedName));
        }

        public Task<string?> ResolveManagerDistinguishedNameAsync(string managerId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(state.ResolveManagerDistinguishedName(managerId));
        }

        public Task<string> ResolveAvailableEmailLocalPartAsync(WorkerSnapshot worker, bool isCreate, CancellationToken cancellationToken)
        {
            _ = isCreate;
            _ = cancellationToken;
            return Task.FromResult(state.ResolveAvailableEmailLocalPart(worker));
        }

        public Task<string> ResolveAvailableEmailLocalPartAsync(
            WorkerSnapshot worker,
            bool isCreate,
            DirectoryUserSnapshot? existingDirectoryUser,
            CancellationToken cancellationToken)
        {
            _ = isCreate;
            _ = cancellationToken;
            if (existingDirectoryUser?.Attributes.TryGetValue("UserPrincipalName", out var currentUpn) == true &&
                !string.IsNullOrWhiteSpace(currentUpn))
            {
                return Task.FromResult(currentUpn.Split('@')[0]);
            }

            if (existingDirectoryUser?.Attributes.TryGetValue("mail", out var currentMail) == true &&
                !string.IsNullOrWhiteSpace(currentMail))
            {
                return Task.FromResult(currentMail.Split('@')[0]);
            }

            return Task.FromResult(state.ResolveAvailableEmailLocalPart(worker));
        }
    }

    internal sealed class StatefulDirectoryCommandGateway(LifecycleSimulationDirectoryState state) : IDirectoryCommandGateway
    {
        public Task<DirectoryCommandResult> ExecuteAsync(DirectoryMutationCommand command, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(state.Apply(command));
        }
    }

    internal sealed class InMemoryRunRepository : IRunRepository
    {
        private readonly List<RunRecord> _savedRuns = [];
        private IReadOnlyList<RunEntryRecord> _lastEntries = [];

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
            _savedRuns.Add(run);
            return Task.CompletedTask;
        }

        public Task ReplaceRunEntriesAsync(string runId, IReadOnlyList<RunEntryRecord> entries, CancellationToken cancellationToken)
        {
            _ = runId;
            _ = cancellationToken;
            _lastEntries = entries.ToArray();
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

        public RunRecord GetLastCompletedRun()
        {
            return _savedRuns.Last(run => !string.Equals(run.Status, "InProgress", StringComparison.OrdinalIgnoreCase));
        }

        public IReadOnlyList<RunEntryRecord> GetLastEntries()
        {
            return _lastEntries;
        }
    }

    internal sealed class InMemoryRuntimeStatusStore : IRuntimeStatusStore
    {
        private RuntimeStatus? _current;
        private bool _active;

        public Task<RuntimeStatus?> GetCurrentAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(_current);
        }

        public Task<bool> TryStartAsync(RuntimeStatus status, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            if (_active)
            {
                return Task.FromResult(false);
            }

            _active = true;
            _current = status;
            return Task.FromResult(true);
        }

        public Task SaveAsync(RuntimeStatus status, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            _current = status;
            if (!string.Equals(status.Status, "InProgress", StringComparison.OrdinalIgnoreCase))
            {
                _active = false;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class NoopWorkerPreviewLogWriter : IWorkerPreviewLogWriter
    {
        public string CreateLogPath(string workerId, DateTimeOffset startedAt)
        {
            _ = workerId;
            _ = startedAt;
            return string.Empty;
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
