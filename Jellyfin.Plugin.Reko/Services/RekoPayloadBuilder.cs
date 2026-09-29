using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Reko.Api;
using Jellyfin.Plugin.Reko.Services.Library;
using Jellyfin.Plugin.Reko.Services.Rows;
using Jellyfin.Plugin.Reko.Services.Seerr;
using Jellyfin.Plugin.Reko.Services.Tmdb;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Reko.Services;

/// <summary>
/// Assembles every payload the browser renders.
/// </summary>
/// <remarks>
/// Two rules shape this class. First, rails are built from TMDB list payloads only, because TMDB v3
/// has no batch detail endpoint and a rail of 20 titles must therefore cost exactly one upstream
/// call. Second, the library and Seerr overlays are applied in one pass over the finished cards
/// rather than per card, because the naive version turns one home page render into hundreds of HTTP
/// requests.
/// </remarks>
public sealed class RekoPayloadBuilder
{
    private readonly TmdbClient _tmdb;
    private readonly SeerrClient _seerr;
    private readonly LibraryIndex _libraryIndex;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly PluginConfigurationAccessor _config;
    private readonly ILogger<RekoPayloadBuilder> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RekoPayloadBuilder"/> class.
    /// </summary>
    /// <param name="tmdb">The TMDB client.</param>
    /// <param name="seerr">The Seerr client.</param>
    /// <param name="libraryIndex">The library index.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="config">The configuration accessor.</param>
    /// <param name="logger">The logger.</param>
    public RekoPayloadBuilder(
        TmdbClient tmdb,
        SeerrClient seerr,
        LibraryIndex libraryIndex,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        PluginConfigurationAccessor config,
        ILogger<RekoPayloadBuilder> logger)
    {
        _tmdb = tmdb;
        _seerr = seerr;
        _libraryIndex = libraryIndex;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Gets the feature and presentation flags for the client.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The client configuration.</returns>
    public async Task<RekoClientConfig> BuildClientConfigAsync(CancellationToken cancellationToken)
    {
        var config = _config.Current;

        var result = new RekoClientConfig
        {
            TabLabel = string.IsNullOrWhiteSpace(config.TabLabel) ? "Reko" : config.TabLabel.Trim(),
            HeroSeconds = Math.Clamp(config.HeroRotationSeconds, 3, 60),
            EnableSearch = config.EnableSearch,
            EnableHoverPreviews = config.EnableHoverPreviews,
            ShowWatchProviders = config.ShowWatchProviders,
            ShowTrailers = config.ShowTrailers,
            ShowCast = config.ShowCast,
            SeerrEnabled = _seerr.IsConfigured
        };

        if (_seerr.IsConfigured)
        {
            var settings = await _seerr.GetPublicSettingsAsync(cancellationToken).ConfigureAwait(false);
            result.SeerrTitle = FirstNonEmpty(settings?.ApplicationTitle, "Overseerr") ?? "Overseerr";
            result.SeerrUrl = config.SeerrUrl.Trim().TrimEnd('/');
        }

        return result;
    }

    /// <summary>
    /// Builds the whole home page payload.
    /// </summary>
    /// <param name="user">The signed-in user, or null.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The home payload.</returns>
    public async Task<RekoHomePayload> BuildHomeAsync(User? user, CancellationToken cancellationToken)
    {
        var payload = new RekoHomePayload
        {
            Config = await BuildClientConfigAsync(cancellationToken).ConfigureAwait(false)
        };

        if (!_tmdb.IsConfigured)
        {
            payload.Notice = "Reko needs a TMDB API Read Access Token. Add one in Dashboard > Plugins > Reko > Settings.";
            return payload;
        }

        var factory = await GetCardFactoryAsync(cancellationToken).ConfigureAwait(false);

        // Everything below is best effort. One failing rail must not blank the home page, so each
        // rail resolves independently and a failure drops only that rail.
        payload.Hero = await SafeAsync(
            () => BuildHeroAsync(factory, user, cancellationToken),
            "hero",
            (List<RekoCard>?)null).ConfigureAwait(false) ?? new List<RekoCard>();

        var rails = new List<RekoRail>();
        foreach (var definition in BuildRailDefinitions())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rail = await SafeAsync(
                () => BuildRailAsync(definition, factory, user, cancellationToken),
                definition.Id,
                (RekoRail?)null).ConfigureAwait(false);

            if (rail is not null && rail.Items.Count > 0)
            {
                rails.Add(rail);
            }
        }

        payload.Rails = rails;
        return payload;
    }

