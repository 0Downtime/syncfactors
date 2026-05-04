using Microsoft.Data.Sqlite;

namespace SyncFactors.Infrastructure;

internal static class SqliteConnections
{
    private const int DefaultCommandTimeoutSeconds = 10;

    public static SqliteConnection Open(string databasePath, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode,
            DefaultTimeout = DefaultCommandTimeoutSeconds,
        }.ToString();

        return new SqliteConnection(connectionString);
    }

    public static bool IsBusyOrLocked(SqliteException exception)
    {
        return exception.SqliteErrorCode is 5 or 6;
    }
}
