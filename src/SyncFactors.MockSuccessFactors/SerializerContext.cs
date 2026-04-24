using System.Text.Json.Serialization;

namespace SyncFactors.MockSuccessFactors;

[JsonSerializable(typeof(FixtureManifest))]
[JsonSerializable(typeof(MockAdminCloneRequest))]
[JsonSerializable(typeof(MockAdminLifecycleStateRequest))]
[JsonSerializable(typeof(MockAdminResetResponse))]
[JsonSerializable(typeof(MockAdminStateResponse))]
[JsonSerializable(typeof(MockAdminWorkerDetailResponse))]
[JsonSerializable(typeof(MockAdminWorkerMutationResponse))]
[JsonSerializable(typeof(MockAdminWorkerUpsertRequest))]
[JsonSerializable(typeof(MockFixtureDocument))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class SerializerContext : JsonSerializerContext
{
}
