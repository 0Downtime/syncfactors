using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncFactors.Api.Pages;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Api.Tests;

public sealed class IndexModelTests
{
    [Fact]
    public async Task OnPostDryRunAsync_LaunchesDryRunAndRedirects()
    {
        var model = new IndexModel(
            new StubDashboardSnapshotService(),
            new StubFullSyncRunService(new RunLaunchResult("full-sync-1", "Succeeded", true, "Dry run complete.")));

        var result = await model.OnPostDryRunAsync(CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("full-sync-1", model.LaunchRunId);
        Assert.Equal("Dry run complete.", model.LaunchMessage);
        Assert.False(model.LaunchFailed);
    }

    [Fact]
    public async Task OnPostLiveRunAsync_PropagatesAcknowledgement()
    {
        var service = new CapturingFullSyncRunService();
        var model = new IndexModel(new StubDashboardSnapshotService(), service)
        {
            AcknowledgeRealSync = true
        };

        var result = await model.OnPostLiveRunAsync(CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.NotNull(service.LastRequest);
        Assert.False(service.LastRequest!.DryRun);
        Assert.True(service.LastRequest.AcknowledgeRealSync);
    }

    [Fact]
    public async Task OnPostLiveRunAsync_OnFailureReloadsSnapshot()
    {
        var model = new IndexModel(
            new StubDashboardSnapshotService(),
            new ThrowingFullSyncRunService(new InvalidOperationException("Another sync run is already in progress.")));

        var result = await model.OnPostLiveRunAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.True(model.LaunchFailed);
        Assert.Equal("Another sync run is already in progress.", model.LaunchMessage);
        Assert.Equal("Idle", model.Status.Status);
    }

    private sealed class StubDashboardSnapshotService : IDashboardSnapshotService
    {
        public Task<DashboardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(new DashboardSnapshot(
                Status: new RuntimeStatus("Idle", "Completed", null, null, true, 0, 0, null, null, null, null, null, null),
                Runs: [],
                ActiveRun: null,
                LastCompletedRun: null,
                RequiresAttention: false,
                AttentionMessage: null,
                CheckedAt: DateTimeOffset.UtcNow));
        }
    }

    private sealed class StubFullSyncRunService(RunLaunchResult result) : IFullSyncRunService
    {
        public Task<RunLaunchResult> LaunchAsync(LaunchFullRunRequest request, CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            return Task.FromResult(result);
        }
    }

    private sealed class CapturingFullSyncRunService : IFullSyncRunService
    {
        public LaunchFullRunRequest? LastRequest { get; private set; }

        public Task<RunLaunchResult> LaunchAsync(LaunchFullRunRequest request, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            LastRequest = request;
            return Task.FromResult(new RunLaunchResult("full-sync-2", "Succeeded", false, "Live sync complete."));
        }
    }

    private sealed class ThrowingFullSyncRunService(Exception exception) : IFullSyncRunService
    {
        public Task<RunLaunchResult> LaunchAsync(LaunchFullRunRequest request, CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            return Task.FromException<RunLaunchResult>(exception);
        }
    }
}
