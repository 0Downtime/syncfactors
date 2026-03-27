using Microsoft.Extensions.Options;
using System.Text.Json;

namespace SyncFactors.MockSuccessFactors;

public sealed class MockFixtureStore(IOptions<MockSuccessFactorsOptions> options)
{
    private readonly Lazy<MockFixtureDocument> _document = new(() => Load(options.Value.FixturePath));

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

    private static MockFixtureDocument Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Mock fixture file was not found at '{path}'.", path);
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<MockFixtureDocument>(json, SerializerContext.Default.MockFixtureDocument)
            ?? new MockFixtureDocument([]);
    }
}
