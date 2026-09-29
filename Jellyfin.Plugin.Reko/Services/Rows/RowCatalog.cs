using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.Reko.Services.Rows;

/// <summary>
/// Where a rail's cards come from.
/// </summary>
public enum RowSource
{
    /// <summary>TMDB trending, which returns exactly 20 items and has no page parameter.</summary>
    Trending,

    /// <summary>TMDB popular, for movies or series.</summary>
    Popular,

    /// <summary>TMDB series that are on the air right now.</summary>
    OnTheAir,

    /// <summary>A TMDB discover query.</summary>
    Discover,

    /// <summary>Items the signed-in user has partly watched, taken from Jellyfin.</summary>
    ContinueWatching,

    /// <summary>TMDB recommendations for a specific title the user recently watched.</summary>
    BecauseYouWatched,

    /// <summary>A discover query built from the genres the user has watched most.</summary>
    BecauseYouLike
}

/// <summary>
/// The definition of one rail.
/// </summary>
/// <param name="Id">A stable id, used for deep links and lazy loading.</param>
/// <param name="Title">The displayed title.</param>
/// <param name="Source">Where the cards come from.</param>
/// <param name="MediaType">One of <c>movie</c>, <c>tv</c> or <c>all</c>.</param>
/// <param name="Query">The TMDB query parameters, for <see cref="RowSource.Discover"/>.</param>
/// <param name="Ranked">Whether the rail renders with oversized Top 10 numerals.</param>
/// <param name="MaxItems">A cap on how many cards the rail shows.</param>
/// <param name="Personalized">Whether the rail depends on the signed-in user's history.</param>
public sealed record RowDefinition(
    string Id,
    string Title,
    RowSource Source,
    string MediaType,
    IReadOnlyDictionary<string, string> Query,
    bool Ranked,
    int MaxItems,
    bool Personalized);

/// <summary>
/// The catalogue of rails that make up the Reko home page.
/// </summary>
/// <remarks>
/// Genre ids are the stable, documented TMDB ids. Keyword ids are deliberately not hard-coded:
/// TMDB documents no keyword list endpoint, so guessing at them would quietly produce wrong rails.
/// Rows that need something TMDB cannot supply honestly, such as a literal "Award Winners" row, are
/// not faked. "Critically Acclaimed" is a vote-consensus proxy and is labelled as such.
/// </remarks>
public static class RowCatalog
{
    // Documented TMDB genre ids. Movies and series use overlapping but distinct numbers.
    private const int MovieAction = 28;
    private const int MovieAdventure = 12;
    private const int MovieAnimation = 16;
    private const int MovieComedy = 35;
    private const int MovieCrime = 80;
    private const int MovieDocumentary = 99;
    private const int MovieDrama = 18;
    private const int MovieFamily = 10751;
    private const int MovieFantasy = 14;
    private const int MovieHorror = 27;
    private const int MovieMystery = 9648;
    private const int MovieRomance = 10749;
    private const int MovieSciFi = 878;
    private const int MovieThriller = 53;
    private const int MovieWar = 10752;

    private const int TvActionAdventure = 10759;
    private const int TvAnimation = 16;
    private const int TvComedy = 35;
    private const int TvCrime = 80;
    private const int TvDocumentary = 99;
    private const int TvDrama = 18;
    private const int TvKids = 10762;
    private const int TvMystery = 9648;
    private const int TvSciFiFantasy = 10765;
    private const int TvWarPolitics = 10768;
    private const int TvWestern = 37;

