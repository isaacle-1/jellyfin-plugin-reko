using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Reko.Configuration;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.Reko.Services.Tmdb;

/// <summary>
/// Raised when TMDB cannot be reached or returns a status Reko cannot recover from.
/// </summary>
public sealed class TmdbException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TmdbException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public TmdbException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TmdbException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public TmdbException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A typed, cached, rate-limited client for the TMDB v3 API.
/// </summary>
/// <remarks>
/// The API key stays on the server: the browser only ever talks to <c>/Reko/*</c> on the Jellyfin
/// server itself. All methods funnel through <see cref="GetAsync{T}"/>, which applies the token bucket,
/// the TTL cache and 429 backoff in one place.
/// </remarks>
public sealed class TmdbClient
{
    private const string DefaultBaseAddress = "https://api.themoviedb.org/3/";
    private const string ImageBaseAddress = "https://image.tmdb.org/t/p/";

    /// <summary>
    /// The name of the <see cref="IHttpClientFactory"/> client used for TMDB calls.
    /// </summary>
    public const string HttpClientName = "Reko.Tmdb";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TtlCache<string, object> _cache = new();
    private readonly TmdbRateLimiter _rateLimiter = new(requestsPerSecond: 8, burst: 12);

    // Read on a synchronous path by the card factory, so these are plain volatile references rather
    // than a lock.
    private volatile Dictionary<int, string> _movieGenreCache = new();
    private volatile Dictionary<int, string> _tvGenreCache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="TmdbClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Factory used to create the HTTP client.</param>
    public TmdbClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Gets the default secure image base URL. Sizes are appended directly, for example <c>w342</c>
    /// then the file path.
    /// </summary>
    public static string DefaultImageBaseUrl => ImageBaseAddress;

    /// <summary>
    /// Gets the image base URL in effect, honouring the advanced mirror override.
    /// </summary>
    public string ImageBaseUrl => BuildImageBaseAddress();

    /// <summary>
    /// Gets a value indicating whether an API key is configured.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(GetApiKey());

    /// <summary>
    /// Gets the language tag used for text metadata.
    /// </summary>
    public string Language => string.IsNullOrWhiteSpace(GetLanguage()) ? "en-US" : GetLanguage();

    /// <summary>
    /// Gets the language tag used when selecting posters and logos.
    /// </summary>
    public string ImageLanguage
    {
        get
        {
            var configured = GetImageLanguage();
            return string.IsNullOrWhiteSpace(configured) ? Language : configured;
        }
    }

    /// <summary>
    /// Gets the last loaded TMDB movie genre map, or an empty map before the first load.
    /// </summary>
    /// <remarks>
    /// The card factory needs the genre maps on a synchronous path. Loading them is cheap after the
    /// first call because it goes through the TTL cache, so callers warm the maps with
    /// <see cref="GetMovieGenresAsync"/> and then read them here, rather than threading an await
    /// through every card-building call.
    /// </remarks>
    /// <returns>The genre map.</returns>
    public Dictionary<int, string> GetMovieGenresCached() => _movieGenreCache;

    /// <summary>
    /// Gets the last loaded TMDB TV genre map, or an empty map before the first load.
    /// </summary>
    /// <returns>The genre map.</returns>
    public Dictionary<int, string> GetTvGenresCached() => _tvGenreCache;

    /// <summary>
    /// Drops every cached TMDB payload. Called when the plugin configuration changes.
    /// </summary>
    public void Invalidate()
    {
        _cache.Clear();
    }

    /// <summary>
    /// Gets the TMDB movie genre id to name map.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The genre map.</returns>
    public Task<Dictionary<int, string>> GetMovieGenresAsync(CancellationToken cancellationToken)
        => GetGenreMapAsync("movie", cancellationToken);

    /// <summary>
    /// Gets the TMDB TV genre id to name map.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The genre map.</returns>
    public Task<Dictionary<int, string>> GetTvGenresAsync(CancellationToken cancellationToken)
        => GetGenreMapAsync("tv", cancellationToken);

