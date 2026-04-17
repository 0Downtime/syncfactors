using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.FileProviders;
using SyncFactors.Api.Pages;
using SyncFactors.Contracts;
using SyncFactors.Domain;

namespace SyncFactors.Api.Tests;

public sealed class IndexModelTests
{
    [Fact]
    public async Task OnGetAsync_LoadsEffectiveDashboardHealthProbeState()
    {
        var settingsStore = new StubDashboardSettingsStore(enabledOverride: true);
        var model = CreateModel(settingsStore, environmentName: "Development");

        await model.OnGetAsync(CancellationToken.None);

        Assert.True(model.HealthProbesEnabled);
        Assert.False(model.DefaultHealthProbesEnabled);
        Assert.True(model.HealthProbesUseOverride);
        Assert.Equal(60, model.HealthProbeIntervalSeconds);
        Assert.Equal(45, model.DefaultHealthProbeIntervalSeconds);
        Assert.True(model.IsDevelopment);
    }

    [Fact]
    public async Task OnPostSetHealthProbesAsync_UpdatesOverrideForAdminInDevelopment()
    {
        var settingsStore = new StubDashboardSettingsStore(enabledOverride: null);
        var model = CreateModel(settingsStore, environmentName: "Development");
        AttachUser(model, "admin@example.com", "Admin");

        var result = await model.OnPostSetHealthProbesAsync(true, CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.True(settingsStore.LastSavedValue);
        Assert.Equal("Dashboard health probes enabled.", model.SuccessMessage);
    }

    [Fact]
    public async Task OnPostSetHealthProbeFrequencyAsync_UpdatesOverrideForAdminInDevelopment()
    {
        var settingsStore = new StubDashboardSettingsStore(enabledOverride: null, intervalSecondsOverride: null);
        var model = CreateModel(settingsStore, environmentName: "Development");
        AttachUser(model, "admin@example.com", "Admin");

        var result = await model.OnPostSetHealthProbeFrequencyAsync(90, CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal(false, settingsStore.LastSavedEnabledValue);
        Assert.Equal(90, settingsStore.LastSavedIntervalSeconds);
        Assert.Equal("Dashboard health probe frequency set to every 90 seconds.", model.SuccessMessage);
    }

    [Fact]
    public async Task OnPostSetHealthProbesAsync_ForbidsNonAdmins()
    {
        var settingsStore = new StubDashboardSettingsStore(enabledOverride: null);
        var model = CreateModel(settingsStore, environmentName: "Development");
        AttachUser(model, "viewer@example.com", "Viewer");

        var result = await model.OnPostSetHealthProbesAsync(true, CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        Assert.Null(settingsStore.LastSavedValue);
    }

    private static IndexModel CreateModel(StubDashboardSettingsStore settingsStore, string environmentName)
    {
        return new IndexModel(
            new StubDashboardSnapshotService(),
            new DashboardSettingsProvider(
                new DashboardOptions(DefaultHealthProbesEnabled: false, DefaultHealthProbeIntervalSeconds: 45),
                settingsStore),
            new StubSyncScheduleStore(),
            new StubWebHostEnvironment(environmentName));
    }

    private static void AttachUser(PageModel model, string username, string role)
    {
        model.PageContext = new PageContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Name, username),
                    new Claim(ClaimTypes.Role, role)
                ], "TestAuth"))
            }
        };
    }

    private sealed class StubDashboardSnapshotService : IDashboardSnapshotService
    {
        public Task<DashboardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(
                new DashboardSnapshot(
                    Status: new RuntimeStatus("Idle", "NotStarted", null, null, true, 0, 0, null, null, null, null, null, null),
                    Runs: [],
                    ActiveRun: null,
                    LastCompletedRun: null,
                    RequiresAttention: false,
                    AttentionMessage: null,
                    CheckedAt: DateTimeOffset.Parse("2026-04-17T12:00:00Z")));
        }
    }

    private sealed class StubSyncScheduleStore : ISyncScheduleStore
    {
        public Task<SyncScheduleStatus> GetCurrentAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(new SyncScheduleStatus(false, 30, null, null, null, null));
        }

        public Task<SyncScheduleStatus> UpdateAsync(UpdateSyncScheduleRequest request, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(new SyncScheduleStatus(request.Enabled, request.IntervalMinutes, null, null, null, null));
        }

        public Task<SyncScheduleStatus> RecordSuccessfulEnqueueAsync(DateTimeOffset enqueuedAt, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(new SyncScheduleStatus(false, 30, null, enqueuedAt, enqueuedAt, null));
        }

        public Task<SyncScheduleStatus> RecordFailedEnqueueAsync(DateTimeOffset attemptedAt, string errorMessage, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(new SyncScheduleStatus(false, 30, null, null, attemptedAt, errorMessage));
        }
    }

    private sealed class StubDashboardSettingsStore(bool? enabledOverride, int? intervalSecondsOverride = 60) : IDashboardSettingsStore
    {
        public bool? LastSavedValue => LastSavedEnabledValue;

        public bool? LastSavedEnabledValue { get; private set; }

        public int? LastSavedIntervalSeconds { get; private set; }

        public Task<bool?> GetHealthProbesEnabledOverrideAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(enabledOverride);
        }

        public Task<int?> GetHealthProbeIntervalSecondsOverrideAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(intervalSecondsOverride);
        }

        public Task SaveHealthProbeOverrideAsync(bool enabled, int intervalSeconds, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            LastSavedEnabledValue = enabled;
            LastSavedIntervalSeconds = intervalSeconds;
            return Task.CompletedTask;
        }
    }

    private sealed class StubWebHostEnvironment(string environmentName) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "SyncFactors.Api.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = null!;
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = environmentName;
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
