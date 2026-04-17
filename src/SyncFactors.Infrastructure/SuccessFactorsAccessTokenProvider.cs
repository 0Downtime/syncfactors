using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SyncFactors.Domain;

namespace SyncFactors.Infrastructure;

public interface ISuccessFactorsAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(SuccessFactorsOAuthConfig oauth, CancellationToken cancellationToken);
}

public sealed class SuccessFactorsAccessTokenProvider : ISuccessFactorsAccessTokenProvider
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FallbackTokenLifetime = TimeSpan.FromMinutes(5);

    private readonly Func<HttpClient> _getHttpClient;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SuccessFactorsAccessTokenProvider> _logger;
    private readonly ConcurrentDictionary<TokenCacheKey, CachedToken> _tokens = new();
    private readonly ConcurrentDictionary<TokenCacheKey, SemaphoreSlim> _refreshLocks = new();

    public SuccessFactorsAccessTokenProvider(
        IHttpClientFactory httpClientFactory,
        TimeProvider timeProvider,
        ILogger<SuccessFactorsAccessTokenProvider> logger,
        string clientName = "SuccessFactorsAuth")
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _getHttpClient = () => httpClientFactory.CreateClient(clientName);
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public SuccessFactorsAccessTokenProvider(
        HttpClient httpClient,
        TimeProvider timeProvider,
        ILogger<SuccessFactorsAccessTokenProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _getHttpClient = () => httpClient;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<string> GetAccessTokenAsync(SuccessFactorsOAuthConfig oauth, CancellationToken cancellationToken)
    {
        var cacheKey = TokenCacheKey.FromConfig(oauth);
        if (TryGetValidToken(cacheKey, out var cachedToken))
        {
            _logger.LogDebug("Reusing cached SuccessFactors OAuth token. TokenUrl={TokenUrl}", oauth.TokenUrl);
            return cachedToken;
        }

        var refreshLock = _refreshLocks.GetOrAdd(cacheKey, static _ => new SemaphoreSlim(1, 1));
        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (TryGetValidToken(cacheKey, out cachedToken))
            {
                _logger.LogDebug("Reusing cached SuccessFactors OAuth token after refresh wait. TokenUrl={TokenUrl}", oauth.TokenUrl);
                return cachedToken;
            }

            var refreshedToken = await RequestAccessTokenAsync(oauth, cancellationToken);
            _tokens[cacheKey] = refreshedToken;
            return refreshedToken.AccessToken;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private bool TryGetValidToken(TokenCacheKey cacheKey, out string accessToken)
    {
        if (_tokens.TryGetValue(cacheKey, out var cachedToken) &&
            cachedToken.ExpiresAtUtc > _timeProvider.GetUtcNow().Add(RefreshSkew))
        {
            accessToken = cachedToken.AccessToken;
            return true;
        }

        accessToken = string.Empty;
        return false;
    }

    private async Task<CachedToken> RequestAccessTokenAsync(SuccessFactorsOAuthConfig oauth, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, oauth.TokenUrl);
        request.Content = new FormUrlEncodedContent(BuildTokenForm(oauth));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
        _logger.LogDebug("Requesting SuccessFactors OAuth token. TokenUrl={TokenUrl}", oauth.TokenUrl);

        using var response = await _getHttpClient().SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OAuth token request failed with HTTP {(int)response.StatusCode}: {TrimForLog(body)}");
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("access_token", out var accessTokenElement) ||
            accessTokenElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(accessTokenElement.GetString()))
        {
            throw new InvalidOperationException("OAuth token response did not contain an access_token.");
        }

        var accessToken = accessTokenElement.GetString()!;
        var expiresAtUtc = ResolveExpiry(document.RootElement);
        return new CachedToken(accessToken, expiresAtUtc);
    }

    private DateTimeOffset ResolveExpiry(JsonElement root)
    {
        if (root.TryGetProperty("expires_in", out var expiresInElement))
        {
            if (expiresInElement.ValueKind == JsonValueKind.Number &&
                expiresInElement.TryGetInt32(out var expiresInSeconds) &&
                expiresInSeconds > 0)
            {
                return _timeProvider.GetUtcNow().AddSeconds(expiresInSeconds);
            }

            if (expiresInElement.ValueKind == JsonValueKind.String &&
                int.TryParse(expiresInElement.GetString(), out expiresInSeconds) &&
                expiresInSeconds > 0)
            {
                return _timeProvider.GetUtcNow().AddSeconds(expiresInSeconds);
            }
        }

        _logger.LogWarning(
            "SuccessFactors OAuth token response omitted a valid expires_in value. Applying fallback lifetime of {FallbackTokenLifetime}.",
            FallbackTokenLifetime);
        return _timeProvider.GetUtcNow().Add(FallbackTokenLifetime);
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

    private static string TrimForLog(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(empty)";
        }

        return LogSafety.SingleLine(value);
    }

    private sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAtUtc);

    private readonly record struct TokenCacheKey(
        string TokenUrl,
        string ClientId,
        string ClientSecret,
        string CompanyId)
    {
        public static TokenCacheKey FromConfig(SuccessFactorsOAuthConfig oauth)
        {
            return new TokenCacheKey(
                oauth.TokenUrl,
                oauth.ClientId,
                oauth.ClientSecret,
                oauth.CompanyId ?? string.Empty);
        }
    }
}
