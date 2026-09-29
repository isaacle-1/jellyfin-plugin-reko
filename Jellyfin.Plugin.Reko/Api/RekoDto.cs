using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Reko.Api;

/// <summary>
/// A single card, normalised across movies and series.
/// </summary>
/// <remarks>
/// Image URLs are resolved to absolute URLs on the server so the client never needs to know TMDB's
/// image base or size tables, and so a size mistake is a server-side bug rather than a broken
/// layout. Every card carries its own library and request state, which means a rail of 20 titles is
/// still exactly one round trip.
/// </remarks>
public sealed class RekoCard
{
    /// <summary>Gets or sets the TMDB id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the media type, either <c>movie</c> or <c>tv</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "movie";

    /// <summary>Gets or sets the display title.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the original title, when it differs.</summary>
    [JsonPropertyName("originalTitle")]
    public string? OriginalTitle { get; set; }

    /// <summary>Gets or sets the synopsis.</summary>
    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    /// <summary>Gets or sets the four digit year, or null when unknown.</summary>
    [JsonPropertyName("year")]
    public int? Year { get; set; }

    /// <summary>Gets or sets the full release or first air date.</summary>
    [JsonPropertyName("date")]
    public string? Date { get; set; }

    /// <summary>Gets or sets the mean vote score, out of ten.</summary>
    [JsonPropertyName("rating")]
    public double Rating { get; set; }

    /// <summary>Gets or sets the vote count.</summary>
    [JsonPropertyName("voteCount")]
    public int VoteCount { get; set; }

    /// <summary>Gets or sets the popularity score.</summary>
    [JsonPropertyName("popularity")]
    public double Popularity { get; set; }

    /// <summary>Gets or sets the genre names.</summary>
    [JsonPropertyName("genres")]
    public List<string> Genres { get; set; } = new();

    /// <summary>Gets or sets the absolute poster URL.</summary>
    [JsonPropertyName("poster")]
    public string? Poster { get; set; }

    /// <summary>Gets or sets the absolute backdrop URL.</summary>
    [JsonPropertyName("backdrop")]
    public string? Backdrop { get; set; }

    /// <summary>Gets or sets the absolute title logo URL, when TMDB has one in the preferred language.</summary>
    [JsonPropertyName("logo")]
    public string? Logo { get; set; }

    /// <summary>Gets or sets the runtime in minutes, for movies. Null for series.</summary>
    [JsonPropertyName("runtime")]
    public int? Runtime { get; set; }

    /// <summary>Gets or sets the certification for the configured region.</summary>
    [JsonPropertyName("certification")]
    public string? Certification { get; set; }

    /// <summary>Gets or sets the average episode runtime in minutes, for series.</summary>
    [JsonPropertyName("episodeRuntime")]
    public int? EpisodeRuntime { get; set; }

    /// <summary>Gets or sets the number of seasons.</summary>
    [JsonPropertyName("seasonCount")]
    public int? SeasonCount { get; set; }

    /// <summary>Gets or sets a value indicating whether the title is in the Jellyfin library.</summary>
    [JsonPropertyName("inLibrary")]
    public bool InLibrary { get; set; }

    /// <summary>Gets or sets the Jellyfin item id, so the client can jump straight to playback.</summary>
    [JsonPropertyName("jellyfinItemId")]
    public string? JellyfinItemId { get; set; }

    /// <summary>Gets or sets the Jellyfin item kind, <c>Movie</c> or <c>Series</c>.</summary>
    [JsonPropertyName("jellyfinKind")]
    public string? JellyfinKind { get; set; }

    /// <summary>Gets or sets a value indicating whether the signed-in user has played the title.</summary>
    [JsonPropertyName("watched")]
    public bool Watched { get; set; }

    /// <summary>Gets or sets a value indicating whether the title is partly watched.</summary>
    [JsonPropertyName("resumable")]
    public bool Resumable { get; set; }

    /// <summary>Gets or sets the resume progress from zero to one.</summary>
    [JsonPropertyName("progress")]
    public double Progress { get; set; }

