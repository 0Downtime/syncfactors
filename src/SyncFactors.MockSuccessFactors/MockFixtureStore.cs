using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SyncFactors.MockSuccessFactors;

public sealed class MockFixtureStore(IOptions<MockSuccessFactorsOptions> options)
{
    private const int SyntheticWorkerIdStart = 10000;
    private readonly Lazy<MockFixtureDocument> _document = new(() => Load(options.Value));
    private readonly MockSuccessFactorsOptions _options = options.Value;

    public MockFixtureDocument GetDocument() => _document.Value;

    public MockWorkerFixture? FindByIdentity(string identityField, string? workerId)
    {
        if (string.IsNullOrWhiteSpace(workerId))
        {
            return null;
        }

        return GetDocument().Workers.FirstOrDefault(worker =>
            string.Equals(identityField, "personIdExternal", StringComparison.OrdinalIgnoreCase)
                ? string.Equals(worker.PersonIdExternal, workerId, StringComparison.OrdinalIgnoreCase)
                : string.Equals(identityField, "userId", StringComparison.OrdinalIgnoreCase)
                    ? string.Equals(worker.UserId ?? worker.UserName, workerId, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(identityField, "username", StringComparison.OrdinalIgnoreCase)
                        ? string.Equals(worker.UserName, workerId, StringComparison.OrdinalIgnoreCase)
                        : false);
    }

    public IReadOnlyList<MockWorkerFixture> QueryWorkers(string entitySet, ODataQuery query)
    {
        IEnumerable<MockWorkerFixture> workers = GetDocument().Workers;

        if (!string.IsNullOrWhiteSpace(query.WorkerId))
        {
            var worker = FindByIdentity(query.IdentityField, query.WorkerId);
            workers = worker is null ? [] : [worker];
        }
        else if (string.Equals(entitySet, "EmpJob", StringComparison.OrdinalIgnoreCase))
        {
            workers = ApplyEmpJobSemantics(workers, query, _options.EmpJob);
        }

        return workers.ToArray();
    }

    private static MockFixtureDocument Load(MockSuccessFactorsOptions options)
    {
        var path = ResolveFixturePath(options);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Mock fixture file was not found at '{path}'.", path);
        }

        var json = File.ReadAllText(path);
        var document = JsonSerializer.Deserialize<MockFixtureDocument>(json, SerializerContext.Default.MockFixtureDocument)
            ?? new MockFixtureDocument([]);

        var populatedDocument = options.SyntheticPopulation.Enabled
            ? ExpandSyntheticPopulation(document, options.SyntheticPopulation.TargetWorkerCount)
            : document;

        return NormalizeSyntheticIdentities(populatedDocument);
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
        var lastName = $"Sample{syntheticId}";
        var preferredName = worker.PreferredName is null ? null : $"Preferred{syntheticId}";

        return worker with
        {
            PersonIdExternal = syntheticId,
            PersonId = syntheticId,
            PerPersonUuid = $"uuid-{syntheticId}",
            UserName = userName,
            UserId = userName,
            Email = $"{userName}@example.test",
            FirstName = $"Worker{syntheticId}",
            LastName = lastName,
            PreferredName = preferredName,
            DisplayName = worker.DisplayName is null ? null : $"{preferredName ?? $"Worker{syntheticId}"} {lastName}",
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

        return seedWorker with
        {
            PersonIdExternal = syntheticId,
            PersonId = syntheticId,
            PerPersonUuid = $"uuid-{syntheticId}",
            UserName = userName,
            UserId = userName,
            Email = $"{userName}@example.test",
            FirstName = $"Worker{syntheticId}",
            LastName = $"Sample{syntheticId}",
            PreferredName = seedWorker.PreferredName is null ? null : $"Preferred{syntheticId}",
            DisplayName = seedWorker.DisplayName is null ? null : $"Preferred{syntheticId} Sample{syntheticId}",
            ManagerId = seedWorker.ManagerId,
            Position = seedWorker.Position is null ? null : $"POS-{syntheticId}",
            BusinessPhoneNumber = seedWorker.BusinessPhoneNumber is null ? null : $"{7000000 + (int.Parse(syntheticId) - SyntheticWorkerIdStart):D7}",
            BusinessPhoneExtension = seedWorker.BusinessPhoneExtension is null ? null : $"{100 + ((int.Parse(syntheticId) - SyntheticWorkerIdStart) % 900):D3}",
            CellPhoneNumber = seedWorker.CellPhoneNumber is null ? null : $"{8000000 + (int.Parse(syntheticId) - SyntheticWorkerIdStart):D7}",
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
                (empJobOptions.IncludeTaggedPrehiresInDefaultListing && IsTaggedPrehire(worker)));
        }

        if (string.IsNullOrWhiteSpace(query.Filter))
        {
            return workers;
        }

        if (query.Filter.Contains("emplStatus", StringComparison.OrdinalIgnoreCase) &&
            query.Filter.Contains(" in ", StringComparison.OrdinalIgnoreCase))
        {
            var allowedStatuses = query.Filter
                .Split('\'')
                .Where((_, index) => index % 2 == 1)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return workers.Where(worker => !string.IsNullOrWhiteSpace(worker.EmploymentStatus) && allowedStatuses.Contains(worker.EmploymentStatus));
        }

        return workers;
    }

    private static bool IsEffectiveOnOrBefore(string startDate, DateTimeOffset asOfDate)
    {
        if (!DateTimeOffset.TryParse(startDate, out var parsedStart))
        {
            return true;
        }

        return parsedStart <= asOfDate;
    }

    private static bool IsTaggedPrehire(MockWorkerFixture worker)
    {
        return worker.ScenarioTags.Any(tag => string.Equals(tag, "prehire", StringComparison.OrdinalIgnoreCase));
    }
}
