using SyncFactors.Contracts;
using SyncFactors.Domain;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SyncFactors.Infrastructure;

public sealed class SuccessFactorsWorkerSource(
    HttpClient httpClient,
    SyncFactorsConfigurationLoader configLoader,
    ScaffoldWorkerSource fallbackSource,
    ILogger<SuccessFactorsWorkerSource> logger) : IWorkerSource
{
    public async Task<WorkerSnapshot?> GetWorkerAsync(string workerId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workerId))
        {
            return null;
        }

        var config = configLoader.GetSyncConfig();
        var query = config.SuccessFactors.PreviewQuery ?? config.SuccessFactors.Query;
        var requestUri = BuildRequestUri(config, query, workerId);

        logger.LogInformation(
            "Fetching worker preview data from SuccessFactors. WorkerId={WorkerId} EntitySet={EntitySet} AuthMode={AuthMode}",
            workerId,
            query.EntitySet,
            config.SuccessFactors.Auth.Mode);

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
        AddTracingHeaders(request, workerId);
        await ApplyAuthenticationAsync(request, config.SuccessFactors.Auth, cancellationToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "SuccessFactors request failed. WorkerId={WorkerId} StatusCode={StatusCode} ContentType={ContentType} Uri={Uri} BodyPreview={BodyPreview}",
                workerId,
                (int)response.StatusCode,
                response.Content.Headers.ContentType?.MediaType ?? "(none)",
                requestUri,
                TrimForLog(body));
            response.EnsureSuccessStatusCode();
        }

        var rawBody = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(rawBody);

            var worker = TryParseWorker(document.RootElement, config, query, workerId);
            if (worker is not null)
            {
                logger.LogInformation(
                    "Resolved worker from SuccessFactors. RequestedWorkerId={RequestedWorkerId} ResolvedWorkerId={ResolvedWorkerId}",
                    workerId,
                    worker.WorkerId);
                return worker;
            }
        }
        catch (JsonException ex)
        {
            logger.LogError(
                ex,
                "SuccessFactors returned non-JSON or invalid JSON. WorkerId={WorkerId} ContentType={ContentType} Uri={Uri} BodyPreview={BodyPreview}",
                workerId,
                response.Content.Headers.ContentType?.MediaType ?? "(none)",
                requestUri,
                TrimForLog(rawBody));
            throw new InvalidOperationException(
                $"SuccessFactors returned invalid JSON. Status={(int)response.StatusCode}, ContentType={response.Content.Headers.ContentType?.MediaType ?? "(none)"}, BodyPreview={TrimForLog(rawBody)}",
                ex);
        }

        logger.LogWarning(
            "No worker was returned from SuccessFactors. Falling back to scaffold worker source. WorkerId={WorkerId}",
            workerId);

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
                logger.LogDebug("Using basic authentication for SuccessFactors request.");
                break;

            case "oauth" when auth.OAuth is not null:
                var accessToken = await GetOAuthTokenAsync(auth.OAuth, cancellationToken);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                logger.LogDebug("Using OAuth bearer authentication for SuccessFactors request.");
                break;
        }
    }

    private async Task<string> GetOAuthTokenAsync(SuccessFactorsOAuthConfig oauth, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, oauth.TokenUrl);
        request.Content = new FormUrlEncodedContent(BuildTokenForm(oauth));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
        logger.LogDebug("Requesting SuccessFactors OAuth token from {TokenUrl}", oauth.TokenUrl);

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

    private static string TrimForLog(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(empty)";
        }

        var flattened = value.ReplaceLineEndings(" ").Trim();
        return flattened.Length <= 240 ? flattened : flattened[..240];
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
            "$format=json",
            $"$filter={Uri.EscapeDataString($"{query.IdentityField} eq '{workerId.Replace("'", "''")}'")}",
            $"$select={Uri.EscapeDataString(string.Join(",", query.Select))}",
        };

        if (query.Expand.Count > 0)
        {
            parts.Add($"$expand={Uri.EscapeDataString(string.Join(",", query.Expand))}");
        }

        return $"{relativePath}?{string.Join("&", parts)}";
    }

    private static WorkerSnapshot? TryParseWorker(JsonElement root, SyncFactorsConfigDocument config, SuccessFactorsQueryConfig query, string workerId)
    {
        var worker = ExtractWorkerArray(root).FirstOrDefault();
        if (worker.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        var personalInfo = GetFirstNavigationResult(worker, "personalInfoNav");
        var employment = GetFirstNavigationResult(worker, "employmentNav");
        var jobInfo = employment is { ValueKind: not JsonValueKind.Undefined }
            ? GetFirstNavigationResult(employment.Value, "jobInfoNav")
            : null;

        var preferredName =
            GetString(personalInfo, "firstName") ??
            GetString(worker, "firstName") ??
            "Unknown";
        var lastName =
            GetString(personalInfo, "lastName") ??
            GetString(worker, "lastName") ??
            "Worker";
        var department =
            GetString(jobInfo, "department") ??
            GetString(employment, "department") ??
            GetString(worker, "department") ??
            "Unknown";
        var startDate =
            GetString(employment, "startDate") ??
            GetString(worker, "startDate");

        return new WorkerSnapshot(
            WorkerId: GetString(worker, query.IdentityField) ?? workerId,
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

    private static string? GetString(JsonElement? element, string propertyName)
    {
        return element is { ValueKind: not JsonValueKind.Undefined }
            ? GetString(element.Value, propertyName)
            : null;
    }

    private static JsonElement? GetFirstNavigationResult(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var navigation))
        {
            return null;
        }

        if (navigation.ValueKind == JsonValueKind.Object &&
            navigation.TryGetProperty("results", out var results) &&
            results.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in results.EnumerateArray())
            {
                return item.Clone();
            }
        }

        return null;
    }

    private static bool IsPrehire(string? startDate, int enableBeforeStartDays)
    {
        if (!DateTimeOffset.TryParse(startDate, out var parsedStart))
        {
            return false;
        }

        return parsedStart > DateTimeOffset.UtcNow.AddDays(enableBeforeStartDays);
    }

    private static void AddTracingHeaders(HttpRequestMessage request, string workerId)
    {
        var correlationId = Guid.NewGuid().ToString("D");
        request.Headers.TryAddWithoutValidation("x-correlation-id", correlationId);
        request.Headers.TryAddWithoutValidation("X-SF-Correlation-Id", correlationId);
        request.Headers.TryAddWithoutValidation("X-SF-Process-Name", "SyncFactors.Next.WorkerPreview");
        request.Headers.TryAddWithoutValidation("X-SF-Execution-Id", workerId);
    }
}