    /// <summary>
    /// Gets or sets the Seerr media status name, for example <c>pending</c> or <c>available</c>.
    /// Null when Seerr is not configured or knows nothing about the title.
    /// </summary>
    [JsonPropertyName("requestStatus")]
    public string? RequestStatus { get; set; }
}

/// <summary>
/// A horizontally scrolling row of cards.
/// </summary>
public sealed class RekoRail
{
    /// <summary>Gets or sets the stable rail id, used for lazy loading and deep links.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the rail title.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the rail is one of the ranked Top 10 rows, which
    /// render with oversized numerals and a larger card.
    /// </summary>
    [JsonPropertyName("ranked")]
    public bool Ranked { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the rail is generated from this user's playback
    /// history rather than from the global catalogue.
    /// </summary>
    [JsonPropertyName("personalized")]
    public bool Personalized { get; set; }

    /// <summary>Gets or sets the cards.</summary>
    [JsonPropertyName("items")]
    public List<RekoCard> Items { get; set; } = new();
}

/// <summary>
/// The feature and presentation flags the client needs before its first render.
/// </summary>
public sealed class RekoClientConfig
{
    /// <summary>Gets or sets the label for the home tab.</summary>
    [JsonPropertyName("tabLabel")]
    public string TabLabel { get; set; } = "Reko";

    /// <summary>Gets or sets the hero rotation interval in seconds.</summary>
    [JsonPropertyName("heroSeconds")]
    public int HeroSeconds { get; set; } = 9;

    /// <summary>Gets or sets a value indicating whether the in-tab search is enabled.</summary>
    [JsonPropertyName("enableSearch")]
    public bool EnableSearch { get; set; }

    /// <summary>Gets or sets a value indicating whether watch providers are shown.</summary>
    [JsonPropertyName("showWatchProviders")]
    public bool ShowWatchProviders { get; set; }

    /// <summary>Gets or sets a value indicating whether trailers are shown.</summary>
    [JsonPropertyName("showTrailers")]
    public bool ShowTrailers { get; set; }

    /// <summary>Gets or sets a value indicating whether cast rows are shown.</summary>
    [JsonPropertyName("showCast")]
    public bool ShowCast { get; set; }

    /// <summary>Gets or sets a value indicating whether Seerr requests are offered.</summary>
    [JsonPropertyName("seerrEnabled")]
    public bool SeerrEnabled { get; set; }

    /// <summary>Gets or sets the Seerr product name, so the UI says "Request on Jellyseerr" or
    /// "Request on Overseerr" rather than a hard-coded brand.</summary>
    [JsonPropertyName("seerrTitle")]
    public string SeerrTitle { get; set; } = "Seerr";

    /// <summary>Gets or sets the Seerr web URL, used as a fallback link.</summary>
    [JsonPropertyName("seerrUrl")]
    public string? SeerrUrl { get; set; }

    /// <summary>Gets or sets a value indicating whether the title page lists seasons.</summary>
    [JsonPropertyName("enableSeasons")]
    public bool EnableSeasons { get; set; } = true;
}

/// <summary>
/// The payload behind <c>GET /Reko/home</c>.
/// </summary>
public sealed class RekoHomePayload
{
    /// <summary>Gets or sets the client configuration.</summary>
    [JsonPropertyName("config")]
    public RekoClientConfig Config { get; set; } = new();

    /// <summary>Gets or sets the rotating hero titles.</summary>
    [JsonPropertyName("hero")]
    public List<RekoCard> Hero { get; set; } = new();

    /// <summary>Gets or sets the rails, in display order.</summary>
    [JsonPropertyName("rails")]
    public List<RekoRail> Rails { get; set; } = new();

    /// <summary>
    /// Gets or sets a non-fatal problem, for example "no TMDB key configured". The client renders the
    /// tab anyway and shows this inline rather than showing an empty page.
    /// </summary>
    [JsonPropertyName("notice")]
    public string? Notice { get; set; }
}

/// <summary>
/// One season as shown on a series page.
/// </summary>
public sealed class RekoSeason
{
    /// <summary>Gets or sets the season number. Zero is the specials season.</summary>
    [JsonPropertyName("number")]
    public int Number { get; set; }

