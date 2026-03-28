using System.Text.Json.Serialization;

namespace SyncFactors.MockSuccessFactors;

[JsonSerializable(typeof(FixtureManifest))]
[JsonSerializable(typeof(MockFixtureDocument))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class SerializerContext : JsonSerializerContext
{
}
