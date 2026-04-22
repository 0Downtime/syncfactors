using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SyncFactors.MockSuccessFactors;

public sealed class MockFixtureStore
{
    private const int SyntheticWorkerIdStart = 10000;
    private readonly object _gate = new();
    private readonly MockSuccessFactorsOptions _options;
    private readonly string _sourceFixturePath;
    private readonly string _runtimeFixturePath;
    private readonly MockFixtureDocument _seedDocument;
    private MockFixtureDocument _document;

    public MockFixtureStore(IOptions<MockSuccessFactorsOptions> options)
    {
        _options = options.Value;
        _sourceFixturePath = ResolveFixturePath(_options);
        _runtimeFixturePath = ResolveRuntimeFixturePath(_options);
        _seedDocument = BuildSeedDocument(_options, _sourceFixturePath);
        _document = InitializeRuntimeDocument(_seedDocument, _runtimeFixturePath);
    }

    public string SourceFixturePath => _sourceFixturePath;

    public string RuntimeFixturePath => _runtimeFixturePath;

    public MockFixtureDocument GetDocument()
    {
        lock (_gate)
        {
            return SnapshotDocument(_document);
        }
    }

    public MockWorkerFixture? FindByIdentity(string identityField, string? workerId)
    {
        if (string.IsNullOrWhiteSpace(workerId))
        {
            return null;
        }

        lock (_gate)
        {
            return FindByIdentityUnsafe(_document.Workers, identityField, workerId);
        }
    }

    public IReadOnlyList<MockWorkerFixture> QueryWorkers(string entitySet, ODataQuery query)
    {
        MockWorkerFixture[] snapshot;
        lock (_gate)
        {
            snapshot = _document.Workers.ToArray();
        }

        IEnumerable<MockWorkerFixture> workers = snapshot;

        if (!string.IsNullOrWhiteSpace(query.WorkerId))
        {
            var worker = FindByIdentityUnsafe(snapshot, query.IdentityField, query.WorkerId);
            workers = worker is null ? [] : [worker];
        }
        else if (string.Equals(entitySet, "EmpJob", StringComparison.OrdinalIgnoreCase))
        {
            workers = ApplyEmpJobSemantics(workers, query, _options.EmpJob);
        }

        return workers.ToArray();
    }

    public MockAdminStateResponse GetAdminState(string? filter, string adminPath)
    {
        lock (_gate)
        {
            var filteredWorkers = ApplyAdminFilter(_document.Workers, filter)
                .Select(BuildSummary)
                .ToArray();

            return new MockAdminStateResponse(
                SourceFixturePath: _sourceFixturePath,
                RuntimeFixturePath: _runtimeFixturePath,
                AdminPath: adminPath,
                TotalWorkers: _document.Workers.Count,
                FilteredWorkers: filteredWorkers.Length,
                Workers: filteredWorkers);
        }
    }

    public MockAdminWorkerUpsertRequest? GetEditableWorker(string workerId)
    {
        lock (_gate)
        {
            var worker = FindWorkerByIdUnsafe(workerId);
            return worker is null ? null : ToEditableWorker(worker);
        }
    }

    public MockWorkerFixture CreateWorker(MockAdminWorkerUpsertRequest request)
    {
        lock (_gate)
        {
            var worker = MaterializeWorkerUnsafe(request, existingWorkerId: null, allocateIdentityIfMissing: true);
            SetDocumentUnsafe([.. _document.Workers, worker]);
            return worker;
        }
    }