    /// <summary>Gets or sets the season name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Gets or sets the season overview.</summary>
    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    /// <summary>Gets or sets the absolute poster URL.</summary>
    [JsonPropertyName("poster")]
    public string? Poster { get; set; }

    /// <summary>Gets or sets the air date of the first episode.</summary>
    [JsonPropertyName("date")]
    public string? Date { get; set; }

    /// <summary>Gets or sets the episode count.</summary>
    [JsonPropertyName("episodeCount")]
    public int EpisodeCount { get; set; }

    /// <summary>Gets or sets the per-season Seerr status name, or null.</summary>
    [JsonPropertyName("requestStatus")]
    public string? RequestStatus { get; set; }
}

/// <summary>
/// One episode as shown on a series page.
/// </summary>
public sealed class RekoEpisode
{
    /// <summary>Gets or sets the episode number.</summary>
    [JsonPropertyName("number")]
    public int Number { get; set; }

    /// <summary>Gets or sets the episode name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Gets or sets the episode overview.</summary>
    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    /// <summary>Gets or sets the absolute still image URL.</summary>
    [JsonPropertyName("still")]
    public string? Still { get; set; }

    /// <summary>Gets or sets the air date.</summary>
    [JsonPropertyName("date")]
    public string? Date { get; set; }

    /// <summary>Gets or sets the runtime in minutes.</summary>
    [JsonPropertyName("runtime")]
    public int? Runtime { get; set; }

    /// <summary>Gets or sets the mean vote score.</summary>
    [JsonPropertyName("rating")]
    public double Rating { get; set; }
}

/// <summary>
/// A person shown on a title page or a person page.
/// </summary>
public sealed class RekoPerson
{
    /// <summary>Gets or sets the TMDB person id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the person's name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the character or job.</summary>
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    /// <summary>Gets or sets the absolute profile image URL.</summary>
    [JsonPropertyName("image")]
    public string? Image { get; set; }
}

/// <summary>
/// A watch provider bucket.
/// </summary>
public sealed class RekoProviderGroup
{
    /// <summary>Gets or sets the bucket label, for example <c>Stream</c> or <c>Rent</c>.</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets the providers in the bucket.</summary>
    [JsonPropertyName("providers")]
    public List<RekoProvider> Providers { get; set; } = new();
}

/// <summary>
/// A single watch provider.
/// </summary>
public sealed class RekoProvider
{
    /// <summary>Gets or sets the provider id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the provider name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the absolute logo URL.</summary>
    [JsonPropertyName("logo")]
    public string? Logo { get; set; }
}

/// <summary>
/// The payload behind <c>GET /Reko/title</c>.
/// </summary>
public sealed class RekoTitlePayload
{
    /// <summary>Gets or sets the primary card, carrying the library and request state.</summary>
    [JsonPropertyName("card")]
    public RekoCard Card { get; set; } = new();

    /// <summary>Gets or sets the tagline.</summary>
    [JsonPropertyName("tagline")]
    public string? Tagline { get; set; }

    /// <summary>Gets or sets the production companies.</summary>
    [JsonPropertyName("companies")]
    public List<string> Companies { get; set; } = new();

    /// <summary>Gets or sets the seasons, for series.</summary>
    [JsonPropertyName("seasons")]
    public List<RekoSeason> Seasons { get; set; } = new();

    /// <summary>Gets or sets the cast.</summary>
    [JsonPropertyName("cast")]
    public List<RekoPerson> Cast { get; set; } = new();

    /// <summary>Gets or sets the YouTube key of the best trailer.</summary>
    [JsonPropertyName("trailerKey")]
    public string? TrailerKey { get; set; }

    /// <summary>Gets or sets the watch providers for the configured region.</summary>
    [JsonPropertyName("providers")]
    public List<RekoProviderGroup> Providers { get; set; } = new();

    /// <summary>Gets or sets the collection name, when the title belongs to one.</summary>
    [JsonPropertyName("collectionName")]
    public string? CollectionName { get; set; }

    /// <summary>Gets or sets the collection id, when the title belongs to one.</summary>
    [JsonPropertyName("collectionId")]
    public int? CollectionId { get; set; }

