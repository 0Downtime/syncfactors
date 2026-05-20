using Microsoft.Data.Sqlite;
using System.Data;

namespace SyncFactors.Infrastructure;

internal static class SqliteConnections
{
    public const string PasswordEnvironmentVariable = "SYNCFACTORS_SQLITE_PASSWORD";
    public const string ConfigurationPasswordEnvironmentVariable = "SyncFactors__SqlitePassword";
    private const int DefaultCommandTimeoutSeconds = 10;

    static SqliteConnections()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    public static SqliteConnection Open(string databasePath, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
        => Open(databasePath, mode, GetConfiguredPassword(), pooling: true);

    public static SqliteConnection OpenPlaintext(string databasePath, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
        => Open(databasePath, mode, password: null, pooling: false);

    public static string? GetConfiguredPassword()
    {
        var password = Environment.GetEnvironmentVariable(PasswordEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(password))
        {
            return password;
        }

        password = Environment.GetEnvironmentVariable(ConfigurationPasswordEnvironmentVariable);
        return string.IsNullOrWhiteSpace(password) ? null : password;
    }

    public static bool EncryptionEnabled => !string.IsNullOrWhiteSpace(GetConfiguredPassword());

    private static SqliteConnection Open(string databasePath, SqliteOpenMode mode, string? password, bool pooling)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode,
            DefaultTimeout = DefaultCommandTimeoutSeconds,
            Pooling = pooling,
        };

        if (!string.IsNullOrWhiteSpace(password))
        {
            builder.Password = password;
        }

        var connection = new SqliteConnection(builder.ToString());
        connection.StateChange += (_, args) =>
        {
            if (args.CurrentState == ConnectionState.Open)
            {
                ConfigureOpenConnection(connection, mode);
            }
        };

        return connection;
    }

    public static bool IsBusyOrLocked(SqliteException exception)
    {
        return exception.SqliteErrorCode is 5 or 6;
    }

    private static void ConfigureOpenConnection(SqliteConnection connection, SqliteOpenMode mode)
    {
        using (var busyTimeoutCommand = connection.CreateCommand())
        {
            busyTimeoutCommand.CommandText = "PRAGMA busy_timeout = 5000;";
            busyTimeoutCommand.ExecuteNonQuery();
        }

        if (mode == SqliteOpenMode.ReadOnly ||
            string.Equals(connection.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            using var journalModeCommand = connection.CreateCommand();
            journalModeCommand.CommandText = "PRAGMA journal_mode = WAL;";
            _ = journalModeCommand.ExecuteScalar();
        }
        catch (SqliteException ex) when (IsBusyOrLocked(ex))
        {
            // Another process may be initializing the same runtime database. The
            // busy timeout still applies, and a later write-capable connection can
            // enable WAL once the transient lock clears.
        }
    }
}
