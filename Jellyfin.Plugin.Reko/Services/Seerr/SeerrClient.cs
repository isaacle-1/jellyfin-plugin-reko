using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Reko.Services.Tmdb;

namespace Jellyfin.Plugin.Reko.Services.Seerr;

/// <summary>
/// Raised when a Seerr call fails in a way the caller should surface to the user.
/// </summary>
public sealed class SeerrException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SeerrException"/> class.
    /// </summary>
    /// <param name="message">The message shown to the user.</param>
    public SeerrException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SeerrException"/> class.
    /// </summary>
    /// <param name="message">The message shown to the user.</param>
    /// <param name="innerException">The inner exception.</param>
    public SeerrException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The outcome of a request submission, which is not simply success or failure.
/// </summary>
/// <param name="Outcome">
/// <see cref="RequestOutcome.Created"/>, <see cref="RequestOutcome.AlreadySatisfied"/>, or
/// <see cref="RequestOutcome.Duplicate"/>.
/// </param>
/// <param name="Request">The created request, when one was created.</param>
/// <param name="Message">A human-readable explanation.</param>
public readonly record struct SeerrRequestResult(RequestOutcome Outcome, SeerrRequest? Request, string Message);

/// <summary>
/// How a request submission ended.
/// </summary>
public enum RequestOutcome
{
    /// <summary>A request row was created.</summary>
    Created,

    /// <summary>Seerr returned 202: every season was already requested or available.</summary>
    AlreadySatisfied,

    /// <summary>Seerr returned 409: a request for this media already exists.</summary>
    Duplicate
}

/// <summary>
/// A client for Overseerr and Jellyseerr, which share the <c>/api/v1</c> surface Reko uses.
/// </summary>
/// <remarks>
/// Two behaviours matter and are not in the published documentation. First, <c>X-Api-Key</c> alone
/// always runs the request as Seerr user 1, so the undocumented <c>X-API-User</c> header is sent to
/// attribute a call to the right person. Second, <c>POST /api/v1/request</c> can answer 202 as a
/// success no-op, which must not be reported as an error.
/// </remarks>
public sealed class SeerrClient
{
    /// <summary>
    /// The name of the <see cref="IHttpClientFactory"/> client used for Seerr calls.
    /// </summary>
    public const string HttpClientName = "Reko.Seerr";

    private const int IndexPageSize = 500;
    private const int MaxIndexPages = 10;
    private static readonly TimeSpan RequestIndexTtl = TimeSpan.FromMinutes(3);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;

    // Nullable values are legitimate here: an unresolvable Seerr user is a null, not a failure,
    // and it must be cacheable so a 404 is not re-fetched on every rail render.
    private readonly TtlCache<string, object?> _cache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SeerrClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Factory used to create the HTTP client.</param>
    public SeerrClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Gets a value indicating whether Seerr is enabled and has both a URL and an API key.
    /// </summary>
    public bool IsConfigured
    {
        get
        {
            var config = Plugin.Instance?.Configuration;
            return config is { EnableSeerr: true }
                   && !string.IsNullOrWhiteSpace(config.SeerrUrl)
                   && !string.IsNullOrWhiteSpace(config.SeerrApiKey);
        }
    }

    /// <summary>
    /// Drops every cached Seerr payload.
    /// </summary>
    public void Invalidate()
    {
        _cache.Clear();
    }

    /// <summary>
    /// Probes the Seerr instance. Returns null when it is unreachable or not set up.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The public settings, or null.</returns>
    public Task<SeerrPublicSettings?> GetPublicSettingsAsync(CancellationToken cancellationToken)
        => TryGetAsync<SeerrPublicSettings>("settings/public", null, cancellationToken);

