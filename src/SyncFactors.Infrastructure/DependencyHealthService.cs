using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Contracts;
using SyncFactors.Domain;
using System.Diagnostics;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SyncFactors.Infrastructure;

public sealed class DependencyHealthService(
    SyncFactorsConfigurationLoader configLoader,
    SqlitePathResolver sqlitePathResolver,
    IWorkerHeartbeatStore workerHeartbeatStore,
    HttpClient httpClient,
    TimeProvider timeProvider,
    ILogger<DependencyHealthService> logger,
    Func<ActiveDirectoryConfig, CancellationToken, Task<(string RequestedTransport, string EffectiveTransport, bool UsedFallback, int SuccessfulBaseCount, int SkippedBaseCount, string? SkippedBaseDetails)>>? activeDirectoryProbe = null) : IDependencyHealthService
{
    private static readonly TimeSpan DefaultActiveDirectoryTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SuccessFactorsTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HealthyHeartbeatAge = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DegradedHeartbeatAge = TimeSpan.FromMinutes(2);
    private static readonly IReadOnlyDictionary<string, string> SuccessFactorsEntityNavigationAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["FOBusinessUnit"] = "businessUnitNav",
            ["FOCostCenter"] = "costCenterNav",
            ["FOCompany"] = "companyNav",
            ["FODepartment"] = "departmentNav",
            ["FODivision"] = "divisionNav",
            ["FOLocation"] = "locationNav"
        };

    public async Task<DependencyHealthSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var checkedAt = timeProvider.GetUtcNow();
        var probes = await Task.WhenAll(
            ProbeSuccessFactorsAsync(checkedAt, cancellationToken),
            ProbeActiveDirectoryAsync(checkedAt, cancellationToken),
            ProbeWorkerAsync(checkedAt, cancellationToken),
            ProbeSqliteAsync(checkedAt, cancellationToken));

        return new DependencyHealthSnapshot(
            Status: CalculateOverallStatus(probes),
            CheckedAt: checkedAt,
            Probes: probes);
    }

    private async Task<DependencyProbeResult> ProbeSuccessFactorsAsync(DateTimeOffset checkedAt, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var config = configLoader.GetSyncConfig().SuccessFactors;
            if (string.IsNullOrWhiteSpace(config.BaseUrl))
            {
                return BuildProbe("SuccessFactors", DependencyHealthStates.Unhealthy, "Base URL is not configured.", checkedAt, stopwatch.ElapsedMilliseconds);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(SuccessFactorsTimeout);
            var probeResult = await ExecuteSuccessFactorsProbeAsync(config, checkedAt, stopwatch.ElapsedMilliseconds, timeoutCts.Token);
            if (probeResult is not null)
            {
                return probeResult;
            }

            return BuildProbe("SuccessFactors", DependencyHealthStates.Unhealthy, "Authenticated read probe failed.", checkedAt, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "SuccessFactors health probe timed out.");
            return BuildSuccessFactorsTimeoutProbe(checkedAt, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested && IsTimeoutLikeFailure(ex))
            {
                logger.LogWarning(ex, "SuccessFactors health probe timed out.");
                return BuildSuccessFactorsTimeoutProbe(checkedAt, stopwatch.ElapsedMilliseconds);
            }
            logger.LogWarning(ex, "SuccessFactors health probe failed.");
            return BuildProbe(
                "SuccessFactors",
                DependencyHealthStates.Unhealthy,
                "Authenticated read probe failed.",
                checkedAt,
                stopwatch.ElapsedMilliseconds,
                details: ex.Message);
        }
    }

    private async Task<DependencyProbeResult?> ExecuteSuccessFactorsProbeAsync(
        SuccessFactorsConfig config,
        DateTimeOffset checkedAt,
        long durationMilliseconds,
        CancellationToken cancellationToken)
    {
        var activeSelect = BuildSuccessFactorsProbeSelect(config.Query);
        var removedFields = new List<string>();

        while (true)
        {
            var requestUri = BuildSuccessFactorsProbeUri(config, activeSelect);
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
            await ApplySuccessFactorsAuthenticationAsync(request, config.Auth, cancellationToken);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    using var document = JsonDocument.Parse(body);
                    _ = document.RootElement.ValueKind;
                }
                catch (JsonException ex)
                {
                    logger.LogWarning(ex, "SuccessFactors probe returned invalid JSON.");
                    return BuildProbe(
                        "SuccessFactors",
                        DependencyHealthStates.Unhealthy,
                        "Read probe returned invalid JSON.",
                        checkedAt,
                        durationMilliseconds,
                        details: TrimForLog(body));
                }

                var details = removedFields.Count == 0
                    ? null
                    : $"Ignored invalid select fields: {string.Join(", ", removedFields)}";
                return BuildProbe(
                    "SuccessFactors",
                    DependencyHealthStates.Healthy,
                    $"Authenticated read succeeded against {config.Query.EntitySet}.",
                    checkedAt,
                    durationMilliseconds,
                    details: details);
            }

            var invalidPropertyPath = TryExtractInvalidPropertyPath(body);
            var queryPath = ResolveQueryPathFromInvalidProperty(invalidPropertyPath, activeSelect);
            if (response.StatusCode == HttpStatusCode.BadRequest &&
                !string.IsNullOrWhiteSpace(queryPath) &&
                activeSelect.Remove(queryPath))
            {
                removedFields.Add(queryPath);
                logger.LogWarning(
                    "SuccessFactors health probe rejected configured property path. InvalidProperty={InvalidProperty} QueryPath={QueryPath}. Retrying without it.",
                    invalidPropertyPath,
                    queryPath);
                continue;
            }

            return BuildProbe(
                "SuccessFactors",
                DependencyHealthStates.Unhealthy,
                $"Read probe failed with HTTP {(int)response.StatusCode}.",
                checkedAt,
                durationMilliseconds,
                details: TrimForLog(body));
        }
    }

    private async Task<DependencyProbeResult> ProbeActiveDirectoryAsync(DateTimeOffset checkedAt, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var isProduction = IsProductionEnvironment();

        try
        {
            var config = configLoader.GetSyncConfig().Ad;
            if (string.IsNullOrWhiteSpace(config.Server))
            {
                return BuildProbe("Active Directory", DependencyHealthStates.Unhealthy, "Server is not configured.", checkedAt, stopwatch.ElapsedMilliseconds);
            }

            var activeDirectoryTimeout = GetActiveDirectoryTimeout(config);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(activeDirectoryTimeout);

            var transportTask = activeDirectoryProbe is null
                ? ProbeActiveDirectoryTransportAsync(config, activeDirectoryTimeout, timeoutCts.Token)
                : activeDirectoryProbe(config, timeoutCts.Token);

            var transportResult = await transportTask.WaitAsync(activeDirectoryTimeout, cancellationToken);

            var details = new List<string>();
            if (transportResult.UsedFallback && !isProduction)
            {
                details.Add($"Requested transport '{transportResult.RequestedTransport}' failed and the probe bound over fallback '{transportResult.EffectiveTransport}'.");
            }

            if (!string.IsNullOrWhiteSpace(transportResult.SkippedBaseDetails))
            {
                details.Add(transportResult.SkippedBaseDetails);
            }

            if (transportResult.SuccessfulBaseCount == 0)
            {
                return BuildProbe(
                    "Active Directory",
                    DependencyHealthStates.Unhealthy,
                    "LDAP bind succeeded, but no configured search base passed lookup validation.",
                    checkedAt,
                    stopwatch.ElapsedMilliseconds,
                    details: details.Count == 0 ? null : string.Join(" ", details));
            }

            if (transportResult.SkippedBaseCount > 0)
            {
                return BuildProbe(
                    "Active Directory",
                    DependencyHealthStates.Degraded,
                    $"LDAP bind and lookup probe succeeded, but {transportResult.SkippedBaseCount} search {(transportResult.SkippedBaseCount == 1 ? "base was" : "bases were")} skipped.",
                    checkedAt,
                    stopwatch.ElapsedMilliseconds,
                    details: details.Count == 0 ? null : string.Join(" ", details));
            }

            return BuildProbe(
                "Active Directory",
                DependencyHealthStates.Healthy,
                transportResult.UsedFallback && !isProduction
                    ? "LDAP bind and lookup probe succeeded via fallback LDAP."
                    : "LDAP bind and lookup probe succeeded.",
                checkedAt,
                stopwatch.ElapsedMilliseconds,
                details: details.Count == 0 ? null : string.Join(" ", details));
        }
        catch (TimeoutException ex)
        {
            logger.LogWarning(ex, "Active Directory health probe timed out.");
            var config = configLoader.GetSyncConfig().Ad;
            return BuildActiveDirectoryTimeoutProbe(
                config.Server,
                GetActiveDirectoryTimeout(config),
                checkedAt,
                stopwatch.ElapsedMilliseconds,
                ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Active Directory health probe timed out.");
            var config = configLoader.GetSyncConfig().Ad;
            return BuildActiveDirectoryTimeoutProbe(
                config.Server,
                GetActiveDirectoryTimeout(config),
                checkedAt,
                stopwatch.ElapsedMilliseconds,
                ex);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Active Directory health probe failed.");
            return BuildProbe(
                "Active Directory",
                DependencyHealthStates.Unhealthy,
                "LDAP bind or lookup probe failed.",
                checkedAt,
                stopwatch.ElapsedMilliseconds,
                details: ex.Message);
        }
    }

    private async Task<DependencyProbeResult> ProbeWorkerAsync(DateTimeOffset checkedAt, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var heartbeat = await workerHeartbeatStore.GetCurrentAsync(cancellationToken);
            if (heartbeat is null)
            {
                return BuildProbe(
                    "Worker Service",
                    DependencyHealthStates.Unhealthy,
                    "No worker heartbeat has been recorded.",
                    checkedAt,
                    stopwatch.ElapsedMilliseconds);
            }

            var age = checkedAt - heartbeat.LastSeenAt;
            if (age <= HealthyHeartbeatAge)
            {
                return BuildProbe(
                    "Worker Service",
                    DependencyHealthStates.Healthy,
                    $"Heartbeat received {FormatAge(age)} ago.",
                    checkedAt,
                    stopwatch.ElapsedMilliseconds,
                    details: heartbeat.Activity,
                    observedAt: heartbeat.LastSeenAt);
            }

            if (age <= DegradedHeartbeatAge &&
                string.Equals(heartbeat.State, "Running", StringComparison.OrdinalIgnoreCase))
            {
                return BuildProbe(
                    "Worker Service",
                    DependencyHealthStates.Healthy,
                    $"Worker is actively processing a run; last heartbeat was {FormatAge(age)} ago.",
                    checkedAt,
                    stopwatch.ElapsedMilliseconds,
                    details: heartbeat.Activity,
                    observedAt: heartbeat.LastSeenAt,
                    isStale: true);
            }

            if (age <= DegradedHeartbeatAge)
            {
                return BuildProbe(
                    "Worker Service",
                    DependencyHealthStates.Degraded,
                    $"Heartbeat is stale at {FormatAge(age)} old.",
                    checkedAt,
                    stopwatch.ElapsedMilliseconds,
                    details: heartbeat.Activity,
                    observedAt: heartbeat.LastSeenAt,
                    isStale: true);
            }

            return BuildProbe(
                "Worker Service",
                DependencyHealthStates.Unhealthy,
                $"Heartbeat is stale at {FormatAge(age)} old.",
                checkedAt,
                stopwatch.ElapsedMilliseconds,
                details: heartbeat.Activity,
                observedAt: heartbeat.LastSeenAt,
                isStale: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Worker heartbeat probe failed.");
            return BuildProbe(
                "Worker Service",
                DependencyHealthStates.Unhealthy,
                "Worker heartbeat probe failed.",
                checkedAt,
                stopwatch.ElapsedMilliseconds,
                details: ex.Message);
        }
    }

    private async Task<DependencyProbeResult> ProbeSqliteAsync(DateTimeOffset checkedAt, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var databasePath = sqlitePathResolver.Resolve();
            if (string.IsNullOrWhiteSpace(databasePath))
            {
                return BuildProbe("SQLite", DependencyHealthStates.Unhealthy, "SQLite path is not configured.", checkedAt, stopwatch.ElapsedMilliseconds);
            }

            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
            }.ToString());

            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type IN ('table', 'view');";
            _ = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);

            return BuildProbe(
                "SQLite",
                DependencyHealthStates.Healthy,
                "Operational store opened successfully.",
                checkedAt,
                stopwatch.ElapsedMilliseconds,
                details: databasePath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SQLite health probe failed.");
            return BuildProbe(
                "SQLite",
                DependencyHealthStates.Unhealthy,
                "Operational store could not be opened.",
                checkedAt,
                stopwatch.ElapsedMilliseconds,
                details: ex.Message);
        }
    }

    private async Task ApplySuccessFactorsAuthenticationAsync(
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
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetOAuthTokenAsync(auth.OAuth, cancellationToken));
                break;

            default:
                throw new InvalidOperationException($"Unsupported SuccessFactors auth mode '{auth.Mode}'.");
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
            throw new InvalidOperationException($"OAuth token request failed with HTTP {(int)response.StatusCode}: {TrimForLog(body)}");
        }

        using var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("access_token", out var accessToken) &&
            accessToken.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(accessToken.GetString()))
        {
            return accessToken.GetString()!;
        }

        throw new InvalidOperationException("OAuth token response did not contain an access_token.");
    }

    private static IReadOnlyList<KeyValuePair<string, string>> BuildTokenForm(SuccessFactorsOAuthConfig oauth)
    {
        var values = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "client_credentials"),
            new("client_id", oauth.ClientId),
            new("client_secret", oauth.ClientSecret)
        };

        if (!string.IsNullOrWhiteSpace(oauth.CompanyId))
        {
            values.Add(new("company_id", oauth.CompanyId));
        }

        return values;
    }

    private static List<string> BuildSuccessFactorsProbeSelect(SuccessFactorsQueryConfig query)
    {
        var selectValues = query.Select
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Take(2)
            .ToList();

        if (!selectValues.Contains(query.IdentityField, StringComparer.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(query.IdentityField))
        {
            selectValues.Insert(0, query.IdentityField);
        }

        return selectValues;
    }

    private static string BuildSuccessFactorsProbeUri(SuccessFactorsConfig config, IReadOnlyList<string> selectValues)
    {
        var entitySet = config.Query.EntitySet.Trim('/');
        var baseUrl = config.BaseUrl.TrimEnd('/');

        var queryString = new List<string>
        {
            "$format=json",
            "$top=1"
        };

        if (selectValues.Count > 0)
        {
            queryString.Add($"$select={Uri.EscapeDataString(string.Join(",", selectValues))}");
        }

        return $"{baseUrl}/{entitySet}?{string.Join("&", queryString)}";
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

        if (SuccessFactorsEntityNavigationAliases.TryGetValue(entityName, out var navigationName))
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

    private static ActiveDirectoryConnectionResult CreateLdapConnection(ActiveDirectoryConfig config, TimeSpan timeout)
        => ActiveDirectoryConnectionFactory.CreateConnectionWithTransport(config, NullLogger<DependencyHealthService>.Instance, timeout);

    private static Task<(string RequestedTransport, string EffectiveTransport, bool UsedFallback, int SuccessfulBaseCount, int SkippedBaseCount, string? SkippedBaseDetails)> ProbeActiveDirectoryTransportAsync(
        ActiveDirectoryConfig config,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var connectionResult = CreateLdapConnection(config, timeout);
            using var connection = connectionResult.Connection;

            var searchBases = GetActiveDirectorySearchBases(config);
            var successfulBaseCount = 0;
            var skippedBases = new List<string>();
            foreach (var searchBase in searchBases)
            {
                var (isSuccessful, failureReason) = ProbeActiveDirectorySearchBase(connection, config, searchBase, timeout);
                if (isSuccessful)
                {
                    successfulBaseCount++;
                    continue;
                }

                skippedBases.Add($"{searchBase} ({failureReason ?? "lookup validation failed"})");
            }

            return (
                connectionResult.RequestedTransport,
                connectionResult.EffectiveTransport,
                connectionResult.UsedFallback,
                successfulBaseCount,
                skippedBases.Count,
                skippedBases.Count == 0 ? null : $"Skipped search bases: {string.Join("; ", skippedBases)}");
        }, cancellationToken);
    }

    private static DependencyProbeResult BuildProbe(
        string dependency,
        string status,
        string summary,
        DateTimeOffset checkedAt,
        long durationMilliseconds,
        string? details = null,
        DateTimeOffset? observedAt = null,
        bool isStale = false)
    {
        return new DependencyProbeResult(
            Dependency: dependency,
            Status: status,
            Summary: summary,
            Details: details,
            CheckedAt: checkedAt,
            DurationMilliseconds: durationMilliseconds,
            ObservedAt: observedAt,
            IsStale: isStale);
    }

    private static DependencyProbeResult BuildSuccessFactorsTimeoutProbe(DateTimeOffset checkedAt, long durationMilliseconds)
    {
        return BuildProbe(
            "SuccessFactors",
            DependencyHealthStates.Unhealthy,
            $"Authenticated read probe timed out after {Math.Max(1, (int)SuccessFactorsTimeout.TotalSeconds)}s.",
            checkedAt,
            durationMilliseconds);
    }

    private static DependencyProbeResult BuildActiveDirectoryTimeoutProbe(
        string server,
        TimeSpan timeout,
        DateTimeOffset checkedAt,
        long durationMilliseconds,
        Exception? innerException = null)
    {
        var exception = ExternalSystemExceptionFactory.CreateActiveDirectoryTimeoutException(
            "health probe",
            server,
            timeout,
            innerException);

        return BuildProbe(
            "Active Directory",
            DependencyHealthStates.Unhealthy,
            $"LDAP bind or lookup probe timed out after {Math.Max(1, (int)timeout.TotalSeconds)}s.",
            checkedAt,
            durationMilliseconds,
            details: exception.Message);
    }

    private static IReadOnlyList<string> GetActiveDirectorySearchBases(ActiveDirectoryConfig config)
    {
        return new[] { config.DefaultActiveOu, config.PrehireOu, config.GraveyardOu, config.LeaveOu }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static (bool IsSuccessful, string? FailureReason) ProbeActiveDirectorySearchBase(
        LdapConnection connection,
        ActiveDirectoryConfig config,
        string searchBase,
        TimeSpan timeout)
    {
        var request = new SearchRequest(
            searchBase,
            "(&(objectCategory=person)(objectClass=user))",
            SearchScope.Subtree,
            BuildActiveDirectoryLookupProbeAttributes(config.IdentityAttribute));
        request.SizeLimit = 1;
        request.TimeLimit = timeout;

        try
        {
            var response = (SearchResponse)connection.SendRequest(request, timeout);
            return response.ResultCode switch
            {
                ResultCode.Success => (true, null),
                ResultCode.SizeLimitExceeded => (true, null),
                ResultCode.Referral => (false, "referral"),
                ResultCode.NoSuchObject => (false, "search base was not found"),
                _ => (false, string.IsNullOrWhiteSpace(response.ErrorMessage) ? response.ResultCode.ToString() : TrimForLog(response.ErrorMessage))
            };
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.SizeLimitExceeded)
        {
            return (true, null);
        }
        catch (DirectoryOperationException ex) when (IsReferralLikeResult(ex.Response?.ResultCode))
        {
            return (false, "referral");
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
        {
            return (false, "search base was not found");
        }
        catch (LdapException ex) when (IsReferralLikeMessage(ex.Message))
        {
            return (false, "referral");
        }
    }

    private static TimeSpan GetActiveDirectoryTimeout(ActiveDirectoryConfig config)
    {
        return TimeSpan.FromSeconds(config.OperationTimeoutSeconds > 0
            ? config.OperationTimeoutSeconds
            : DefaultActiveDirectoryTimeout.TotalSeconds);
    }

    private static string[] BuildActiveDirectoryLookupProbeAttributes(string identityAttribute)
    {
        return string.IsNullOrWhiteSpace(identityAttribute)
            ? ["distinguishedName"]
            : ["distinguishedName", identityAttribute];
    }

    private static bool IsReferralLikeResult(ResultCode? resultCode)
        => resultCode == ResultCode.Referral;

    private static bool IsReferralLikeMessage(string? message)
        => !string.IsNullOrWhiteSpace(message) &&
           message.Contains("referral", StringComparison.OrdinalIgnoreCase);

    private static string CalculateOverallStatus(IReadOnlyList<DependencyProbeResult> probes)
    {
        if (probes.Any(probe => string.Equals(probe.Status, DependencyHealthStates.Unhealthy, StringComparison.OrdinalIgnoreCase)))
        {
            return DependencyHealthStates.Unhealthy;
        }

        if (probes.Any(probe =>
            string.Equals(probe.Status, DependencyHealthStates.Degraded, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(probe.Status, DependencyHealthStates.Unknown, StringComparison.OrdinalIgnoreCase)))
        {
            return DependencyHealthStates.Degraded;
        }

        return DependencyHealthStates.Healthy;
    }

    private static bool IsProductionEnvironment()
    {
        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? "Production";
        return string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase);
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

    private static bool IsTimeoutLikeFailure(Exception exception)
    {
        return exception switch
        {
            OperationCanceledException => true,
            HttpRequestException { InnerException: OperationCanceledException } => true,
            _ when exception.InnerException is not null => IsTimeoutLikeFailure(exception.InnerException),
            _ => false
        };
    }
    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalSeconds < 60)
        {
            return $"{Math.Max(0, (int)age.TotalSeconds)}s";
        }

        return $"{Math.Max(0, (int)age.TotalMinutes)}m";
    }
}
