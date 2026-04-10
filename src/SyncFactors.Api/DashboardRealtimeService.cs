using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Api;

public sealed class DashboardRealtimeService(
    IServiceScopeFactory scopeFactory,
    IHubContext<DashboardHub, IDashboardRealtimeClient> hubContext,
    DashboardRealtimeConnectionTracker connectionTracker,
    TimeProvider timeProvider,
    ILogger<DashboardRealtimeService> logger) : BackgroundService
{
    private static readonly TimeSpan DashboardInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(45);
    private static readonly JsonSerializerOptions SignatureSerializerOptions = new(JsonSerializerDefaults.Web);

    private string? _lastDashboardSignature;
    private string? _lastHealthSignature;
    private DateTimeOffset _nextHealthProbeAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _nextHealthProbeAt = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(DashboardInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (!connectionTracker.HasConnections)
                {
                    ResetSignatures();
                    continue;
                }

                try
                {
                    await PublishDashboardSnapshotIfChangedAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _lastDashboardSignature = null;
                    logger.LogWarning(ex, "Dashboard snapshot publish failed.");
                }

                var now = timeProvider.GetUtcNow();
                if (now >= _nextHealthProbeAt)
                {
                    try
                    {
                        await PublishHealthSnapshotIfChangedAsync(stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _lastHealthSignature = null;
                        logger.LogWarning(ex, "Health snapshot publish failed.");
                    }

                    _nextHealthProbeAt = now.Add(HealthInterval);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogDebug("Dashboard realtime publisher stopped.");
        }
    }

    private async Task PublishDashboardSnapshotIfChangedAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dashboardSnapshotService = scope.ServiceProvider.GetRequiredService<IDashboardSnapshotService>();
        var snapshot = await dashboardSnapshotService.GetSnapshotAsync(cancellationToken);
        var signature = JsonSerializer.Serialize(
            new DashboardSnapshotSignature(
                snapshot.Status,
                snapshot.Runs,
                snapshot.ActiveRun,
                snapshot.LastCompletedRun,
                snapshot.RequiresAttention,
                snapshot.AttentionMessage),
            SignatureSerializerOptions);

        if (string.Equals(signature, _lastDashboardSignature, StringComparison.Ordinal))
        {
            return;
        }

        _lastDashboardSignature = signature;
        await hubContext.Clients.All.DashboardEvent(
            new DashboardRealtimeEvent(
                DashboardRealtimeEventTypes.DashboardSnapshotUpdated,
                snapshot.CheckedAt,
                DashboardSnapshot: snapshot));
    }

    private async Task PublishHealthSnapshotIfChangedAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dependencyHealthService = scope.ServiceProvider.GetRequiredService<IDependencyHealthService>();
        var snapshot = await dependencyHealthService.GetSnapshotAsync(cancellationToken);
        var signature = JsonSerializer.Serialize(
            new DependencyHealthSnapshotSignature(
                snapshot.Status,
                snapshot.Probes.Select(probe => new DependencyProbeSignature(
                    probe.Dependency,
                    probe.Status,
                    probe.Summary,
                    probe.Details,
                    probe.ObservedAt,
                    probe.IsStale)).ToArray()),
            SignatureSerializerOptions);

        if (string.Equals(signature, _lastHealthSignature, StringComparison.Ordinal))
        {
            return;
        }

        _lastHealthSignature = signature;
        await hubContext.Clients.All.DashboardEvent(
            new DashboardRealtimeEvent(
                DashboardRealtimeEventTypes.HealthSnapshotUpdated,
                snapshot.CheckedAt,
                HealthSnapshot: snapshot));
    }

    private void ResetSignatures()
    {
        _lastDashboardSignature = null;
        _lastHealthSignature = null;
        _nextHealthProbeAt = DateTimeOffset.MinValue;
    }

    private sealed record DashboardSnapshotSignature(
        RuntimeStatus Status,
        IReadOnlyList<RunSummary> Runs,
        RunSummary? ActiveRun,
        RunSummary? LastCompletedRun,
        bool RequiresAttention,
        string? AttentionMessage);

    private sealed record DependencyHealthSnapshotSignature(
        string Status,
        IReadOnlyList<DependencyProbeSignature> Probes);

    private sealed record DependencyProbeSignature(
        string Dependency,
        string Status,
        string Summary,
        string? Details,
        DateTimeOffset? ObservedAt,
        bool IsStale);
}
