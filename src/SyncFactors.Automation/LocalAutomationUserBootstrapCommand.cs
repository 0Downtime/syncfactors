using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using SyncFactors.Infrastructure;

namespace SyncFactors.Automation;

public static class LocalAutomationUserBootstrapCommand
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken)
    {
        try
        {
            var options = Parse(args);
            var pathResolver = new SqlitePathResolver(options.SqlitePath);
            await new SqliteDatabaseInitializer(pathResolver).InitializeAsync(cancellationToken);

            var userStore = new SqliteLocalUserStore(pathResolver);
            var authService = new LocalAuthService(
                userStore,
                new PasswordHasher<LocalUserRecord>(),
                Options.Create(new LocalAuthOptions
                {
                    Mode = "hybrid",
                    LocalBreakGlass = new LocalBreakGlassOptions { Enabled = true }
                }),
                TimeProvider.System,
                new NoopSecurityAuditService());

            var existing = await userStore.FindByUsernameAsync(options.Username, cancellationToken);
            LocalUserCommandResult result;
            if (existing is null)
            {
                result = await authService.CreateUserAsync(options.Username, options.Password, options.Admin, cancellationToken);
            }
            else
            {
                result = await authService.ResetPasswordAsync(existing.UserId, options.Password, cancellationToken);
                if (result.Succeeded && options.Admin && !string.Equals(existing.Role, SecurityRoles.Admin, StringComparison.OrdinalIgnoreCase))
                {
                    result = await authService.SetUserRoleAsync(existing.UserId, isAdmin: true, actingUserId: "automation-bootstrap", cancellationToken);
                }

                if (result.Succeeded && !existing.IsActive)
                {
                    result = await authService.SetUserActiveStateAsync(existing.UserId, isActive: true, actingUserId: "automation-bootstrap", cancellationToken);
                }
            }

            if (!result.Succeeded)
            {
                throw new InvalidOperationException(result.Message);
            }

            await output.WriteLineAsync(result.Message);
            return 0;
        }
        catch (Exception ex)
        {
            await output.WriteLineAsync($"Automation local user bootstrap failed: {ex.Message}");
            return 1;
        }
    }

    private static BootstrapOptions Parse(string[] args)
    {
        string? sqlitePath = null;
        string? username = null;
        string? password = null;
        var admin = false;

        for (var index = 0; index < args.Length; index++)
        {
            var key = args[index];
            if (string.Equals(key, "--admin", StringComparison.OrdinalIgnoreCase))
            {
                admin = true;
                continue;
            }

            if (index + 1 >= args.Length)
            {
                throw new InvalidOperationException($"Expected value after '{key}'.");
            }

            var value = args[++index];
            switch (key)
            {
                case "--sqlite":
                    sqlitePath = value;
                    break;
                case "--username":
                    username = value;
                    break;
                case "--password":
                    password = value;
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported argument '{key}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(sqlitePath))
        {
            throw new InvalidOperationException("--sqlite is required.");
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            throw new InvalidOperationException("--username is required.");
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("--password is required.");
        }

        return new BootstrapOptions(Path.GetFullPath(sqlitePath), username, password, admin);
    }

    private sealed record BootstrapOptions(string SqlitePath, string Username, string Password, bool Admin);

    private sealed class NoopSecurityAuditService : ISecurityAuditService
    {
        public void Write(string eventType, string outcome, params (string Key, object? Value)[] fields)
        {
            _ = eventType;
            _ = outcome;
            _ = fields;
        }
    }
}
