using Microsoft.Extensions.Logging;
using SyncFactors.Domain;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SyncFactors.Infrastructure;

public sealed class SuccessFactorsUserLookupService(
    HttpClient httpClient,
    SyncFactorsConfigurationLoader configLoader,
    ILogger<SuccessFactorsUserLookupService> logger)
{
    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly IReadOnlyList<string> PerPersonExpands =
    [
        "personalInfoNav",
        "emailNav",
        "phoneNav",
        "employmentNav",
        "employmentNav/jobInfoNav",
        "employmentNav/jobInfoNav/companyNav",
        "employmentNav/jobInfoNav/departmentNav",
        "employmentNav/jobInfoNav/businessUnitNav",
        "employmentNav/jobInfoNav/divisionNav",
        "employmentNav/jobInfoNav/costCenterNav",
        "employmentNav/jobInfoNav/locationNav",
        "employmentNav/jobInfoNav/locationNav/addressNavDEFLT",
        "employmentNav/userNav",
        "employmentNav/userNav/manager",
        "employmentNav/userNav/manager/empInfo",
        "personEmpTerminationInfoNav"
    ];

    private static readonly IReadOnlyList<string> EmpJobExpands =
    [
        "companyNav",
        "departmentNav",
        "businessUnitNav",
        "divisionNav",
        "costCenterNav",
        "locationNav",
        "locationNav/addressNavDEFLT",
        "payGradeNav"
    ];

    private static readonly IReadOnlyList<string> UserExpands =
    [
        "empInfo",
        "manager",
        "manager/empInfo"
    ];

    public async Task<SuccessFactorsUserLookupResult> LookupAsync(string lookupValue, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(lookupValue))
        {
            throw new ArgumentException("Lookup value is required.", nameof(lookupValue));
        }

        var trimmedLookupValue = lookupValue.Trim();
        var config = configLoader.GetSyncConfig();
        var requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<SuccessFactorsLookupEntityResult>();

        await QueryOnceAsync("PerPerson", $"personIdExternal eq {ODataStringLiteral(trimmedLookupValue)}", PerPersonExpands);
        await QueryOnceAsync("EmpJob", $"userId eq {ODataStringLiteral(trimmedLookupValue)}", EmpJobExpands);
        await QueryOnceAsync("User", $"userId eq {ODataStringLiteral(trimmedLookupValue)}", UserExpands);

        var discoveredPersonIds = results
            .SelectMany(result => result.PersonIdExternalValues)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();
        foreach (var discoveredPersonId in discoveredPersonIds)
        {
            await QueryOnceAsync("PerPerson", $"personIdExternal eq {ODataStringLiteral(discoveredPersonId)}", PerPersonExpands);
        }

        var discoveredUserIds = results
            .SelectMany(result => result.UserIdValues)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();
        foreach (var discoveredUserId in discoveredUserIds)
        {
            await QueryOnceAsync("EmpJob", $"userId eq {ODataStringLiteral(discoveredUserId)}", EmpJobExpands);
            await QueryOnceAsync("User", $"userId eq {ODataStringLiteral(discoveredUserId)}", UserExpands);
        }

        var attributes = results
            .Where(result => result.IsSuccess)
            .SelectMany(result => result.Attributes)
            .DistinctBy(attribute => $"{attribute.EntitySet}\u001f{attribute.Path}\u001f{attribute.Value}", StringComparer.OrdinalIgnoreCase)
            .OrderBy(attribute => attribute.EntitySet, StringComparer.OrdinalIgnoreCase)
            .ThenBy(attribute => attribute.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new SuccessFactorsUserLookupResult(
            LookupValue: trimmedLookupValue,
            RetrievedAt: DateTimeOffset.UtcNow,
            EntityResults: results,
            Attributes: attributes);

        async Task QueryOnceAsync(string entitySet, string filter, IReadOnlyList<string> expands)
        {
            var requestKey = $"{entitySet}|{filter}";
            if (!requested.Add(requestKey))
            {
                return;
            }

            results.Add(await ExecuteEntityLookupAsync(config, entitySet, filter, expands, cancellationToken));
        }
    }

    private async Task<SuccessFactorsLookupEntityResult> ExecuteEntityLookupAsync(
        SyncFactorsConfigDocument config,
        string entitySet,
        string filter,
        IReadOnlyList<string> expands,
        CancellationToken cancellationToken)
    {
        var expandedResult = await ExecuteEntityRequestAsync(config, entitySet, filter, expands, cancellationToken);
        if (expandedResult.IsSuccess || expands.Count == 0)
        {
            return expandedResult;
        }

        if (expandedResult.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
        {
            var unexpandedResult = await ExecuteEntityRequestAsync(config, entitySet, filter, [], cancellationToken);
            if (unexpandedResult.IsSuccess)
            {
                return unexpandedResult with
                {
                    Error = $"Expanded lookup failed with HTTP {(int?)expandedResult.StatusCode}; retried without $expand."
                };
            }
        }

        return expandedResult;
    }

    private async Task<SuccessFactorsLookupEntityResult> ExecuteEntityRequestAsync(
        SyncFactorsConfigDocument config,
        string entitySet,
        string filter,
        IReadOnlyList<string> expands,
        CancellationToken cancellationToken)
    {
        var requestUri = BuildRequestUri(config.SuccessFactors.BaseUrl, entitySet, filter, expands);

        logger.LogInformation(
            "Looking up SuccessFactors OData user data. EntitySet={EntitySet} Expanded={Expanded}",
            entitySet,
            expands.Count > 0);

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
        await ApplyAuthenticationAsync(request, config.SuccessFactors.Auth, cancellationToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new SuccessFactorsLookupEntityResult(
                EntitySet: entitySet,
                Filter: filter,
                RequestUri: requestUri,
                StatusCode: response.StatusCode,
                IsSuccess: false,
                ItemCount: 0,
                Json: null,
                Error: BuildFailureSummary(response, body),
                Attributes: [],
                PersonIdExternalValues: [],
                UserIdValues: []);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var attributes = FlattenAttributes(entitySet, root).ToArray();
            var personIds = FindStringProperties(root, "personIdExternal").ToArray();
            var userIds = FindStringProperties(root, "userId").ToArray();

            return new SuccessFactorsLookupEntityResult(
                EntitySet: entitySet,
                Filter: filter,
                RequestUri: requestUri,
                StatusCode: response.StatusCode,
                IsSuccess: true,
                ItemCount: CountItems(root),
                Json: JsonSerializer.Serialize(root, PrettyJsonOptions),
                Error: null,
                Attributes: attributes,
                PersonIdExternalValues: personIds,
                UserIdValues: userIds);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "SuccessFactors OData lookup returned invalid JSON. EntitySet={EntitySet}", entitySet);
            return new SuccessFactorsLookupEntityResult(
                EntitySet: entitySet,
                Filter: filter,
                RequestUri: requestUri,
                StatusCode: response.StatusCode,
                IsSuccess: false,
                ItemCount: 0,
                Json: null,
                Error: $"The API returned invalid JSON. ContentType={response.Content.Headers.ContentType?.MediaType ?? "(none)"}.",
                Attributes: [],
                PersonIdExternalValues: [],
                UserIdValues: []);
        }
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
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(BuildFailureSummary(response, body));
        }

        using var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("access_token", out var accessToken) &&
            accessToken.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(accessToken.GetString()))
        {
            return accessToken.GetString()!;
        }

        throw new InvalidOperationException("The SuccessFactors OAuth response did not contain an access_token.");
    }

    private static IEnumerable<KeyValuePair<string, string>> BuildTokenForm(SuccessFactorsOAuthConfig oauth)
    {
        yield return new KeyValuePair<string, string>("grant_type", "client_credentials");
        yield return new KeyValuePair<string, string>("client_id", oauth.ClientId);
        yield return new KeyValuePair<string, string>("client_secret", oauth.ClientSecret);

        if (!string.IsNullOrWhiteSpace(oauth.CompanyId))
        {
            yield return new KeyValuePair<string, string>("company_id", oauth.CompanyId);
        }
    }

    private static string BuildRequestUri(string baseUrl, string entitySet, string filter, IReadOnlyList<string> expands)
    {
        var parts = new List<string>
        {
            "$format=json",
            "$top=10",
            $"$filter={Uri.EscapeDataString(filter)}"
        };

        if (expands.Count > 0)
        {
            parts.Add($"$expand={Uri.EscapeDataString(string.Join(",", expands))}");
        }

        return $"{baseUrl.TrimEnd('/')}/{entitySet}?{string.Join("&", parts)}";
    }

    private static string ODataStringLiteral(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string BuildFailureSummary(HttpResponseMessage response, string? body)
    {
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "(none)";
        if (string.IsNullOrWhiteSpace(body))
        {
            return $"SuccessFactors request failed. Status={(int)response.StatusCode}, ContentType={contentType}.";
        }

        return $"SuccessFactors request failed. Status={(int)response.StatusCode}, ContentType={contentType}, BodyPreview={LogSafety.SingleLine(body)}";
    }

    private static int CountItems(JsonElement root)
    {
        if (TryGetResultsArray(root, out var results))
        {
            return results.GetArrayLength();
        }

        if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            return value.GetArrayLength();
        }

        return root.ValueKind == JsonValueKind.Object ? 1 : 0;
    }

    private static bool TryGetResultsArray(JsonElement root, out JsonElement results)
    {
        if (root.TryGetProperty("d", out var d) &&
            d.ValueKind == JsonValueKind.Object &&
            d.TryGetProperty("results", out results) &&
            results.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        results = default;
        return false;
    }

    private static IEnumerable<SuccessFactorsLookupAttribute> FlattenAttributes(string entitySet, JsonElement root)
    {
        if (TryGetResultsArray(root, out var results))
        {
            var index = 0;
            foreach (var item in results.EnumerateArray())
            {
                foreach (var attribute in FlattenElement(entitySet, $"[{index}]", item))
                {
                    yield return attribute;
                }

                index++;
            }

            yield break;
        }

        if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                foreach (var attribute in FlattenElement(entitySet, $"[{index}]", item))
                {
                    yield return attribute;
                }

                index++;
            }

            yield break;
        }

        foreach (var attribute in FlattenElement(entitySet, string.Empty, root))
        {
            yield return attribute;
        }
    }

    private static IEnumerable<SuccessFactorsLookupAttribute> FlattenElement(string entitySet, string path, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, "__metadata", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var propertyPath = string.IsNullOrWhiteSpace(path)
                        ? property.Name
                        : string.Equals(property.Name, "results", StringComparison.OrdinalIgnoreCase)
                            ? path
                            : $"{path}.{property.Name}";

                    foreach (var attribute in FlattenElement(entitySet, propertyPath, property.Value))
                    {
                        yield return attribute;
                    }
                }

                yield break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    var itemPath = $"{path}[{index}]";
                    foreach (var attribute in FlattenElement(entitySet, itemPath, item))
                    {
                        yield return attribute;
                    }

                    index++;
                }

                yield break;

            case JsonValueKind.String:
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                yield return new SuccessFactorsLookupAttribute(
                    EntitySet: entitySet,
                    Path: path,
                    Value: GetDisplayValue(element));
                yield break;
        }
    }

    private static string GetDisplayValue(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Null => "null",
            _ => element.GetRawText()
        };

    private static IEnumerable<string> FindStringProperties(JsonElement element, string propertyName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                        property.Value.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(property.Value.GetString()))
                    {
                        yield return property.Value.GetString()!;
                    }

                    foreach (var nestedValue in FindStringProperties(property.Value, propertyName))
                    {
                        yield return nestedValue;
                    }
                }

                yield break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nestedValue in FindStringProperties(item, propertyName))
                    {
                        yield return nestedValue;
                    }
                }

                yield break;
        }
    }
}

public sealed record SuccessFactorsUserLookupResult(
    string LookupValue,
    DateTimeOffset RetrievedAt,
    IReadOnlyList<SuccessFactorsLookupEntityResult> EntityResults,
    IReadOnlyList<SuccessFactorsLookupAttribute> Attributes)
{
    public bool HasMatches => EntityResults.Any(result => result.IsSuccess && result.ItemCount > 0);
}

public sealed record SuccessFactorsLookupEntityResult(
    string EntitySet,
    string Filter,
    string RequestUri,
    HttpStatusCode? StatusCode,
    bool IsSuccess,
    int ItemCount,
    string? Json,
    string? Error,
    IReadOnlyList<SuccessFactorsLookupAttribute> Attributes,
    IReadOnlyList<string> PersonIdExternalValues,
    IReadOnlyList<string> UserIdValues);

public sealed record SuccessFactorsLookupAttribute(
    string EntitySet,
    string Path,
    string Value);