    /// <summary>
    /// Gets a trending page. TMDB returns exactly 20 items and has no page parameter for trending,
    /// so this is always a single call.
    /// </summary>
    /// <param name="mediaType">One of <c>movie</c>, <c>tv</c> or <c>all</c>.</param>
    /// <param name="window">One of <c>day</c> or <c>week</c>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The trending list.</returns>
    public Task<TmdbPage<TmdbListItem>> GetTrendingAsync(string mediaType, string window, CancellationToken cancellationToken)
        => GetAsync<TmdbPage<TmdbListItem>>(
            $"trending/{mediaType}/{window}",
            Ttl("trending", 45),
            cancellationToken);

    /// <summary>
    /// Gets a discover page for movies.
    /// </summary>
    /// <param name="query">The discover query parameters.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The discover result.</returns>
    public Task<TmdbPage<TmdbListItem>> DiscoverMoviesAsync(IReadOnlyDictionary<string, string> query, CancellationToken cancellationToken)
        => GetAsync<TmdbPage<TmdbListItem>>(
            BuildPath("discover/movie", query),
            Ttl("discover", 180),
            cancellationToken);

    /// <summary>
    /// Gets a discover page for series.
    /// </summary>
    /// <param name="query">The discover query parameters.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The discover result.</returns>
    public Task<TmdbPage<TmdbListItem>> DiscoverTvAsync(IReadOnlyDictionary<string, string> query, CancellationToken cancellationToken)
        => GetAsync<TmdbPage<TmdbListItem>>(
            BuildPath("discover/tv", query),
            Ttl("discover", 180),
            cancellationToken);

    /// <summary>
    /// Gets a popular movies page.
    /// </summary>
    /// <param name="page">The 1-based page number.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The popular list.</returns>
    public Task<TmdbPage<TmdbListItem>> GetPopularMoviesAsync(int page, CancellationToken cancellationToken)
        => GetAsync<TmdbPage<TmdbListItem>>(
            $"movie/popular?page={page}",
            Ttl("popular", 180),
            cancellationToken);

    /// <summary>
    /// Gets a popular series page.
    /// </summary>
    /// <param name="page">The 1-based page number.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The popular list.</returns>
    public Task<TmdbPage<TmdbListItem>> GetPopularTvAsync(int page, CancellationToken cancellationToken)
        => GetAsync<TmdbPage<TmdbListItem>>(
            $"tv/popular?page={page}",
            Ttl("popular", 180),
            cancellationToken);

    /// <summary>
    /// Gets the series that are on the air now.
    /// </summary>
    /// <param name="page">The 1-based page number.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The on-the-air list.</returns>
    public Task<TmdbPage<TmdbListItem>> GetOnTheAirAsync(int page, CancellationToken cancellationToken)
        => GetAsync<TmdbPage<TmdbListItem>>(
            $"tv/on_the_air?page={page}",
            Ttl("airing", 120),
            cancellationToken);

    /// <summary>
    /// Gets a full movie detail payload with everything Reko's title page needs appended in one call.
    /// </summary>
    /// <param name="id">The TMDB movie id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The movie detail.</returns>
    public Task<TmdbMovieDetail> GetMovieDetailAsync(int id, CancellationToken cancellationToken)
    {
        const string Append = "credits,videos,images,recommendations,similar,release_dates,watch/providers,keywords";
        return GetAsync<TmdbMovieDetail>(
            string.Create(CultureInfo.InvariantCulture, $"movie/{id}?append_to_response={Append}&include_image_language={ImageLanguage}"),
            Ttl("detail", 720),
            cancellationToken);
    }

    /// <summary>
    /// Gets a full series detail payload with everything Reko's title page needs appended in one call.
    /// </summary>
    /// <param name="id">The TMDB series id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The series detail.</returns>
    public Task<TmdbSeriesDetail> GetSeriesDetailAsync(int id, CancellationToken cancellationToken)
    {
        const string Append = "aggregate_credits,videos,images,recommendations,similar,content_ratings,watch/providers,keywords";
        return GetAsync<TmdbSeriesDetail>(
            string.Create(CultureInfo.InvariantCulture, $"tv/{id}?append_to_response={Append}&include_image_language={ImageLanguage}"),
            Ttl("detail", 720),
            cancellationToken);
    }

