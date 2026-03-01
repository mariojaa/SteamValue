using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SteamValue.Configuration;

namespace SteamValue.Services
{
    /// <summary>
    /// HTTP client wrapper with rate limiting and retry logic for Steam API
    /// Implements best practices from Steam Web API documentation
    /// </summary>
    public class SteamHttpClient
    {
        private readonly HttpClient _httpClient;
        private readonly SteamApiConfig _config;
        private readonly ILogger<SteamHttpClient> _logger;

        // Rate limiters per API type
        private readonly SemaphoreSlim _webApiSemaphore = new(1, 1);
        private readonly SemaphoreSlim _marketApiSemaphore = new(1, 1);
        private readonly SemaphoreSlim _storeApiSemaphore;
        
        private DateTime _lastWebApiRequest = DateTime.MinValue;
        private DateTime _lastMarketApiRequest = DateTime.MinValue;
        private DateTime _lastStoreApiRequest = DateTime.MinValue;

        // Circuit breaker for Market API
        private int _consecutiveMarket429s = 0;
        private DateTime _marketCircuitOpenUntil = DateTime.MinValue;

        public SteamHttpClient(
            HttpClient httpClient,
            IOptions<SteamApiConfig> config,
            ILogger<SteamHttpClient> logger)
        {
            _httpClient = httpClient;
            _config = config.Value;
            _logger = logger;

            _storeApiSemaphore = new SemaphoreSlim(
                _config.RateLimits.StoreApiMaxConcurrency,
                _config.RateLimits.StoreApiMaxConcurrency);

            ConfigureHttpClient();
        }

        private void ConfigureHttpClient()
        {
            _httpClient.Timeout = TimeSpan.FromSeconds(_config.Timeouts.HttpClientTimeoutSeconds);
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
            _httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("pt-BR,pt;q=0.9,en-US;q=0.8,en;q=0.7");
        }

        /// <summary>
        /// Makes a GET request to Steam Web API endpoint (ISteamUser, IPlayerService, etc)
        /// </summary>
        public async Task<HttpResponseMessage> GetWebApiAsync(string url, CancellationToken cancellationToken = default)
        {
            return await ExecuteWithRateLimitAsync(
                url,
                ApiType.WebApi,
                cancellationToken);
        }

        /// <summary>
        /// Makes a GET request to Steam Market API endpoint (heavily rate limited)
        /// </summary>
        public async Task<HttpResponseMessage> GetMarketApiAsync(string url, CancellationToken cancellationToken = default)
        {
            return await ExecuteWithRateLimitAsync(
                url,
                ApiType.MarketApi,
                cancellationToken);
        }

        /// <summary>
        /// Makes a GET request to Steam Store API endpoint
        /// </summary>
        public async Task<HttpResponseMessage> GetStoreApiAsync(string url, CancellationToken cancellationToken = default)
        {
            return await ExecuteWithRateLimitAsync(
                url,
                ApiType.StoreApi,
                cancellationToken);
        }

        /// <summary>
        /// Makes a GET request to Community endpoints (inventory, etc)
        /// </summary>
        public async Task<HttpResponseMessage> GetCommunityApiAsync(string url, bool addCookies = true, CancellationToken cancellationToken = default)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            
            if (addCookies)
            {
                request.Headers.Add("Cookie", "Steam_Language=english; timezoneOffset=0,0");
                request.Headers.Add("Referer", "https://steamcommunity.com/");
                request.Headers.Add("Origin", "https://steamcommunity.com");
            }

            return await ExecuteWithRetryAsync(request, _config.RateLimits.MaxRetries, cancellationToken);
        }

        private async Task<HttpResponseMessage> ExecuteWithRateLimitAsync(
            string url,
            ApiType apiType,
            CancellationToken cancellationToken)
        {
            var semaphore = GetSemaphoreForApiType(apiType);
            var delay = GetDelayForApiType(apiType);
            var lastRequest = GetLastRequestTimeForApiType(apiType);

            await semaphore.WaitAsync(cancellationToken);
            try
            {
                // Enforce minimum delay between requests
                var elapsed = DateTime.UtcNow - lastRequest;
                if (elapsed < delay)
                {
                    var waitTime = delay - elapsed;
                    var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 200));
                    await Task.Delay(waitTime + jitter, cancellationToken);
                }

                // Market API circuit breaker check
                if (apiType == ApiType.MarketApi && _marketCircuitOpenUntil > DateTime.UtcNow)
                {
                    var circuitWait = _marketCircuitOpenUntil - DateTime.UtcNow;
                    _logger.LogWarning("Market API circuit breaker open. Waiting {Seconds}s", circuitWait.TotalSeconds);
                    await Task.Delay(circuitWait, cancellationToken);
                    _consecutiveMarket429s = 0;
                }