    public MockWorkerFixture UpdateWorker(string workerId, MockAdminWorkerUpsertRequest request)
    {
        lock (_gate)
        {
            var existing = FindWorkerByIdUnsafe(workerId)
                ?? throw new KeyNotFoundException($"Worker '{workerId}' was not found.");
            var worker = MaterializeWorkerUnsafe(request, existing.PersonIdExternal, allocateIdentityIfMissing: false);

            var updatedWorkers = _document.Workers
                .Where(candidate => !string.Equals(candidate.PersonIdExternal, workerId, StringComparison.OrdinalIgnoreCase))
                .Select(candidate =>
                    string.Equals(candidate.ManagerId, workerId, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(worker.PersonIdExternal, workerId, StringComparison.OrdinalIgnoreCase)
                        ? candidate with
                        {
                            ManagerId = worker.PersonIdExternal,
                            LastModifiedDateTime = DateTimeOffset.UtcNow.ToString("O")
                        }
                        : candidate)
                .Append(worker)
                .ToArray();
            SetDocumentUnsafe(updatedWorkers);
            return worker;
        }
    }

    public MockWorkerFixture CloneWorker(string workerId)
    {
        lock (_gate)
        {
            var existing = FindWorkerByIdUnsafe(workerId)
                ?? throw new KeyNotFoundException($"Worker '{workerId}' was not found.");

            var request = ToEditableWorker(existing) with
            {
                PersonIdExternal = null,
                PersonId = null,
                PerPersonUuid = null,
                UserName = null,
                UserId = null,
                Email = null,
                LastModifiedDateTime = null
            };

            var cloned = MaterializeWorkerUnsafe(request, existingWorkerId: null, allocateIdentityIfMissing: true);
            SetDocumentUnsafe([.. _document.Workers, cloned]);
            return cloned;
        }
    }

    public MockWorkerFixture TerminateWorker(string workerId)
    {
        lock (_gate)
        {
            var existing = FindWorkerByIdUnsafe(workerId)
                ?? throw new KeyNotFoundException($"Worker '{workerId}' was not found.");
            var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
            var terminated = existing with
            {
                EmploymentStatus = "T",
                LifecycleState = MockLifecycleState.Terminated,
                EndDate = string.IsNullOrWhiteSpace(existing.EndDate) ? today : existing.EndDate,
                LastDateWorked = string.IsNullOrWhiteSpace(existing.LastDateWorked) ? today : existing.LastDateWorked,
                LatestTerminationDate = string.IsNullOrWhiteSpace(existing.LatestTerminationDate) ? today : existing.LatestTerminationDate,
                LastModifiedDateTime = DateTimeOffset.UtcNow.ToString("O")
            };

            return ReplaceWorkerUnsafe(workerId, terminated);
        }
    }

    public MockWorkerFixture RehireWorker(string workerId)
    {
        lock (_gate)
        {
            var existing = FindWorkerByIdUnsafe(workerId)
                ?? throw new KeyNotFoundException($"Worker '{workerId}' was not found.");
            var scenarioTags = NormalizeScenarioTags(existing.ScenarioTags);
            var lifecycleState = MockLifecycleState.Infer(existing.StartDate, "A", null, scenarioTags);
            var rehired = existing with
            {
                EmploymentStatus = "A",
                LifecycleState = lifecycleState,
                EndDate = null,
                LastDateWorked = null,
                LatestTerminationDate = null,
                LastModifiedDateTime = DateTimeOffset.UtcNow.ToString("O")
            };

            return ReplaceWorkerUnsafe(workerId, rehired);
        }
    }

    public void DeleteWorker(string workerId)
    {
        lock (_gate)
        {
            var existing = FindWorkerByIdUnsafe(workerId)
                ?? throw new KeyNotFoundException($"Worker '{workerId}' was not found.");
            var updatedWorkers = _document.Workers
                .Where(candidate => !string.Equals(candidate.PersonIdExternal, existing.PersonIdExternal, StringComparison.OrdinalIgnoreCase))
                .Select(candidate => string.Equals(candidate.ManagerId, existing.PersonIdExternal, StringComparison.OrdinalIgnoreCase)
                    ? candidate with { ManagerId = null, LastModifiedDateTime = DateTimeOffset.UtcNow.ToString("O") }
                    : candidate)
                .ToArray();
            SetDocumentUnsafe(updatedWorkers);
        }
    }

    public int ResetToSeed()
    {
        lock (_gate)
        {
            SetDocumentUnsafe(_seedDocument.Workers);
            return _document.Workers.Count;
        }
    }

    private MockWorkerFixture ReplaceWorkerUnsafe(string workerId, MockWorkerFixture worker)
    {
        var updatedWorkers = _document.Workers
            .Where(candidate => !string.Equals(candidate.PersonIdExternal, workerId, StringComparison.OrdinalIgnoreCase))
            .Append(worker)
            .ToArray();
        SetDocumentUnsafe(updatedWorkers);
        return worker;
    }

    private MockAdminWorkerSummary BuildSummary(MockWorkerFixture worker)
    {
        var displayName = worker.DisplayName;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = $"{worker.FirstName} {worker.LastName}".Trim();
        }

        return new MockAdminWorkerSummary(
            PersonIdExternal: worker.PersonIdExternal,
            UserId: worker.UserId ?? worker.UserName,
            DisplayName: displayName,
            Email: worker.Email,
            EmploymentStatus: worker.EmploymentStatus ?? "A",
            LifecycleState: ResolveLifecycleState(worker),
            Company: worker.Company,
            Department: worker.Department,
            ManagerId: worker.ManagerId,
            ScenarioTags: worker.ScenarioTags);
    }

    private static IEnumerable<MockWorkerFixture> ApplyAdminFilter(IEnumerable<MockWorkerFixture> workers, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return workers.OrderBy(worker => worker.PersonIdExternal, StringComparer.OrdinalIgnoreCase);
        }