    /// <summary>Gets or sets the more-like-this cards.</summary>
    [JsonPropertyName("similar")]
    public List<RekoCard> Similar { get; set; } = new();
}

/// <summary>
/// The payload behind <c>GET /Reko/season</c>.
/// </summary>
public sealed class RekoSeasonPayload
{
    /// <summary>Gets or sets the episodes.</summary>
    [JsonPropertyName("episodes")]
    public List<RekoEpisode> Episodes { get; set; } = new();
}

/// <summary>
/// The payload behind <c>GET /Reko/person</c>.
/// </summary>
public sealed class RekoPersonPayload
{
    /// <summary>Gets or sets the person's name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the biography.</summary>
    [JsonPropertyName("biography")]
    public string? Biography { get; set; }

    /// <summary>Gets or sets the absolute profile image URL.</summary>
    [JsonPropertyName("image")]
    public string? Image { get; set; }

    /// <summary>Gets or sets the date of birth.</summary>
    [JsonPropertyName("birthday")]
    public string? Birthday { get; set; }

    /// <summary>Gets or sets the place of birth.</summary>
    [JsonPropertyName("placeOfBirth")]
    public string? PlaceOfBirth { get; set; }

    /// <summary>Gets or sets the known-for credits.</summary>
    [JsonPropertyName("credits")]
    public List<RekoCard> Credits { get; set; } = new();
}

/// <summary>
/// The payload behind <c>GET /Reko/browse</c> and <c>GET /Reko/search</c>.
/// </summary>
public sealed class RekoBrowsePayload
{
    /// <summary>Gets or sets the cards.</summary>
    [JsonPropertyName("items")]
    public List<RekoCard> Items { get; set; } = new();

    /// <summary>Gets or sets the total result count.</summary>
    [JsonPropertyName("total")]
    public int Total { get; set; }

    /// <summary>Gets or sets the current page.</summary>
    [JsonPropertyName("page")]
    public int Page { get; set; }

    /// <summary>Gets or sets the page count.</summary>
    [JsonPropertyName("totalPages")]
    public int TotalPages { get; set; }
}

/// <summary>
/// The Seerr state of one title, used to refresh badges without reloading a rail.
/// </summary>
public sealed class RekoRequestState
{
    /// <summary>Gets or sets the TMDB id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the media type.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "movie";

    /// <summary>Gets or sets the overall media status name.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>Gets or sets the newest request status name.</summary>
    [JsonPropertyName("requestStatus")]
    public string? RequestStatus { get; set; }

    /// <summary>Gets or sets the per-season status names, keyed by season number.</summary>
    [JsonPropertyName("seasons")]
    public Dictionary<int, string> Seasons { get; set; } = new();
}

/// <summary>
/// The payload behind <c>GET /Reko/seerr/status</c>.
/// </summary>
public sealed class RekoSeerrStatus
{
    /// <summary>Gets or sets a value indicating whether Seerr is configured in Reko.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>Gets or sets a value indicating whether the Seerr instance answered.</summary>
    [JsonPropertyName("reachable")]
    public bool Reachable { get; set; }

    /// <summary>Gets or sets the Seerr version.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>Gets or sets the product name reported by the instance.</summary>
    [JsonPropertyName("product")]
    public string? Product { get; set; }

    /// <summary>Gets or sets a value indicating whether Seerr's setup wizard has been completed.</summary>
    [JsonPropertyName("initialized")]
    public bool Initialized { get; set; }

    /// <summary>Gets or sets the linked media server type name.</summary>
    [JsonPropertyName("mediaServer")]
    public string? MediaServer { get; set; }

    /// <summary>Gets or sets a value indicating whether the signed-in user was matched to a Seerr
    /// account, so requests are attributed to them rather than to the API key owner.</summary>
    [JsonPropertyName("userMapped")]
    public bool UserMapped { get; set; }

    /// <summary>Gets or sets the name of the matched Seerr account.</summary>
    [JsonPropertyName("seerrUser")]
    public string? SeerrUser { get; set; }

    /// <summary>Gets or sets a warning to show the user, for example an incomplete setup.</summary>
    [JsonPropertyName("warning")]
    public string? Warning { get; set; }
}
