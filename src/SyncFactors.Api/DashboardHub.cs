using Microsoft.AspNetCore.SignalR;

namespace SyncFactors.Api;

public sealed class DashboardHub(
    DashboardRealtimeConnectionTracker connectionTracker,
    ILogger<DashboardHub> logger) : Hub<IDashboardRealtimeClient>
{
    public override async Task OnConnectedAsync()
    {
        connectionTracker.Increment();
        logger.LogDebug("Dashboard hub connected: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        connectionTracker.Decrement();
        logger.LogDebug("Dashboard hub disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