    /// <summary>
    /// Builds the global rails.
    /// </summary>
    /// <returns>The rails, in display order. Personalised rails are inserted ahead of these.</returns>
    public static IReadOnlyList<RowDefinition> Build()
    {
        var rows = new List<RowDefinition>
        {
            // --- Ranked Top 10. TMDB trending has no page parameter and returns 20 items, so a
            //     ranked rail is exactly 10 of those with no paging of its own.
            Ranked("top10-today", "Top 10 Today", "all", "day", 10),
            Ranked("top10-week", "Top 10 This Week", "all", "week", 10),

            // --- What is moving
            Simple("trending-movies", "Trending Movies", RowSource.Trending, "movie", "week", 20),
            Simple("trending-series", "Trending Series", RowSource.Trending, "tv", "week", 20),
            Simple("popular-movies", "Popular Movies", RowSource.Popular, "movie", null, 20),
            Simple("popular-series", "Popular Series", RowSource.Popular, "tv", null, 20),
            Simple("on-the-air", "Airing Now", RowSource.OnTheAir, "tv", null, 20),

            // --- Fresh
            Discover(
                "new-movies",
                "New Movies",
                "movie",
                new Dictionary<string, string>
                {
                    ["sort_by"] = "primary_release_date.desc",
                    ["include_adult"] = "false",
                    ["vote_count.gte"] = "10"
                }),
            Discover(
                "new-series",
                "New Series",
                "tv",
                new Dictionary<string, string>
                {
                    ["sort_by"] = "first_air_date.desc",
                    ["include_adult"] = "false",
                    ["vote_count.gte"] = "5"
                }),

            // --- Quality
            Discover(
                "top-rated-movies",
                "Top Rated Movies",
                "movie",
                new Dictionary<string, string>
                {
                    ["sort_by"] = "vote_average.desc",
                    ["vote_count.gte"] = "3000",
                    ["include_adult"] = "false"
                }),
            Discover(
                "top-rated-series",
                "Top Rated Series",
                "tv",
                new Dictionary<string, string>
                {
                    ["sort_by"] = "vote_average.desc",
                    ["vote_count.gte"] = "500",
                    ["include_adult"] = "false"
                }),

            // A vote-consensus proxy. TMDB has no awards dataset, so this row is not called
            // "Award Winners"; that would be a claim the data cannot support.
            Discover(
                "critically-acclaimed",
                "Critically Acclaimed",
                "movie",
                new Dictionary<string, string>
                {
                    ["sort_by"] = "vote_average.desc",
                    ["vote_count.gte"] = "2000",
                    ["vote_average.gte"] = "7.5",
                    ["release_date.gte"] = "2000-01-01",
                    ["include_adult"] = "false"
                }),

            // --- Genre rails
            Genre("action-adventure", "Action & Adventure", "movie", MovieAction),
            Genre("sci-fi-fantasy", "Sci-Fi & Fantasy", "movie", MovieSciFi),
            Genre("thrillers", "Thrillers", "movie", MovieThriller),
            Genre("crime-mysteries", "Crime & Mysteries", "movie", MovieCrime),
            Genre("horror", "Horror", "movie", MovieHorror),
            Genre("comedy", "Comedy", "movie", MovieComedy),
            Genre("drama", "Drama", "movie", MovieDrama),
            Genre("romance", "Romance", "movie", MovieRomance),
            Genre("animation", "Animation", "movie", MovieAnimation),
            Genre("documentaries", "Documentaries", "movie", MovieDocumentary),
            Genre("family", "Family", "movie", MovieFamily),
            Genre("war-western", "War & Western", "movie", MovieWar),
            Genre(
                "adventure",
                "Adventure",
                "movie",
                MovieAdventure,
                MovieFantasy),
            Genre("mystery", "Mystery", "movie", MovieMystery),

            Discover(
                "series-action-adventure",
                "Action & Adventure Series",
                "tv",
                new Dictionary<string, string>
                {
                    ["with_genres"] = TvActionAdventure.ToString(CultureInfo.InvariantCulture),
                    ["sort_by"] = "popularity.desc",
                    ["include_adult"] = "false"
                }),
            Discover(
                "series-sci-fi-fantasy",
                "Sci-Fi & Fantasy Series",
                "tv",
                new Dictionary<string, string>
                {
                    ["with_genres"] = TvSciFiFantasy.ToString(CultureInfo.InvariantCulture),
                    ["sort_by"] = "popularity.desc",
                    ["include_adult"] = "false"
                }),
            Discover(
                "series-comedy",
                "Comedy Series",
                "tv",
                new Dictionary<string, string>
                {
                    ["with_genres"] = TvComedy.ToString(CultureInfo.InvariantCulture),
                    ["sort_by"] = "popularity.desc",
                    ["include_adult"] = "false"
                }),
            Discover(
                "series-drama",
                "Drama Series",
                "tv",
                new Dictionary<string, string>
                {
                    ["with_genres"] = TvDrama.ToString(CultureInfo.InvariantCulture),
                    ["sort_by"] = "popularity.desc",
                    ["include_adult"] = "false"
                }),
            Discover(
                "series-crime",
                "Crime Series",
                "tv",
                new Dictionary<string, string>
                {
                    ["with_genres"] = TvCrime.ToString(CultureInfo.InvariantCulture),
                    ["sort_by"] = "popularity.desc",
                    ["include_adult"] = "false"
                }),
            Discover(
                "series-kids",
                "Kids & Family",
                "tv",
                new Dictionary<string, string>
                {
                    ["with_genres"] = TvKids.ToString(CultureInfo.InvariantCulture),
                    ["sort_by"] = "popularity.desc",
                    ["include_adult"] = "false"
                }),
            Discover(
                "series-mystery",
                "Mystery Series",
                "tv",
                new Dictionary<string, string>
                {
                    ["with_genres"] = TvMystery.ToString(CultureInfo.InvariantCulture),
                    ["sort_by"] = "popularity.desc",
                    ["include_adult"] = "false"
                }),
            Discover(
                "series-documentaries",
                "Documentary Series",
                "tv",
                new Dictionary<string, string>
                {
                    ["with_genres"] = TvDocumentary.ToString(CultureInfo.InvariantCulture),
                    ["sort_by"] = "popularity.desc",
                    ["include_adult"] = "false"
                }),
            Discover(
                "series-war-western",
                "War & Western Series",
                "tv",
                new Dictionary<string, string>
                {
                    ["with_genres"] = string.Create(CultureInfo.InvariantCulture, $"{TvWarPolitics}|{TvWestern}"),
                    ["sort_by"] = "popularity.desc",
                    ["include_adult"] = "false"
                }),

            // --- Character rows
            Discover(
                "anime",
                "Anime",
                "tv",
                new Dictionary<string, string>
                {
                    ["with_genres"] = TvAnimation.ToString(CultureInfo.InvariantCulture),
                    ["with_origin_country"] = "JP",
                    ["with_original_language"] = "ja",
                    ["sort_by"] = "popularity.desc",
                    ["include_adult"] = "false"
                }),
            Discover(
                "korean",
                "Korean",
                "tv",
                new Dictionary<string, string>
                {
                    ["with_origin_country"] = "KR",
                    ["with_original_language"] = "ko",
                    ["sort_by"] = "popularity.desc",
                    ["include_adult"] = "false"
                }),
            Discover(
                "korean-movies",
                "Korean Movies",
                "movie",
                new Dictionary<string, string>
                {
                    ["with_original_language"] = "ko",
                    ["sort_by"] = "popularity.desc",
                    ["include_adult"] = "false"
                }),
            Discover(
                "classics",
                "Classics",
                "movie",
                new Dictionary<string, string>
                {
                    ["primary_release_date.lte"] = "1999-12-31",
                    ["primary_release_date.gte"] = "1960-01-01",
                    ["sort_by"] = "vote_average.desc",
                    ["vote_count.gte"] = "800",
                    ["include_adult"] = "false"
                })
        };

        return rows;
    }

