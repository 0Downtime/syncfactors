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
    private static readonly IReadOnlyDictionary<string, string> EntityNavigationAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["FOBusinessUnit"] = "businessUnitNav",
            ["FOCostCenter"] = "costCenterNav",
            ["FOCompany"] = "companyNav",
            ["FODepartment"] = "departmentNav",
            ["FODivision"] = "divisionNav",
            ["FOLocation"] = "locationNav"
        };

    public async Task<WorkerSnapshot?> GetWorkerAsync(string workerId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workerId))
        {
            return null;
        }

        var config = configLoader.GetSyncConfig();
        var query = config.SuccessFactors.PreviewQuery ?? config.SuccessFactors.Query;
        var responsePayload = await ExecuteWorkerRequestAsync(config, query, workerId, cancellationToken);
        var requestUri = responsePayload.RequestUri;
        var rawBody = responsePayload.Body;
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
                responsePayload.ContentType,
                requestUri,
                TrimForLog(rawBody));
            throw ExternalSystemExceptionFactory.CreateSuccessFactorsException(
                operation: "response parsing",
                endpoint: requestUri,
                summary: $"The API returned invalid JSON. Status={responsePayload.StatusCode}, ContentType={responsePayload.ContentType}, BodyPreview={TrimForLog(rawBody)}",
                innerException: ex);
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
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "SuccessFactors OAuth token request failed. StatusCode={StatusCode} ContentType={ContentType} TokenUrl={TokenUrl} BodyPreview={BodyPreview}",
                (int)response.StatusCode,
                response.Content.Headers.ContentType?.MediaType ?? "(none)",
                oauth.TokenUrl,
                TrimForLog(body));

            throw CreateDetailedSuccessFactorsException(
                messagePrefix: "SuccessFactors OAuth token request failed.",
                response: response,
                requestUri: oauth.TokenUrl,
                body: body,
                query: null);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.TryGetProperty("access_token", out var accessToken) && accessToken.ValueKind == JsonValueKind.String)
        {
            return accessToken.GetString()!;
        }

        throw ExternalSystemExceptionFactory.CreateSuccessFactorsException(
            operation: "OAuth token request",
            endpoint: oauth.TokenUrl,
            summary: "The OAuth response did not contain an access_token.");
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

    private static InvalidOperationException CreateDetailedSuccessFactorsException(
        string messagePrefix,
        HttpResponseMessage response,
        string requestUri,
        string? body,
        SuccessFactorsQueryConfig? query)
    {
        var parts = new List<string>
        {
            messagePrefix,
            $"Status={(int)response.StatusCode}",
            $"ContentType={response.Content.Headers.ContentType?.MediaType ?? "(none)"}",
            $"RequestUri={requestUri}"
        };

        if (query is not null)
        {
            parts.Add($"Select={string.Join(",", query.Select)}");
            parts.Add($"Expand={string.Join(",", query.Expand)}");
        }

        if (!string.IsNullOrWhiteSpace(body))
        {
            parts.Add($"BodyPreview={TrimForLog(body)}");
        }

        return ExternalSystemExceptionFactory.CreateSuccessFactorsException(
            operation: messagePrefix.TrimEnd('.'),
            endpoint: requestUri,
            summary: string.Join(Environment.NewLine, parts));
    }

    private async Task<SuccessFactorsResponsePayload> ExecuteWorkerRequestAsync(
        SyncFactorsConfigDocument config,
        SuccessFactorsQueryConfig query,
        string workerId,
        CancellationToken cancellationToken)
    {
        var activeSelect = query.Select.ToList();

        while (true)
        {
            var activeQuery = query with { Select = activeSelect };
            var requestUri = BuildRequestUri(config, activeQuery, workerId);

            logger.LogInformation(
                "Fetching worker preview data from SuccessFactors. WorkerId={WorkerId} EntitySet={EntitySet} AuthMode={AuthMode} SelectedFieldCount={SelectedFieldCount}",
                workerId,
                query.EntitySet,
                config.SuccessFactors.Auth.Mode,
                activeQuery.Select.Count);

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
            AddTracingHeaders(request, workerId);
            await ApplyAuthenticationAsync(request, config.SuccessFactors.Auth, cancellationToken);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return new SuccessFactorsResponsePayload(
                    RequestUri: requestUri,
                    Body: body,
                    StatusCode: (int)response.StatusCode,
                    ContentType: response.Content.Headers.ContentType?.MediaType ?? "(none)");
            }

            var invalidPropertyPath = TryExtractInvalidPropertyPath(body);
            var queryPath = ResolveQueryPathFromInvalidProperty(invalidPropertyPath, activeSelect);
            if (response.StatusCode == HttpStatusCode.BadRequest &&
                !string.IsNullOrWhiteSpace(queryPath) &&
                activeSelect.Remove(queryPath))
            {
                logger.LogWarning(
                    "SuccessFactors rejected configured property path. WorkerId={WorkerId} InvalidProperty={InvalidProperty} QueryPath={QueryPath}. Retrying without it.",
                    workerId,
                    invalidPropertyPath,
                    queryPath);
                continue;
            }

            logger.LogError(
                "SuccessFactors request failed. WorkerId={WorkerId} StatusCode={StatusCode} ContentType={ContentType} Uri={Uri} BodyPreview={BodyPreview}",
                workerId,
                (int)response.StatusCode,
                response.Content.Headers.ContentType?.MediaType ?? "(none)",
                requestUri,
                TrimForLog(body));

            throw CreateDetailedSuccessFactorsException(
                messagePrefix: "SuccessFactors request failed.",
                response: response,
                requestUri: requestUri,
                body: body,
                query: activeQuery);
        }
    }

    private static string? TryExtractInvalidPropertyPath(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!document.RootElement.TryGetProperty("error", out var error) ||
                error.ValueKind != JsonValueKind.Object ||
                !error.TryGetProperty("code", out var code) ||
                !string.Equals(code.GetString(), "COE_PROPERTY_NOT_FOUND", StringComparison.Ordinal))
            {
                return null;
            }

            if (!error.TryGetProperty("message", out var message) ||
                message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("value", out var value))
            {
                return null;
            }

            var rawMessage = value.GetString();
            if (string.IsNullOrWhiteSpace(rawMessage))
            {
                return null;
            }

            const string marker = "Invalid property names:";
            var markerIndex = rawMessage.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                return null;
            }

            var candidate = rawMessage[(markerIndex + marker.Length)..].Trim();
            var terminalPunctuationIndex = candidate.IndexOfAny(['.', ' ', ',']);
            return terminalPunctuationIndex >= 0
                ? candidate[..terminalPunctuationIndex]
                : candidate;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ResolveQueryPathFromInvalidProperty(string? invalidPropertyPath, IReadOnlyList<string> currentSelectValues)
    {
        if (string.IsNullOrWhiteSpace(invalidPropertyPath))
        {
            return null;
        }

        var directMatch = currentSelectValues.FirstOrDefault(value => string.Equals(value, invalidPropertyPath, StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(directMatch))
        {
            return directMatch;
        }

        var parts = invalidPropertyPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        var propertyName = parts[^1];
        var entityName = parts.Length > 1 ? parts[0] : string.Empty;
        var propertyOnlyMatch = currentSelectValues.FirstOrDefault(value => string.Equals(value, propertyName, StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(propertyOnlyMatch))
        {
            return propertyOnlyMatch;
        }

        if (EntityNavigationAliases.TryGetValue(entityName, out var navigationName))
        {
            var aliasedMatch = currentSelectValues.FirstOrDefault(value =>
                value.EndsWith($"{navigationName}/{propertyName}", StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(aliasedMatch))
            {
                return aliasedMatch;
            }
        }

        return currentSelectValues.FirstOrDefault(value => value.EndsWith($"/{propertyName}", StringComparison.Ordinal));
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
        var userNav = employment is { ValueKind: not JsonValueKind.Undefined }
            ? GetNavigationObject(employment.Value, "userNav")
            : null;
        JsonElement? jobInfo = GetFirstNavigationResult(worker, "jobInfoNav")
            ?? (worker.TryGetProperty("jobTitle", out _)
                || worker.TryGetProperty("company", out _)
                || worker.TryGetProperty("department", out _)
                ? (JsonElement?)worker.Clone()
                : null)
            ?? (employment is { ValueKind: not JsonValueKind.Undefined }
                ? GetFirstNavigationResult(employment.Value, "jobInfoNav")
                : null);

        var preferredName =
            GetString(personalInfo, "preferredName") ??
            GetString(personalInfo, "firstName") ??
            GetString(worker, "preferredName") ??
            GetString(worker, "firstName") ??
            "Unknown";
        var lastName =
            GetString(personalInfo, "lastName") ??
            GetString(worker, "lastName") ??
            "Worker";
        var department =
            GetNavigationProperty(jobInfo, "departmentNav", "name_localized") ??
            GetNavigationProperty(jobInfo, "departmentNav", "name") ??
            GetString(jobInfo, "department") ??
            GetNavigationProperty(jobInfo, "departmentNav", "department") ??
            GetString(employment, "department") ??
            GetString(worker, "department") ??
            "Unknown";
        var startDate =
            GetString(jobInfo, "startDate") ??
            GetString(employment, "startDate") ??
            GetString(worker, "startDate");

        return new WorkerSnapshot(
            WorkerId: GetString(worker, query.IdentityField)
                ?? GetString(employment, query.IdentityField)
                ?? GetString(userNav, query.IdentityField)
                ?? workerId,
            PreferredName: preferredName,
            LastName: lastName,
            Department: department,
            TargetOu: config.Ad.DefaultActiveOu,
            IsPrehire: IsPrehire(startDate, config.Sync.EnableBeforeStartDays),
            Attributes: BuildAttributes(worker, personalInfo, employment, jobInfo));
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
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.ToString(),
            JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
            JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
            _ => null
        };
    }

    private static string? GetString(JsonElement? element, string propertyName)
    {
        return element is { ValueKind: not JsonValueKind.Undefined }
            ? GetString(element.Value, propertyName)
            : null;
    }

    private static string? GetNavigationProperty(JsonElement? element, string navigationPropertyName, string propertyName)
    {
        if (element is not { ValueKind: not JsonValueKind.Undefined })
        {
            return null;
        }

        var navigation = GetNavigationObject(element.Value, navigationPropertyName);
        return navigation is { ValueKind: not JsonValueKind.Undefined }
            ? GetString(navigation.Value, propertyName)
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

    private static IReadOnlyDictionary<string, string?> BuildAttributes(
        JsonElement worker,
        JsonElement? personalInfo,
        JsonElement? employment,
        JsonElement? jobInfo)
    {
        var primaryEmail = GetPreferredNavigationResult(worker, "emailNav", static email => IsTrue(email, "isPrimary"))
            ?? GetFirstNavigationResult(worker, "emailNav");
        var primaryPhone = GetPreferredNavigationResult(worker, "phoneNav", static phone => IsTrue(phone, "isPrimary"))
            ?? GetFirstNavigationResult(worker, "phoneNav");
        var businessPhone = GetPreferredNavigationResult(worker, "phoneNav", static phone => PropertyEquals(phone, "phoneType", "10605"));
        var cellPhone = GetPreferredNavigationResult(worker, "phoneNav", static phone => PropertyEquals(phone, "phoneType", "10606"));
        var companyNav = jobInfo is { ValueKind: not JsonValueKind.Undefined }
            ? GetNavigationObject(jobInfo.Value, "companyNav")
            : null;
        var departmentNav = jobInfo is { ValueKind: not JsonValueKind.Undefined }
            ? GetNavigationObject(jobInfo.Value, "departmentNav")
            : null;
        var businessUnitNav = jobInfo is { ValueKind: not JsonValueKind.Undefined }
            ? GetNavigationObject(jobInfo.Value, "businessUnitNav")
            : null;
        var costCenterNav = jobInfo is { ValueKind: not JsonValueKind.Undefined }
            ? GetNavigationObject(jobInfo.Value, "costCenterNav")
            : null;
        var divisionNav = jobInfo is { ValueKind: not JsonValueKind.Undefined }
            ? GetNavigationObject(jobInfo.Value, "divisionNav")
            : null;
        var locationNav = jobInfo is { ValueKind: not JsonValueKind.Undefined }
            ? GetNavigationObject(jobInfo.Value, "locationNav")
            : null;
        var addressNavDefault = locationNav is { ValueKind: not JsonValueKind.Undefined }
            ? GetNavigationObject(locationNav.Value, "addressNavDEFLT")
            : null;
        var companyCountryNav = companyNav is { ValueKind: not JsonValueKind.Undefined }
            ? GetNavigationObject(companyNav.Value, "countryOfRegistrationNav")
            : null;
        var userNav = employment is { ValueKind: not JsonValueKind.Undefined }
            ? GetNavigationObject(employment.Value, "userNav")
            : null;
        var managerNav = userNav is { ValueKind: not JsonValueKind.Undefined }
            ? GetNavigationObject(userNav.Value, "manager")
            : null;
        var managerEmpInfo = managerNav is { ValueKind: not JsonValueKind.Undefined }
            ? GetNavigationObject(managerNav.Value, "empInfo")
            : null;
        var payGradeNav = jobInfo is { ValueKind: not JsonValueKind.Undefined }
            ? GetNavigationObject(jobInfo.Value, "payGradeNav")
            : null;
        var jobCodeNav = jobInfo is { ValueKind: not JsonValueKind.Undefined }
            ? GetNavigationObject(jobInfo.Value, "jobCodeNav")
            : null;
        var personEmpTerminationInfoNav = GetNavigationObject(worker, "personEmpTerminationInfoNav");

        var firstName = GetString(personalInfo, "firstName") ?? GetString(worker, "firstName");
        var lastName = GetString(personalInfo, "lastName") ?? GetString(worker, "lastName");
        var preferredName = GetString(personalInfo, "preferredName") ?? firstName ?? GetString(worker, "preferredName");
        var displayName = GetString(personalInfo, "displayName") ?? GetString(worker, "displayName");
        var primaryEmailAddress = GetString(primaryEmail, "emailAddress") ?? GetString(worker, "email");
        var primaryEmailType = GetString(primaryEmail, "emailType");
        var primaryEmailIsPrimary = GetString(primaryEmail, "isPrimary");
        var companyName = GetString(companyNav, "name_localized") ?? GetString(companyNav, "name") ?? GetString(companyNav, "company") ?? GetString(jobInfo, "company") ?? GetString(worker, "company");
        var departmentName = GetString(departmentNav, "name_localized") ?? GetString(departmentNav, "name") ?? GetString(departmentNav, "department") ?? GetString(jobInfo, "department") ?? GetString(employment, "department") ?? GetString(worker, "department");
        var businessUnitName = GetString(businessUnitNav, "name_localized") ?? GetString(businessUnitNav, "name") ?? GetString(businessUnitNav, "businessUnit") ?? GetString(jobInfo, "businessUnit") ?? GetString(worker, "businessUnit");
        var divisionName = GetString(divisionNav, "name_localized") ?? GetString(divisionNav, "name") ?? GetString(divisionNav, "division") ?? GetString(jobInfo, "division") ?? GetString(worker, "division");
        var locationName = GetString(locationNav, "name") ?? GetString(locationNav, "LocationName") ?? GetString(jobInfo, "location") ?? GetString(worker, "location");
        var officeLocationAddress = GetString(addressNavDefault, "address1") ?? GetString(locationNav, "officeLocationAddress");
        var officeLocationCity = GetString(addressNavDefault, "city") ?? GetString(locationNav, "officeLocationCity");
        var officeLocationZipCode = GetString(addressNavDefault, "zipCode") ?? GetString(locationNav, "officeLocationZipCode");
        var officeLocationCustomString4 = GetString(addressNavDefault, "customString4");
        var costCenterName = GetString(costCenterNav, "name_localized") ?? GetString(costCenterNav, "costCenter") ?? GetString(jobInfo, "costCenter") ?? GetString(worker, "costCenter");
        var costCenterDescription = GetString(costCenterNav, "description_localized") ?? GetString(costCenterNav, "costCenterDescription") ?? costCenterName;
        var costCenterId = GetString(costCenterNav, "externalCode");
        var managerId = GetString(managerEmpInfo, "personIdExternal") ?? GetString(jobInfo, "managerId") ?? GetString(worker, "managerId");

        var attributes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["personIdExternal"] = GetString(worker, "personIdExternal"),
            ["personId"] = GetString(worker, "personId"),
            ["perPersonUuid"] = GetString(worker, "perPersonUuid"),
            ["displayName"] = displayName,
            ["firstName"] = firstName,
            ["lastName"] = lastName,
            ["middleName"] = GetString(personalInfo, "middleName") ?? GetString(worker, "middleName"),
            ["preferredName"] = preferredName,
            ["gender"] = GetString(personalInfo, "gender") ?? GetString(worker, "gender"),
            ["email"] = primaryEmailAddress,
            ["emailType"] = primaryEmailType,
            ["emailIsPrimary"] = primaryEmailIsPrimary,
            ["department"] = departmentName,
            ["departmentnew"] = GetString(departmentNav, "name") ?? departmentName,
            ["departmentcode"] = GetString(departmentNav, "costCenter"),
            ["company"] = companyName,
            ["companyId"] = GetString(companyNav, "externalCode"),
            ["countryOfCompany"] = GetString(jobInfo, "countryOfCompany"),
            ["twoCharCountryCode"] = GetString(companyCountryNav, "twoCharCountryCode"),
            ["location"] = locationName,
            ["officeLocationAddress"] = officeLocationAddress,
            ["officeLocationCity"] = officeLocationCity,
            ["officeLocationZipCode"] = officeLocationZipCode,
            ["officeLocationCustomString4"] = officeLocationCustomString4,
            ["jobTitle"] = GetString(jobInfo, "jobTitle") ?? GetString(worker, "jobTitle"),
            ["jobCode"] = GetString(jobCodeNav, "name_localized") ?? GetString(jobInfo, "jobCode"),
            ["jobCodeId"] = GetString(jobCodeNav, "externalCode"),
            ["position"] = GetString(jobInfo, "position"),
            ["payGrade"] = GetString(payGradeNav, "name"),
            ["businessUnit"] = businessUnitName,
            ["businessUnitId"] = GetString(businessUnitNav, "externalCode"),
            ["division"] = divisionName,
            ["divisionId"] = GetString(divisionNav, "externalCode"),
            ["costCenter"] = costCenterName,
            ["costCenterDescription"] = costCenterDescription,
            ["costCenterId"] = costCenterId,
            ["employeeClass"] = GetString(jobInfo, "employeeClass") ?? GetString(worker, "employeeClass"),
            ["employeeType"] = GetString(jobInfo, "employeeType") ?? GetString(worker, "employeeType"),
            ["emplStatus"] = GetString(jobInfo, "emplStatus"),
            ["managerId"] = managerId,
            ["manager"] = managerId,
            ["peopleGroup"] = GetString(jobInfo, "customString3") ?? GetString(worker, "customString3"),
            ["leadershipLevel"] = GetString(jobInfo, "customString20") ?? GetString(worker, "customString20"),
            ["region"] = GetString(jobInfo, "customString87") ?? GetString(worker, "customString87"),
            ["geozone"] = GetString(jobInfo, "customString110") ?? GetString(worker, "customString110"),
            ["bargainingUnit"] = GetString(jobInfo, "customString111") ?? GetString(worker, "customString111"),
            ["unionJobCode"] = GetString(jobInfo, "customString91") ?? GetString(worker, "customString91"),
            ["cintasUniformCategory"] = GetString(jobInfo, "customString112") ?? GetString(worker, "customString112"),
            ["cintasUniformAllotment"] = GetString(jobInfo, "customString113") ?? GetString(worker, "customString113"),
            ["activeEmploymentsCount"] = GetString(personEmpTerminationInfoNav, "activeEmploymentsCount"),
            ["latestTerminationDate"] = GetString(personEmpTerminationInfoNav, "latestTerminationDate"),
            ["startDate"] = GetString(employment, "startDate") ?? GetString(worker, "startDate"),
            ["endDate"] = GetString(employment, "endDate"),
            ["firstDateWorked"] = GetString(employment, "firstDateWorked"),
            ["lastDateWorked"] = GetString(employment, "lastDateWorked"),
            ["isContingentWorker"] = GetString(employment, "isContingentWorker"),
            ["userId"] = GetString(employment, "userId") ?? GetString(worker, "userId"),
            ["addressLine1"] = GetString(userNav, "addressLine1"),
            ["addressLine2"] = GetString(userNav, "addressLine2"),
            ["addressLine3"] = GetString(userNav, "addressLine3"),
            ["businessPhone"] = GetString(userNav, "businessPhone"),
            ["cellPhone"] = GetString(userNav, "cellPhone"),
            ["city"] = GetString(userNav, "city"),
            ["country"] = GetString(userNav, "country"),
            ["empId"] = GetString(userNav, "empId"),
            ["homePhone"] = GetString(userNav, "homePhone"),
            ["jobFamily"] = GetString(userNav, "jobFamily"),
            ["loginMethod"] = GetString(userNav, "loginMethod"),
            ["nickname"] = GetString(userNav, "nickname"),
            ["state"] = GetString(userNav, "state"),
            ["timeZone"] = GetString(userNav, "timeZone"),
            ["username"] = GetString(userNav, "username"),
            ["zipCode"] = GetString(userNav, "zipCode"),
            ["areaCode"] = GetString(primaryPhone, "areaCode"),
            ["countryCode"] = GetString(primaryPhone, "countryCode"),
            ["extension"] = GetString(primaryPhone, "extension"),
            ["phoneNumber"] = GetString(primaryPhone, "phoneNumber"),
            ["phoneType"] = GetString(primaryPhone, "phoneType"),
            ["businessPhoneAreaCode"] = GetString(businessPhone, "areaCode"),
            ["businessPhoneCountryCode"] = GetString(businessPhone, "countryCode"),
            ["businessPhoneExtension"] = GetString(businessPhone, "extension"),
            ["businessPhoneIsPrimary"] = GetString(businessPhone, "isPrimary"),
            ["businessPhoneNumber"] = GetString(businessPhone, "phoneNumber"),
            ["businessPhoneType"] = GetString(businessPhone, "phoneType"),
            ["cellPhoneAreaCode"] = GetString(cellPhone, "areaCode"),
            ["cellPhoneCountryCode"] = GetString(cellPhone, "countryCode"),
            ["cellPhoneIsPrimary"] = GetString(cellPhone, "isPrimary"),
            ["cellPhoneNumber"] = GetString(cellPhone, "phoneNumber"),
            ["cellPhoneType"] = GetString(cellPhone, "phoneType"),
            ["employmentNav[0].jobInfoNav[0].companyNav.company"] = companyName,
            ["employmentNav[0].jobInfoNav[0].companyNav.name_localized"] = companyName,
            ["employmentNav[0].jobInfoNav[0].companyNav.externalCode"] = GetString(companyNav, "externalCode"),
            ["employmentNav[0].jobInfoNav[0].companyNav.countryOfRegistrationNav.twoCharCountryCode"] = GetString(companyCountryNav, "twoCharCountryCode"),
            ["employmentNav[0].jobInfoNav[0].departmentNav.department"] = departmentName,
            ["employmentNav[0].jobInfoNav[0].departmentNav.name_localized"] = departmentName,
            ["employmentNav[0].jobInfoNav[0].departmentNav.name"] = GetString(departmentNav, "name") ?? departmentName,
            ["employmentNav[0].jobInfoNav[0].departmentNav.externalCode"] = GetString(departmentNav, "externalCode"),
            ["employmentNav[0].jobInfoNav[0].departmentNav.costCenter"] = GetString(departmentNav, "costCenter"),
            ["employmentNav[0].jobInfoNav[0].businessUnitNav.businessUnit"] = businessUnitName,
            ["employmentNav[0].jobInfoNav[0].businessUnitNav.name_localized"] = businessUnitName,
            ["employmentNav[0].jobInfoNav[0].businessUnitNav.externalCode"] = GetString(businessUnitNav, "externalCode"),
            ["employmentNav[0].jobInfoNav[0].costCenterNav.costCenterDescription"] = costCenterDescription,
            ["employmentNav[0].jobInfoNav[0].costCenterNav.name_localized"] = costCenterName,
            ["employmentNav[0].jobInfoNav[0].costCenterNav.description_localized"] = costCenterDescription,
            ["employmentNav[0].jobInfoNav[0].costCenterNav.externalCode"] = costCenterId,
            ["employmentNav[0].jobInfoNav[0].divisionNav.division"] = divisionName,
            ["employmentNav[0].jobInfoNav[0].divisionNav.name_localized"] = divisionName,
            ["employmentNav[0].jobInfoNav[0].divisionNav.externalCode"] = GetString(divisionNav, "externalCode"),
            ["employmentNav[0].jobInfoNav[0].locationNav.LocationName"] = locationName,
            ["employmentNav[0].jobInfoNav[0].locationNav.name"] = locationName,
            ["employmentNav[0].jobInfoNav[0].locationNav.addressNavDEFLT.address1"] = officeLocationAddress,
            ["employmentNav[0].jobInfoNav[0].locationNav.addressNavDEFLT.city"] = officeLocationCity,
            ["employmentNav[0].jobInfoNav[0].locationNav.addressNavDEFLT.customString4"] = officeLocationCustomString4,
            ["employmentNav[0].jobInfoNav[0].locationNav.addressNavDEFLT.zipCode"] = officeLocationZipCode,
            ["employmentNav[0].jobInfoNav[0].locationNav.officeLocationAddress"] = officeLocationAddress,
            ["employmentNav[0].jobInfoNav[0].locationNav.officeLocationCity"] = officeLocationCity,
            ["employmentNav[0].jobInfoNav[0].locationNav.officeLocationZipCode"] = officeLocationZipCode,
            ["employmentNav[0].jobInfoNav[0].jobTitle"] = GetString(jobInfo, "jobTitle"),
            ["employmentNav[0].jobInfoNav[0].jobCodeNav.name_localized"] = GetString(jobCodeNav, "name_localized"),
            ["employmentNav[0].jobInfoNav[0].jobCodeNav.externalCode"] = GetString(jobCodeNav, "externalCode"),
            ["employmentNav[0].jobInfoNav[0].position"] = GetString(jobInfo, "position"),
            ["employmentNav[0].jobInfoNav[0].payGradeNav.name"] = GetString(payGradeNav, "name"),
            ["employmentNav[0].jobInfoNav[0].emplStatus"] = GetString(jobInfo, "emplStatus"),
            ["employmentNav[0].jobInfoNav[0].customString3"] = GetString(jobInfo, "customString3"),
            ["employmentNav[0].jobInfoNav[0].customString20"] = GetString(jobInfo, "customString20"),
            ["employmentNav[0].jobInfoNav[0].customString87"] = GetString(jobInfo, "customString87"),
            ["employmentNav[0].jobInfoNav[0].customString110"] = GetString(jobInfo, "customString110"),
            ["employmentNav[0].jobInfoNav[0].customString111"] = GetString(jobInfo, "customString111"),
            ["employmentNav[0].jobInfoNav[0].customString91"] = GetString(jobInfo, "customString91"),
            ["employmentNav[0].jobInfoNav[0].customString112"] = GetString(jobInfo, "customString112"),
            ["employmentNav[0].jobInfoNav[0].customString113"] = GetString(jobInfo, "customString113"),
            ["employmentNav[0].userNav.manager.empInfo.personIdExternal"] = managerId,
            ["emailNav[?(@.isPrimary == true)].emailAddress"] = primaryEmailAddress,
            ["emailNav[?(@.isPrimary == true)].emailType"] = primaryEmailType,
            ["emailNav[?(@.isPrimary == true)].isPrimary"] = primaryEmailIsPrimary,
            ["phoneNav[?(@.isPrimary == true)].areaCode"] = GetString(primaryPhone, "areaCode"),
            ["phoneNav[?(@.isPrimary == true)].countryCode"] = GetString(primaryPhone, "countryCode"),
            ["phoneNav[?(@.isPrimary == true)].extension"] = GetString(primaryPhone, "extension"),
            ["phoneNav[?(@.isPrimary == true)].phoneNumber"] = GetString(primaryPhone, "phoneNumber"),
            ["phoneNav[?(@.isPrimary == true)].phoneType"] = GetString(primaryPhone, "phoneType"),
            ["phoneNav[?(@.phoneType == '10605')].areaCode"] = GetString(businessPhone, "areaCode"),
            ["phoneNav[?(@.phoneType == '10605')].countryCode"] = GetString(businessPhone, "countryCode"),
            ["phoneNav[?(@.phoneType == '10605')].extension"] = GetString(businessPhone, "extension"),
            ["phoneNav[?(@.phoneType == '10605')].isPrimary"] = GetString(businessPhone, "isPrimary"),
            ["phoneNav[?(@.phoneType == '10605')].phoneNumber"] = GetString(businessPhone, "phoneNumber"),
            ["phoneNav[?(@.phoneType == '10605')].phoneType"] = GetString(businessPhone, "phoneType"),
            ["phoneNav[?(@.phoneType == '10606')].areaCode"] = GetString(cellPhone, "areaCode"),
            ["phoneNav[?(@.phoneType == '10606')].countryCode"] = GetString(cellPhone, "countryCode"),
            ["phoneNav[?(@.phoneType == '10606')].isPrimary"] = GetString(cellPhone, "isPrimary"),
            ["phoneNav[?(@.phoneType == '10606')].phoneNumber"] = GetString(cellPhone, "phoneNumber"),
            ["phoneNav[?(@.phoneType == '10606')].phoneType"] = GetString(cellPhone, "phoneType")
        };

        for (var index = 1; index <= 15; index++)
        {
            var propertyName = $"customString{index}";
            var jobValue = GetString(jobInfo, propertyName);
            var employmentValue = GetString(employment, propertyName);
            var userValue = GetString(userNav, $"custom{index:00}");

            attributes[$"empJobNavCustomString{index}"] = jobValue;
            attributes[$"employmentNav[0].jobInfoNav[0].{propertyName}"] = jobValue;
            attributes[$"empNavCustomString{index}"] = employmentValue;
            attributes[$"employmentNav[0].{propertyName}"] = employmentValue;
            attributes[$"custom{index:00}"] = userValue;
            attributes[$"employmentNav[0].userNav.custom{index:00}"] = userValue;
        }

        return attributes;
    }

    private static JsonElement? GetPreferredNavigationResult(
        JsonElement element,
        string propertyName,
        Func<JsonElement, bool> predicate)
    {
        if (!element.TryGetProperty(propertyName, out var navigation) ||
            navigation.ValueKind != JsonValueKind.Object ||
            !navigation.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in results.EnumerateArray())
        {
            if (predicate(item))
            {
                return item.Clone();
            }
        }

        return null;
    }

    private static bool PropertyEquals(JsonElement element, string propertyName, string expectedValue)
    {
        return string.Equals(GetString(element, propertyName), expectedValue, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTrue(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => bool.TryParse(property.GetString(), out var parsed) && parsed,
            _ => false
        };
    }

    private static JsonElement? GetNavigationObject(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Object &&
            property.TryGetProperty("results", out var results) &&
            results.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in results.EnumerateArray())
            {
                return item.Clone();
            }
        }

        if (property.ValueKind == JsonValueKind.Object)
        {
            return property.Clone();
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

    private sealed record SuccessFactorsResponsePayload(string RequestUri, string Body, int StatusCode, string ContentType);
}
