using Microsoft.Data.Sqlite;
using SyncFactors.Contracts;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;

namespace SyncFactors.Infrastructure.Tests;

public sealed class SqliteGraveyardRetentionStoreTests
{
    [Fact]
    public async Task InitializeAsync_AddsHoldColumnsToGraveyardRetention()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-graveyard-schema-{Guid.NewGuid():N}.db");

        try
        {
            await CreateVersion9DatabaseAsync(databasePath);

            var initializer = new SqliteDatabaseInitializer(new SqlitePathResolver(databasePath));
            await initializer.InitializeAsync(CancellationToken.None);

            await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info(graveyard_retention);";
            await using var reader = await command.ExecuteReaderAsync();

            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(1));
            }

            Assert.Contains("is_on_hold", columns);
            Assert.Contains("hold_placed_at_utc", columns);
            Assert.Contains("hold_placed_by", columns);
            Assert.Contains("version", columns);
            Assert.Contains("deletion_claim_id", columns);
            Assert.Contains("deletion_claim_version", columns);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task SetHoldAsync_AndUpsertObservedAsync_PreserveHoldState()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-graveyard-store-{Guid.NewGuid():N}.db");

        try
        {
            var initializer = new SqliteDatabaseInitializer(new SqlitePathResolver(databasePath));
            await initializer.InitializeAsync(CancellationToken.None);

            var store = new SqliteGraveyardRetentionStore(new SqlitePathResolver(databasePath));
            await store.UpsertObservedAsync(
                new GraveyardRetentionRecord(
                    WorkerId: "10001",
                    SamAccountName: "10001",
                    DisplayName: "Retired User",
                    DistinguishedName: "CN=Retired User,OU=Graveyard,DC=example,DC=com",
                    Status: "T",
                    EndDateUtc: DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                    LastObservedAtUtc: DateTimeOffset.Parse("2026-04-01T00:00:00Z"),
                    Active: true),
                CancellationToken.None);

            var holdChangedAt = DateTimeOffset.Parse("2026-04-11T12:00:00Z");
            await store.SetHoldAsync("10001", true, "admin-1", holdChangedAt, CancellationToken.None);

            await store.UpsertObservedAsync(
                new GraveyardRetentionRecord(
                    WorkerId: "10001",
                    SamAccountName: "10001",
                    DisplayName: "Retired User Updated",
                    DistinguishedName: "CN=Retired User,OU=Graveyard,DC=example,DC=com",
                    Status: "T",
                    EndDateUtc: DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                    LastObservedAtUtc: DateTimeOffset.Parse("2026-04-12T00:00:00Z"),
                    Active: true),
                CancellationToken.None);

            var record = Assert.Single(await store.ListActiveAsync(CancellationToken.None));
            Assert.True(record.IsOnHold);
            Assert.Equal(holdChangedAt, record.HoldPlacedAtUtc);
            Assert.Equal("admin-1", record.HoldPlacedBy);
            Assert.Equal("Retired User Updated", record.DisplayName);

            await store.SetHoldAsync("10001", false, "admin-2", DateTimeOffset.Parse("2026-04-12T12:00:00Z"), CancellationToken.None);

            record = Assert.Single(await store.ListActiveAsync(CancellationToken.None));
            Assert.False(record.IsOnHold);
            Assert.Null(record.HoldPlacedAtUtc);
            Assert.Null(record.HoldPlacedBy);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task DeletionLease_BlocksHoldUntilItIsReleased()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-graveyard-claim-{Guid.NewGuid():N}.db");

        try
        {
            await new SqliteDatabaseInitializer(new SqlitePathResolver(databasePath)).InitializeAsync(CancellationToken.None);
            IGraveyardRetentionStore store = new SqliteGraveyardRetentionStore(new SqlitePathResolver(databasePath));
            await store.UpsertObservedAsync(
                new GraveyardRetentionRecord(
                    WorkerId: "10001",
                    SamAccountName: "10001",
                    DisplayName: "Retired User",
                    DistinguishedName: "CN=Retired User,OU=Graveyard,DC=example,DC=com",
                    Status: "T",
                    EndDateUtc: DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                    LastObservedAtUtc: DateTimeOffset.Parse("2026-04-01T00:00:00Z"),
                    Active: true),
                CancellationToken.None);

            var now = DateTimeOffset.Parse("2026-04-11T12:00:00Z");
            var firstClaim = await store.TryClaimDeletionAsync("10001", expectedVersion: 0, claimId: "claim-one", now, now.AddMinutes(5), CancellationToken.None);
            var duplicateClaim = await store.TryClaimDeletionAsync("10001", expectedVersion: 0, claimId: "claim-two", now, now.AddMinutes(5), CancellationToken.None);

            Assert.NotNull(firstClaim);
            Assert.Equal(1, firstClaim!.Version);
            Assert.Null(duplicateClaim);

            var holdDuringLease = await store.SetHoldAsync("10001", true, "admin-1", now, CancellationToken.None);

            Assert.False(holdDuringLease.Succeeded);
            Assert.Equal(GraveyardHoldChangeOutcome.ActiveDeletionLease, holdDuringLease.Outcome);
            Assert.NotNull(await store.GetDeletionClaimAsync("10001", "claim-one", firstClaim.Version, now, CancellationToken.None));
            Assert.True(await store.ReleaseDeletionClaimAsync("10001", "claim-one", firstClaim.Version, CancellationToken.None));

            var holdAfterRelease = await store.SetHoldAsync("10001", true, "admin-1", now, CancellationToken.None);
            Assert.True(holdAfterRelease.Succeeded);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task Hold_PreventsDeletionLeaseFromBeingClaimed()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-graveyard-hold-first-{Guid.NewGuid():N}.db");

        try
        {
            await new SqliteDatabaseInitializer(new SqlitePathResolver(databasePath)).InitializeAsync(CancellationToken.None);
            IGraveyardRetentionStore store = new SqliteGraveyardRetentionStore(new SqlitePathResolver(databasePath));
            await store.UpsertObservedAsync(CreateRecord("10001"), CancellationToken.None);
            var now = DateTimeOffset.Parse("2026-04-11T12:00:00Z");

            var hold = await store.SetHoldAsync("10001", true, "admin-1", now, CancellationToken.None);
            var claim = await store.TryClaimDeletionAsync("10001", expectedVersion: 1, "claim-one", now, now.AddMinutes(5), CancellationToken.None);

            Assert.True(hold.Succeeded);
            Assert.Null(claim);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task ExpiredDeletionLease_CanBeRecoveredByANewVersionedClaim()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-graveyard-expired-lease-{Guid.NewGuid():N}.db");

        try
        {
            await new SqliteDatabaseInitializer(new SqlitePathResolver(databasePath)).InitializeAsync(CancellationToken.None);
            IGraveyardRetentionStore store = new SqliteGraveyardRetentionStore(new SqlitePathResolver(databasePath));
            await store.UpsertObservedAsync(CreateRecord("10001"), CancellationToken.None);
            var acquiredAtUtc = DateTimeOffset.Parse("2026-04-11T12:00:00Z");
            var firstClaim = await store.TryClaimDeletionAsync("10001", expectedVersion: 0, "claim-one", acquiredAtUtc, acquiredAtUtc.AddMinutes(1), CancellationToken.None);
            Assert.NotNull(firstClaim);

            var recoveryAtUtc = acquiredAtUtc.AddMinutes(2);
            var recoveredClaim = await store.TryClaimDeletionAsync("10001", firstClaim!.Version, "claim-two", recoveryAtUtc, recoveryAtUtc.AddMinutes(5), CancellationToken.None);

            Assert.NotNull(recoveredClaim);
            Assert.Equal(firstClaim.Version + 1, recoveredClaim!.Version);
            Assert.Null(await store.GetDeletionClaimAsync("10001", "claim-one", firstClaim.Version, recoveryAtUtc, CancellationToken.None));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task ObservationAndResolution_DoNotClearAnActiveDeletionLease()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"syncfactors-graveyard-preserve-lease-{Guid.NewGuid():N}.db");

        try
        {
            await new SqliteDatabaseInitializer(new SqlitePathResolver(databasePath)).InitializeAsync(CancellationToken.None);
            IGraveyardRetentionStore store = new SqliteGraveyardRetentionStore(new SqlitePathResolver(databasePath));
            var record = CreateRecord("10001");
            await store.UpsertObservedAsync(record, CancellationToken.None);
            var now = DateTimeOffset.UtcNow;
            var claim = await store.TryClaimDeletionAsync("10001", expectedVersion: 0, "claim-one", now, now.AddMinutes(5), CancellationToken.None);
            Assert.NotNull(claim);

            await store.UpsertObservedAsync(record with { DisplayName = "Observed Again" }, CancellationToken.None);
            await store.ResolveAsync("10001", CancellationToken.None);

            Assert.NotNull(await store.GetDeletionClaimAsync("10001", "claim-one", claim!.Version, DateTimeOffset.UtcNow, CancellationToken.None));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    private static GraveyardRetentionRecord CreateRecord(string workerId) =>
        new(
            WorkerId: workerId,
            SamAccountName: workerId,
            DisplayName: "Retired User",
            DistinguishedName: "CN=Retired User,OU=Graveyard,DC=example,DC=com",
            Status: "T",
            EndDateUtc: DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            LastObservedAtUtc: DateTimeOffset.Parse("2026-04-01T00:00:00Z"),
            Active: true);

    private static async Task CreateVersion9DatabaseAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWriteCreate");
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE schema_versions (
              version INTEGER NOT NULL PRIMARY KEY,
              applied_at TEXT NOT NULL
            );

            INSERT INTO schema_versions (version, applied_at)
            VALUES
              (1, '2026-04-01T00:00:00Z'),
              (2, '2026-04-01T00:00:00Z'),
              (3, '2026-04-01T00:00:00Z'),
              (4, '2026-04-01T00:00:00Z'),
              (5, '2026-04-01T00:00:00Z'),
              (6, '2026-04-01T00:00:00Z'),
              (7, '2026-04-01T00:00:00Z'),
              (8, '2026-04-01T00:00:00Z'),
              (9, '2026-04-01T00:00:00Z');

            CREATE TABLE graveyard_retention (
              worker_id TEXT NOT NULL PRIMARY KEY,
              sam_account_name TEXT NULL,
              display_name TEXT NULL,
              distinguished_name TEXT NULL,
              status TEXT NOT NULL,
              end_date_utc TEXT NULL,
              last_observed_at_utc TEXT NOT NULL,
              active INTEGER NOT NULL DEFAULT 1
            );
            """;
        await command.ExecuteNonQueryAsync();
    }
}
