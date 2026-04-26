using System.Collections.Concurrent;
using System.DirectoryServices.Protocols;
using Microsoft.Extensions.Logging;

namespace SyncFactors.Infrastructure;

public interface IActiveDirectoryConnectionPool
{
    ActiveDirectoryConnectionPool.ActiveDirectoryConnectionLease Lease(ActiveDirectoryConfig config, ILogger logger, TimeSpan timeout);
    void InvalidateIdleConnections(ActiveDirectoryConfig config);
}

public sealed class ActiveDirectoryConnectionPool : IActiveDirectoryConnectionPool, IDisposable
{
    private readonly ConcurrentDictionary<PoolKey, PoolBucket> _buckets = new();
    private readonly Func<ActiveDirectoryConfig, ILogger, TimeSpan, ActiveDirectoryConnectionResult> _connectionFactory;
    private readonly int _maxIdleConnectionsPerKey;
    private bool _disposed;

    public ActiveDirectoryConnectionPool(
        Func<ActiveDirectoryConfig, ILogger, TimeSpan, ActiveDirectoryConnectionResult>? connectionFactory = null,
        int maxIdleConnectionsPerKey = 8)
    {
        if (maxIdleConnectionsPerKey < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxIdleConnectionsPerKey), "The pool must retain at least one idle connection per key.");
        }

        _connectionFactory = connectionFactory ?? ((config, logger, timeout) => ActiveDirectoryConnectionFactory.CreateConnectionWithTransport(config, logger, timeout));
        _maxIdleConnectionsPerKey = maxIdleConnectionsPerKey;
    }

    public ActiveDirectoryConnectionPool.ActiveDirectoryConnectionLease Lease(ActiveDirectoryConfig config, ILogger logger, TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = PoolKey.FromConfig(config);
        var bucket = _buckets.GetOrAdd(key, static _ => new PoolBucket());
        while (bucket.IdleConnections.TryTake(out var pooledConnection))
        {
            Interlocked.Decrement(ref bucket.IdleCount);
            if (IsExpired(pooledConnection, config))
            {
                logger.LogDebug("Discarding stale pooled AD connection. Server={Server}", config.Server);
                pooledConnection.Connection.Dispose();
                continue;
            }

            logger.LogDebug("Reusing pooled AD connection. Server={Server}", config.Server);
            return new ActiveDirectoryConnectionLease(this, key, bucket, pooledConnection, wasReused: true);
        }

        return new ActiveDirectoryConnectionLease(this, key, bucket, PooledConnection.From(_connectionFactory(config, logger, timeout)), wasReused: false);
    }

    public void InvalidateIdleConnections(ActiveDirectoryConfig config)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = PoolKey.FromConfig(config);
        if (!_buckets.TryGetValue(key, out var bucket))
        {
            return;
        }

        while (bucket.IdleConnections.TryTake(out var pooledConnection))
        {
            Interlocked.Decrement(ref bucket.IdleCount);
            pooledConnection.Connection.Dispose();
        }
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
            while (bucket.IdleConnections.TryTake(out var pooledConnection))
            {
                pooledConnection.Connection.Dispose();
            }
        }

        _buckets.Clear();
    }

    private void Return(PoolKey key, PoolBucket bucket, PooledConnection pooledConnection, bool isReusable)
    {
        if (_disposed || !isReusable)
        {
            pooledConnection.Connection.Dispose();
            return;
        }

        var nextIdleCount = Interlocked.Increment(ref bucket.IdleCount);
        if (nextIdleCount > _maxIdleConnectionsPerKey)
        {
            Interlocked.Decrement(ref bucket.IdleCount);
            pooledConnection.Connection.Dispose();
            return;
        }

        bucket.IdleConnections.Add(pooledConnection with { LastReturnedAt = DateTimeOffset.UtcNow });
    }

    private static bool IsExpired(PooledConnection pooledConnection, ActiveDirectoryConfig config)
    {
        var maxIdle = TimeSpan.FromSeconds(Math.Max(1, config.ConnectionPoolMaxIdleSeconds));
        return DateTimeOffset.UtcNow - pooledConnection.LastReturnedAt >= maxIdle;
    }

    public sealed class ActiveDirectoryConnectionLease : IDisposable
    {
        private readonly ActiveDirectoryConnectionPool _owner;
        private readonly PoolKey _key;
        private readonly PoolBucket _bucket;
        private PooledConnection? _pooledConnection;
        private bool _isReusable = true;

        internal ActiveDirectoryConnectionLease(
            ActiveDirectoryConnectionPool owner,
            PoolKey key,
            PoolBucket bucket,
            PooledConnection pooledConnection,
            bool wasReused)
        {
            _owner = owner;
            _key = key;
            _bucket = bucket;
            _pooledConnection = pooledConnection;
            WasReused = wasReused;
        }

        public LdapConnection Connection =>
            _pooledConnection?.Connection ?? throw new ObjectDisposedException(nameof(ActiveDirectoryConnectionLease));

        public string RequestedTransport =>
            _pooledConnection?.RequestedTransport ?? throw new ObjectDisposedException(nameof(ActiveDirectoryConnectionLease));

        public string EffectiveTransport =>
            _pooledConnection?.EffectiveTransport ?? throw new ObjectDisposedException(nameof(ActiveDirectoryConnectionLease));

        public bool UsedFallback =>
            _pooledConnection?.UsedFallback ?? throw new ObjectDisposedException(nameof(ActiveDirectoryConnectionLease));

        public bool WasReused { get; }

        public void Invalidate()
        {
            _isReusable = false;
        }

        public void Dispose()
        {
            var pooledConnection = Interlocked.Exchange(ref _pooledConnection, null);
            if (pooledConnection is null)
            {
                return;
            }

            _owner.Return(_key, _bucket, pooledConnection, _isReusable);
        }
    }

    internal sealed class PoolBucket
    {
        public ConcurrentBag<PooledConnection> IdleConnections { get; } = new();
        public int IdleCount;
    }

    internal sealed record PooledConnection(
        LdapConnection Connection,
        string RequestedTransport,
        string EffectiveTransport,
        bool UsedFallback,
        DateTimeOffset LastReturnedAt)
    {
        public static PooledConnection From(ActiveDirectoryConnectionResult result)
        {
            return new PooledConnection(
                result.Connection,
                result.RequestedTransport,
                result.EffectiveTransport,
                result.UsedFallback,
                DateTimeOffset.UtcNow);
        }
    }

    internal readonly record struct PoolKey(
        string Server,
        int? Port,
        string Username,
        string BindPassword,
        string TransportMode,
        int OperationTimeoutSeconds,
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
                config.OperationTimeoutSeconds,
                config.Transport.AllowLdapFallback,
                config.Transport.RequireCertificateValidation,
                config.Transport.RequireSigning,
                string.Join(";", config.Transport.TrustedCertificateThumbprints));
        }
    }
}