    /// <summary>
    /// Gets the Seerr version string, or null when unreachable.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The version, or null.</returns>
    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken)
    {
        // checkUpdateAvailable=false keeps the probe fast and offline-safe; otherwise Seerr makes an
        // outbound GitHub call on every health check.
        var status = await TryGetAsync<SeerrStatus>("status?checkUpdateAvailable=false", null, cancellationToken)
            .ConfigureAwait(false);
        return status?.Version;
    }

    /// <summary>
    /// Resolves a Jellyfin user GUID to their Seerr user, so requests and quotas are attributed to
    /// the right person.
    /// </summary>
    /// <param name="jellyfinUserId">The Jellyfin user GUID.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The Seerr user, or null when the user has never signed in to Seerr.</returns>
    public async Task<SeerrUser?> ResolveJellyfinUserAsync(Guid jellyfinUserId, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        // Seerr normalises the GUID server-side, so either form works. Dash-stripped matches its
        // regex exactly.
        var normalised = jellyfinUserId.ToString("N", CultureInfo.InvariantCulture);
        var key = "seerr:user:" + normalised;

        var result = await _cache.GetOrAddAsync(
            key,
            TimeSpan.FromMinutes(30),
            async ct => await FetchAsync<SeerrUser>(
                string.Create(CultureInfo.InvariantCulture, $"user/jellyfin/{normalised}"),
                null,
                ct,
                allowNotFound: true).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        return (SeerrUser?)result;
    }

    /// <summary>
    /// Gets the Seerr request state for a title.
    /// </summary>
    /// <param name="mediaType">Either <c>movie</c> or <c>tv</c>.</param>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <param name="seerrUserId">The acting Seerr user id, or null to run as the API key owner.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The media record, or null when the title is unknown to Seerr.</returns>
    public async Task<SeerrMediaInfo?> GetMediaInfoAsync(string mediaType, int tmdbId, int? seerrUserId, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        var user = seerrUserId?.ToString(CultureInfo.InvariantCulture) ?? "key";
        var key = string.Create(CultureInfo.InvariantCulture, $"seerr:media:{mediaType}:{tmdbId}:{user}");

        async Task<object?> ProduceAsync(CancellationToken ct)
            => await FetchAsync<SeerrMediaInfo>(
                string.Create(CultureInfo.InvariantCulture, $"{mediaType}/{tmdbId}"),
                seerrUserId,
                ct,
                allowNotFound: true).ConfigureAwait(false);

        var result = await _cache.GetOrAddAsync(key, TimeSpan.FromMinutes(2), ProduceAsync, cancellationToken).ConfigureAwait(false);
        return (SeerrMediaInfo?)result;
    }

    /// <summary>
    /// Submits a request through Seerr.
    /// </summary>
    /// <param name="body">The request body.</param>
    /// <param name="seerrUserId">The acting Seerr user id, or null to run as the API key owner.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome of the submission.</returns>
    public async Task<SeerrRequestResult> RequestAsync(SeerrRequestBody body, int? seerrUserId, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new SeerrException("Overseerr/Jellyseerr is not configured. Set its URL and API key in Dashboard > Plugins > Reko.");
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl("request"));
        AddAuth(request, seerrUserId);
        request.Content = new StringContent(
            JsonSerializer.Serialize(body, SerializerOptions),
            Encoding.UTF8,
            "application/json");

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new SeerrException("Could not reach Overseerr/Jellyseerr. Check the URL in Dashboard > Plugins > Reko.", ex);
        }

        using (response)
        {
            var payload = await SafeReadAsync(response, cancellationToken).ConfigureAwait(false);

            switch ((int)response.StatusCode)
            {
                case 201:
                    InvalidateMedia(body.MediaType, body.MediaId);
                    return new SeerrRequestResult(
                        RequestOutcome.Created,
                        JsonSerializer.Deserialize<SeerrRequest>(payload, SerializerOptions),
                        "Request sent.");

                // 202 is a success no-op: every requested season was already requested or available.
                case 202:
                    return new SeerrRequestResult(
                        RequestOutcome.AlreadySatisfied,
                        null,
                        "Everything you selected is already requested or available.");

                case 409:
                    return new SeerrRequestResult(
                        RequestOutcome.Duplicate,
                        null,
                        "A request for this already exists.");

                case 403:
                    throw new SeerrException(DescribePermissionFailure(payload));

                case 401:
                    throw new SeerrException("Seerr rejected the API key.");

                default:
                    throw new SeerrException(
                        string.IsNullOrWhiteSpace(payload)
                            ? $"Seerr returned {(int)response.StatusCode} {response.ReasonPhrase}."
                            : payload);
            }
        }
    }

    /// <summary>
    /// Reads the request states for a set of titles, so a whole rail's worth of badges can be
    /// resolved without a call per card.
    /// </summary>
    /// <param name="mediaType">Either <c>movie</c> or <c>tv</c>.</param>
    /// <param name="tmdbIds">The TMDB ids.</param>
    /// <param name="seerrUserId">The acting Seerr user id, or null.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A map of TMDB id to media record; only known titles appear.</returns>
    public async Task<Dictionary<int, SeerrMediaInfo>> GetMediaInfoBatchAsync(
        string mediaType,
        IReadOnlyCollection<int> tmdbIds,
        int? seerrUserId,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<int, SeerrMediaInfo>();
        if (!IsConfigured || tmdbIds.Count == 0)
        {
            return map;
        }

        foreach (var id in tmdbIds)
        {
            var info = await GetMediaInfoAsync(mediaType, id, seerrUserId, cancellationToken).ConfigureAwait(false);
            if (info is not null)
            {
                map[id] = info;
            }
        }

        return map;
    }

    /// <summary>
    /// Builds a TMDB-id-keyed index of everything ever requested on this Seerr instance.
    /// </summary>
    /// <remarks>
    /// Annotating 20 rails of 20 cards with per-title Seerr calls would be hundreds of HTTP requests
    /// for a single home page render. The request list is small on a personal server, so paging
    /// through it a few times and caching the result is far cheaper, and it reflects global request
    /// state rather than only the signed-in user's.
    /// </remarks>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The index, keyed by TMDB id.</returns>
    public async Task<Dictionary<int, SeerrMediaInfo>> GetRequestIndexAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return new Dictionary<int, SeerrMediaInfo>();
        }

        async Task<object?> ProduceAsync(CancellationToken ct)
        {
            var index = new Dictionary<int, SeerrMediaInfo>();
            var skip = 0;

            // Bounded, so a very large request history cannot become an unbounded loop. Anything
            // past the cap is simply not badged, which is a cosmetic loss rather than a failure.
            for (var page = 0; page < MaxIndexPages; page++)
            {
                var path = string.Create(
                    CultureInfo.InvariantCulture,
                    $"request?take={IndexPageSize}&skip={skip}&sort=added&sortDirection=desc");

                var result = await FetchAsync<SeerrRequestPage>(path, null, ct).ConfigureAwait(false);
                if (result is null || result.Results.Count == 0)
                {
                    break;
                }

                foreach (var row in result.Results)
                {
                    if (row.Media is null || row.Media.TmdbId <= 0)
                    {
                        continue;
                    }

                    // The list endpoint populates a subset of the media object, so normalise it into
                    // the same shape the per-title route returns before storing it.
                    if (row.Media.Status == 0)
                    {
                        row.Media.Status = SeerrMediaStatus.Unknown;
                    }

                    if (row.Media.Requests.Count == 0)
                    {
                        row.Media.Requests =
                        [
                            new SeerrRequest { Id = row.Id, Status = row.Status, Seasons = row.Seasons }
                        ];
                    }

                    if (row.Seasons.Count > 0 && row.Media.Seasons.Count == 0)
                    {
                        row.Media.Seasons = row.Seasons
                            .Select(s => new SeerrMediaSeason { SeasonNumber = s.SeasonNumber, Status = s.Status })
                            .ToList();
                    }

                    index[row.Media.TmdbId] = row.Media;
                }

                if (result.Results.Count < IndexPageSize || result.PageInfo.Page >= result.PageInfo.Pages)
                {
                    break;
                }

                skip += IndexPageSize;
            }

            return index;
        }

        var cached = await _cache
            .GetOrAddAsync("seerr:requestIndex", RequestIndexTtl, ProduceAsync, cancellationToken)
            .ConfigureAwait(false);

        return cached as Dictionary<int, SeerrMediaInfo> ?? new Dictionary<int, SeerrMediaInfo>();
    }

    private void InvalidateMedia(string mediaType, int tmdbId)
    {
        _cache.InvalidatePrefix($"seerr:media:{mediaType}:{tmdbId}:");
    }

    private static string DescribePermissionFailure(string payload)
    {
        if (payload.Contains("quota", StringComparison.OrdinalIgnoreCase))
        {
            return "You have reached your request quota in Overseerr/Jellyseerr.";
        }

        if (payload.Contains("blocklist", StringComparison.OrdinalIgnoreCase))
        {
            return "This title is blocklisted in Overseerr/Jellyseerr.";
        }

        if (payload.Contains("csrf", StringComparison.OrdinalIgnoreCase))
        {
            return "Seerr rejected the request because CSRF protection is enabled. Turn off Network > CSRF Protection in Seerr.";
        }

        if (payload.Contains("permission", StringComparison.OrdinalIgnoreCase))
        {
            return "Your Seerr account does not have permission to request this.";
        }

        return string.IsNullOrWhiteSpace(payload) ? "Seerr refused the request." : payload;
    }

    private async Task<T?> TryGetAsync<T>(string pathAndQuery, int? seerrUserId, CancellationToken cancellationToken)
        where T : class
    {
        if (!IsConfigured)
        {
            return null;
        }

        try
        {
            return await FetchAsync<T>(pathAndQuery, seerrUserId, cancellationToken, allowNotFound: true).ConfigureAwait(false);
        }
        catch (SeerrException)
        {
            return null;
        }
    }

    private async Task<T?> FetchAsync<T>(string pathAndQuery, int? seerrUserId, CancellationToken cancellationToken, bool allowNotFound = true)
        where T : class
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(pathAndQuery));
        AddAuth(request, seerrUserId);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new SeerrException("Could not reach Overseerr/Jellyseerr.", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                if (allowNotFound)
                {
                    return null;
                }

                throw new SeerrException("Seerr has no record of that title.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await SafeReadAsync(response, cancellationToken).ConfigureAwait(false);
                throw new SeerrException(
                    string.IsNullOrWhiteSpace(body)
                        ? $"Seerr returned {(int)response.StatusCode} {response.ReasonPhrase}."
                        : body);
            }

            var payload = await SafeReadAsync(response, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<T>(payload, SerializerOptions);
            }
            catch (JsonException ex)
            {
                throw new SeerrException("Could not parse the Seerr response.", ex);
            }
        }
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private void AddAuth(HttpRequestMessage request, int? seerrUserId)
    {
        var config = Plugin.Instance!.Configuration;
        request.Headers.TryAddWithoutValidation("X-Api-Key", config.SeerrApiKey.Trim());

        // Undocumented but present in both projects. Without it every call runs as Seerr user 1, which
        // means the wrong request quota and the wrong request attribution.
        if (seerrUserId is > 0 && config.SeerrMapJellyfinUsers)
        {
            request.Headers.TryAddWithoutValidation("X-API-User", seerrUserId.Value.ToString(CultureInfo.InvariantCulture));
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private string BuildUrl(string pathAndQuery)
    {
        var baseUrl = Plugin.Instance!.Configuration.SeerrUrl.Trim().TrimEnd('/');
        return baseUrl + "/api/v1/" + pathAndQuery.TrimStart('/');
    }
}
