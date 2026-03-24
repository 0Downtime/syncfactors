using SyncFactors.Contracts;
using SyncFactors.Domain;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SyncFactors.Infrastructure;

public sealed class SuccessFactorsWorkerSource(
    HttpClient httpClient,
    SyncFactorsConfigurationLoader configLoader,
    ScaffoldWorkerSource fallbackSource) : IWorkerSource
{
    public async Task<WorkerSnapshot?> GetWorkerAsync(string workerId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workerId))
        {
            return null;
        }

        var config = configLoader.GetSyncConfig();
        var query = config.SuccessFactors.PreviewQuery ?? config.SuccessFactors.Query;

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildRequestUri(config, query, workerId));
        await ApplyAuthenticationAsync(request, config.SuccessFactors.Auth, cancellationToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            response.EnsureSuccessStatusCode();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var worker = TryParseWorker(document.RootElement, config, workerId);
        if (worker is not null)
        {
            return worker;
        }

        return await fallbackSource.GetWorkerAsync(workerId, cancellationToken);
    }

    private async Task ApplyAuthenticationAsync(
        HttpRequestMessage request,
        SuccessFactorsAuthConfig auth,
        CancellationToken cancellationToken)
    {
        switch (auth.Mode.ToLowerInvariant())
        {
            case "basic" when auth.Basic is not null:
                var basicToken = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{auth.Basic.Username}:{auth.Basic.Password}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicToken);
                break;

            case "oauth" when auth.OAuth is not null:
                var accessToken = await GetOAuthTokenAsync(auth.OAuth, cancellationToken);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                break;
        }
    }

    private async Task<string> GetOAuthTokenAsync(SuccessFactorsOAuthConfig oauth, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, oauth.TokenUrl);
        request.Content = new FormUrlEncodedContent(BuildTokenForm(oauth));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.TryGetProperty("access_token", out var accessToken) && accessToken.ValueKind == JsonValueKind.String)
        {
            return accessToken.GetString()!;
        }

        throw new InvalidOperationException("SuccessFactors OAuth response did not contain access_token.");
    }

    private static IEnumerable<KeyValuePair<string, string>> BuildTokenForm(SuccessFactorsOAuthConfig oauth)
    {
        var values = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "client_credentials"),
            new("client_id", oauth.ClientId),
            new("client_secret", oauth.ClientSecret),
        };

        if (!string.IsNullOrWhiteSpace(oauth.CompanyId))
        {
            values.Add(new KeyValuePair<string, string>("company_id", oauth.CompanyId));
        }

        return values;
    }

    private static string BuildRequestUri(SyncFactorsConfigDocument config, SuccessFactorsQueryConfig query, string workerId)
    {
        var baseUrl = config.SuccessFactors.BaseUrl.TrimEnd('/');
        var relativePath = $"{baseUrl}/{query.EntitySet}";
        var parts = new List<string>
        {
            $"$filter={Uri.EscapeDataString($"{query.IdentityField} eq '{workerId.Replace("'", "''")}'")}",
            $"$select={Uri.EscapeDataString(string.Join(",", query.Select))}",
        };

        if (query.Expand.Count > 0)
        {
            parts.Add($"$expand={Uri.EscapeDataString(string.Join(",", query.Expand))}");
        }

        return $"{relativePath}?{string.Join("&", parts)}";
    }

    private static WorkerSnapshot? TryParseWorker(JsonElement root, SyncFactorsConfigDocument config, string workerId)
    {
        var worker = ExtractWorkerArray(root).FirstOrDefault();
        if (worker.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        var preferredName = GetString(worker, "firstName") ?? "Unknown";
        var lastName = GetString(worker, "lastName") ?? "Worker";
        var department = GetString(worker, "department") ?? "Unknown";
        var startDate = GetString(worker, "startDate");

        return new WorkerSnapshot(
            WorkerId: GetString(worker, config.SuccessFactors.Query.IdentityField) ?? workerId,
            PreferredName: preferredName,
            LastName: lastName,
            Department: department,
            TargetOu: config.Ad.DefaultActiveOu,
            IsPrehire: IsPrehire(startDate, config.Sync.EnableBeforeStartDays));
    }

    private static IReadOnlyList<JsonElement> ExtractWorkerArray(JsonElement root)
    {
        if (root.TryGetProperty("d", out var d))
        {
            if (d.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                return results.EnumerateArray().Select(item => item.Clone()).ToArray();
            }
        }

        if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray().Select(item => item.Clone()).ToArray();
        }

        return [];
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool IsPrehire(string? startDate, int enableBeforeStartDays)
    {
        if (!DateTimeOffset.TryParse(startDate, out var parsedStart))
        {
            return false;
        }

        return parsedStart > DateTimeOffset.UtcNow.AddDays(enableBeforeStartDays);
    }
}
