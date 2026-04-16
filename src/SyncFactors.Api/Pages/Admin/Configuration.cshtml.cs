using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SyncFactors.Api.Pages.Admin;

[Authorize(Roles = "Admin,BreakGlassAdmin")]
public sealed class ConfigurationModel(AdminConfigurationSnapshotBuilder snapshotBuilder) : PageModel
{
    internal AdminConfigurationPageSnapshot Snapshot { get; private set; } = new([]);

    public void OnGet()
    {
        Snapshot = snapshotBuilder.Build();
    }

    public string GetSourceBadgeClass(string sourceLabel)
    {
        if (string.Equals(sourceLabel, "Default", StringComparison.OrdinalIgnoreCase))
        {
            return "dim";
        }

        if (string.Equals(sourceLabel, "Environment/AppSettings", StringComparison.OrdinalIgnoreCase))
        {
            return "good";
        }

        if (string.Equals(sourceLabel, "Mapping config", StringComparison.OrdinalIgnoreCase))
        {
            return "info";
        }

        if (sourceLabel.Contains('_', StringComparison.Ordinal))
        {
            return "info";
        }

        return "neutral";
    }
}
