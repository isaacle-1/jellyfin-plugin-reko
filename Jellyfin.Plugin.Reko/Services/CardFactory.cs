using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Reko.Api;
using Jellyfin.Plugin.Reko.Services.Library;
using Jellyfin.Plugin.Reko.Services.Seerr;
using Jellyfin.Plugin.Reko.Services.Tmdb;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Reko.Services;

/// <summary>
/// Turns TMDB payloads into the card shape the browser renders.
/// </summary>
/// <remarks>
/// Centralising this matters more than it looks. TMDB returns movies and series as two different
/// shapes with different field names, and the same object arrives from eight different endpoints
/// (trending, popular, discover, search, collection, recommendations, person credits). Resolving the
/// media type and the field aliases once, here, is what keeps the browser from branching on all of it.
/// </remarks>
public sealed class CardFactory
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly Dictionary<int, string> _movieGenres;
    private readonly Dictionary<int, string> _tvGenres;
    private readonly string _imageLanguage;
    private readonly string _region;
    private readonly string _imageBaseUrl;

    /// <summary>
    /// Initializes a new instance of the <see cref="CardFactory"/> class.
    /// </summary>
    /// <param name="libraryManager">Used to resolve items for the per-user playback overlay.</param>
    /// <param name="userDataManager">Used for the per-user played and resume overlay.</param>
    /// <param name="movieGenres">The TMDB movie genre id to name map.</param>
    /// <param name="tvGenres">The TMDB TV genre id to name map.</param>
    /// <param name="imageLanguage">The preferred image language.</param>
    /// <param name="region">The region code used for certifications.</param>
    /// <param name="imageBaseUrl">The image base URL, up to but excluding the size token.</param>
    public CardFactory(
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        Dictionary<int, string> movieGenres,
        Dictionary<int, string> tvGenres,
        string imageLanguage,
        string region,
        string imageBaseUrl)
    {
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _movieGenres = movieGenres;
        _tvGenres = tvGenres;
        _imageLanguage = imageLanguage;
        _region = region;
        _imageBaseUrl = imageBaseUrl;
    }

    /// <summary>
    /// Gets the region code used for certifications and watch providers.
    /// </summary>
    public string Region => _region;

    /// <summary>
    /// Builds cards from a TMDB list payload.
    /// </summary>
    /// <param name="items">The TMDB list items.</param>
    /// <param name="defaultMediaType">
    /// The media type to assume when the payload does not set one. Discover, collection and
    /// recommendations do not set it; trending/all and search/multi do.
    /// </param>
    /// <returns>The cards.</returns>
    public List<RekoCard> CreateCards(IEnumerable<TmdbListItem> items, string defaultMediaType)
    {
        ArgumentNullException.ThrowIfNull(items);

        var result = new List<RekoCard>();
        foreach (var item in items)
        {
            if (item is null || item.Id <= 0)
            {
                continue;
            }

            var mediaType = ResolveMediaType(item, defaultMediaType);
            if (mediaType is null)
            {
                continue;
            }

            result.Add(CreateCard(item, mediaType));
        }

        return result;
    }

    /// <summary>
    /// Builds one card without the library overlay.
    /// </summary>
    /// <param name="item">The TMDB list item.</param>
    /// <param name="mediaType">The resolved media type.</param>
    /// <returns>The card.</returns>
    public RekoCard CreateCard(TmdbListItem item, string mediaType)
    {
        var isMovie = mediaType == "movie";
        var date = isMovie ? item.ReleaseDate : item.FirstAirDate;
        var title = isMovie ? item.Title : item.Name;
        var originalTitle = isMovie ? item.OriginalTitle : item.OriginalName;

        var card = new RekoCard
        {
            Id = item.Id,
            Type = mediaType,
            Title = FirstNonEmpty(title, originalTitle) ?? string.Empty,
            Overview = Trim(item.Overview),
            Date = string.IsNullOrWhiteSpace(date) ? null : date,
            Year = ParseYear(date),
            Rating = Math.Round(item.VoteAverage, 1),
            VoteCount = item.VoteCount,
            Popularity = Math.Round(item.Popularity, 2),
            Poster = BuildImageUrl(item.PosterPath, "w342"),
            Backdrop = BuildImageUrl(item.BackdropPath, "w780")
        };

        if (!string.IsNullOrWhiteSpace(originalTitle)
            && !string.Equals(originalTitle, title, StringComparison.Ordinal))
        {
            card.OriginalTitle = originalTitle;
        }

        foreach (var genreId in item.GenreIds)
        {
            if (TryGetGenreName(genreId, isMovie, out var name))
            {
                card.Genres.Add(name);
            }
        }

        return card;
    }

    /// <summary>
    /// Applies the library badge, playback target, per-user played state, resume position and Seerr
    /// status to a whole rail, resolving everything in batch.
    /// </summary>
    /// <param name="cards">The cards to update in place.</param>
    /// <param name="libraryIndex">The library index.</param>
    /// <param name="user">The signed-in user, or null for an anonymous context.</param>
    public void ApplyLocalState(
        IReadOnlyList<RekoCard> cards,
        IReadOnlyDictionary<int, LibraryEntry> libraryIndex,
        User? user)
    {
        if (cards.Count == 0 || libraryIndex.Count == 0)
        {
            return;
        }

        var entries = new List<LibraryEntry>(cards.Count);
        foreach (var card in cards)
        {
            if (!libraryIndex.TryGetValue(card.Id, out var entry))
            {
                continue;
            }

            card.InLibrary = true;
            card.JellyfinItemId = entry.ItemId.ToString("D", CultureInfo.InvariantCulture);
            card.JellyfinKind = entry.Kind.ToString();

            if (user is not null)
            {
                entries.Add(entry);
            }
        }

        if (user is null || entries.Count == 0)
        {
            return;
        }

        // IUserDataManager is keyed by BaseItem, not by id, so the items have to be resolved. This is
        // a cache read per card and it is batched from here on.
        var items = new List<BaseItem>(entries.Count);
        var progressByItemId = new Dictionary<Guid, RekoCard>(entries.Count);
        foreach (var entry in entries)
        {
            var item = _libraryManager.GetItemById(entry.ItemId);
            if (item is null)
            {
                continue;
            }

            items.Add(item);
        }

        if (items.Count == 0)
        {
            return;
        }

        foreach (var card in cards)
        {
            if (card.JellyfinItemId is not null && Guid.TryParse(card.JellyfinItemId, out var cardItemId))
            {
                progressByItemId[cardItemId] = card;
            }
        }

        var userData = _userDataManager.GetUserDataBatch(items, user);
        foreach (var pair in userData)
        {
            if (!progressByItemId.TryGetValue(pair.Key, out var card))
            {
                continue;
            }

            card.Watched = pair.Value.Played;

            if (pair.Value.PlaybackPositionTicks <= 0 || !libraryIndex.TryGetValue(card.Id, out var entry))
            {
                continue;
            }

            if (entry.RunTimeTicks is not > 0)
            {
                continue;
            }

            var progress = (double)pair.Value.PlaybackPositionTicks / entry.RunTimeTicks.Value;
            if (progress is <= 0.001 or >= 0.95 || pair.Value.Played)
            {
                continue;
            }

            card.Progress = Math.Clamp(progress, 0, 1);
            card.Resumable = true;
        }
    }

    /// <summary>
    /// Applies Seerr request state to a rail's cards in place.
    /// </summary>
    /// <param name="cards">The cards to update.</param>
    /// <param name="mediaInfo">The Seerr records, keyed by TMDB id.</param>
    public static void ApplyRequestState(IReadOnlyList<RekoCard> cards, IReadOnlyDictionary<int, SeerrMediaInfo> mediaInfo)
    {
        if (mediaInfo.Count == 0)
        {
            return;
        }

        foreach (var card in cards)
        {
            if (mediaInfo.TryGetValue(card.Id, out var info))
            {
                card.RequestStatus = DescribeMediaStatus(info.Status);
            }
        }
    }

    /// <summary>
    /// Turns a Seerr numeric media status into the name shown on a badge.
    /// </summary>
    /// <param name="status">The numeric status.</param>
    /// <returns>The badge name, or null when the title is unknown to Seerr.</returns>
    public static string? DescribeMediaStatus(int status) => status switch
    {
        SeerrMediaStatus.Pending => "pending",
        SeerrMediaStatus.Processing => "downloading",
        SeerrMediaStatus.PartiallyAvailable => "partially-available",
        SeerrMediaStatus.Available => "available",
        SeerrMediaStatus.Blocklisted => "blocked",
        SeerrMediaStatus.Deleted => "removed",
        _ => null
    };

    /// <summary>
    /// Turns a Seerr request status into the name shown on a badge.
    /// </summary>
    /// <param name="status">The numeric status.</param>
    /// <returns>The badge name, or null for an unknown value.</returns>
    public static string? DescribeRequestStatus(int status) => status switch
    {
        SeerrRequestStatus.Pending => "request-pending",
        SeerrRequestStatus.Approved => "request-approved",
        SeerrRequestStatus.Declined => "request-declined",
        SeerrRequestStatus.Failed => "request-failed",
        SeerrRequestStatus.Completed => "request-completed",
        _ => null
    };

    /// <summary>
    /// Builds an absolute TMDB image URL.
    /// </summary>
    /// <param name="path">The TMDB file path, which may be null.</param>
    /// <param name="size">The size token, for example <c>w342</c> or <c>original</c>.</param>
    /// <returns>The absolute URL, or null when there is no path.</returns>
    public string? BuildImageUrl(string? path, string size)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.StartsWith('/') ? path : "/" + path;
        return _imageBaseUrl + size + trimmed;
    }

    /// <summary>
    /// Picks the best available title logo, preferring the configured image language.
    /// </summary>
    /// <param name="logos">The logo candidates.</param>
    /// <returns>The absolute logo URL, or null.</returns>
    public string? PickLogo(IEnumerable<TmdbImage>? logos)
    {
        if (logos is null)
        {
            return null;
        }

        // TMDB tags image language variants with a bare ISO 639-1 code ("en", "fr", "ja") while the
        // configured language is a locale ("en-US"). Comparing them directly never matches, which is
        // how a language-neutral wordmark ends up beating the correct localised one. The primary
        // subtag is what actually has to be compared.
        var primarySubtag = PrimarySubtag(_imageLanguage);

        TmdbImage? best = null;
        var bestScore = int.MinValue;
        var bestVote = double.MinValue;

        foreach (var logo in logos)
        {
            if (string.IsNullOrWhiteSpace(logo.FilePath))
            {
                continue;
            }

            int score;
            if (!string.IsNullOrEmpty(logo.Iso6391)
                && string.Equals(logo.Iso6391, primarySubtag, StringComparison.OrdinalIgnoreCase))
            {
                score = 3;
            }
            else if (string.IsNullOrEmpty(logo.Iso6391))
            {
                score = 2;
            }
            else
            {
                score = 1;
            }

            if (score > bestScore || (score == bestScore && logo.VoteAverage > bestVote))
            {
                best = logo;
                bestScore = score;
                bestVote = logo.VoteAverage;
            }
        }

        return best is null ? null : BuildImageUrl(best.FilePath, "w500");
    }

    /// <summary>
    /// Reduces a locale to its primary language subtag.
    /// </summary>
    /// <param name="language">A locale such as <c>en-US</c>, or a bare code such as <c>en</c>.</param>
    /// <returns>The subtag, lower-cased, or the input when it is not a locale.</returns>
    private static string PrimarySubtag(string language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return string.Empty;
        }

        var trimmed = language.Trim();
        var separator = trimmed.IndexOfAny(['-', '_']);
        return (separator > 0 ? trimmed[..separator] : trimmed).ToLowerInvariant();
    }

    /// <summary>
    /// Picks the best backdrop for a hero billboard.
    /// </summary>
    /// <param name="backdrops">The backdrop candidates.</param>
    /// <returns>The absolute URL, or null.</returns>
    public string? PickBackdrop(IEnumerable<TmdbImage>? backdrops)
    {
        if (backdrops is null)
        {
            return null;
        }

        TmdbImage? best = null;
        foreach (var backdrop in backdrops)
        {
            if (string.IsNullOrWhiteSpace(backdrop.FilePath))
            {
                continue;
            }

            if (best is null || backdrop.VoteAverage > best.VoteAverage)
            {
                best = backdrop;
            }
        }

        return best is null ? null : BuildImageUrl(best.FilePath, "w1280");
    }

    /// <summary>
    /// Resolves the certification for the configured region.
    /// </summary>
    /// <param name="movieReleaseDates">The movie release dates, or null.</param>
    /// <param name="tvContentRatings">The TV content ratings, or null.</param>
    /// <returns>The certification, or null when the region is not listed.</returns>
    public string? ResolveCertification(TmdbReleaseDates? movieReleaseDates, TmdbContentRatings? tvContentRatings)
        => CertificationResolver.Resolve(_region, movieReleaseDates, tvContentRatings);

    /// <summary>
    /// Picks the best YouTube trailer key.
    /// </summary>
    /// <param name="videos">The video candidates.</param>
    /// <param name="imageLanguage">The preferred language.</param>
    /// <returns>The YouTube key, or null when there is no usable trailer.</returns>
    public static string? PickTrailerKey(IEnumerable<TmdbVideo>? videos, string imageLanguage)
    {
        if (videos is null)
        {
            return null;
        }

        // TMDB does not declare `type` as an enum, so unknown values are ranked rather than dropped.
        var candidates = videos
            .Where(v => string.Equals(v.Site, "YouTube", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(v.Key))
            .Select(v =>
            {
                var rank = v.Type switch
                {
                    "Trailer" => 4,
                    "Teaser" => 3,
                    "Clip" => 1,
                    _ => 0
                };

                if (string.Equals(v.Iso6391, imageLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    rank += 10;
                }

                return (Rank: rank, v.Official, v.Key, v.PublishedAt);
            })
            .OrderByDescending(x => x.Rank)
            .ThenByDescending(x => x.Official)
            .ThenByDescending(x => x.PublishedAt, StringComparer.Ordinal)
            .ToList();

        return candidates.Count == 0 ? null : candidates[0].Key;
    }

    private bool TryGetGenreName(int genreId, bool isMovie, out string name)
        => (isMovie ? _movieGenres : _tvGenres).TryGetValue(genreId, out name!);

    private static string? ResolveMediaType(TmdbListItem item, string defaultMediaType)
    {
        if (string.Equals(item.MediaType, "movie", StringComparison.Ordinal))
        {
            return "movie";
        }

        if (string.Equals(item.MediaType, "tv", StringComparison.Ordinal))
        {
            return "tv";
        }

        // People are handled by the person endpoints, not by a card rail.
        if (string.Equals(item.MediaType, "person", StringComparison.Ordinal))
        {
            return null;
        }

        return defaultMediaType;
    }

    private static string? FirstNonEmpty(string? first, string? second)
        => string.IsNullOrWhiteSpace(first) ? (string.IsNullOrWhiteSpace(second) ? null : second) : first;

    private static string? Trim(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static int? ParseYear(string? date)
    {
        if (string.IsNullOrWhiteSpace(date) || date.Length < 4)
        {
            return null;
        }

        return int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            ? year
            : null;
    }
}