    /// <summary>
    /// Gets the episode list for a season.
    /// </summary>
    /// <param name="seriesId">The TMDB series id.</param>
    /// <param name="seasonNumber">The season number. Zero is the specials season.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The season detail.</returns>
    public Task<TmdbSeasonDetail> GetSeasonAsync(int seriesId, int seasonNumber, CancellationToken cancellationToken)
        => GetAsync<TmdbSeasonDetail>(
            string.Create(CultureInfo.InvariantCulture, $"tv/{seriesId}/season/{seasonNumber}"),
            Ttl("season", 720),
            cancellationToken);

    /// <summary>
    /// Gets a collection and its parts.
    /// </summary>
    /// <param name="id">The TMDB collection id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The collection detail.</returns>
    public Task<TmdbCollectionDetail> GetCollectionAsync(int id, CancellationToken cancellationToken)
        => GetAsync<TmdbCollectionDetail>(
            string.Create(CultureInfo.InvariantCulture, $"collection/{id}"),
            Ttl("collection", 1440),
            cancellationToken);

    /// <summary>
    /// Gets a person and their combined credits.
    /// </summary>
    /// <param name="id">The TMDB person id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The person detail.</returns>
    public Task<TmdbPersonDetail> GetPersonAsync(int id, CancellationToken cancellationToken)
        => GetAsync<TmdbPersonDetail>(
            string.Create(CultureInfo.InvariantCulture, $"person/{id}?append_to_response=combined_credits,images"),
            Ttl("person", 1440),
            cancellationToken);

    /// <summary>
    /// Runs a multi search across movies, series and people.
    /// </summary>
    /// <param name="query">The search text.</param>
    /// <param name="page">The 1-based page number.</param>
    /// <param name="includeAdult">Whether adult results are included.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The search results.</returns>
    public Task<TmdbMultiSearchResult> SearchAsync(string query, int page, bool includeAdult, CancellationToken cancellationToken)
        => GetAsync<TmdbMultiSearchResult>(
            $"search/multi?query={Uri.EscapeDataString(query)}&page={page}&include_adult={(includeAdult ? "true" : "false")}",
            Ttl("search", 15),
            cancellationToken);

    /// <summary>
    /// Gets recommendations for a title, used by the "more like this" rail on a title page.
    /// </summary>
    /// <param name="mediaType">One of <c>movie</c> or <c>tv</c>.</param>
    /// <param name="id">The TMDB id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The recommendation list.</returns>
    public Task<TmdbPage<TmdbListItem>> GetRecommendationsAsync(string mediaType, int id, CancellationToken cancellationToken)
        => GetAsync<TmdbPage<TmdbListItem>>(
            string.Create(CultureInfo.InvariantCulture, $"{mediaType}/{id}/recommendations"),
            Ttl("recommend", 720),
            cancellationToken);

    private static string BuildPath(string path, IReadOnlyDictionary<string, string> query)
    {
        var builder = new StringBuilder(path);
        var first = true;
        foreach (var pair in query)
        {
            if (string.IsNullOrWhiteSpace(pair.Value))
            {
                continue;
            }

            builder.Append(first ? '?' : '&');
            builder.Append(Uri.EscapeDataString(pair.Key));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(pair.Value));
            first = false;
        }