                UpdateLastRequestTime(apiType);

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                return await ExecuteWithRetryAsync(request, _config.RateLimits.MaxRetries, cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }
        }

        private async Task<HttpResponseMessage> ExecuteWithRetryAsync(
            HttpRequestMessage request,
            int maxRetries,
            CancellationToken cancellationToken)
        {
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    // Clone request for retry (can't reuse HttpRequestMessage)
                    var requestToSend = attempt == 0 ? request : CloneRequest(request);
                    var response = await _httpClient.SendAsync(requestToSend, cancellationToken);

                    // Handle 429 Too Many Requests
                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        var isMarketApi = request.RequestUri?.Host.Contains("steamcommunity.com") == true &&
                                         request.RequestUri?.AbsolutePath.Contains("/market/") == true;

                        if (isMarketApi)
                        {
                            _consecutiveMarket429s++;
                            
                            // Open circuit breaker after threshold
                            if (_consecutiveMarket429s >= _config.RateLimits.CircuitBreakerThreshold)
                            {
                                var cooldown = TimeSpan.FromSeconds(_config.RateLimits.CircuitBreakerCooldownSeconds);
                                _marketCircuitOpenUntil = DateTime.UtcNow.Add(cooldown);
                                _consecutiveMarket429s = 0;
                                
                                _logger.LogWarning("Market API circuit breaker activated. Cooldown: {Seconds}s", cooldown.TotalSeconds);
                                await Task.Delay(cooldown, cancellationToken);
                                continue;
                            }
                        }

                        if (attempt < maxRetries)
                        {
                            var waitSeconds = GetRetryAfterSeconds(response);
                            if (waitSeconds <= 0)
                                waitSeconds = Helpers.SteamHelpers.CalculateRetryDelay(attempt, _config.RateLimits.ExponentialBackoffBaseMs) / 1000;

                            _logger.LogWarning("Rate limited (429). Waiting {Seconds}s before retry {Attempt}/{Max}",
                                waitSeconds, attempt + 1, maxRetries);

                            await Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken);
                            continue;
                        }

                        return response;
                    }

                    // Handle 5xx errors (server-side)
                    if ((int)response.StatusCode >= 500 && (int)response.StatusCode < 600)
                    {
                        if (attempt < maxRetries)
                        {
                            var delay = Helpers.SteamHelpers.CalculateRetryDelay(attempt, 500);
                            _logger.LogWarning("Server error {StatusCode}. Retrying in {Ms}ms (Attempt {Attempt}/{Max})",
                                response.StatusCode, delay, attempt + 1, maxRetries);
                            
                            await Task.Delay(delay, cancellationToken);
                            continue;
                        }
                    }

                    // Success or non-retryable error
                    if (response.IsSuccessStatusCode)
                    {
                        // Reset circuit breaker on success
                        _consecutiveMarket429s = 0;
                    }

                    return response;
                }
                catch (TaskCanceledException) when (attempt < maxRetries)
                {
                    _logger.LogWarning("Request timeout. Retrying (Attempt {Attempt}/{Max})", attempt + 1, maxRetries);
                    await Task.Delay(Helpers.SteamHelpers.CalculateRetryDelay(attempt, 500), cancellationToken);
                }
                catch (HttpRequestException) when (attempt < maxRetries)
                {
                    _logger.LogWarning("Network error. Retrying (Attempt {Attempt}/{Max})", attempt + 1, maxRetries);
                    await Task.Delay(Helpers.SteamHelpers.CalculateRetryDelay(attempt, 1000), cancellationToken);
                }
            }

            throw new HttpRequestException($"Failed after {maxRetries} retries: {request.RequestUri}");
        }

        private static HttpRequestMessage CloneRequest(HttpRequestMessage original)
        {
            var clone = new HttpRequestMessage(original.Method, original.RequestUri);
            
            foreach (var header in original.Headers)
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

            return clone;
        }

        private static int GetRetryAfterSeconds(HttpResponseMessage response)
        {
            try
            {
                if (response.Headers.TryGetValues("Retry-After", out var values))
                {
                    var value = values.FirstOrDefault();
                    if (string.IsNullOrEmpty(value)) return 0;

                    // Try parse as seconds
                    if (int.TryParse(value, out var seconds))
                        return seconds;

                    // Try parse as HTTP date
                    if (DateTimeOffset.TryParse(value, out var dateTime))
                    {
                        var wait = (int)(dateTime - DateTimeOffset.UtcNow).TotalSeconds;
                        return Math.Max(wait, 1);
                    }
                }
            }
            catch { }

            return 0;
        }

        private SemaphoreSlim GetSemaphoreForApiType(ApiType apiType) => apiType switch
        {
            ApiType.WebApi => _webApiSemaphore,
            ApiType.MarketApi => _marketApiSemaphore,
            ApiType.StoreApi => _storeApiSemaphore,
            _ => _webApiSemaphore
        };

        private TimeSpan GetDelayForApiType(ApiType apiType) => apiType switch
        {
            ApiType.WebApi => TimeSpan.FromMilliseconds(_config.RateLimits.WebApiDelayMs),
            ApiType.MarketApi => TimeSpan.FromMilliseconds(_config.RateLimits.MarketApiDelayMs),
            ApiType.StoreApi => TimeSpan.FromMilliseconds(_config.RateLimits.StoreApiDelayMs),
            _ => TimeSpan.Zero
        };

        private DateTime GetLastRequestTimeForApiType(ApiType apiType) => apiType switch
        {
            ApiType.WebApi => _lastWebApiRequest,
            ApiType.MarketApi => _lastMarketApiRequest,
            ApiType.StoreApi => _lastStoreApiRequest,
            _ => DateTime.MinValue
        };

        private void UpdateLastRequestTime(ApiType apiType)
        {
            var now = DateTime.UtcNow;
            switch (apiType)
            {
                case ApiType.WebApi:
                    _lastWebApiRequest = now;
                    break;
                case ApiType.MarketApi:
                    _lastMarketApiRequest = now;
                    break;
                case ApiType.StoreApi:
                    _lastStoreApiRequest = now;
                    break;
            }
        }

        private enum ApiType
        {
            WebApi,
            MarketApi,
            StoreApi
        }
    }
}