        var tokens = filter
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return workers.OrderBy(worker => worker.PersonIdExternal, StringComparer.OrdinalIgnoreCase);
        }

        return workers
            .Where(worker =>
            {
                var haystack = string.Join(
                    '\n',
                    [
                        worker.PersonIdExternal,
                        worker.UserName,
                        worker.UserId,
                        worker.FirstName,
                        worker.LastName,
                        worker.PreferredName,
                        worker.DisplayName,
                        worker.Email,
                        worker.Company,
                        worker.Department,
                        worker.JobTitle,
                        worker.BusinessUnit,
                        worker.Division,
                        worker.CostCenter,
                        worker.EmployeeType,
                        worker.EmployeeClass,
                        worker.ManagerId,
                        worker.EmploymentStatus,
                        ResolveLifecycleState(worker),
                        string.Join(' ', worker.ScenarioTags)
                    ]);

                return tokens.All(token => haystack.Contains(token, StringComparison.OrdinalIgnoreCase));
            })
            .OrderBy(worker => worker.PersonIdExternal, StringComparer.OrdinalIgnoreCase);
    }

    private MockWorkerFixture MaterializeWorkerUnsafe(
        MockAdminWorkerUpsertRequest request,
        string? existingWorkerId,
        bool allocateIdentityIfMissing)
    {
        var requestedId = NormalizeOptionalValue(request.PersonIdExternal);
        var resolvedId = string.IsNullOrWhiteSpace(requestedId)
            ? allocateIdentityIfMissing
                ? AllocateNextWorkerIdUnsafe()
                : throw new InvalidOperationException("personIdExternal is required.")
            : requestedId;

        var resolvedUserName = NormalizeOptionalValue(request.UserName) ?? $"user.{resolvedId}";
        var resolvedUserId = NormalizeOptionalValue(request.UserId) ?? resolvedUserName;
        var resolvedEmail = NormalizeOptionalValue(request.Email) ?? $"{resolvedUserName}@example.test";
        var resolvedFirstName = NormalizeRequiredValue(request.FirstName, "firstName");
        var resolvedLastName = NormalizeRequiredValue(request.LastName, "lastName");
        var resolvedStartDate = NormalizeRequiredValue(request.StartDate, "startDate");
        var scenarioTags = NormalizeScenarioTags(request.ScenarioTags);
        var employmentStatus = NormalizeOptionalValue(request.EmploymentStatus)?.ToUpperInvariant() ?? "A";
        var endDate = NormalizeOptionalValue(request.EndDate);
        var lifecycleState = string.IsNullOrWhiteSpace(request.LifecycleState)
            ? MockLifecycleState.Infer(resolvedStartDate, employmentStatus, endDate, scenarioTags)
            : MockLifecycleState.Normalize(request.LifecycleState);
        var response = NormalizeResponse(request.Response);
        var location = NormalizeLocation(request.Location);
        var displayName = NormalizeOptionalValue(request.DisplayName)
            ?? BuildDisplayName(NormalizeOptionalValue(request.PreferredName), resolvedFirstName, resolvedLastName);
        var lastModifiedDateTime = NormalizeOptionalValue(request.LastModifiedDateTime) ?? DateTimeOffset.UtcNow.ToString("O");
        var managerId = NormalizeOptionalValue(request.ManagerId);

        ValidateUniquenessUnsafe(existingWorkerId, resolvedId, resolvedUserName, resolvedUserId, resolvedEmail);
        ValidateManagerUnsafe(existingWorkerId, resolvedId, managerId);

        return new MockWorkerFixture(
            PersonIdExternal: resolvedId,
            UserName: resolvedUserName,
            Email: resolvedEmail,
            FirstName: resolvedFirstName,
            LastName: resolvedLastName,
            StartDate: resolvedStartDate,
            Department: NormalizeOptionalValue(request.Department),
            Company: NormalizeOptionalValue(request.Company),
            Location: location,
            JobTitle: NormalizeOptionalValue(request.JobTitle),
            BusinessUnit: NormalizeOptionalValue(request.BusinessUnit),
            Division: NormalizeOptionalValue(request.Division),
            CostCenter: NormalizeOptionalValue(request.CostCenter),
            EmployeeClass: NormalizeOptionalValue(request.EmployeeClass),
            EmployeeType: NormalizeOptionalValue(request.EmployeeType),
            ManagerId: managerId,
            PeopleGroup: NormalizeOptionalValue(request.PeopleGroup),
            LeadershipLevel: NormalizeOptionalValue(request.LeadershipLevel),
            Region: NormalizeOptionalValue(request.Region),
            Geozone: NormalizeOptionalValue(request.Geozone),
            BargainingUnit: NormalizeOptionalValue(request.BargainingUnit),
            UnionJobCode: NormalizeOptionalValue(request.UnionJobCode),
            CintasUniformCategory: NormalizeOptionalValue(request.CintasUniformCategory),
            CintasUniformAllotment: NormalizeOptionalValue(request.CintasUniformAllotment),
            EmploymentStatus: employmentStatus,
            LifecycleState: lifecycleState,
            EndDate: endDate,
            FirstDateWorked: NormalizeOptionalValue(request.FirstDateWorked),
            LastDateWorked: NormalizeOptionalValue(request.LastDateWorked),
            IsContingentWorker: NormalizeOptionalValue(request.IsContingentWorker),
            LastModifiedDateTime: lastModifiedDateTime,
            ScenarioTags: scenarioTags,
            Response: response,
            PersonId: NormalizeOptionalValue(request.PersonId) ?? resolvedId,
            PerPersonUuid: NormalizeOptionalValue(request.PerPersonUuid) ?? $"uuid-{resolvedId}",
            PreferredName: NormalizeOptionalValue(request.PreferredName),
            DisplayName: displayName,
            UserId: resolvedUserId,
            EmailType: NormalizeOptionalValue(request.EmailType),
            DepartmentName: NormalizeOptionalValue(request.DepartmentName) ?? NormalizeOptionalValue(request.Department),
            DepartmentId: NormalizeOptionalValue(request.DepartmentId),
            DepartmentCostCenter: NormalizeOptionalValue(request.DepartmentCostCenter),
            CompanyId: NormalizeOptionalValue(request.CompanyId),
            BusinessUnitId: NormalizeOptionalValue(request.BusinessUnitId),
            DivisionId: NormalizeOptionalValue(request.DivisionId),
            CostCenterDescription: NormalizeOptionalValue(request.CostCenterDescription) ?? NormalizeOptionalValue(request.CostCenter),
            CostCenterId: NormalizeOptionalValue(request.CostCenterId),
            TwoCharCountryCode: NormalizeOptionalValue(request.TwoCharCountryCode),
            Position: NormalizeOptionalValue(request.Position),
            PayGrade: NormalizeOptionalValue(request.PayGrade),
            BusinessPhoneNumber: NormalizeOptionalValue(request.BusinessPhoneNumber),
            BusinessPhoneAreaCode: NormalizeOptionalValue(request.BusinessPhoneAreaCode),
            BusinessPhoneCountryCode: NormalizeOptionalValue(request.BusinessPhoneCountryCode),
            BusinessPhoneExtension: NormalizeOptionalValue(request.BusinessPhoneExtension),
            CellPhoneNumber: NormalizeOptionalValue(request.CellPhoneNumber),
            CellPhoneAreaCode: NormalizeOptionalValue(request.CellPhoneAreaCode),
            CellPhoneCountryCode: NormalizeOptionalValue(request.CellPhoneCountryCode),
            ActiveEmploymentsCount: NormalizeOptionalValue(request.ActiveEmploymentsCount),
            LatestTerminationDate: NormalizeOptionalValue(request.LatestTerminationDate));
    }

    private static MockAdminWorkerUpsertRequest ToEditableWorker(MockWorkerFixture worker)
    {
        return new MockAdminWorkerUpsertRequest(
            PersonIdExternal: worker.PersonIdExternal,
            UserName: worker.UserName,
            Email: worker.Email,
            FirstName: worker.FirstName,
            LastName: worker.LastName,
            StartDate: worker.StartDate,
            Department: worker.Department,
            Company: worker.Company,
            Location: worker.Location is null
                ? null
                : new MockAdminLocationInput(
                    Name: worker.Location.Name,
                    Address: worker.Location.Address,
                    City: worker.Location.City,
                    ZipCode: worker.Location.ZipCode,
                    CustomString4: worker.Location.CustomString4),
            JobTitle: worker.JobTitle,
            BusinessUnit: worker.BusinessUnit,
            Division: worker.Division,
            CostCenter: worker.CostCenter,
            EmployeeClass: worker.EmployeeClass,
            EmployeeType: worker.EmployeeType,
            ManagerId: worker.ManagerId,
            PeopleGroup: worker.PeopleGroup,
            LeadershipLevel: worker.LeadershipLevel,
            Region: worker.Region,
            Geozone: worker.Geozone,
            BargainingUnit: worker.BargainingUnit,
            UnionJobCode: worker.UnionJobCode,
            CintasUniformCategory: worker.CintasUniformCategory,
            CintasUniformAllotment: worker.CintasUniformAllotment,
            EmploymentStatus: worker.EmploymentStatus,
            LifecycleState: worker.LifecycleState,
            EndDate: worker.EndDate,
            FirstDateWorked: worker.FirstDateWorked,
            LastDateWorked: worker.LastDateWorked,
            IsContingentWorker: worker.IsContingentWorker,
            LastModifiedDateTime: worker.LastModifiedDateTime,
            ScenarioTags: worker.ScenarioTags,
            Response: worker.Response is null
                ? null
                : new MockAdminResponseControlsInput(
                    ForceUnauthorized: worker.Response.ForceUnauthorized,
                    ForceNotFound: worker.Response.ForceNotFound,
                    ForceMalformedPayload: worker.Response.ForceMalformedPayload,
                    ForceEmptyResults: worker.Response.ForceEmptyResults),
            PersonId: worker.PersonId,
            PerPersonUuid: worker.PerPersonUuid,
            PreferredName: worker.PreferredName,
            DisplayName: worker.DisplayName,
            UserId: worker.UserId,
            EmailType: worker.EmailType,
            DepartmentName: worker.DepartmentName,
            DepartmentId: worker.DepartmentId,
            DepartmentCostCenter: worker.DepartmentCostCenter,
            CompanyId: worker.CompanyId,
            BusinessUnitId: worker.BusinessUnitId,
            DivisionId: worker.DivisionId,
            CostCenterDescription: worker.CostCenterDescription,
            CostCenterId: worker.CostCenterId,
            TwoCharCountryCode: worker.TwoCharCountryCode,
            Position: worker.Position,
            PayGrade: worker.PayGrade,
            BusinessPhoneNumber: worker.BusinessPhoneNumber,
            BusinessPhoneAreaCode: worker.BusinessPhoneAreaCode,
            BusinessPhoneCountryCode: worker.BusinessPhoneCountryCode,
            BusinessPhoneExtension: worker.BusinessPhoneExtension,
            CellPhoneNumber: worker.CellPhoneNumber,
            CellPhoneAreaCode: worker.CellPhoneAreaCode,
            CellPhoneCountryCode: worker.CellPhoneCountryCode,
            ActiveEmploymentsCount: worker.ActiveEmploymentsCount,
            LatestTerminationDate: worker.LatestTerminationDate);
    }

    private static string BuildDisplayName(string? preferredName, string firstName, string lastName)
    {
        var displayFirstName = string.IsNullOrWhiteSpace(preferredName) ? firstName : preferredName;
        return $"{displayFirstName} {lastName}".Trim();
    }

    private void ValidateUniquenessUnsafe(string? existingWorkerId, string personIdExternal, string userName, string userId, string email)
    {
        foreach (var worker in _document.Workers)
        {
            if (string.Equals(worker.PersonIdExternal, existingWorkerId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(worker.PersonIdExternal, personIdExternal, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Worker id '{personIdExternal}' is already in use.");
            }

            if (string.Equals(worker.UserName, userName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"userName '{userName}' is already in use.");
            }

            if (string.Equals(worker.UserId ?? worker.UserName, userId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"userId '{userId}' is already in use.");
            }

            if (string.Equals(worker.Email, email, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"email '{email}' is already in use.");
            }
        }
    }

    private void ValidateManagerUnsafe(string? existingWorkerId, string resolvedId, string? managerId)
    {
        if (string.IsNullOrWhiteSpace(managerId))
        {
            return;
        }

        if (string.Equals(managerId, resolvedId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("managerId cannot reference the same worker.");
        }

        var manager = FindWorkerByIdUnsafe(managerId);
        if (manager is null)
        {
            if (!string.IsNullOrWhiteSpace(existingWorkerId) &&
                string.Equals(existingWorkerId, managerId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("managerId cannot reference the same worker.");
            }

            throw new InvalidOperationException($"managerId '{managerId}' does not reference an existing worker.");
        }
    }

    private MockWorkerFixture? FindWorkerByIdUnsafe(string? workerId)
        => FindByIdentityUnsafe(_document.Workers, "personIdExternal", workerId);

    private static MockWorkerFixture? FindByIdentityUnsafe(IEnumerable<MockWorkerFixture> workers, string identityField, string? workerId)
    {
        if (string.IsNullOrWhiteSpace(workerId))
        {
            return null;
        }

        return workers.FirstOrDefault(worker =>
            string.Equals(identityField, "personIdExternal", StringComparison.OrdinalIgnoreCase)
                ? string.Equals(worker.PersonIdExternal, workerId, StringComparison.OrdinalIgnoreCase)
                : string.Equals(identityField, "userId", StringComparison.OrdinalIgnoreCase)
                    ? string.Equals(worker.UserId ?? worker.UserName, workerId, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(identityField, "username", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(identityField, "userName", StringComparison.OrdinalIgnoreCase)
                        ? string.Equals(worker.UserName, workerId, StringComparison.OrdinalIgnoreCase)
                        : false);
    }

    private string AllocateNextWorkerIdUnsafe()
    {
        var maxValue = _document.Workers
            .Select(worker => int.TryParse(worker.PersonIdExternal, out var numericId) ? numericId : SyntheticWorkerIdStart - 1)
            .DefaultIfEmpty(SyntheticWorkerIdStart - 1)
            .Max();
        return (Math.Max(SyntheticWorkerIdStart, maxValue + 1)).ToString("D5");
    }

    private void SetDocumentUnsafe(IEnumerable<MockWorkerFixture> workers)
    {
        var orderedWorkers = workers
            .OrderBy(worker => worker.PersonIdExternal, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _document = new MockFixtureDocument(orderedWorkers);
        SaveRuntimeDocumentUnsafe(_document);
    }

    private void SaveRuntimeDocumentUnsafe(MockFixtureDocument document)
    {
        var directory = Path.GetDirectoryName(_runtimeFixturePath)
            ?? throw new InvalidOperationException("Runtime fixture path must include a directory.");
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $"{Path.GetFileName(_runtimeFixturePath)}.{Guid.NewGuid():N}.tmp");
        var json = JsonSerializer.Serialize(document, SerializerContext.Default.MockFixtureDocument);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _runtimeFixturePath, overwrite: true);
    }

    private static MockFixtureDocument SnapshotDocument(MockFixtureDocument document)
        => new(document.Workers.ToArray());

    private static MockFixtureDocument BuildSeedDocument(MockSuccessFactorsOptions options, string fixturePath)
    {
        var json = File.ReadAllText(fixturePath);
        var document = JsonSerializer.Deserialize(json, SerializerContext.Default.MockFixtureDocument)
            ?? new MockFixtureDocument([]);

        var populatedDocument = options.SyntheticPopulation.Enabled
            ? ExpandSyntheticPopulation(document, options.SyntheticPopulation.TargetWorkerCount)
            : document;

        return NormalizeSyntheticIdentities(populatedDocument);
    }

    private static MockFixtureDocument InitializeRuntimeDocument(MockFixtureDocument seedDocument, string runtimePath)
    {
        if (!File.Exists(runtimePath))
        {
            var directory = Path.GetDirectoryName(runtimePath)
                ?? throw new InvalidOperationException("Runtime fixture path must include a directory.");
            Directory.CreateDirectory(directory);
            File.WriteAllText(runtimePath, JsonSerializer.Serialize(seedDocument, SerializerContext.Default.MockFixtureDocument));
            return seedDocument;
        }

        var runtimeJson = File.ReadAllText(runtimePath);
        return JsonSerializer.Deserialize(runtimeJson, SerializerContext.Default.MockFixtureDocument)
            ?? new MockFixtureDocument([]);
    }

    private static string ResolveFixturePath(MockSuccessFactorsOptions options)
    {
        if (options.SyntheticPopulation.Enabled &&
            !string.IsNullOrWhiteSpace(options.SyntheticPopulation.SeedFixturePath))
        {
            return options.SyntheticPopulation.SeedFixturePath;
        }

        return options.FixturePath;
    }

    private static string ResolveRuntimeFixturePath(MockSuccessFactorsOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Runtime.FixturePath))
        {
            return options.Runtime.FixturePath;
        }

        return Path.Combine(Path.GetTempPath(), "syncfactors-mock-successfactors", $"runtime-{Guid.NewGuid():N}.json");
    }

    private static string NormalizeRequiredValue(string? value, string fieldName)
    {
        var normalized = NormalizeOptionalValue(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException($"{fieldName} is required.");
        }

        return normalized;
    }

    private static string? NormalizeOptionalValue(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static MockLocationFixture? NormalizeLocation(MockAdminLocationInput? location)
    {
        if (location is null)
        {
            return null;
        }

        var normalized = new MockLocationFixture(
            Name: NormalizeOptionalValue(location.Name),
            Address: NormalizeOptionalValue(location.Address),
            City: NormalizeOptionalValue(location.City),
            ZipCode: NormalizeOptionalValue(location.ZipCode),
            CustomString4: NormalizeOptionalValue(location.CustomString4));

        return string.IsNullOrWhiteSpace(normalized.Name) &&
               string.IsNullOrWhiteSpace(normalized.Address) &&
               string.IsNullOrWhiteSpace(normalized.City) &&
               string.IsNullOrWhiteSpace(normalized.ZipCode) &&
               string.IsNullOrWhiteSpace(normalized.CustomString4)
            ? null
            : normalized;
    }

    private static MockWorkerResponseControls? NormalizeResponse(MockAdminResponseControlsInput? response)
    {
        if (response is null)
        {
            return null;
        }

        return response.ForceUnauthorized ||
               response.ForceNotFound ||
               response.ForceMalformedPayload ||
               response.ForceEmptyResults
            ? new MockWorkerResponseControls(
                ForceUnauthorized: response.ForceUnauthorized,
                ForceNotFound: response.ForceNotFound,
                ForceMalformedPayload: response.ForceMalformedPayload,
                ForceEmptyResults: response.ForceEmptyResults)
            : null;
    }

    private static IReadOnlyList<string> NormalizeScenarioTags(IEnumerable<string>? tags)
    {
        return (tags ?? [])
            .Select(tag => tag?.Trim())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static MockFixtureDocument ExpandSyntheticPopulation(MockFixtureDocument seedDocument, int targetWorkerCount)
    {
        if (targetWorkerCount <= 0)
        {
            throw new InvalidOperationException("Synthetic population target worker count must be greater than zero.");
        }

        if (seedDocument.Workers.Count == 0)
        {
            return seedDocument;
        }

        var workers = new List<MockWorkerFixture>(targetWorkerCount);
        var personIdMap = new Dictionary<(int SeedIndex, int Replication), string>();

        for (var index = 0; index < targetWorkerCount; index++)
        {
            var seedIndex = index % seedDocument.Workers.Count;
            var replication = index / seedDocument.Workers.Count;
            var syntheticId = (SyntheticWorkerIdStart + index).ToString("D5");
            workers.Add(CreateSyntheticWorker(seedDocument.Workers[seedIndex], seedIndex, syntheticId, replication));
            personIdMap[(seedIndex, replication)] = syntheticId;
        }

        for (var index = 0; index < workers.Count; index++)
        {
            var seedIndex = index % seedDocument.Workers.Count;
            var replication = index / seedDocument.Workers.Count;
            var remappedManagerId = ResolveSyntheticManagerId(seedDocument.Workers, seedIndex, replication, personIdMap);
            if (string.Equals(workers[index].ManagerId, remappedManagerId, StringComparison.Ordinal))
            {
                continue;
            }

            workers[index] = workers[index] with { ManagerId = remappedManagerId };
        }

        return new MockFixtureDocument(workers);
    }

    private static MockFixtureDocument NormalizeSyntheticIdentities(MockFixtureDocument document)
    {
        var normalizedWorkers = document.Workers
            .Select((worker, index) => NormalizeSyntheticIdentity(worker, index))
            .ToArray();

        var normalizedIds = normalizedWorkers
            .Select(worker => worker.PersonIdExternal)
            .ToArray();
        var normalizedIdSet = normalizedIds.ToHashSet(StringComparer.Ordinal);

        var workers = normalizedWorkers
            .Select((worker, index) => worker with
            {
                ManagerId = NormalizeManagerId(worker.ManagerId, normalizedIds, normalizedIdSet, index)
            })
            .ToArray();

        return new MockFixtureDocument(workers);
    }

    private static MockWorkerFixture NormalizeSyntheticIdentity(MockWorkerFixture worker, int index)
    {
        var syntheticId = NormalizeSyntheticId(worker.PersonIdExternal, index);
        var suffix = int.TryParse(syntheticId, out var numericId)
            ? Math.Max(0, numericId - SyntheticWorkerIdStart)
            : index;
        var userName = $"user.{syntheticId}";
        var nameProfile = MockNameCatalog.GetNameProfile(suffix, worker.PreferredName is not null);

        return worker with
        {
            PersonIdExternal = syntheticId,
            PersonId = syntheticId,
            PerPersonUuid = $"uuid-{syntheticId}",
            UserName = userName,
            UserId = userName,
            Email = $"{userName}@example.test",
            FirstName = nameProfile.FirstName,
            LastName = nameProfile.LastName,
            PreferredName = nameProfile.PreferredName,
            DisplayName = worker.DisplayName is null ? null : nameProfile.DisplayName,
            Position = worker.Position is null ? null : $"POS-{syntheticId}",
            BusinessPhoneNumber = worker.BusinessPhoneNumber is null ? null : $"{7000000 + suffix:D7}",
            BusinessPhoneExtension = worker.BusinessPhoneExtension is null ? null : $"{100 + (suffix % 900):D3}",
            CellPhoneNumber = worker.CellPhoneNumber is null ? null : $"{8000000 + suffix:D7}",
            Location = worker.Location is null
                ? null
                : worker.Location with
                {
                    Address = worker.Location.Address is null ? null : $"Suite {syntheticId}",
                    ZipCode = worker.Location.ZipCode is null ? null : syntheticId,
                    CustomString4 = worker.Location.CustomString4 is null ? null : $"Floor {100 + (suffix % 900):D3}"
                }
        };
    }

    private static string NormalizeSyntheticId(string workerId, int index)
    {
        if (!string.IsNullOrWhiteSpace(workerId) &&
            workerId.Length == 5 &&
            workerId.All(char.IsDigit))
        {
            return workerId;
        }

        if (string.IsNullOrWhiteSpace(workerId))
        {
            return (SyntheticWorkerIdStart + index).ToString("D5");
        }

        return StableNumber(workerId, 10_000, 99_999).ToString("D5");
    }

    private static string? NormalizeManagerId(
        string? managerId,
        IReadOnlyList<string> normalizedIds,
        IReadOnlySet<string> normalizedIdSet,
        int index)
    {
        if (normalizedIds.Count == 0 || index <= 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(managerId) && normalizedIdSet.Contains(managerId))
        {
            var managerIndex = normalizedIds
                .Select((id, candidateIndex) => new { Id = id, Index = candidateIndex })
                .FirstOrDefault(candidate => string.Equals(candidate.Id, managerId, StringComparison.Ordinal))
                ?.Index;

            if (managerIndex is not null && managerIndex.Value < index)
            {
                return managerId;
            }
        }

        return normalizedIds[index - 1];
    }

    private static int StableNumber(string value, int minInclusive, int maxInclusive)
    {
        if (maxInclusive < minInclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(maxInclusive));
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var number = BitConverter.ToUInt32(bytes, 0);
        var range = (uint)(maxInclusive - minInclusive + 1);
        return (int)(minInclusive + (number % range));
    }

    private static MockWorkerFixture CreateSyntheticWorker(MockWorkerFixture seedWorker, int seedIndex, string syntheticId, int replication)
    {
        var userName = $"user.{syntheticId}";
        var departmentSuffix = seedIndex == 0 && replication == 0 ? string.Empty : $" {seedIndex + 1:D2}-{replication + 1:D3}";
        var sequence = int.Parse(syntheticId) - SyntheticWorkerIdStart;
        var nameProfile = MockNameCatalog.GetNameProfile(sequence, seedWorker.PreferredName is not null);

        return seedWorker with
        {
            PersonIdExternal = syntheticId,
            PersonId = syntheticId,
            PerPersonUuid = $"uuid-{syntheticId}",
            UserName = userName,
            UserId = userName,
            Email = $"{userName}@example.test",
            FirstName = nameProfile.FirstName,
            LastName = nameProfile.LastName,
            PreferredName = nameProfile.PreferredName,
            DisplayName = seedWorker.DisplayName is null ? null : nameProfile.DisplayName,
            ManagerId = seedWorker.ManagerId,
            Position = seedWorker.Position is null ? null : $"POS-{syntheticId}",
            BusinessPhoneNumber = seedWorker.BusinessPhoneNumber is null ? null : $"{7000000 + sequence:D7}",
            BusinessPhoneExtension = seedWorker.BusinessPhoneExtension is null ? null : $"{100 + (sequence % 900):D3}",
            CellPhoneNumber = seedWorker.CellPhoneNumber is null ? null : $"{8000000 + sequence:D7}",
            Location = seedWorker.Location is null
                ? null
                : seedWorker.Location with
                {
                    Address = seedWorker.Location.Address is null ? null : $"Suite {syntheticId}",
                    ZipCode = seedWorker.Location.ZipCode is null ? null : syntheticId
                },
            Department = AppendVariant(seedWorker.Department, departmentSuffix),
            DepartmentName = AppendVariant(seedWorker.DepartmentName, departmentSuffix),
            JobTitle = AppendVariant(seedWorker.JobTitle, departmentSuffix),
            BusinessUnit = AppendVariant(seedWorker.BusinessUnit, departmentSuffix),
            Division = AppendVariant(seedWorker.Division, departmentSuffix),
            CostCenterDescription = AppendVariant(seedWorker.CostCenterDescription, departmentSuffix)
        };
    }

    private static string? ResolveSyntheticManagerId(
        IReadOnlyList<MockWorkerFixture> seedWorkers,
        int seedIndex,
        int replication,
        IReadOnlyDictionary<(int SeedIndex, int Replication), string> personIdMap)
    {
        var managerId = seedWorkers[seedIndex].ManagerId;
        if (string.IsNullOrWhiteSpace(managerId))
        {
            return managerId;
        }

        var managerSeedIndex = seedWorkers
            .Select((worker, index) => new { worker.PersonIdExternal, Index = index })
            .FirstOrDefault(candidate => string.Equals(candidate.PersonIdExternal, managerId, StringComparison.OrdinalIgnoreCase))
            ?.Index;

        if (managerSeedIndex is null)
        {
            return null;
        }

        return personIdMap.TryGetValue((managerSeedIndex.Value, replication), out var remappedManagerId)
            ? remappedManagerId
            : null;
    }

    private static string? AppendVariant(string? value, string suffix)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(suffix))
        {
            return value;
        }

        return $"{value}{suffix}";
    }

    private static IEnumerable<MockWorkerFixture> ApplyEmpJobSemantics(IEnumerable<MockWorkerFixture> workers, ODataQuery query, MockEmpJobOptions empJobOptions)
    {
        if (!string.IsNullOrWhiteSpace(query.AsOfDate) && DateTimeOffset.TryParse(query.AsOfDate, out var explicitAsOf))
        {
            workers = workers.Where(worker => IsEffectiveOnOrBefore(worker.StartDate, explicitAsOf));
        }
        else
        {
            workers = workers.Where(worker =>
                IsEffectiveOnOrBefore(worker.StartDate, DateTimeOffset.UtcNow) ||
                (empJobOptions.IncludeTaggedPrehiresInDefaultListing && IsPreboarding(worker)));
        }

        if (string.IsNullOrWhiteSpace(query.Filter))
        {
            return workers;
        }

        return workers.Where(worker => MatchesSupportedFilter(worker, query.Filter));
    }

    private static bool IsEffectiveOnOrBefore(string startDate, DateTimeOffset asOfDate)
    {
        if (!DateTimeOffset.TryParse(startDate, out var parsedStart))
        {
            return true;
        }

        return parsedStart <= asOfDate;
    }

    private static bool IsPreboarding(MockWorkerFixture worker)
    {
        return string.Equals(ResolveLifecycleState(worker), MockLifecycleState.Preboarding, StringComparison.OrdinalIgnoreCase) ||
               worker.ScenarioTags.Any(tag =>
                   string.Equals(tag, "preboarding", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(tag, "prehire", StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveLifecycleState(MockWorkerFixture worker)
    {
        return !string.IsNullOrWhiteSpace(worker.LifecycleState)
            ? MockLifecycleState.Normalize(worker.LifecycleState)
            : MockLifecycleState.Infer(worker.StartDate, worker.EmploymentStatus, worker.EndDate, worker.ScenarioTags);
    }

    private static bool MatchesSupportedFilter(MockWorkerFixture worker, string filter)
    {
        var orBranches = SplitTopLevelExpression(filter, "or");
        if (orBranches.Count == 0)
        {
            return true;
        }

        return orBranches.Any(branch => MatchesSupportedBranch(worker, branch));
    }

    private static bool MatchesSupportedBranch(MockWorkerFixture worker, string branch)
    {
        var conditions = SplitTopLevelExpression(branch, "and");

        foreach (var condition in conditions)
        {
            if (!TryMatchCondition(worker, condition, out var matches))
            {
                continue;
            }

            if (!matches)
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<string> SplitTopLevelExpression(string expression, string separator)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return [];
        }

        var parts = new List<string>();
        var depth = 0;
        var inString = false;
        var start = 0;
        var token = $" {separator} ";

        for (var index = 0; index < expression.Length; index++)
        {
            var current = expression[index];
            if (current == '\'')
            {
                if (inString && index + 1 < expression.Length && expression[index + 1] == '\'')
                {
                    index++;
                    continue;
                }

                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (current == '(')
            {
                depth++;
                continue;
            }

            if (current == ')')
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (depth == 0 &&
                index + token.Length <= expression.Length &&
                string.Equals(expression.Substring(index, token.Length), token, StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(TrimWrappingParentheses(expression[start..index]));
                start = index + token.Length;
                index += token.Length - 1;
            }
        }

        parts.Add(TrimWrappingParentheses(expression[start..]));
        return parts
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();
    }

    private static string TrimWrappingParentheses(string value)
    {
        var trimmed = value.Trim();
        while (trimmed.Length >= 2 &&
               trimmed[0] == '(' &&
               trimmed[^1] == ')' &&
               HasBalancedOuterParentheses(trimmed))
        {
            trimmed = trimmed[1..^1].Trim();
        }

        return trimmed;
    }

    private static bool HasBalancedOuterParentheses(string value)
    {
        var depth = 0;
        var inString = false;

        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (current == '\'')
            {
                if (inString && index + 1 < value.Length && value[index + 1] == '\'')
                {
                    index++;
                    continue;
                }

                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (current == '(')
            {
                depth++;
            }
            else if (current == ')')
            {
                depth--;
                if (depth == 0 && index < value.Length - 1)
                {
                    return false;
                }
            }
        }

        return depth == 0;
    }

    private static bool TryMatchCondition(MockWorkerFixture worker, string condition, out bool matches)
    {
        matches = false;

        if (TryParseStatusSetCondition(condition, out var allowedStatuses))
        {
            matches = !string.IsNullOrWhiteSpace(worker.EmploymentStatus) &&
                      allowedStatuses.Contains(worker.EmploymentStatus);
            return true;
        }

        if (TryParseDateThresholdCondition(condition, out var fieldName, out var operatorToken, out var threshold))
        {
            matches = MatchesDateCondition(worker, fieldName, operatorToken, threshold);
            return true;
        }

        return false;
    }

    private static bool TryParseStatusSetCondition(string condition, out IReadOnlySet<string> allowedStatuses)
    {
        allowedStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = condition.Trim();
        if (!normalized.Contains("emplStatus", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (normalized.Contains(" in ", StringComparison.OrdinalIgnoreCase))
        {
            allowedStatuses = normalized
                .Split('\'')
                .Where((_, index) => index % 2 == 1)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return allowedStatuses.Count > 0;
        }

        if (normalized.Contains(" eq ", StringComparison.OrdinalIgnoreCase))
        {
            var value = normalized
                .Split('\'')
                .Where((_, index) => index % 2 == 1)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            allowedStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { value };
            return true;
        }

        return false;
    }

    private static bool TryParseDateThresholdCondition(string condition, out string fieldName, out string operatorToken, out DateTimeOffset threshold)
    {
        fieldName = string.Empty;
        operatorToken = string.Empty;
        threshold = default;

        var tokens = condition.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length != 3)
        {
            return false;
        }

        fieldName = tokens[0];
        operatorToken = tokens[1];
        if (!fieldName.EndsWith("Date", StringComparison.OrdinalIgnoreCase) &&
            !fieldName.EndsWith("DateTime", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var valueToken = tokens[2];
        const string datetimeOffsetPrefix = "datetimeoffset'";
        const string datetimePrefix = "datetime'";
        var prefix = valueToken.StartsWith(datetimeOffsetPrefix, StringComparison.OrdinalIgnoreCase)
            ? datetimeOffsetPrefix
            : valueToken.StartsWith(datetimePrefix, StringComparison.OrdinalIgnoreCase)
                ? datetimePrefix
                : null;
        if (prefix is null || !valueToken.EndsWith('\''))
        {
            return false;
        }

        var rawValue = valueToken[prefix.Length..^1];
        return DateTimeOffset.TryParse(rawValue, out threshold);
    }

    private static bool MatchesDateCondition(MockWorkerFixture worker, string fieldName, string operatorToken, DateTimeOffset threshold)
    {
        var candidate = ResolveDateField(worker, fieldName);
        if (!DateTimeOffset.TryParse(candidate, out var value))
        {
            return false;
        }

        return operatorToken.ToLowerInvariant() switch
        {
            "ge" => value >= threshold,
            "gt" => value > threshold,
            "le" => value <= threshold,
            "lt" => value < threshold,
            "eq" => value == threshold,
            _ => false
        };
    }

    private static string? ResolveDateField(MockWorkerFixture worker, string fieldName)
    {
        return fieldName.Trim().ToLowerInvariant() switch
        {
            "startdate" => worker.StartDate,
            "employmentnav/startdate" => worker.StartDate,
            "enddate" => worker.EndDate,
            "employmentnav/enddate" => worker.EndDate,
            "firstdateworked" => worker.FirstDateWorked,
            "employmentnav/firstdateworked" => worker.FirstDateWorked,
            "lastdateworked" => worker.LastDateWorked,
            "employmentnav/lastdateworked" => worker.LastDateWorked,
            "lastmodifieddatetime" => worker.LastModifiedDateTime,
            _ => null
        };
    }
}