    /// <summary>
    /// Builds a single rail, for lazy loading a row the home page did not include.
    /// </summary>
    /// <param name="railId">The rail id.</param>
    /// <param name="user">The signed-in user, or null.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The rail, or null when the id is unknown.</returns>
    public async Task<RekoRail?> BuildRailByIdAsync(string railId, User? user, CancellationToken cancellationToken)
    {
        if (!_tmdb.IsConfigured)
        {
            return null;
        }

        var definition = BuildRailDefinitions()
            .FirstOrDefault(r => string.Equals(r.Id, railId, StringComparison.OrdinalIgnoreCase));

        if (definition is null)
        {
            return null;
        }

        var factory = await GetCardFactoryAsync(cancellationToken).ConfigureAwait(false);
        return await SafeAsync(
            () => BuildRailAsync(definition, factory, user, cancellationToken),
            definition.Id,
            (RekoRail?)null).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a full title page.
    /// </summary>
    /// <param name="id">The TMDB id.</param>
    /// <param name="mediaType">Either <c>movie</c> or <c>tv</c>.</param>
    /// <param name="user">The signed-in user, or null.</param>
    /// <param name="seerrUserId">The signed-in user's Seerr id, or null.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The title payload, or null when TMDB has no such title.</returns>
    public async Task<RekoTitlePayload?> BuildTitleAsync(
        int id,
        string mediaType,
        User? user,
        int? seerrUserId,
        CancellationToken cancellationToken)
    {
        if (!_tmdb.IsConfigured)
        {
            return null;
        }

        var factory = await GetCardFactoryAsync(cancellationToken).ConfigureAwait(false);
        var isMovie = mediaType != "tv";
        var result = new RekoTitlePayload();

        if (isMovie)
        {
            var detail = await _tmdb.GetMovieDetailAsync(id, cancellationToken).ConfigureAwait(false);
            if (detail is null)
            {
                return null;
            }

            result.Card = factory.CreateCard(
                new TmdbListItem
                {
                    Id = detail.Id,
                    MediaType = "movie",
                    Title = detail.Title,
                    OriginalTitle = detail.OriginalTitle,
                    Overview = detail.Overview,
                    ReleaseDate = detail.ReleaseDate,
                    PosterPath = detail.PosterPath,
                    BackdropPath = detail.BackdropPath,
                    VoteAverage = detail.VoteAverage,
                    VoteCount = detail.VoteCount,
                    Popularity = detail.Popularity,
                    GenreIds = detail.Genres.Select(g => g.Id).ToList()
                },
                "movie");

            result.Card.Runtime = detail.Runtime;
            result.Card.Certification = factory.ResolveCertification(detail.ReleaseDates, null);
            result.Card.Backdrop = factory.PickBackdrop(detail.Images?.Backdrops) ?? result.Card.Backdrop;
            result.Card.Logo = factory.PickLogo(detail.Images?.Logos);

            result.Tagline = detail.Tagline;
            result.Companies = detail.ProductionCompanies
                .Select(c => c.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();
            result.TrailerKey = CardFactory.PickTrailerKey(detail.Videos?.Results, _tmdb.ImageLanguage);
            result.Providers = MapProviders(detail.WatchProviders, factory.Region, factory);
            result.Cast = MapCast(detail.Credits?.Cast, factory);
            result.CollectionId = detail.BelongsToCollection?.Id;
            result.CollectionName = detail.BelongsToCollection?.Name;

            var related = new List<TmdbListItem>(
                (detail.Recommendations ?? detail.Similar)?.Results ?? new List<TmdbListItem>());

            if (detail.BelongsToCollection is not null)
            {
                var collection = await SafeAsync(
                    () => _tmdb.GetCollectionAsync(detail.BelongsToCollection!.Id, cancellationToken),
                    "collection",
                    (TmdbCollectionDetail?)null).ConfigureAwait(false);

                if (collection is not null)
                {
                    related.AddRange(collection.Parts);
                }
            }

            result.Similar = factory.CreateCards(related, "movie");
        }
        else
        {
            var detail = await _tmdb.GetSeriesDetailAsync(id, cancellationToken).ConfigureAwait(false);
            if (detail is null)
            {
                return null;
            }

            result.Card = factory.CreateCard(
                new TmdbListItem
                {
                    Id = detail.Id,
                    MediaType = "tv",
                    Name = detail.Name,
                    OriginalName = detail.OriginalName,
                    Overview = detail.Overview,
                    FirstAirDate = detail.FirstAirDate,
                    PosterPath = detail.PosterPath,
                    BackdropPath = detail.BackdropPath,
                    VoteAverage = detail.VoteAverage,
                    VoteCount = detail.VoteCount,
                    Popularity = detail.Popularity,
                    GenreIds = detail.Genres.Select(g => g.Id).ToList()
                },
                "tv");

            result.Card.EpisodeRuntime = detail.EpisodeRunTime.Count > 0
                ? (int)Math.Round(detail.EpisodeRunTime.Average())
                : null;
            result.Card.SeasonCount = detail.Seasons.Count(s => s.SeasonNumber > 0);
            result.Card.Certification = factory.ResolveCertification(null, detail.ContentRatings);
            result.Card.Backdrop = factory.PickBackdrop(detail.Images?.Backdrops) ?? result.Card.Backdrop;
            result.Card.Logo = factory.PickLogo(detail.Images?.Logos);

            result.Tagline = detail.Tagline;
            result.Companies = detail.Network is null
                ? new List<string>()
                : new List<string> { detail.Network.Name };
            result.TrailerKey = CardFactory.PickTrailerKey(detail.Videos?.Results, _tmdb.ImageLanguage);
            result.Providers = MapProviders(detail.WatchProviders, factory.Region, factory);
            result.Cast = MapCast(detail.AggregateCredits?.Cast, factory);
            result.Similar = factory.CreateCards(
                (detail.Recommendations ?? detail.Similar)?.Results ?? new List<TmdbListItem>(),
                "tv");

            var requestIndex = await GetRequestIndexAsync(cancellationToken).ConfigureAwait(false);
            requestIndex.TryGetValue(id, out var mediaInfo);

            foreach (var season in detail.Seasons.OrderBy(s => s.SeasonNumber))
            {
                var seerrSeason = mediaInfo?.Seasons.FirstOrDefault(s => s.SeasonNumber == season.SeasonNumber);
                result.Seasons.Add(new RekoSeason
                {
                    Number = season.SeasonNumber,
                    Name = season.Name,
                    Overview = string.IsNullOrWhiteSpace(season.Overview) ? null : season.Overview,
                    Poster = factory.BuildImageUrl(season.PosterPath, "w342"),
                    Date = string.IsNullOrWhiteSpace(season.AirDate) ? null : season.AirDate,
                    EpisodeCount = season.EpisodeCount,
                    RequestStatus = seerrSeason is null ? null : CardFactory.DescribeMediaStatus(seerrSeason.Status)
                });
            }
        }

        result.Similar = result.Similar
            .Where(c => c.Id != id)
            .GroupBy(c => c.Id)
            .Select(g => g.First())
            .Take(30)
            .ToList();

        var withSelf = new List<RekoCard> { result.Card };
        await ApplyOverlaysAsync(withSelf, user, cancellationToken).ConfigureAwait(false);
        await ApplyOverlaysAsync(result.Similar, user, cancellationToken).ConfigureAwait(false);

        _ = seerrUserId;
        return result;
    }

    /// <summary>
    /// Builds the episode list for a season.
    /// </summary>
    /// <param name="seriesId">The TMDB series id.</param>
    /// <param name="seasonNumber">The season number.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The season payload, or null when TMDB has no such season.</returns>
    public async Task<RekoSeasonPayload?> BuildSeasonAsync(int seriesId, int seasonNumber, CancellationToken cancellationToken)
    {
        if (!_tmdb.IsConfigured)
        {
            return null;
        }

        var factory = await GetCardFactoryAsync(cancellationToken).ConfigureAwait(false);
        var season = await _tmdb.GetSeasonAsync(seriesId, seasonNumber, cancellationToken).ConfigureAwait(false);
        if (season is null)
        {
            return null;
        }

        var result = new RekoSeasonPayload();
        foreach (var episode in season.Episodes.OrderBy(e => e.EpisodeNumber))
        {
            result.Episodes.Add(new RekoEpisode
            {
                Number = episode.EpisodeNumber,
                Name = episode.Name,
                Overview = string.IsNullOrWhiteSpace(episode.Overview) ? null : episode.Overview,
                Still = factory.BuildImageUrl(episode.StillPath, "w300"),
                Date = string.IsNullOrWhiteSpace(episode.AirDate) ? null : episode.AirDate,
                Runtime = episode.Runtime,
                Rating = Math.Round(episode.VoteAverage, 1)
            });
        }

        return result;
    }

    /// <summary>
    /// Builds a person page.
    /// </summary>
    /// <param name="id">The TMDB person id.</param>
    /// <param name="user">The signed-in user, or null.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The person payload, or null.</returns>
    public async Task<RekoPersonPayload?> BuildPersonAsync(int id, User? user, CancellationToken cancellationToken)
    {
        if (!_tmdb.IsConfigured)
        {
            return null;
        }

        var detail = await _tmdb.GetPersonAsync(id, cancellationToken).ConfigureAwait(false);
        if (detail is null)
        {
            return null;
        }

        var factory = await GetCardFactoryAsync(cancellationToken).ConfigureAwait(false);
        var credits = (detail.CombinedCredits?.Cast ?? new List<TmdbListItem>())
            .Concat(detail.CombinedCredits?.Crew ?? new List<TmdbListItem>())
            .Where(c => !string.Equals(c.MediaType, "person", StringComparison.Ordinal))
            .GroupBy(c => c.Id)
            .Select(g => g.First())
            .OrderByDescending(c => c.Popularity)
            .ToList();

        var result = new RekoPersonPayload
        {
            Name = FirstNonEmpty(detail.Name, "Unknown") ?? "Unknown",
            Biography = string.IsNullOrWhiteSpace(detail.Biography) ? null : detail.Biography,
            Image = factory.BuildImageUrl(detail.ProfilePath, "w185"),
            Birthday = string.IsNullOrWhiteSpace(detail.Birthday) ? null : detail.Birthday,
            PlaceOfBirth = string.IsNullOrWhiteSpace(detail.PlaceOfBirth) ? null : detail.PlaceOfBirth,
            Credits = factory.CreateCards(credits, "all")
        };

        await ApplyOverlaysAsync(result.Credits, user, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Runs a TMDB multi search.
    /// </summary>
    /// <param name="query">The search text.</param>
    /// <param name="page">The 1-based page number.</param>
    /// <param name="user">The signed-in user, or null.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The browse payload.</returns>
    public async Task<RekoBrowsePayload> SearchAsync(
        string query,
        int page,
        User? user,
        CancellationToken cancellationToken)
    {
        var result = new RekoBrowsePayload { Page = Math.Max(1, page) };

        if (!_tmdb.IsConfigured || string.IsNullOrWhiteSpace(query))
        {
            return result;
        }

        var factory = await GetCardFactoryAsync(cancellationToken).ConfigureAwait(false);
        var search = await _tmdb.SearchAsync(query.Trim(), result.Page, false, cancellationToken).ConfigureAwait(false);
        if (search is null)
        {
            return result;
        }

        result.Total = search.TotalResults;
        result.TotalPages = (int)Math.Ceiling(search.TotalResults / 20d);
        result.Items = factory.CreateCards(search.Results, "movie");
        await ApplyOverlaysAsync(result.Items, user, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Runs a discover browse, used by the category pages.
    /// </summary>
    /// <param name="mediaType">Either <c>movie</c> or <c>tv</c>.</param>
    /// <param name="query">The discover query parameters.</param>
    /// <param name="page">The 1-based page number.</param>
    /// <param name="user">The signed-in user, or null.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The browse payload.</returns>
    public async Task<RekoBrowsePayload> BrowseAsync(
        string mediaType,
        IReadOnlyDictionary<string, string> query,
        int page,
        User? user,
        CancellationToken cancellationToken)
    {
        var result = new RekoBrowsePayload { Page = Math.Max(1, page) };

        if (!_tmdb.IsConfigured)
        {
            return result;
        }

        var factory = await GetCardFactoryAsync(cancellationToken).ConfigureAwait(false);
        var parameters = new Dictionary<string, string>(query, StringComparer.Ordinal)
        {
            ["page"] = result.Page.ToString(CultureInfo.InvariantCulture),
            ["include_adult"] = "false"
        };

        var response = mediaType == "tv"
            ? await _tmdb.DiscoverTvAsync(parameters, cancellationToken).ConfigureAwait(false)
            : await _tmdb.DiscoverMoviesAsync(parameters, cancellationToken).ConfigureAwait(false);

        if (response is null)
        {
            return result;
        }

        result.Total = response.TotalResults;
        result.TotalPages = response.TotalPages;
        result.Items = factory.CreateCards(response.Results, mediaType);
        await ApplyOverlaysAsync(result.Items, user, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Gets the global request index, tolerating an unreachable Seerr.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The request index, empty when Seerr is unavailable.</returns>
    public async Task<Dictionary<int, SeerrMediaInfo>> GetRequestIndexAsync(CancellationToken cancellationToken)
    {
        if (!_seerr.IsConfigured)
        {
            return new Dictionary<int, SeerrMediaInfo>();
        }

        try
        {
            return await _seerr.GetRequestIndexAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SeerrException ex)
        {
            _logger.LogWarning(ex, "Could not read the Seerr request index; request badges will be hidden.");
            return new Dictionary<int, SeerrMediaInfo>();
        }
    }

    /// <summary>
    /// Resolves the Seerr user id for a Jellyfin user, or null when they are not mapped.
    /// </summary>
    /// <param name="user">The Jellyfin user, or null.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The Seerr user id, or null.</returns>
    public async Task<int?> ResolveSeerrUserAsync(User? user, CancellationToken cancellationToken)
    {
        if (user is null || !_seerr.IsConfigured)
        {
            return null;
        }

        try
        {
            var mapped = await _seerr.ResolveJellyfinUserAsync(user.Id, cancellationToken).ConfigureAwait(false);
            return mapped?.Id;
        }
        catch (SeerrException ex)
        {
            _logger.LogWarning(ex, "Could not map Jellyfin user {User} to a Seerr user.", user.Username);
            return null;
        }
    }

    private static List<RowDefinition> BuildRailDefinitions()
    {
        var config = Plugin.Instance?.Configuration;
        var global = RowCatalog.ApplyOrder(RowCatalog.Build(), config?.RailOrder ?? string.Empty);

        var personalized = RowCatalog.BuildPersonalized(
            config?.EnableContinueWatching ?? true,
            config?.EnablePersonalizedRows ?? true);

        // Personalised rails first, the way a streaming home page leads with what is relevant to you.
        return personalized.Concat(global).ToList();
    }

    private async Task<CardFactory> GetCardFactoryAsync(CancellationToken cancellationToken)
    {
        // Both calls are served from TmdbClient's TTL cache after the first hit, so this is a
        // dictionary read, not a network round trip. Building the factory is therefore cheap enough
        // to do per request, which keeps it automatically correct when a setting changes.
        await _tmdb.GetMovieGenresAsync(cancellationToken).ConfigureAwait(false);
        await _tmdb.GetTvGenresAsync(cancellationToken).ConfigureAwait(false);

        var config = _config.Current;
        var region = string.IsNullOrWhiteSpace(config.TmdbRegion) ? "US" : config.TmdbRegion.Trim().ToUpperInvariant();

        return new CardFactory(
            _libraryManager,
            _userDataManager,
            _tmdb.GetMovieGenresCached(),
            _tmdb.GetTvGenresCached(),
            _tmdb.ImageLanguage,
            region,
            _tmdb.ImageBaseUrl);
    }

    private async Task<List<RekoCard>> BuildHeroAsync(CardFactory factory, User? user, CancellationToken cancellationToken)
    {
        var wanted = Math.Clamp(_config.Current.HeroCount, 1, 10);

        // Trending carries a 16:9 backdrop on almost every row and is the strongest "today" signal.
        var trending = await _tmdb.GetTrendingAsync("all", "week", cancellationToken).ConfigureAwait(false);
        var cards = factory.CreateCards(trending?.Results ?? new List<TmdbListItem>(), "all");

        if (cards.Count < wanted)
        {
            var popular = await _tmdb.GetPopularMoviesAsync(1, cancellationToken).ConfigureAwait(false);
            var extra = factory.CreateCards(popular?.Results ?? new List<TmdbListItem>(), "movie");
            cards.AddRange(extra.Where(e => cards.All(c => c.Id != e.Id)));
        }

        // A hero with no backdrop is a blank billboard, so drop anything that cannot fill one.
        var usable = cards
            .Where(c => !string.IsNullOrWhiteSpace(c.Backdrop))
            .Take(wanted)
            .ToList();

        await ApplyHeroArtworkAsync(factory, usable, cancellationToken).ConfigureAwait(false);
        await ApplyOverlaysAsync(usable, user, cancellationToken).ConfigureAwait(false);
        return usable;
    }

    /// <summary>
    /// Enriches hero cards with the artwork a list payload cannot carry.
    /// </summary>
    /// <remarks>
    /// The hero is the one place a title lockup matters, and a billboard that says "The Matrix
    /// Reloaded" in a 64px heading looks like a settings page. TMDB list objects carry no logo, so
    /// this costs one detail call per hero slot. That is at most ten calls, they run concurrently,
    /// and they are cached for hours, so the price is paid once per trending rotation rather than on
    /// every page view.
    /// </remarks>
    /// <param name="factory">The card factory.</param>
    /// <param name="cards">The hero cards.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the enrichment is done or has failed.</returns>
    private async Task ApplyHeroArtworkAsync(CardFactory factory, List<RekoCard> cards, CancellationToken cancellationToken)
    {
        await Task.WhenAll(cards.Select(card => EnrichAsync(card, factory, cancellationToken)))
            .ConfigureAwait(false);

        async Task EnrichAsync(RekoCard card, CardFactory localFactory, CancellationToken token)
        {
            if (card.Type == "tv")
            {
                var series = await SafeAsync(
                    () => _tmdb.GetSeriesDetailAsync(card.Id, token),
                    "hero-artwork:" + card.Id.ToString(CultureInfo.InvariantCulture),
                    null).ConfigureAwait(false);

                if (series is null)
                {
                    return;
                }

                card.Logo = localFactory.PickLogo(series.Images?.Logos);
                card.Backdrop = localFactory.PickBackdrop(series.Images?.Backdrops) ?? card.Backdrop;
                card.EpisodeRuntime = series.EpisodeRunTime.Count > 0
                    ? (int)Math.Round(series.EpisodeRunTime.Average())
                    : null;
                card.SeasonCount = series.Seasons.Count(s => s.SeasonNumber > 0);
                card.Certification = localFactory.ResolveCertification(null, series.ContentRatings);
                return;
            }

            var movie = await SafeAsync(
                () => _tmdb.GetMovieDetailAsync(card.Id, token),
                "hero-artwork:" + card.Id.ToString(CultureInfo.InvariantCulture),
                null).ConfigureAwait(false);

            if (movie is null)
            {
                return;
            }

            card.Logo = localFactory.PickLogo(movie.Images?.Logos);
            card.Backdrop = localFactory.PickBackdrop(movie.Images?.Backdrops) ?? card.Backdrop;
            card.Runtime = movie.Runtime;
            card.Certification = localFactory.ResolveCertification(movie.ReleaseDates, null);
        }
    }

    private async Task<RekoRail> BuildRailAsync(
        RowDefinition definition,
        CardFactory factory,
        User? user,
        CancellationToken cancellationToken)
    {
        var title = definition.Title;
        List<RekoCard> items;

        switch (definition.Source)
        {
            case RowSource.Trending:
                var trending = await _tmdb
                    .GetTrendingAsync(definition.MediaType, WindowOf(definition), cancellationToken)
                    .ConfigureAwait(false);
                items = factory.CreateCards(trending?.Results ?? new List<TmdbListItem>(), definition.MediaType);
                break;

            case RowSource.Popular:
                var popular = definition.MediaType == "tv"
                    ? await _tmdb.GetPopularTvAsync(1, cancellationToken).ConfigureAwait(false)
                    : await _tmdb.GetPopularMoviesAsync(1, cancellationToken).ConfigureAwait(false);
                items = factory.CreateCards(popular?.Results ?? new List<TmdbListItem>(), definition.MediaType);
                break;

            case RowSource.OnTheAir:
                var airing = await _tmdb.GetOnTheAirAsync(1, cancellationToken).ConfigureAwait(false);
                items = factory.CreateCards(airing?.Results ?? new List<TmdbListItem>(), "tv");
                break;

            case RowSource.Discover:
                // There is no discover/all. A rail that asked for both gets movies, which is the
                // larger catalogue, and no rail definition relies on a mixed discover row.
                var query = new Dictionary<string, string>(definition.Query, StringComparer.Ordinal);
                var discover = await _tmdb
                    .DiscoverMoviesAsync(query, cancellationToken)
                    .ConfigureAwait(false);
                items = factory.CreateCards(discover?.Results ?? new List<TmdbListItem>(), "movie");
                break;

            case RowSource.ContinueWatching:
                items = await BuildContinueWatchingAsync(factory, user, definition.MaxItems, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case RowSource.BecauseYouWatched:
                var watched = await BuildBecauseYouWatchedAsync(factory, user, definition.MaxItems, cancellationToken)
                    .ConfigureAwait(false);
                items = watched.Items;
                title = watched.Title;
                break;

            case RowSource.BecauseYouLike:
                items = await BuildBecauseYouLikeAsync(factory, user, definition.MaxItems, cancellationToken)
                    .ConfigureAwait(false);
                break;

            default:
                items = new List<RekoCard>();
                break;
        }

        items = items.Take(definition.Ranked ? 10 : definition.MaxItems).ToList();

        var rail = new RekoRail
        {
            Id = definition.Id,
            Title = title,
            Ranked = definition.Ranked,
            Personalized = definition.Personalized,
            Items = items
        };

        await ApplyOverlaysAsync(items, user, cancellationToken).ConfigureAwait(false);
        return rail;
    }

    private async Task<List<RekoCard>> BuildContinueWatchingAsync(
        CardFactory factory,
        User? user,
        int maxItems,
        CancellationToken cancellationToken)
    {
        if (user is null || !_libraryIndex.IsReady)
        {
            return new List<RekoCard>();
        }

        var index = _libraryIndex.Snapshot;
        var query = new InternalItemsQuery
        {
            User = user,
            IsResumable = true,
            Recursive = true,
            OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
            Limit = maxItems * 3
        };

        var resumable = SafeGetItemList(query);
        if (resumable.Count == 0)
        {
            return new List<RekoCard>();
        }

        var cards = new List<RekoCard>(maxItems);
        var seen = new HashSet<int>();

        foreach (var item in resumable)
        {
            if (cards.Count >= maxItems)
            {
                break;
            }

            if (item.GetBaseItemKind() == BaseItemKind.Episode)
            {
                // A half-watched episode advertises the show, not the episode, so the card carries
                // the series' TMDB id and plays through Jellyfin's own resume logic.
                var series = item is Episode episode ? episode.Series : null;
                if (series is null
                    || !TryGetTmdbId(series, out var seriesTmdbId)
                    || !index.TryGetValue(seriesTmdbId, out var seriesEntry)
                    || !seen.Add(seriesTmdbId))
                {
                    continue;
                }

                var seriesCard = factory.CreateCard(
                    new TmdbListItem
                    {
                        Id = seriesTmdbId,
                        MediaType = "tv",
                        Name = series.Name,
                        Overview = series.Overview,
                        FirstAirDate = series.PremiereDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        VoteAverage = series.CommunityRating ?? 0,
                        PosterPath = series.GetImagePath(ImageType.Primary, 0),
                        BackdropPath = series.GetImagePath(ImageType.Backdrop, 0)
                    },
                    "tv");

                seriesCard.InLibrary = true;
                seriesCard.JellyfinItemId = seriesEntry.ItemId.ToString("D", CultureInfo.InvariantCulture);
                seriesCard.JellyfinKind = seriesEntry.Kind.ToString();
                seriesCard.Resumable = true;
                cards.Add(seriesCard);
                continue;
            }

            if (!TryGetTmdbId(item, out var tmdbId)
                || !seen.Add(tmdbId)
                || !index.TryGetValue(tmdbId, out var entry))
            {
                continue;
            }

            var card = factory.CreateCard(
                new TmdbListItem
                {
                    Id = tmdbId,
                    MediaType = item.GetBaseItemKind() == BaseItemKind.Movie ? "movie" : "tv",
                    Title = item.GetBaseItemKind() == BaseItemKind.Movie ? item.Name : null,
                    Name = item.GetBaseItemKind() == BaseItemKind.Movie ? null : item.Name,
                    Overview = item.Overview,
                    ReleaseDate = item.PremiereDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    FirstAirDate = item.PremiereDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    VoteAverage = item.CommunityRating ?? 0,
                    PosterPath = item.GetImagePath(ImageType.Primary, 0),
                    BackdropPath = item.GetImagePath(ImageType.Backdrop, 0)
                },
                item.GetBaseItemKind() == BaseItemKind.Movie ? "movie" : "tv");

            card.InLibrary = true;
            card.JellyfinItemId = entry.ItemId.ToString("D", CultureInfo.InvariantCulture);
            card.JellyfinKind = entry.Kind.ToString();
            card.Resumable = true;

            if (entry.RunTimeTicks is > 0)
            {
                var userData = _userDataManager.GetUserData(user, item);
                if (userData is not null && userData.PlaybackPositionTicks > 0)
                {
                    card.Progress = Math.Clamp((double)userData.PlaybackPositionTicks / entry.RunTimeTicks.Value, 0, 1);
                }
            }

            cards.Add(card);
        }

        return cards;
    }

    private async Task<RailResult> BuildBecauseYouWatchedAsync(
        CardFactory factory,
        User? user,
        int maxItems,
        CancellationToken cancellationToken)
    {
        if (user is null || !_libraryIndex.IsReady)
        {
            return RailResult.Empty("Because You Watched");
        }

        var seeds = GetWatchedSeeds(user);
        if (seeds.Count == 0)
        {
            return RailResult.Empty("Because You Watched");
        }

        // The most recent thing watched is the reason the row exists, and the row title says so
        // rather than being a generic "Because You Watched".
        var seed = seeds[0];
        var title = string.IsNullOrWhiteSpace(seed.Name) ? "Because You Watched" : "Because You Watched " + seed.Name;

        var recommendations = await SafeAsync(
            () => _tmdb.GetRecommendationsAsync(seed.Type, seed.TmdbId, cancellationToken),
            "recommend:" + seed.TmdbId,
            (TmdbPage<TmdbListItem>?)null).ConfigureAwait(false);

        if (recommendations is null)
        {
            return RailResult.Empty(title);
        }

        var seen = seeds.Select(s => s.TmdbId).ToHashSet();
        var items = factory
            .CreateCards(recommendations.Results, seed.Type)
            .Where(c => !seen.Contains(c.Id))
            .Take(maxItems)
            .ToList();

        return new RailResult(title, items);
    }

    private async Task<List<RekoCard>> BuildBecauseYouLikeAsync(
        CardFactory factory,
        User? user,
        int maxItems,
        CancellationToken cancellationToken)
    {
        if (user is null || !_libraryIndex.IsReady)
        {
            return new List<RekoCard>();
        }

        var seeds = GetWatchedSeeds(user);
        if (seeds.Count == 0)
        {
            return new List<RekoCard>();
        }

        // Weight genres by how many watched titles carry them, then take the strongest few. The
        // user's own Jellyfin genre tags are the signal; no external profile service is involved.
        var weights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var seed in seeds)
        {
            foreach (var genre in seed.Genres)
            {
                weights[genre] = weights.TryGetValue(genre, out var count) ? count + 1 : 1;
            }
        }

        if (weights.Count == 0)
        {
            return new List<RekoCard>();
        }

        var movieGenres = await _tmdb.GetMovieGenresAsync(cancellationToken).ConfigureAwait(false);
        var topGenreIds = weights
            .OrderByDescending(p => p.Value)
            .ThenBy(p => p.Key, StringComparer.Ordinal)
            .Take(3)
            .Select(p => movieGenres.FirstOrDefault(g => string.Equals(g.Value, p.Key, StringComparison.OrdinalIgnoreCase)).Key)
            .Where(id => id > 0)
            .ToList();

        if (topGenreIds.Count == 0)
        {
            return new List<RekoCard>();
        }

        var seen = seeds.Select(s => s.TmdbId).ToHashSet();
        var results = new List<RekoCard>();

        foreach (var genreId in topGenreIds)
        {
            if (results.Count >= maxItems)
            {
                break;
            }

            var page = await SafeAsync(
                () => _tmdb.DiscoverMoviesAsync(
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["with_genres"] = genreId.ToString(CultureInfo.InvariantCulture),
                        ["sort_by"] = "popularity.desc",
                        ["vote_count.gte"] = "200",
                        ["include_adult"] = "false"
                    },
                    cancellationToken),
                "discover:genre:" + genreId.ToString(CultureInfo.InvariantCulture),
                (TmdbPage<TmdbListItem>?)null).ConfigureAwait(false);

            if (page is null)
            {
                continue;
            }

            foreach (var card in factory.CreateCards(page.Results, "movie"))
            {
                if (results.Count >= maxItems || !seen.Add(card.Id))
                {
                    continue;
                }

                results.Add(card);
            }
        }

        return results;
    }

    private List<WatchedSeed> GetWatchedSeeds(User user)
    {
        var config = _config.Current;
        var query = new InternalItemsQuery
        {
            User = user,
            IsPlayed = true,
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
            Limit = Math.Max(50, config.MinimumWatchedForPersonalized * 20)
        };

        var played = SafeGetItemList(query);
        if (played.Count < Math.Max(1, config.MinimumWatchedForPersonalized))
        {
            return new List<WatchedSeed>();
        }

        var seeds = new List<WatchedSeed>();
        var seen = new HashSet<int>();

        foreach (var item in played)
        {
            if (seeds.Count >= 12 || !TryGetTmdbId(item, out var tmdbId) || !seen.Add(tmdbId))
            {
                continue;
            }

            seeds.Add(new WatchedSeed(
                tmdbId,
                item.GetBaseItemKind() == BaseItemKind.Movie ? "movie" : "tv",
                item.Name,
                item.Genres ?? Array.Empty<string>()));
        }

        return seeds;
    }

    private IReadOnlyList<BaseItem> SafeGetItemList(InternalItemsQuery query)
    {
        try
        {
            return _libraryManager.GetItemList(query) ?? new List<BaseItem>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reko could not query the library.");
            return new List<BaseItem>();
        }
    }

    private static bool TryGetTmdbId(BaseItem item, out int tmdbId)
    {
        tmdbId = 0;
        return item.ProviderIds is not null
               && item.ProviderIds.TryGetValue(LibraryIndex.TmdbProviderKey, out var raw)
               && int.TryParse(raw, out tmdbId)
               && tmdbId > 0;
    }

    private async Task ApplyOverlaysAsync(
        List<RekoCard> cards,
        User? user,
        CancellationToken cancellationToken)
    {
        if (cards.Count == 0)
        {
            return;
        }

        if (_libraryIndex.IsReady)
        {
            try
            {
                var factory = await GetCardFactoryAsync(cancellationToken).ConfigureAwait(false);
                factory.ApplyLocalState(cards, _libraryIndex.Snapshot, user);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not apply library state; in-library badges will be missing.");
            }
        }

        if (_seerr.IsConfigured)
        {
            var index = await GetRequestIndexAsync(cancellationToken).ConfigureAwait(false);
            CardFactory.ApplyRequestState(cards, index);
        }
    }

    private static List<RekoProviderGroup> MapProviders(TmdbWatchProviders? providers, string region, CardFactory factory)
    {
        if (providers is null || providers.Results.Count == 0)
        {
            return new List<RekoProviderGroup>();
        }

        // TMDB returns every region it knows about. Showing them all would be noise, so the
        // configured region wins and the first available region is the fallback.
        var set = providers.Results.TryGetValue(region, out var exact)
            ? exact
            : providers.Results.Values.First();

        return new List<RekoProviderGroup>
        {
            Map("Stream", set.Flatrate),
            Map("Free with ads", set.Ads),
            Map("Rent", set.Rent),
            Map("Buy", set.Buy)
        }
        .Where(g => g.Providers.Count > 0)
        .ToList();

        RekoProviderGroup Map(string label, List<TmdbProvider> source)
            => new()
            {
                Label = label,
                Providers = source
                    .OrderBy(p => p.DisplayPriority)
                    .GroupBy(p => p.ProviderId)
                    .Select(g => g.First())
                    .Take(8)
                    .Select(p => new RekoProvider
                    {
                        Id = p.ProviderId,
                        Name = p.ProviderName,
                        Logo = factory.BuildImageUrl(p.LogoPath, "w92")
                    })
                    .ToList()
            };
    }

    private static List<RekoPerson> MapCast(List<TmdbPerson>? cast, CardFactory factory)
    {
        if (cast is null)
        {
            return new List<RekoPerson>();
        }

        return cast
            .Where(p => !string.IsNullOrWhiteSpace(p.Name) && !string.IsNullOrWhiteSpace(p.ProfilePath))
            .OrderByDescending(p => p.Popularity)
            .Take(20)
            .Select(p => new RekoPerson
            {
                Id = p.Id,
                Name = p.Name,
                Role = FirstNonEmpty(p.Character, p.Job),
                Image = factory.BuildImageUrl(p.ProfilePath, "w185")
            })
            .ToList();
    }

    private static string WindowOf(RowDefinition definition)
        => definition.Query.TryGetValue("time_window", out var window) ? window : "week";

    private static string? FirstNonEmpty(string? first, string? second)
        => string.IsNullOrWhiteSpace(first) ? (string.IsNullOrWhiteSpace(second) ? null : second) : first;

    private async Task<T?> SafeAsync<T>(Func<Task<T>> operation, string what, T? fallback)
        where T : class
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The person closed the tab or moved on. Nothing is wrong, and the caller has already
            // gone: propagate so the request unwinds quietly, rather than logging a failure and
            // building a half-finished page nobody is looking at any more.
            throw;
        }
        catch (TmdbException ex)
        {
            _logger.LogWarning("Reko rail {Rail} could not be built: {Reason}", what, ex.Message);

            return fallback;
        }
        catch (SeerrException ex)
        {
            _logger.LogWarning("Reko rail {Rail} could not reach Seerr: {Reason}", what, ex.Message);

            return fallback;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reko rail {Rail} failed unexpectedly.", what);
            return fallback;
        }
    }

    private readonly record struct WatchedSeed(int TmdbId, string Type, string Name, IReadOnlyList<string> Genres);

    private readonly record struct RailResult(string Title, List<RekoCard> Items)
    {
        public static RailResult Empty(string title) => new(title, new List<RekoCard>());
    }
}
