using System.Diagnostics;
using SyncFactors.Api;
using SyncFactors.Infrastructure;

namespace SyncFactors.Api.Tests;

public sealed class LauncherProbeTests
{
    [Fact]
    public async Task BootstrapRequiredProbe_InProductionWithMissingIntegrityKey_DoesNotInitializeSqlite()
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"syncfactors-launcher-probe-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(temporaryDirectory, "state", "runtime", "syncfactors.db");
        var auditPath = Path.Combine(temporaryDirectory, "state", "runtime", "security-audit.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(auditPath)!);
        await File.WriteAllTextAsync(auditPath, "tampered audit entry");

        try
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = temporaryDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(typeof(LauncherProbe).Assembly.Location);
            startInfo.ArgumentList.Add("--launcher-probe");
            startInfo.ArgumentList.Add(LauncherProbe.BootstrapRequiredAction);
            startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
            startInfo.Environment["SYNCFACTORS_SECURITY_AUDIT_LOG_PATH"] = auditPath;
            startInfo.Environment["SyncFactors__SqlitePath"] = databasePath;
            startInfo.Environment.Remove(SecurityAuditService.IntegrityKeyEnvironmentVariable);

            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            await process!.WaitForExitAsync();

            Assert.NotEqual(0, process.ExitCode);
            Assert.False(File.Exists(databasePath));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task IsBootstrapRequiredAsync_ReturnsFalseWhenLocalAuthIsDisabled()
    {
        var options = new LocalAuthOptions
        {
            Mode = "oidc",
            LocalBreakGlass = new LocalBreakGlassOptions
            {
                Enabled = false
            },
            BootstrapAdmin = new BootstrapAdminOptions
            {
                Username = "admin"
            }
        };

        var required = await LauncherProbe.IsBootstrapRequiredAsync(options, new StubLocalUserStore(hasUsers: false), CancellationToken.None);

        Assert.False(required);
    }

    [Fact]
    public async Task IsBootstrapRequiredAsync_ReturnsTrueWhenHybridModeHasNoLocalUsers()
    {
        var options = new LocalAuthOptions
        {
            Mode = "hybrid",
            BootstrapAdmin = new BootstrapAdminOptions
            {
                Username = "admin"
            }
        };

        var required = await LauncherProbe.IsBootstrapRequiredAsync(options, new StubLocalUserStore(hasUsers: false), CancellationToken.None);

        Assert.True(required);
    }

    [Fact]
    public async Task IsBootstrapRequiredAsync_ReturnsFalseWhenHybridModeAlreadyHasLocalUsers()
    {
        var options = new LocalAuthOptions
        {
            Mode = "hybrid",
            BootstrapAdmin = new BootstrapAdminOptions
            {
                Username = "admin"
            }
        };

        var required = await LauncherProbe.IsBootstrapRequiredAsync(options, new StubLocalUserStore(hasUsers: true), CancellationToken.None);

        Assert.False(required);
    }

    [Fact]
    public async Task IsBootstrapRequiredAsync_ReturnsFalseWhenBootstrapUsernameIsMissing()
    {
        var options = new LocalAuthOptions
        {
            Mode = "hybrid",
            BootstrapAdmin = new BootstrapAdminOptions
            {
                Username = ""
            }
        };

        var required = await LauncherProbe.IsBootstrapRequiredAsync(options, new StubLocalUserStore(hasUsers: false), CancellationToken.None);

        Assert.False(required);
    }

    private sealed class StubLocalUserStore(bool hasUsers) : ILocalUserStore
    {
        public Task<bool> AnyUsersAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(hasUsers);
        }

        public Task<IReadOnlyList<LocalUserSummary>> ListUsersAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<LocalUserRecord?> FindByIdAsync(string userId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<LocalUserRecord?> FindByUsernameAsync(string username, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> CountActiveAdminsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CreateAsync(LocalUserRecord user, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task UpdateAsync(LocalUserRecord user, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task UpdateLastLoginAsync(string userId, DateTimeOffset lastLoginAt, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteAsync(string userId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
