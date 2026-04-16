using System.Collections.Concurrent;
using System.DirectoryServices.Protocols;
using Microsoft.Extensions.Logging;

namespace SyncFactors.Infrastructure;

public interface IActiveDirectoryConnectionPool
{
    ActiveDirectoryConnectionPool.ActiveDirectoryConnectionLease Lease(ActiveDirectoryConfig config, ILogger logger, TimeSpan timeout);
}

public sealed class ActiveDirectoryConnectionPool : IActiveDirectoryConnectionPool, IDisposable
{
    private readonly ConcurrentDictionary<PoolKey, PoolBucket> _buckets = new();
    private readonly Func<ActiveDirectoryConfig, ILogger, TimeSpan, LdapConnection> _connectionFactory;
    private readonly int _maxIdleConnectionsPerKey;
    private bool _disposed;

    public ActiveDirectoryConnectionPool(
        Func<ActiveDirectoryConfig, ILogger, TimeSpan, LdapConnection>? connectionFactory = null,
        int maxIdleConnectionsPerKey = 8)
    {
        if (maxIdleConnectionsPerKey < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxIdleConnectionsPerKey), "The pool must retain at least one idle connection per key.");
        }

        _connectionFactory = connectionFactory ?? ActiveDirectoryConnectionFactory.CreateConnection;
        _maxIdleConnectionsPerKey = maxIdleConnectionsPerKey;
    }

    public ActiveDirectoryConnectionPool.ActiveDirectoryConnectionLease Lease(ActiveDirectoryConfig config, ILogger logger, TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = PoolKey.FromConfig(config);
        var bucket = _buckets.GetOrAdd(key, static _ => new PoolBucket());
        if (bucket.IdleConnections.TryTake(out var connection))
        {
            Interlocked.Decrement(ref bucket.IdleCount);
            logger.LogDebug("Reusing pooled AD connection. Server={Server}", config.Server);
            return new ActiveDirectoryConnectionLease(this, key, bucket, connection);
        }

        return new ActiveDirectoryConnectionLease(this, key, bucket, _connectionFactory(config, logger, timeout));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var bucket in _buckets.Values)
        {
            while (bucket.IdleConnections.TryTake(out var connection))
            {
                connection.Dispose();
            }
        }

        _buckets.Clear();
    }

    private void Return(PoolKey key, PoolBucket bucket, LdapConnection connection, bool isReusable)
    {
        if (_disposed || !isReusable)
        {
            connection.Dispose();
            return;
        }

        var nextIdleCount = Interlocked.Increment(ref bucket.IdleCount);
        if (nextIdleCount > _maxIdleConnectionsPerKey)
        {
            Interlocked.Decrement(ref bucket.IdleCount);
            connection.Dispose();
            return;
        }

        bucket.IdleConnections.Add(connection);
    }

    public sealed class ActiveDirectoryConnectionLease : IDisposable
    {
        private readonly ActiveDirectoryConnectionPool _owner;
        private readonly PoolKey _key;
        private readonly PoolBucket _bucket;
        private LdapConnection? _connection;
        private bool _isReusable = true;

        internal ActiveDirectoryConnectionLease(
            ActiveDirectoryConnectionPool owner,
            PoolKey key,
            PoolBucket bucket,
            LdapConnection connection)
        {
            _owner = owner;
            _key = key;
            _bucket = bucket;
            _connection = connection;
        }

        public LdapConnection Connection =>
            _connection ?? throw new ObjectDisposedException(nameof(ActiveDirectoryConnectionLease));

        public void Invalidate()
        {
            _isReusable = false;
        }

        public void Dispose()
        {
            var connection = Interlocked.Exchange(ref _connection, null);
            if (connection is null)
            {
                return;
            }

            _owner.Return(_key, _bucket, connection, _isReusable);
        }
    }

    internal sealed class PoolBucket
    {
        public ConcurrentBag<LdapConnection> IdleConnections { get; } = new();
        public int IdleCount;
    }

    internal readonly record struct PoolKey(
        string Server,
        int? Port,
        string Username,
        string BindPassword,
        string TransportMode,
        bool AllowLdapFallback,
        bool RequireCertificateValidation,
        bool RequireSigning,
        string TrustedCertificateThumbprints)
    {
        public static PoolKey FromConfig(ActiveDirectoryConfig config)
        {
            return new PoolKey(
                config.Server,
                config.Port,
                config.Username ?? string.Empty,
                config.BindPassword ?? string.Empty,
                config.Transport.Mode,
                config.Transport.AllowLdapFallback,
                config.Transport.RequireCertificateValidation,
                config.Transport.RequireSigning,
                string.Join(";", config.Transport.TrustedCertificateThumbprints));
        }
    }
}