    /// <summary>
    /// The rails that go above the global ones for a signed-in user with enough history.
    /// </summary>
    /// <param name="continueWatchingEnabled">Whether continue watching is enabled.</param>
    /// <param name="personalizedEnabled">Whether personalised rows are enabled.</param>
    /// <returns>The personalised rail definitions.</returns>
    public static IReadOnlyList<RowDefinition> BuildPersonalized(bool continueWatchingEnabled, bool personalizedEnabled)
    {
        var rows = new List<RowDefinition>();

        if (continueWatchingEnabled)
        {
            rows.Add(new RowDefinition(
                "continue-watching",
                "Continue Watching",
                RowSource.ContinueWatching,
                "all",
                new Dictionary<string, string>(),
                Ranked: false,
                MaxItems: 12,
                Personalized: true));
        }

        if (personalizedEnabled)
        {
            rows.Add(new RowDefinition(
                "because-you-watched",
                "Because You Watched",
                RowSource.BecauseYouWatched,
                "all",
                new Dictionary<string, string>(),
                Ranked: false,
                MaxItems: 20,
                Personalized: true));

            rows.Add(new RowDefinition(
                "because-you-like",
                "Because You Like These",
                RowSource.BecauseYouLike,
                "all",
                new Dictionary<string, string>(),
                Ranked: false,
                MaxItems: 20,
                Personalized: true));
        }

        return rows;
    }

    /// <summary>
    /// Applies the administrator's rail order, dropping unknown ids and appending anything the
    /// administrator did not mention so nothing silently disappears.
    /// </summary>
    /// <param name="rails">The default rails.</param>
    /// <param name="configuredOrder">A comma separated list of rail ids.</param>
    /// <returns>The reordered rails.</returns>
    public static IReadOnlyList<RowDefinition> ApplyOrder(IReadOnlyList<RowDefinition> rails, string configuredOrder)
    {
        if (string.IsNullOrWhiteSpace(configuredOrder))
        {
            return rails;
        }

        var wanted = configuredOrder
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (wanted.Count == 0)
        {
            return rails;
        }

        var ordered = rails.Where(r => wanted.Contains(r.Id)).ToList();
        var seen = ordered.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ordered.AddRange(rails.Where(r => !seen.Contains(r.Id)));
        return ordered;
    }

    private static RowDefinition Ranked(string id, string title, string mediaType, string window, int maxItems)
        => new(
            id,
            title,
            RowSource.Trending,
            mediaType,
            new Dictionary<string, string> { ["time_window"] = window },
            Ranked: true,
            maxItems,
            Personalized: false);

    private static RowDefinition Simple(string id, string title, RowSource source, string mediaType, string? window, int maxItems)
    {
        var query = new Dictionary<string, string>();
        if (window is not null)
        {
            query["time_window"] = window;
        }

        return new RowDefinition(id, title, source, mediaType, query, Ranked: false, maxItems, Personalized: false);
    }

    private static RowDefinition Discover(string id, string title, string mediaType, IReadOnlyDictionary<string, string> query)
        => new(
            id,
            title,
            RowSource.Discover,
            mediaType,
            query,
            Ranked: false,
            MaxItems: 20,
            Personalized: false);

    private static RowDefinition Genre(string id, string title, string mediaType, params int[] genreIds)
    {
        var value = genreIds.Length == 1
            ? genreIds[0].ToString(CultureInfo.InvariantCulture)
            : string.Join("|", genreIds.Select(g => g.ToString(CultureInfo.InvariantCulture)));

        return Discover(
            id,
            title,
            mediaType,
            new Dictionary<string, string>
            {
                // A comma is AND in TMDB's genre filter, which would return nothing for a rail
                // labelled "Adventure". A pipe is OR, which is what a genre rail means.
                ["with_genres"] = value,
                ["sort_by"] = "popularity.desc",
                ["include_adult"] = "false"
            });
    }
}
