using SyncFactors.Contracts;
using SyncFactors.Domain;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SyncFactors.Infrastructure;

public sealed class SuccessFactorsWorkerSource(
    HttpClient httpClient,
    SyncFactorsConfigurationLoader configLoader,
    IDeltaSyncService deltaSyncService,
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
        var canonicalWorker = await TryResolveWorkerAsync(config, config.SuccessFactors.Query, workerId, cancellationToken);
        var worker = canonicalWorker is null
            ? null
            : await EnrichWorkerAsync(config, canonicalWorker, previewLookup: null, cancellationToken);

        if (worker is not null)
        {
            worker = NormalizeWorkerIdentity(worker, config.SuccessFactors.Query.IdentityField);
            logger.LogInformation("Resolved worker from SuccessFactors.");
            return worker;
        }

        logger.LogWarning("No worker was returned from SuccessFactors. Falling back to scaffold worker source.");

        return await fallbackSource.GetWorkerAsync(workerId, cancellationToken);
    }

    private async Task<WorkerSnapshot?> TryResolveWorkerAsync(
        SyncFactorsConfigDocument config,
        SuccessFactorsQueryConfig query,
        string workerId,
        CancellationToken cancellationToken,
        string? fallbackWorkerId = null)
    {
        var responsePayload = await ExecuteWorkerRequestAsync(config, query, workerId, cancellationToken);
        var requestUri = responsePayload.RequestUri;
        var rawBody = responsePayload.Body;
        try
        {
            using var document = JsonDocument.Parse(rawBody);
            return TryParseWorker(document.RootElement, config, query, fallbackWorkerId ?? workerId);
        }
        catch (JsonException ex)
        {
            logger.LogError(
                ex,
                "SuccessFactors returned non-JSON or invalid JSON. StatusCode={StatusCode} ContentType={ContentType}",
                responsePayload.StatusCode,
                responsePayload.ContentType);
            throw ExternalSystemExceptionFactory.CreateSuccessFactorsException(
                operation: "response parsing",
                endpoint: requestUri,
                summary: $"The API returned invalid JSON. Status={responsePayload.StatusCode}, ContentType={responsePayload.ContentType}, BodyPreview={TrimForLog(rawBody)}",
                innerException: ex);
        }
    }

    private static WorkerSnapshot NormalizeWorkerIdentity(WorkerSnapshot worker, string canonicalIdentityField)
    {
        var canonicalWorkerId = ResolveIdentityValue(canonicalIdentityField, worker);
        return string.IsNullOrWhiteSpace(canonicalWorkerId) || string.Equals(canonicalWorkerId, worker.WorkerId, StringComparison.Ordinal)
            ? worker
            : worker with { WorkerId = canonicalWorkerId };
    }

    private static string? ResolveIdentityValue(string identityField, WorkerSnapshot worker)
    {
        if (string.IsNullOrWhiteSpace(identityField))
        {
            return worker.WorkerId;
        }

        if (worker.Attributes.TryGetValue(identityField, out var attributeValue) &&
            !string.IsNullOrWhiteSpace(attributeValue))
        {
            return attributeValue;
        }

        if (identityField.Contains('/', StringComparison.Ordinal))
        {
            var leafPropertyName = identityField.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];
            if (worker.Attributes.TryGetValue(leafPropertyName, out var leafAttributeValue) &&
                !string.IsNullOrWhiteSpace(leafAttributeValue))
            {
                return leafAttributeValue;
            }
        }

        return string.Equals(identityField, "workerId", StringComparison.OrdinalIgnoreCase)
            ? worker.WorkerId
            : null;
    }

    public async IAsyncEnumerable<WorkerSnapshot> ListWorkersAsync(WorkerListingMode mode, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var config = configLoader.GetSyncConfig();
        var query = config.SuccessFactors.Query;
        var previewLookup = config.SuccessFactors.PreviewQuery is null
            ? null
            : await BuildPreviewLookupAsync(config, cancellationToken);
        var effectiveQuery = await BuildEffectiveListQueryAsync(query, mode, cancellationToken);
        var pageSize = Math.Max(1, effectiveQuery.PageSize);
        var firstRequestUri = BuildServerPagedListRequestUri(config, effectiveQuery, pageSize);
        SuccessFactorsResponsePayload? firstPayload = null;

        try
        {
            firstPayload = await ExecuteListRequestAsync(effectiveQuery, firstRequestUri, cancellationToken);
        }
        catch (InvalidOperationException ex) when (IsServerPagingCompatibilityFailure(ex, firstRequestUri))
        {
            logger.LogWarning(
                "SuccessFactors rejected server-side pagination parameters. Falling back to legacy offset paging. EntitySet={EntitySet} PageSize={PageSize}",
                effectiveQuery.EntitySet,
                pageSize);
            firstPayload = null;
        }

        if (firstPayload is not null)
        {
            await foreach (var worker in ListWorkersServerPagedAsync(config, effectiveQuery, firstPayload, pageSize, previewLookup, cancellationToken))
            {
                yield return worker;
            }

            yield break;
        }

        await foreach (var worker in ListWorkersLegacyPagedAsync(config, effectiveQuery, pageSize, previewLookup, cancellationToken))
        {
            yield return worker;
        }
    }

    private async Task<SuccessFactorsQueryConfig> BuildEffectiveListQueryAsync(
        SuccessFactorsQueryConfig query,
        WorkerListingMode mode,
        CancellationToken cancellationToken)
    {
        if (mode != WorkerListingMode.DeltaPreferred)
        {
            return query;
        }

        var deltaWindow = await deltaSyncService.GetWindowAsync(cancellationToken);
        if (!deltaWindow.Enabled)
        {
            return query;
        }

        if (!deltaWindow.HasCheckpoint || string.IsNullOrWhiteSpace(deltaWindow.Filter))
        {
            logger.LogInformation(
                "Listing workers with a full scan because no delta checkpoint is available yet. EntitySet={EntitySet} DeltaField={DeltaField}",
                query.EntitySet,
                deltaWindow.DeltaField);
            return query;
        }

        logger.LogInformation(
            "Listing workers using SuccessFactors delta sync. EntitySet={EntitySet} DeltaField={DeltaField} CheckpointUtc={CheckpointUtc} EffectiveSinceUtc={EffectiveSinceUtc}",
            query.EntitySet,
            deltaWindow.DeltaField,
            deltaWindow.CheckpointUtc,
            deltaWindow.EffectiveSinceUtc);
        var populationFilter = BuildListFilter(query);
        return query with
        {
            BaseFilter = CombineFilters(populationFilter, deltaWindow.Filter),
            InactiveRetentionDays = null
        };
    }

    private async IAsyncEnumerable<WorkerSnapshot> ListWorkersServerPagedAsync(
        SyncFactorsConfigDocument config,
        SuccessFactorsQueryConfig query,
        SuccessFactorsResponsePayload initialPayload,
        int pageSize,
        IReadOnlyDictionary<string, WorkerSnapshot>? previewLookup,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = initialPayload;
        while (true)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(payload.Body);
            }
            catch (JsonException ex)
            {
                logger.LogError(
                    ex,
                    "SuccessFactors returned non-JSON or invalid JSON while listing workers. ContentType={ContentType} Uri={Uri} PageSize={PageSize} BodyPreview={BodyPreview}",
                    payload.ContentType,
                    payload.RequestUri,
                    pageSize,
                    TrimForLog(payload.Body));
                throw ExternalSystemExceptionFactory.CreateSuccessFactorsException(
                    operation: "worker list response parsing",
                    endpoint: payload.RequestUri,
                    summary: $"The API returned invalid JSON. Status={payload.StatusCode}, ContentType={payload.ContentType}, BodyPreview={TrimForLog(payload.Body)}",
                    innerException: ex);
            }

            WorkerSnapshot[] workers;
            string? nextRequestUri;
            using (document)
            {
                workers = ExtractWorkerArray(document.RootElement)
                    .Select(worker => TryParseWorkerElement(worker, config, query))
                    .Where(worker => worker is not null)
                    .Select(worker => worker!)
                    .ToArray();
                nextRequestUri = TryGetNextPageRequestUri(document.RootElement, payload.RequestUri);
            }

            if (workers.Length == 0)
            {
                yield break;
            }

            foreach (var worker in workers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return await EnrichWorkerAsync(config, worker, previewLookup, cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(nextRequestUri))
            {
                yield break;
            }

            payload = await ExecuteListRequestAsync(query, nextRequestUri, cancellationToken);
        }
    }

    private async IAsyncEnumerable<WorkerSnapshot> ListWorkersLegacyPagedAsync(
        SyncFactorsConfigDocument config,
        SuccessFactorsQueryConfig query,
        int pageSize,
        IReadOnlyDictionary<string, WorkerSnapshot>? previewLookup,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var skip = 0;
        while (true)
        {
            var requestUri = BuildLegacyListRequestUri(config, query, skip, pageSize);
            var payload = await ExecuteListRequestAsync(query, requestUri, cancellationToken);
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(payload.Body);
            }
            catch (JsonException ex)
            {
                logger.LogError(
                    ex,
                    "SuccessFactors returned non-JSON or invalid JSON while listing workers. ContentType={ContentType} Uri={Uri} Skip={Skip} Top={Top} BodyPreview={BodyPreview}",
                    payload.ContentType,
                    payload.RequestUri,
                    skip,
                    pageSize,
                    TrimForLog(payload.Body));
                throw ExternalSystemExceptionFactory.CreateSuccessFactorsException(
                    operation: "worker list response parsing",
                    endpoint: payload.RequestUri,
                    summary: $"The API returned invalid JSON. Status={payload.StatusCode}, ContentType={payload.ContentType}, BodyPreview={TrimForLog(payload.Body)}",
                    innerException: ex);
            }

            WorkerSnapshot[] workers;
            using (document)
            {
                workers = ExtractWorkerArray(document.RootElement)
                    .Select(worker => TryParseWorkerElement(worker, config, query))
                    .Where(worker => worker is not null)
                    .Select(worker => worker!)
                    .ToArray();
            }

            if (workers.Length == 0)
            {
                yield break;
            }

            foreach (var worker in workers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return await EnrichWorkerAsync(config, worker, previewLookup, cancellationToken);
            }

            if (workers.Length < pageSize)
            {
                yield break;
            }

            skip += workers.Length;
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
        logger.LogDebug("Using basic authentication for SuccessFactors request.");
                break;

            case "oauth" when auth.OAuth is not null:
                var accessToken = await GetOAuthTokenAsync(auth.OAuth, cancellationToken);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                logger.LogDebug("Using OAuth bearer authentication for SuccessFactors request.");
                break;
        }
    }

    private async Task<WorkerSnapshot> EnrichWorkerAsync(
        SyncFactorsConfigDocument config,
        WorkerSnapshot canonicalWorker,
        IReadOnlyDictionary<string, WorkerSnapshot>? previewLookup,
        CancellationToken cancellationToken)
    {
        var previewQuery = config.SuccessFactors.PreviewQuery;
        if (previewQuery is null)
        {
            return canonicalWorker;
        }

        if (previewLookup is not null)
        {
            var matchedPreview = TryMatchPreviewWorker(canonicalWorker, previewLookup, previewQuery.IdentityField);
            return matchedPreview is null
                ? canonicalWorker
                : MergeWorkerSnapshots(canonicalWorker, matchedPreview);
        }

        var previewIdentity = ResolveIdentityValue(previewQuery.IdentityField, canonicalWorker) ?? canonicalWorker.WorkerId;
        if (string.IsNullOrWhiteSpace(previewIdentity))
        {
            return canonicalWorker;
        }

        var previewWorker = await TryResolveWorkerAsync(
            config,
            previewQuery,
            previewIdentity,
            cancellationToken,
            canonicalWorker.WorkerId);

        if (previewWorker is not null)
        {
            return MergeWorkerSnapshots(canonicalWorker, previewWorker);
        }

        var fallbackPreviewLookup = await BuildPreviewLookupAsync(config, cancellationToken);
        var fallbackPreview = TryMatchPreviewWorker(canonicalWorker, fallbackPreviewLookup, previewQuery.IdentityField);
        return fallbackPreview is null
            ? canonicalWorker
            : MergeWorkerSnapshots(canonicalWorker, fallbackPreview);
    }

    private static string? GetAttributeValue(WorkerSnapshot worker, string attributeName)
    {
        return worker.Attributes.TryGetValue(attributeName, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static WorkerSnapshot MergeWorkerSnapshots(WorkerSnapshot canonicalWorker, WorkerSnapshot previewWorker)
    {
        var attributes = new Dictionary<string, string?>(canonicalWorker.Attributes, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in previewWorker.Attributes)
        {
            attributes[pair.Key] = pair.Value;
        }

        return canonicalWorker with
        {
            PreferredName = MergePreferredValue(canonicalWorker.PreferredName, previewWorker.PreferredName, "Unknown"),
            LastName = MergePreferredValue(canonicalWorker.LastName, previewWorker.LastName, "Worker"),
            Department = MergePreferredValue(canonicalWorker.Department, previewWorker.Department, "Unknown"),
            Attributes = attributes
        };
    }

    private static string MergePreferredValue(string canonicalValue, string previewValue, string previewFallback)
    {
        return string.IsNullOrWhiteSpace(previewValue) || string.Equals(previewValue, previewFallback, StringComparison.OrdinalIgnoreCase)
            ? canonicalValue
            : previewValue;
    }

    private async Task<IReadOnlyDictionary<string, WorkerSnapshot>> BuildPreviewLookupAsync(
        SyncFactorsConfigDocument config,
        CancellationToken cancellationToken)
    {
        var previewQuery = config.SuccessFactors.PreviewQuery;
        if (previewQuery is null)
        {
            return new Dictionary<string, WorkerSnapshot>(StringComparer.OrdinalIgnoreCase);
        }

        var workers = await LoadWorkersForQueryAsync(config, previewQuery, cancellationToken);
        var lookup = new Dictionary<string, WorkerSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var worker in workers)
        {
            AddPreviewLookupEntry(lookup, worker.WorkerId, worker);
            AddPreviewLookupEntry(lookup, GetAttributeValue(worker, "userId"), worker);
            AddPreviewLookupEntry(lookup, GetAttributeValue(worker, "personIdExternal"), worker);
        }

        return lookup;
    }

    private async Task<IReadOnlyList<WorkerSnapshot>> LoadWorkersForQueryAsync(
        SyncFactorsConfigDocument config,
        SuccessFactorsQueryConfig query,
        CancellationToken cancellationToken)
    {
        var pageSize = Math.Max(1, query.PageSize);
        var firstRequestUri = BuildServerPagedListRequestUri(config, query, pageSize);
        SuccessFactorsResponsePayload? firstPayload = null;

        try
        {
            firstPayload = await ExecuteListRequestAsync(query, firstRequestUri, cancellationToken);
        }
        catch (InvalidOperationException ex) when (IsServerPagingCompatibilityFailure(ex, firstRequestUri))
        {
            logger.LogWarning(
                "SuccessFactors rejected server-side pagination parameters while loading preview lookup. Falling back to legacy offset paging. EntitySet={EntitySet} PageSize={PageSize}",
                query.EntitySet,
                pageSize);
            firstPayload = null;
        }

        var workers = new List<WorkerSnapshot>();
        if (firstPayload is not null)
        {
            await LoadWorkersServerPagedAsync(config, query, firstPayload, workers, cancellationToken);
            return workers;
        }

        await LoadWorkersLegacyPagedAsync(config, query, pageSize, workers, cancellationToken);
        return workers;
    }

    private async Task LoadWorkersServerPagedAsync(
        SyncFactorsConfigDocument config,
        SuccessFactorsQueryConfig query,
        SuccessFactorsResponsePayload initialPayload,
        List<WorkerSnapshot> workers,
        CancellationToken cancellationToken)
    {
        var payload = initialPayload;
        while (true)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(payload.Body);
            }
            catch (JsonException ex)
            {
                logger.LogError(
                    ex,
                    "SuccessFactors returned non-JSON or invalid JSON while loading preview lookup. ContentType={ContentType} Uri={Uri} BodyPreview={BodyPreview}",
                    payload.ContentType,
                    payload.RequestUri,
                    TrimForLog(payload.Body));
                throw ExternalSystemExceptionFactory.CreateSuccessFactorsException(
                    operation: "preview lookup response parsing",
                    endpoint: payload.RequestUri,
                    summary: $"The API returned invalid JSON. Status={payload.StatusCode}, ContentType={payload.ContentType}, BodyPreview={TrimForLog(payload.Body)}",
                    innerException: ex);
            }

            string? nextRequestUri;
            using (document)
            {
                foreach (var worker in ExtractWorkerArray(document.RootElement)
                             .Select(item => TryParseWorkerElement(item, config, query))
                             .Where(item => item is not null)
                             .Select(item => item!))
                {
                    workers.Add(worker);
                }

                nextRequestUri = TryGetNextPageRequestUri(document.RootElement, payload.RequestUri);
            }

            if (string.IsNullOrWhiteSpace(nextRequestUri))
            {
                return;
            }

            payload = await ExecuteListRequestAsync(query, nextRequestUri, cancellationToken);
        }
    }

    private async Task LoadWorkersLegacyPagedAsync(
        SyncFactorsConfigDocument config,
        SuccessFactorsQueryConfig query,
        int pageSize,
        List<WorkerSnapshot> workers,
        CancellationToken cancellationToken)
    {
        var skip = 0;
        while (true)
        {
            var requestUri = BuildLegacyListRequestUri(config, query, skip, pageSize);
            var payload = await ExecuteListRequestAsync(query, requestUri, cancellationToken);
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(payload.Body);
            }
            catch (JsonException ex)
            {
                logger.LogError(
                    ex,
                    "SuccessFactors returned non-JSON or invalid JSON while loading preview lookup. ContentType={ContentType} Uri={Uri} Skip={Skip} Top={Top} BodyPreview={BodyPreview}",
                    payload.ContentType,
                    payload.RequestUri,
                    skip,
                    pageSize,
                    TrimForLog(payload.Body));
                throw ExternalSystemExceptionFactory.CreateSuccessFactorsException(
                    operation: "preview lookup response parsing",
                    endpoint: payload.RequestUri,
                    summary: $"The API returned invalid JSON. Status={payload.StatusCode}, ContentType={payload.ContentType}, BodyPreview={TrimForLog(payload.Body)}",
                    innerException: ex);
            }

            WorkerSnapshot[] parsedWorkers;
            using (document)
            {
                parsedWorkers = ExtractWorkerArray(document.RootElement)
                    .Select(item => TryParseWorkerElement(item, config, query))
                    .Where(item => item is not null)
                    .Select(item => item!)
                    .ToArray();
            }

            if (parsedWorkers.Length == 0)
            {
                return;
            }

            workers.AddRange(parsedWorkers);

            if (parsedWorkers.Length < pageSize)
            {
                return;
            }

            skip += parsedWorkers.Length;
        }
    }

    private static void AddPreviewLookupEntry(
        IDictionary<string, WorkerSnapshot> lookup,
        string? key,
        WorkerSnapshot worker)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (lookup.TryGetValue(key, out var existing))
        {
            if (CountPopulatedAttributes(worker) > CountPopulatedAttributes(existing))
            {
                lookup[key] = worker;
            }

            return;
        }

        lookup[key] = worker;
    }

    private static WorkerSnapshot? TryMatchPreviewWorker(
        WorkerSnapshot canonicalWorker,
        IReadOnlyDictionary<string, WorkerSnapshot> previewLookup,
        string previewIdentityField)
    {
        foreach (var candidate in ResolvePreviewMatchCandidates(canonicalWorker, previewIdentityField))
        {
            if (previewLookup.TryGetValue(candidate, out var matchedPreview))
            {
                return matchedPreview;
            }
        }

        return null;
    }

    private static IEnumerable<string> ResolvePreviewMatchCandidates(WorkerSnapshot worker, string previewIdentityField)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in new[]
                 {
                     ResolveIdentityValue(previewIdentityField, worker),
                     worker.WorkerId,
                     GetAttributeValue(worker, "userId"),
                     GetAttributeValue(worker, "personIdExternal")
                 })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static int CountPopulatedAttributes(WorkerSnapshot worker)
    {
        return worker.Attributes.Count(pair => !string.IsNullOrWhiteSpace(pair.Value));
    }

    private async Task<string> GetOAuthTokenAsync(SuccessFactorsOAuthConfig oauth, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, oauth.TokenUrl);
        request.Content = new FormUrlEncodedContent(BuildTokenForm(oauth));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
        logger.LogDebug("Requesting SuccessFactors OAuth token.");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "SuccessFactors OAuth token request failed. StatusCode={StatusCode} ContentType={ContentType}",
                (int)response.StatusCode,
                response.Content.Headers.ContentType?.MediaType ?? "(none)");

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

        return LogSafety.SingleLine(value);
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
                "Fetching worker preview data from SuccessFactors. EntitySet={EntitySet} AuthMode={AuthMode} SelectedFieldCount={SelectedFieldCount}",
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
                    "SuccessFactors rejected a configured property path. Retrying without the rejected field.");
                continue;
            }

            logger.LogError(
                "SuccessFactors request failed. StatusCode={StatusCode} ContentType={ContentType}",
                (int)response.StatusCode,
                response.Content.Headers.ContentType?.MediaType ?? "(none)");

            throw CreateDetailedSuccessFactorsException(
                messagePrefix: "SuccessFactors request failed.",
                response: response,
                requestUri: requestUri,
                body: body,
                query: activeQuery);
        }
    }

    private async Task<SuccessFactorsResponsePayload> ExecuteWorkersRequestAsync(
        SyncFactorsConfigDocument config,
        SuccessFactorsQueryConfig query,
        CancellationToken cancellationToken)
    {
        var requestUri = BuildRequestUri(config, query, workerId: null);

        logger.LogInformation(
            "Fetching full sync data from SuccessFactors. EntitySet={EntitySet} AuthMode={AuthMode} SelectedFieldCount={SelectedFieldCount}",
            query.EntitySet,
            config.SuccessFactors.Auth.Mode,
            query.Select.Count);

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
        AddTracingHeaders(request, "full-sync");
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

        logger.LogError(
            "SuccessFactors full sync request failed. StatusCode={StatusCode} ContentType={ContentType}",
            (int)response.StatusCode,
            response.Content.Headers.ContentType?.MediaType ?? "(none)");

        throw CreateDetailedSuccessFactorsException(
            messagePrefix: "SuccessFactors full sync request failed.",
            response: response,
            requestUri: requestUri,
            body: body,
            query: query);
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

    private static string BuildRequestUri(SyncFactorsConfigDocument config, SuccessFactorsQueryConfig query, string? workerId)
    {
        var baseUrl = config.SuccessFactors.BaseUrl.TrimEnd('/');
        var relativePath = $"{baseUrl}/{query.EntitySet}";
        var parts = new List<string>
        {
            "$format=json",
            $"$select={Uri.EscapeDataString(string.Join(",", query.Select))}",
        };

        if (!string.IsNullOrWhiteSpace(workerId))
        {
            parts.Insert(1, $"$filter={Uri.EscapeDataString($"{query.IdentityField} eq '{workerId.Replace("'", "''")}'")}");
        }

        if (!string.IsNullOrWhiteSpace(query.OrderBy))
        {
            parts.Add($"$orderby={Uri.EscapeDataString(query.OrderBy)}");
        }

        if (query.Expand.Count > 0)
        {
            parts.Add($"$expand={Uri.EscapeDataString(string.Join(",", query.Expand))}");
        }

        if (!string.IsNullOrWhiteSpace(query.AsOfDate))
        {
            parts.Add($"asOfDate={Uri.EscapeDataString(query.AsOfDate)}");
        }

        return $"{relativePath}?{string.Join("&", parts)}";
    }

    private static bool IsServerPagingCompatibilityFailure(InvalidOperationException exception, string requestUri)
    {
        return exception.Message.Contains("Status=400", StringComparison.Ordinal) &&
               (requestUri.Contains("customPageSize=", StringComparison.OrdinalIgnoreCase) ||
                requestUri.Contains("paging=snapshot", StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildServerPagedListRequestUri(SyncFactorsConfigDocument config, SuccessFactorsQueryConfig query, int pageSize)
    {
        var baseUrl = config.SuccessFactors.BaseUrl.TrimEnd('/');
        var relativePath = $"{baseUrl}/{query.EntitySet}";
        var parts = new List<string>
        {
            "$format=json",
            $"customPageSize={pageSize}",
            "paging=snapshot",
            $"$select={Uri.EscapeDataString(string.Join(",", query.Select))}"
        };

        var listFilter = BuildListFilter(query);
        if (!string.IsNullOrWhiteSpace(listFilter))
        {
            parts.Add($"$filter={Uri.EscapeDataString(listFilter)}");
        }

        if (!string.IsNullOrWhiteSpace(query.OrderBy))
        {
            parts.Add($"$orderby={Uri.EscapeDataString(query.OrderBy)}");
        }

        if (query.Expand.Count > 0)
        {
            parts.Add($"$expand={Uri.EscapeDataString(string.Join(",", query.Expand))}");
        }

        if (!string.IsNullOrWhiteSpace(query.AsOfDate))
        {
            parts.Add($"asOfDate={Uri.EscapeDataString(query.AsOfDate)}");
        }

        return $"{relativePath}?{string.Join("&", parts)}";
    }

    private static string BuildLegacyListRequestUri(SyncFactorsConfigDocument config, SuccessFactorsQueryConfig query, int skip, int top)
    {
        var baseUrl = config.SuccessFactors.BaseUrl.TrimEnd('/');
        var relativePath = $"{baseUrl}/{query.EntitySet}";
        var parts = new List<string>
        {
            "$format=json",
            $"$top={top}",
            $"$skip={skip}",
            $"$select={Uri.EscapeDataString(string.Join(",", query.Select))}"
        };

        var listFilter = BuildListFilter(query);
        if (!string.IsNullOrWhiteSpace(listFilter))
        {
            parts.Add($"$filter={Uri.EscapeDataString(listFilter)}");
        }

        if (!string.IsNullOrWhiteSpace(query.OrderBy))
        {
            parts.Add($"$orderby={Uri.EscapeDataString(query.OrderBy)}");
        }

        if (query.Expand.Count > 0)
        {
            parts.Add($"$expand={Uri.EscapeDataString(string.Join(",", query.Expand))}");
        }

        if (!string.IsNullOrWhiteSpace(query.AsOfDate))
        {
            parts.Add($"asOfDate={Uri.EscapeDataString(query.AsOfDate)}");
        }

        return $"{relativePath}?{string.Join("&", parts)}";
    }

    private static string? TryGetNextPageRequestUri(JsonElement root, string requestUri)
    {
        string? next = null;

        if (root.TryGetProperty("d", out var d) &&
            d.ValueKind == JsonValueKind.Object &&
            d.TryGetProperty("__next", out var nextProperty) &&
            nextProperty.ValueKind == JsonValueKind.String)
        {
            next = nextProperty.GetString();
        }
        else if (root.TryGetProperty("@odata.nextLink", out var odataNextProperty) &&
                 odataNextProperty.ValueKind == JsonValueKind.String)
        {
            next = odataNextProperty.GetString();
        }
        else if (root.TryGetProperty("odata.nextLink", out var legacyNextProperty) &&
                 legacyNextProperty.ValueKind == JsonValueKind.String)
        {
            next = legacyNextProperty.GetString();
        }

        if (string.IsNullOrWhiteSpace(next))
        {
            return null;
        }

        return Uri.TryCreate(next, UriKind.Absolute, out var absoluteUri)
            ? absoluteUri.ToString()
            : new Uri(new Uri(requestUri), next).ToString();
    }

    private static string? BuildListFilter(SuccessFactorsQueryConfig query)
    {
        var baseFilter = string.IsNullOrWhiteSpace(query.BaseFilter)
            ? null
            : query.BaseFilter.Trim();

        if (query.InactiveRetentionDays is null)
        {
            return baseFilter;
        }

        var cutoff = DateTime.UtcNow.Date.AddDays(-query.InactiveRetentionDays.Value);
        var retentionClause = BuildInactiveRetentionClause(query, cutoff);

        return string.IsNullOrWhiteSpace(baseFilter)
            ? retentionClause
            : $"({baseFilter}) or ({retentionClause})";
    }

    private static string BuildInactiveRetentionClause(SuccessFactorsQueryConfig query, DateTime cutoff)
    {
        var statusField = string.IsNullOrWhiteSpace(query.InactiveStatusField)
            ? "emplStatus"
            : query.InactiveStatusField.Trim();
        var dateField = string.IsNullOrWhiteSpace(query.InactiveDateField)
            ? "endDate"
            : query.InactiveDateField.Trim();
        var statusValues = query.InactiveStatusValues
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (statusValues.Length == 0)
        {
            statusValues = ["T"];
        }

        var escapedStatuses = statusValues
            .Select(value => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'")
            .ToArray();
        var statusClause = escapedStatuses.Length == 1
            ? $"{statusField} eq {escapedStatuses[0]}"
            : $"{statusField} in {string.Join(",", escapedStatuses)}";

        return $"{statusClause} and {dateField} ge datetime'{cutoff.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}'";
    }

    private static WorkerSnapshot? TryParseWorker(JsonElement root, SyncFactorsConfigDocument config, SuccessFactorsQueryConfig query, string workerId)
    {
        var worker = ExtractWorkerArray(root).FirstOrDefault();
        if (worker.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        return TryParseWorkerElement(worker, config, query, workerId);
    }

    private static WorkerSnapshot? TryParseWorkerElement(JsonElement worker, SyncFactorsConfigDocument config, SuccessFactorsQueryConfig query, string? fallbackWorkerId = null)
    {
        var workerId = GetStringByPath(worker, query.IdentityField) ?? fallbackWorkerId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(workerId))
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
            WorkerId: GetStringByPath(worker, query.IdentityField) ?? workerId,
            PreferredName: preferredName,
            LastName: lastName,
            Department: department,
            TargetOu: config.Ad.DefaultActiveOu,
            IsPrehire: IsPrehire(startDate),
            Attributes: BuildAttributes(worker, personalInfo, employment, jobInfo));
    }

    private static string CombineFilters(string? baseFilter, string deltaFilter)
    {
        if (string.IsNullOrWhiteSpace(baseFilter))
        {
            return deltaFilter;
        }

        return $"({baseFilter}) and ({deltaFilter})";
    }

    private async Task<SuccessFactorsResponsePayload> ExecuteListRequestAsync(
        SuccessFactorsQueryConfig query,
        string requestUri,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
        AddTracingHeaders(request, "list-page");
        await ApplyAuthenticationAsync(request, configLoader.GetSyncConfig().SuccessFactors.Auth, cancellationToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            var pagingMode = TryGetHeaderValue(response.Headers, "X-SF-Paging");
            logger.LogInformation(
                "SuccessFactors list page returned successfully. PagingMode={PagingMode}",
                string.IsNullOrWhiteSpace(pagingMode) ? "(none)" : pagingMode);
            return new SuccessFactorsResponsePayload(
                RequestUri: requestUri,
                Body: body,
                StatusCode: (int)response.StatusCode,
                ContentType: response.Content.Headers.ContentType?.MediaType ?? "(none)",
                PagingMode: pagingMode);
        }

        throw CreateDetailedSuccessFactorsException(
            messagePrefix: "SuccessFactors request failed.",
            response: response,
            requestUri: requestUri,
            body: body,
            query: query);
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

        return GetScalarString(property);
    }

    private static string? GetStringByPath(JsonElement element, string propertyPath)
    {
        if (string.IsNullOrWhiteSpace(propertyPath))
        {
            return null;
        }

        var directMatch = GetString(element, propertyPath);
        if (!string.IsNullOrWhiteSpace(directMatch))
        {
            return directMatch;
        }

        if (!propertyPath.Contains('/', StringComparison.Ordinal))
        {
            return null;
        }

        var current = element;
        foreach (var segment in propertyPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var property))
            {
                return null;
            }

            if (property.ValueKind == JsonValueKind.Object &&
                property.TryGetProperty("results", out var results) &&
                results.ValueKind == JsonValueKind.Array)
            {
                var firstResult = results.EnumerateArray().FirstOrDefault();
                if (firstResult.ValueKind == JsonValueKind.Undefined)
                {
                    return null;
                }

                current = firstResult.Clone();
                continue;
            }

            current = property.Clone();
        }

        return GetScalarString(current);
    }

    private static string? GetScalarString(JsonElement property)
    {
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

    private static string? TryGetHeaderValue(HttpResponseHeaders headers, string headerName)
    {
        return headers.TryGetValues(headerName, out var values)
            ? values.FirstOrDefault()
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

    private static bool IsPrehire(string? startDate)
    {
        if (!SourceDateParser.TryParse(startDate, out var parsedStart))
        {
            return false;
        }

        return parsedStart.Date > DateTimeOffset.UtcNow.Date;
    }

    private static void AddTracingHeaders(HttpRequestMessage request, string workerId)
    {
        var correlationId = Guid.NewGuid().ToString("D");
        request.Headers.TryAddWithoutValidation("x-correlation-id", correlationId);
        request.Headers.TryAddWithoutValidation("X-SF-Correlation-Id", correlationId);
        request.Headers.TryAddWithoutValidation("X-SF-Process-Name", "SyncFactors.WorkerPreview");
        request.Headers.TryAddWithoutValidation("X-SF-Execution-Id", workerId);
    }

    private sealed record SuccessFactorsResponsePayload(string RequestUri, string Body, int StatusCode, string ContentType, string? PagingMode = null);
}
