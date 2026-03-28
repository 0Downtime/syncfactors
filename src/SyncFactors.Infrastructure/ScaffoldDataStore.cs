using System.Text.Json;

namespace SyncFactors.Infrastructure;

public sealed class ScaffoldDataStore(ScaffoldDataPathResolver pathResolver)
{
    private readonly Lazy<ScaffoldDataDocument> _document = new(() => Load(pathResolver.Resolve()));

    public ScaffoldDataDocument GetDocument() => _document.Value;

    private static ScaffoldDataDocument Load(string path)
    {
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DefaultDocumentJson);
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ScaffoldDataDocument>(json, JsonOptions.Default)
            ?? new ScaffoldDataDocument([], []);
    }

    private const string DefaultDocumentJson =
        """
        {
          "workers": [
            {
              "workerId": "1000123",
              "preferredName": "Bootstrap",
              "lastName": "Worker123",
              "department": "Platform",
              "targetOu": "OU=Bootstrap,DC=example,DC=com",
              "isPrehire": false
            },
            {
              "workerId": "existing-2000456",
              "preferredName": "Existing",
              "lastName": "Worker456",
              "department": "Platform",
              "targetOu": "OU=Platform,DC=example,DC=com",
              "isPrehire": false
            }
          ],
          "directoryUsers": [
            {
              "workerId": "existing-2000456",
              "samAccountName": "existing.worker456",
              "distinguishedName": "CN=Existing Worker456,OU=Platform,DC=example,DC=com",
              "enabled": true,
              "displayName": "Existing Worker456 (Current)"
            }
          ]
        }
        """;
}
