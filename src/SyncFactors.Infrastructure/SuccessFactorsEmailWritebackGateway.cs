using Microsoft.Extensions.Logging;
using SyncFactors.Contracts;
using SyncFactors.Domain;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SyncFactors.Infrastructure;

public sealed class SuccessFactorsEmailWritebackGateway(
    HttpClient httpClient,
    SyncFactorsConfigurationLoader configLoader,
    ILogger<SuccessFactorsEmailWritebackGateway> logger) : ISuccessFactorsEmailWritebackGateway
{
    public async Task<SuccessFactorsEmailWritebackResult?> WriteBackEmailAsync(
        PlannedWorkerAction plan,
        DirectoryMutationCommand command,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var config = configLoader.GetSyncConfig();
        var writeback = config.SuccessFactors.EmailWriteback;
        if (!writeback.Enabled)
        {
            return null;
        }

        var userId = ResolveAttribute(plan.Worker, writeback.UserIdSourceAttribute) ?? plan.Worker.WorkerId;
        var emailAddress = string.IsNullOrWhiteSpace(command.Mail) ? plan.ProposedEmailAddress : command.Mail;
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(emailAddress))
        {
            return new SuccessFactorsEmailWritebackResult(
                UserId: userId ?? plan.Worker.WorkerId,
                EmailAddress: emailAddress ?? string.Empty,
                PreviousEmailAddress: ResolveAttribute(plan.Worker, writeback.SourceEmailAttribute),
                Endpoint: BuildUpsertEndpoint(config, writeback),
                Applied: false,
                Succeeded: false,
                Message: "SuccessFactors email writeback could not run because userId or email address was empty.");
        }

        var currentEmailAddress = ResolveAttribute(plan.Worker, writeback.SourceEmailAttribute);
        if (string.Equals(currentEmailAddress, emailAddress, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Skipping SuccessFactors email writeback because email already matches. UserId={UserId}", userId);
            return new SuccessFactorsEmailWritebackResult(
                UserId: userId,
                EmailAddress: emailAddress,
                PreviousEmailAddress: currentEmailAddress,
                Endpoint: BuildUpsertEndpoint(config, writeback),
                Applied: false,
                Succeeded: true,
                Message: "SuccessFactors email already matched Active Directory.");
        }

        var endpoint = BuildUpsertEndpoint(config, writeback);
        if (dryRun)
        {
            logger.LogInformation("Planned SuccessFactors email writeback. UserId={UserId}", userId);
            return new SuccessFactorsEmailWritebackResult(
                UserId: userId,
                EmailAddress: emailAddress,
                PreviousEmailAddress: currentEmailAddress,
                Endpoint: endpoint,
                Applied: false,
                Succeeded: true,
                Message: "SuccessFactors email writeback planned.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
        request.Headers.TryAddWithoutValidation("x-correlation-id", Guid.NewGuid().ToString("D"));
        request.Headers.TryAddWithoutValidation("X-SF-Process-Name", "SyncFactors.EmailWriteback");
        request.Headers.TryAddWithoutValidation("X-SF-Execution-Id", userId);
        await ApplyAuthenticationAsync(request, config.SuccessFactors.Auth, cancellationToken);

        request.Content = JsonContent.Create(BuildPayload(writeback, userId, emailAddress));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var message = $"SuccessFactors email writeback request failed. Status={(int)response.StatusCode}, ContentType={response.Content.Headers.ContentType?.MediaType ?? "(none)"}, Endpoint={endpoint}, BodyPreview={TrimForLog(body)}";
            logger.LogError("SuccessFactors email writeback failed. StatusCode={StatusCode} UserId={UserId}", (int)response.StatusCode, userId);
            return new SuccessFactorsEmailWritebackResult(userId, emailAddress, currentEmailAddress, endpoint, Applied: true, Succeeded: false, Message: message);
        }

        var upsertResult = ParseUpsertResult(body);
        var succeeded = upsertResult.Succeeded;
        var resultMessage = succeeded
            ? $"SuccessFactors email updated for user {userId}."
            : $"SuccessFactors email writeback failed for user {userId}. {upsertResult.Message ?? "No upsert message was returned."}";

        if (!succeeded)
        {
            logger.LogError("SuccessFactors email writeback returned an upsert failure. UserId={UserId} Message={Message}", userId, upsertResult.Message);
        }
        else
        {
            logger.LogInformation("SuccessFactors email writeback succeeded. UserId={UserId}", userId);
        }

        return new SuccessFactorsEmailWritebackResult(
            UserId: userId,
            EmailAddress: emailAddress,
            PreviousEmailAddress: currentEmailAddress,
            Endpoint: endpoint,
            Applied: true,
            Succeeded: succeeded,
            Message: resultMessage);
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

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw ExternalSystemExceptionFactory.CreateSuccessFactorsException(
                operation: "OAuth token request",
                endpoint: oauth.TokenUrl,
                summary: $"SuccessFactors OAuth token request failed. Status={(int)response.StatusCode}, BodyPreview={TrimForLog(body)}");
        }

        using var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("access_token", out var accessToken) && accessToken.ValueKind == JsonValueKind.String)
        {
            return accessToken.GetString()!;
        }

        throw ExternalSystemExceptionFactory.CreateSuccessFactorsException(
            operation: "OAuth token request",
            endpoint: oauth.TokenUrl,
            summary: "The OAuth response did not contain an access_token.");
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

    private static object BuildPayload(SuccessFactorsEmailWritebackConfig writeback, string userId, string emailAddress)
    {
        return new Dictionary<string, object?>
        {
            ["__metadata"] = new Dictionary<string, string>
            {
                ["uri"] = $"{writeback.UserEntitySet}('{EscapeODataKey(userId)}')",
                ["type"] = $"SFOData.{writeback.UserEntitySet}"
            },
            ["userId"] = userId,
            [writeback.EmailField] = emailAddress
        };
    }

    private static string BuildUpsertEndpoint(SyncFactorsConfigDocument config, SuccessFactorsEmailWritebackConfig writeback)
    {
        return $"{config.SuccessFactors.BaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(writeback.UserEntitySet)}/upsert";
    }

    private static string? ResolveAttribute(WorkerSnapshot worker, string attribute)
    {
        if (worker.Attributes.TryGetValue(attribute, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var normalized = NormalizeSourceAttribute(attribute);
        return worker.Attributes.TryGetValue(normalized, out value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static string NormalizeSourceAttribute(string attribute) => attribute switch
    {
        "emailNav[0].emailAddress" => "email",
        "emailNav[?(@.isPrimary == true)].emailAddress" => "email",
        "employmentNav[0].userId" => "userId",
        "employmentNav/userId" => "userId",
        _ => attribute
    };

    private static (bool Succeeded, string? Message) ParseUpsertResult(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (true, null);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var result = ResolveFirstUpsertResult(document.RootElement);
            if (result is null)
            {
                return (true, null);
            }

            var status = GetString(result.Value, "status");
            var httpCode = GetInt32(result.Value, "httpCode");
            var message = GetString(result.Value, "message");
            return (string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase) && (httpCode is null or < 400), message);
        }
        catch (JsonException)
        {
            return (true, null);
        }
    }

    private static JsonElement? ResolveFirstUpsertResult(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("d", out var d))
        {
            return null;
        }

        if (d.ValueKind == JsonValueKind.Array)
        {
            var enumerator = d.EnumerateArray();
            return enumerator.MoveNext() ? enumerator.Current : null;
        }

        if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            var enumerator = results.EnumerateArray();
            return enumerator.MoveNext() ? enumerator.Current : null;
        }

        return d.ValueKind == JsonValueKind.Object ? d : null;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static int? GetInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), out var value) => value,
            _ => null
        };
    }

    private static string EscapeODataKey(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string TrimForLog(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(empty)" : LogSafety.RedactPii(value);
    }
}