        return builder.ToString();
    }

    private TimeSpan Ttl(string className, int minutes)
    {
        // Scope the TTL to the live configuration so changing a setting takes effect without a restart.
        var ttlMinutes = className switch
        {
            "trending" => Plugin.Instance?.Configuration.TrendingCacheMinutes ?? minutes,
            "detail" or "season" or "collection" or "person" or "recommend" => Plugin.Instance?.Configuration.DetailCacheHours * 60 ?? minutes,
            _ => Plugin.Instance?.Configuration.CacheMinutes ?? minutes
        };

        return TimeSpan.FromMinutes(Math.Max(1, ttlMinutes));
    }

    private async Task<Dictionary<int, string>> GetGenreMapAsync(string mediaType, CancellationToken cancellationToken)
    {
        var list = await GetAsync<TmdbGenreList>(
            $"genre/{mediaType}/list",
            TimeSpan.FromDays(7),
            cancellationToken).ConfigureAwait(false);

        var map = new Dictionary<int, string>(list.Genres.Count);
        foreach (var genre in list.Genres)
        {
            map[genre.Id] = genre.Name;
        }

        if (string.Equals(mediaType, "movie", StringComparison.Ordinal))
        {
            _movieGenreCache = map;
        }
        else
        {
            _tvGenreCache = map;
        }

        return map;
    }

    private async Task<T> GetAsync<T>(string pathAndQuery, TimeSpan ttl, CancellationToken cancellationToken)
        where T : class
    {
        var key = GetApiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new TmdbException("No TMDB API key is configured. Set one in Dashboard > Plugins > Reko.");
        }

        var fullPath = pathAndQuery
            + (pathAndQuery.Contains('?', StringComparison.Ordinal) ? "&" : "?")
            + "language=" + Uri.EscapeDataString(Language);

        // Include the credentials in the cache key so switching keys or languages cannot serve stale data.
        var cacheKey = string.Create(CultureInfo.InvariantCulture, $"{key.GetHashCode(StringComparison.Ordinal)}|{Language}|{fullPath}");

        async Task<object> ProduceAsync(CancellationToken ct)
            => await FetchAsync<T>(key, fullPath, ct).ConfigureAwait(false);

        var boxed = await _cache.GetOrAddAsync(cacheKey, ttl, ProduceAsync, cancellationToken).ConfigureAwait(false);
        return (T)boxed;
    }

    private async Task<T> FetchAsync<T>(string apiKey, string pathAndQuery, CancellationToken cancellationToken)
        where T : class
    {
        await _rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildBaseAddress() + pathAndQuery);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new TmdbException("Could not reach TMDB. Check the server's outbound network access.", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                _rateLimiter.Penalise(retryAfter);
                throw new TmdbException($"TMDB rate limit hit. Backing off for {retryAfter.TotalSeconds:0}s.");
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new TmdbException("TMDB rejected the API key. Check it in Dashboard > Plugins > Reko.");
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new TmdbException("TMDB has no item with that id.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new TmdbException($"TMDB returned {(int)response.StatusCode} {response.ReasonPhrase} for {pathAndQuery}.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var parsed = JsonSerializer.Deserialize<T>(body, SerializerOptions);
                return parsed ?? throw new TmdbException($"TMDB returned an empty payload for {pathAndQuery}.");
            }
            catch (JsonException ex)
            {
                throw new TmdbException($"Could not parse the TMDB response for {pathAndQuery}.", ex);
            }
        }
    }

    // BasePlugin<T>.Configuration returns the same live object until SaveConfiguration is called, so
    // reading these is a field access rather than a file read. No caching wrapper is needed.
    private string GetApiKey() => Plugin.Instance?.Configuration.TmdbApiKey ?? string.Empty;

    private string GetLanguage() => Plugin.Instance?.Configuration.TmdbLanguage ?? string.Empty;

    private string GetImageLanguage() => Plugin.Instance?.Configuration.TmdbImageLanguage ?? string.Empty;

    /// <summary>
    /// Resolves the API base URL, honouring the advanced override.
    /// </summary>
    /// <returns>The base URL, always ending in a slash.</returns>
    private static string BuildBaseAddress()
    {
        var configured = Plugin.Instance?.Configuration.TmdbBaseUrl;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultBaseAddress;
        }

        var trimmed = configured.Trim();

        // Refuse anything that is not an absolute http(s) URL, so a typo in the settings cannot turn
        // into a confusing request failure on every rail.
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return DefaultBaseAddress;
        }

        return trimmed.EndsWith('/') ? trimmed : trimmed + "/";
    }

    /// <summary>
    /// Resolves the image base URL, honouring the advanced mirror override.
    /// </summary>
    /// <returns>The image base URL, always ending in a slash.</returns>
    private static string BuildImageBaseAddress()
    {
        var configured = Plugin.Instance?.Configuration.TmdbImageBaseUrl;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return ImageBaseAddress;
        }

        var trimmed = configured.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return ImageBaseAddress;
        }

        return trimmed.EndsWith('/') ? trimmed : trimmed + "/";
    }
}
