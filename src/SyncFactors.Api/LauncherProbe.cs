using SyncFactors.Infrastructure;

namespace SyncFactors.Api;

public static class LauncherProbe
{
    public const string BootstrapRequiredAction = "bootstrap-required";

    public static string? GetRequestedAction(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, "--launcher-probe", StringComparison.Ordinal))
            {
                if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
                {
                    throw new InvalidOperationException("Missing launcher probe name after '--launcher-probe'.");
                }

                return args[index + 1].Trim();
            }

            const string prefix = "--launcher-probe=";
            if (argument.StartsWith(prefix, StringComparison.Ordinal))
            {
                var action = argument[prefix.Length..].Trim();
                if (string.IsNullOrWhiteSpace(action))
                {
                    throw new InvalidOperationException("Missing launcher probe name after '--launcher-probe='.");
                }

                return action;
            }
        }

        return null;
    }

    public static async Task<bool> IsBootstrapRequiredAsync(LocalAuthOptions options, ILocalUserStore userStore, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(userStore);

        var localAuthEnabled =
            string.Equals(options.Mode, "local-break-glass", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(options.Mode, "hybrid", StringComparison.OrdinalIgnoreCase) ||
            options.LocalBreakGlass.Enabled;

        if (!localAuthEnabled || string.IsNullOrWhiteSpace(options.BootstrapAdmin.Username))
        {
            return false;
        }

        return !await userStore.AnyUsersAsync(cancellationToken);
    }
}
