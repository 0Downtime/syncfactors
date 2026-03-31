using Microsoft.Data.Sqlite;
using SyncFactors.Infrastructure;

namespace SyncFactors.Infrastructure.Tests;

public sealed class SqliteDeltaSyncStateStoreTests
{
    [Fact]
    public async Task SaveCheckpointAsync_UpsertsAndRoundTripsCheckpoint()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-delta-state-{Guid.NewGuid():N}.db");

        try
        {
            var initializer = new SqliteDatabaseInitializer(new SqlitePathResolver(databasePath));
            await initializer.InitializeAsync(CancellationToken.None);

            var store = new SqliteDeltaSyncStateStore(new SqlitePathResolver(databasePath));
            var expected = DateTimeOffset.Parse("2026-03-31T10:15:00Z");

            await store.SaveCheckpointAsync("EmpJob|userId|lastModifiedDateTime|emplStatus in 'A','U'|", expected, CancellationToken.None);

            var actual = await store.GetCheckpointAsync("EmpJob|userId|lastModifiedDateTime|emplStatus in 'A','U'|", CancellationToken.None);
            Assert.Equal(expected, actual);

            await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM delta_sync_state;";
            var rowCount = (long)(await command.ExecuteScalarAsync() ?? 0L);
            Assert.Equal(1L, rowCount);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }
}
