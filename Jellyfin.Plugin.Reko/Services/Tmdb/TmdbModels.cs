using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Reko.Services.Tmdb;

/// <summary>
/// The list payload envelope TMDB returns from paged endpoints.
/// </summary>
/// <typeparam name="T">The item type.</typeparam>
public sealed class TmdbPage<T>
{
    /// <summary>Gets or sets the 1-based page number.</summary>
    [JsonPropertyName("page")]
    public int Page { get; set; }

    /// <summary>Gets or sets the results on this page.</summary>
    [JsonPropertyName("results")]
    public List<T> Results { get; set; } = new();

    /// <summary>Gets or sets the total page count.</summary>
    [JsonPropertyName("total_pages")]
    public int TotalPages { get; set; }

    /// <summary>Gets or sets the total result count.</summary>
    [JsonPropertyName("total_results")]
    public int TotalResults { get; set; }
}

/// <summary>
/// The shared shape of a TMDB list item. Movie and TV objects are deliberately merged into one type
/// because the browser rows mix both and branch on <see cref="MediaType"/>.
/// </summary>
public sealed class TmdbListItem
{
    /// <summary>Gets or sets the TMDB id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the media type, populated on <c>trending/all</c> and search results.</summary>
    [JsonPropertyName("media_type")]
    public string? MediaType { get; set; }

    /// <summary>Gets or sets the movie title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>Gets or sets the series name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Gets or sets the movie original title.</summary>
    [JsonPropertyName("original_title")]
    public string? OriginalTitle { get; set; }

    /// <summary>Gets or sets the series original name.</summary>
    [JsonPropertyName("original_name")]
    public string? OriginalName { get; set; }

    /// <summary>Gets or sets the movie release date, <c>yyyy-MM-dd</c>.</summary>
    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }

    /// <summary>Gets or sets the series first air date, <c>yyyy-MM-dd</c>.</summary>
    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; set; }

    /// <summary>Gets or sets the movie overview.</summary>
    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    /// <summary>Gets or sets the poster path.</summary>
    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    /// <summary>Gets or sets the backdrop path.</summary>
    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; set; }

    /// <summary>Gets or sets the mean vote score.</summary>
    [JsonPropertyName("vote_average")]
    public double VoteAverage { get; set; }

    /// <summary>Gets or sets the vote count.</summary>
    [JsonPropertyName("vote_count")]
    public int VoteCount { get; set; }

    /// <summary>Gets or sets the TMDB popularity score.</summary>
    [JsonPropertyName("popularity")]
    public double Popularity { get; set; }

    /// <summary>Gets or sets the adult flag.</summary>
    [JsonPropertyName("adult")]
    public bool Adult { get; set; }

    /// <summary>Gets or sets the genre ids present on list items.</summary>
    [JsonPropertyName("genre_ids")]
    public List<int> GenreIds { get; set; } = new();

    /// <summary>Gets or sets the origin country codes for series.</summary>
    [JsonPropertyName("origin_country")]
    public List<string> OriginCountry { get; set; } = new();

    /// <summary>Gets or sets the original language code.</summary>
    [JsonPropertyName("original_language")]
    public string? OriginalLanguage { get; set; }

    /// <summary>Gets or sets the person name, on person search results.</summary>
    [JsonPropertyName("known_for_department")]
    public string? KnownForDepartment { get; set; }
}

/// <summary>
/// A TMDB genre reference.
/// </summary>
public sealed class TmdbGenre
{
    /// <summary>Gets or sets the genre id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the genre name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// The <c>/3/genre/movie/list</c> and <c>/3/genre/tv/list</c> responses.
/// </summary>
public sealed class TmdbGenreList
{
    /// <summary>Gets or sets the genres.</summary>
    [JsonPropertyName("genres")]
    public List<TmdbGenre> Genres { get; set; } = new();
}

/// <summary>
/// A production company.
/// </summary>
public sealed class TmdbCompany
{
    /// <summary>Gets or sets the company id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the company name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the company logo path.</summary>
    [JsonPropertyName("logo_path")]
    public string? LogoPath { get; set; }

    /// <summary>Gets or sets the company origin country.</summary>
    [JsonPropertyName("origin_country")]
    public string? OriginCountry { get; set; }
}

/// <summary>
/// A cast or crew member.
/// </summary>
public sealed class TmdbPerson
{
    /// <summary>Gets or sets the person id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the person's name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the character name, on cast entries.</summary>
    [JsonPropertyName("character")]
    public string? Character { get; set; }

    /// <summary>Gets or sets the job, on crew entries.</summary>
    [JsonPropertyName("job")]
    public string? Job { get; set; }

    /// <summary>Gets or sets the profile image path.</summary>
    [JsonPropertyName("profile_path")]
    public string? ProfilePath { get; set; }

    /// <summary>Gets or sets the popularity score.</summary>
    [JsonPropertyName("popularity")]
    public double Popularity { get; set; }

    /// <summary>Gets or sets the media type, on aggregate credit entries.</summary>
    [JsonPropertyName("media_type")]
    public string? MediaType { get; set; }
}

/// <summary>
/// The <c>credits</c> / <c>aggregate_credits</c> object.
/// </summary>
public sealed class TmdbCredits
{
    /// <summary>Gets or sets the cast.</summary>
    [JsonPropertyName("cast")]
    public List<TmdbPerson> Cast { get; set; } = new();

    /// <summary>Gets or sets the crew.</summary>
    [JsonPropertyName("crew")]
    public List<TmdbPerson> Crew { get; set; } = new();
}

/// <summary>
/// One entry of the <c>videos</c> object.
/// </summary>
public sealed class TmdbVideo
{
    /// <summary>Gets or sets the video id.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the key, which is the YouTube id for YouTube videos.</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the site, for example <c>YouTube</c>.</summary>
    [JsonPropertyName("site")]
    public string Site { get; set; } = string.Empty;

    /// <summary>Gets or sets the type, for example <c>Trailer</c> or <c>Teaser</c>. Not a fixed enum in the spec.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets a display name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the official flag.</summary>
    [JsonPropertyName("official")]
    public bool Official { get; set; }

    /// <summary>Gets or sets the ISO 639-1 language code.</summary>
    [JsonPropertyName("iso_639_1")]
    public string? Iso6391 { get; set; }

    /// <summary>Gets or sets the ISO 3166-1 country code.</summary>
    [JsonPropertyName("iso_3166_1")]
    public string? Iso31661 { get; set; }

    /// <summary>Gets or sets the published date.</summary>
    [JsonPropertyName("published_at")]
    public string? PublishedAt { get; set; }
}

/// <summary>
/// The <c>videos</c> object.
/// </summary>
public sealed class TmdbVideos
{
    /// <summary>Gets or sets the videos.</summary>
    [JsonPropertyName("results")]
    public List<TmdbVideo> Results { get; set; } = new();
}

/// <summary>
/// One language variant inside a TMDB image.
/// </summary>
public sealed class TmdbImageVariant
{
    /// <summary>Gets or sets the file path.</summary>
    [JsonPropertyName("file_path")]
    public string? FilePath { get; set; }

    /// <summary>Gets or sets the ISO 639-1 language code.</summary>
    [JsonPropertyName("iso_639_1")]
    public string? Iso6391 { get; set; }

    /// <summary>Gets or sets the mean vote for this variant.</summary>
    [JsonPropertyName("vote_average")]
    public double VoteAverage { get; set; }

    /// <summary>Gets or sets the vote count for this variant.</summary>
    [JsonPropertyName("vote_count")]
    public int VoteCount { get; set; }

    /// <summary>Gets or sets the image width in pixels.</summary>
    [JsonPropertyName("width")]
    public int Width { get; set; }
}

/// <summary>
/// One image inside the <c>images</c> object.
/// </summary>
public sealed class TmdbImage
{
    /// <summary>Gets or sets the language variants.</summary>
    [JsonPropertyName("iso_639_1")]
    public string? Iso6391 { get; set; }

    /// <summary>Gets or sets the file path.</summary>
    [JsonPropertyName("file_path")]
    public string? FilePath { get; set; }

    /// <summary>Gets or sets the mean vote for this image.</summary>
    [JsonPropertyName("vote_average")]
    public double VoteAverage { get; set; }

    /// <summary>Gets or sets the vote count for this image.</summary>
    [JsonPropertyName("vote_count")]
    public int VoteCount { get; set; }

    /// <summary>Gets or sets the image width in pixels.</summary>
    [JsonPropertyName("width")]
    public int Width { get; set; }
}

/// <summary>
/// The <c>images</c> object.
/// </summary>
public sealed class TmdbImages
{
    /// <summary>Gets or sets the posters.</summary>
    [JsonPropertyName("posters")]
    public List<TmdbImage> Posters { get; set; } = new();

    /// <summary>Gets or sets the backdrops.</summary>
    [JsonPropertyName("backdrops")]
    public List<TmdbImage> Backdrops { get; set; } = new();

    /// <summary>Gets or sets the logos.</summary>
    [JsonPropertyName("logos")]
    public List<TmdbImage> Logos { get; set; } = new();
}

/// <summary>
/// A watch provider inside <c>watch/providers</c>.
/// </summary>
public sealed class TmdbProvider
{
    /// <summary>Gets or sets the provider id, as used by Seerr.</summary>
    [JsonPropertyName("provider_id")]
    public int ProviderId { get; set; }

    /// <summary>Gets or sets the provider name.</summary>
    [JsonPropertyName("provider_name")]
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>Gets or sets the provider logo path.</summary>
    [JsonPropertyName("logo_path")]
    public string? LogoPath { get; set; }

    /// <summary>Gets or sets the display priority; lower sorts first.</summary>
    [JsonPropertyName("display_priority")]
    public int DisplayPriority { get; set; }
}

/// <summary>
/// A single region block of the <c>watch/providers</c> object.
/// </summary>
public sealed class TmdbProviderRegion
{
    /// <summary>Gets or sets the ISO 3166-1 region code.</summary>
    [JsonPropertyName("iso_3166_1")]
    public string? Iso31661 { get; set; }

    /// <summary>Gets or sets the English display name.</summary>
    [JsonPropertyName("english_name")]
    public string? EnglishName { get; set; }

    /// <summary>Gets or sets the link to the TMDB watch page.</summary>
    [JsonPropertyName("link")]
    public string? Link { get; set; }
}

/// <summary>
/// The <c>watch/providers</c> object.
/// </summary>
public sealed class TmdbWatchProviders
{
    /// <summary>Gets or sets the per-region provider maps.</summary>
    [JsonPropertyName("results")]
    public Dictionary<string, TmdbWatchProviderSet> Results { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The provider buckets for one region.
/// </summary>
public sealed class TmdbWatchProviderSet
{
    /// <summary>Gets or sets the region metadata.</summary>
    [JsonPropertyName("link")]
    public string? Link { get; set; }

    /// <summary>Gets or sets the flatrate (subscription) providers.</summary>
    [JsonPropertyName("flatrate")]
    public List<TmdbProvider> Flatrate { get; set; } = new();

    /// <summary>Gets or sets the free-with-ads providers.</summary>
    [JsonPropertyName("ads")]
    public List<TmdbProvider> Ads { get; set; } = new();

    /// <summary>Gets or sets the rental providers.</summary>
    [JsonPropertyName("rent")]
    public List<TmdbProvider> Rent { get; set; } = new();

    /// <summary>Gets or sets the purchase providers.</summary>
    [JsonPropertyName("buy")]
    public List<TmdbProvider> Buy { get; set; } = new();
}

/// <summary>
/// A TV season summary as returned on the series detail object.
/// </summary>
public sealed class TmdbSeasonSummary
{
    /// <summary>Gets or sets the season id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the season number. Specials are 0.</summary>
    [JsonPropertyName("season_number")]
    public int SeasonNumber { get; set; }

    /// <summary>Gets or sets the season name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Gets or sets the season overview.</summary>
    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    /// <summary>Gets or sets the poster path.</summary>
    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    /// <summary>Gets or sets the air date of the first episode.</summary>
    [JsonPropertyName("air_date")]
    public string? AirDate { get; set; }

    /// <summary>Gets or sets the episode count.</summary>
    [JsonPropertyName("episode_count")]
    public int EpisodeCount { get; set; }
}

/// <summary>
/// One episode inside a season payload.
/// </summary>
public sealed class TmdbEpisode
{
    /// <summary>Gets or sets the episode id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the episode name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Gets or sets the episode number.</summary>
    [JsonPropertyName("episode_number")]
    public int EpisodeNumber { get; set; }

    /// <summary>Gets or sets the season number.</summary>
    [JsonPropertyName("season_number")]
    public int SeasonNumber { get; set; }

    /// <summary>Gets or sets the episode overview.</summary>
    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    /// <summary>Gets or sets the air date.</summary>
    [JsonPropertyName("air_date")]
    public string? AirDate { get; set; }

    /// <summary>Gets or sets the runtime in minutes.</summary>
    [JsonPropertyName("runtime")]
    public int? Runtime { get; set; }

    /// <summary>Gets or sets the mean vote score.</summary>
    [JsonPropertyName("vote_average")]
    public double VoteAverage { get; set; }

    /// <summary>Gets or sets the still image path.</summary>
    [JsonPropertyName("still_path")]
    public string? StillPath { get; set; }

    /// <summary>Gets or sets the episode number this follows.</summary>
    [JsonPropertyName("episode_type")]
    public string? EpisodeType { get; set; }
}

/// <summary>
/// The <c>/3/tv/{id}/season/{n}</c> response.
/// </summary>
public sealed class TmdbSeasonDetail
{
    /// <summary>Gets or sets the season id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the season number.</summary>
    [JsonPropertyName("season_number")]
    public int SeasonNumber { get; set; }

    /// <summary>Gets or sets the season name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Gets or sets the season overview.</summary>
    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    /// <summary>Gets or sets the poster path.</summary>
    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    /// <summary>Gets or sets the air date.</summary>
    [JsonPropertyName("air_date")]
    public string? AirDate { get; set; }

    /// <summary>Gets or sets the episodes.</summary>
    [JsonPropertyName("episodes")]
    public List<TmdbEpisode> Episodes { get; set; } = new();
}

/// <summary>
/// A collection reference on a movie detail object.
/// </summary>
public sealed class TmdbCollectionRef
{
    /// <summary>Gets or sets the collection id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the collection name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Gets or sets the poster path.</summary>
    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    /// <summary>Gets or sets the backdrop path.</summary>
    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; set; }
}

/// <summary>
/// The <c>/3/collection/{id}</c> response.
/// </summary>
public sealed class TmdbCollectionDetail
{
    /// <summary>Gets or sets the collection id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the collection name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Gets or sets the collection overview.</summary>
    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    /// <summary>Gets or sets the poster path.</summary>
    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    /// <summary>Gets or sets the backdrop path.</summary>
    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; set; }

    /// <summary>Gets or sets the collection parts.</summary>
    [JsonPropertyName("parts")]
    public List<TmdbListItem> Parts { get; set; } = new();
}

/// <summary>
/// A keyword entry. TMDB returns these under <c>keywords</c> for movies and <c>results</c> for TV.
/// </summary>
public sealed class TmdbKeyword
{
    /// <summary>Gets or sets the keyword id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the keyword name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

/// <summary>
/// A movie detail object, including everything appended to it.
/// </summary>
public sealed class TmdbMovieDetail
{
    /// <summary>Gets or sets the TMDB id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>Gets or sets the original title.</summary>
    [JsonPropertyName("original_title")]
    public string? OriginalTitle { get; set; }

    /// <summary>Gets or sets the overview.</summary>
    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    /// <summary>Gets or sets the tagline.</summary>
    [JsonPropertyName("tagline")]
    public string? Tagline { get; set; }

    /// <summary>Gets or sets the release date.</summary>
    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }

    /// <summary>Gets or sets the runtime in minutes.</summary>
    [JsonPropertyName("runtime")]
    public int? Runtime { get; set; }

    /// <summary>Gets or sets the mean vote score.</summary>
    [JsonPropertyName("vote_average")]
    public double VoteAverage { get; set; }

    /// <summary>Gets or sets the vote count.</summary>
    [JsonPropertyName("vote_count")]
    public int VoteCount { get; set; }

    /// <summary>Gets or sets the popularity score.</summary>
    [JsonPropertyName("popularity")]
    public double Popularity { get; set; }

    /// <summary>Gets or sets the status, for example <c>Released</c>.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>Gets or sets the poster path.</summary>
    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    /// <summary>Gets or sets the backdrop path.</summary>
    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; set; }

    /// <summary>Gets or sets the genres.</summary>
    [JsonPropertyName("genres")]
    public List<TmdbGenre> Genres { get; set; } = new();

    /// <summary>Gets or sets the runtime for the current country, keyed by ISO 3166-1.</summary>
    [JsonPropertyName("runtime_by_country")]
    public Dictionary<string, int> RuntimeByCountry { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets the production companies.</summary>
    [JsonPropertyName("production_companies")]
    public List<TmdbCompany> ProductionCompanies { get; set; } = new();

    /// <summary>Gets or sets the collection this movie belongs to.</summary>
    [JsonPropertyName("belongs_to_collection")]
    public TmdbCollectionRef? BelongsToCollection { get; set; }

    /// <summary>Gets or sets the original language code.</summary>
    [JsonPropertyName("original_language")]
    public string? OriginalLanguage { get; set; }

    /// <summary>Gets or sets the IMDb id.</summary>
    [JsonPropertyName("imdb_id")]
    public string? ImdbId { get; set; }

    /// <summary>Gets or sets the appended homepage.</summary>
    [JsonPropertyName("homepage")]
    public string? Homepage { get; set; }

    /// <summary>Gets or sets the appended credits.</summary>
    [JsonPropertyName("credits")]
    public TmdbCredits? Credits { get; set; }

    /// <summary>Gets or sets the appended videos.</summary>
    [JsonPropertyName("videos")]
    public TmdbVideos? Videos { get; set; }

    /// <summary>Gets or sets the appended images.</summary>
    [JsonPropertyName("images")]
    public TmdbImages? Images { get; set; }

    /// <summary>Gets or sets the appended recommendations.</summary>
    [JsonPropertyName("recommendations")]
    public TmdbPage<TmdbListItem>? Recommendations { get; set; }

    /// <summary>Gets or sets the appended similar titles.</summary>
    [JsonPropertyName("similar")]
    public TmdbPage<TmdbListItem>? Similar { get; set; }

    /// <summary>Gets or sets the appended watch providers.</summary>
    [JsonPropertyName("watch/providers")]
    public TmdbWatchProviders? WatchProviders { get; set; }

    /// <summary>Gets or sets the appended release dates, used for certifications.</summary>
    [JsonPropertyName("release_dates")]
    public TmdbReleaseDates? ReleaseDates { get; set; }

    /// <summary>Gets or sets the appended keywords.</summary>
    [JsonPropertyName("keywords")]
    public TmdbPage<TmdbKeyword>? Keywords { get; set; }
}

/// <summary>
/// The <c>release_dates</c> object, used to resolve a certification for a region.
/// </summary>
public sealed class TmdbReleaseDates
{
    /// <summary>Gets or sets the per-country result map.</summary>
    [JsonPropertyName("results")]
    public Dictionary<string, TmdbReleaseDateCountry> Results { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The release entries for one country.
/// </summary>
public sealed class TmdbReleaseDateCountry
{
    /// <summary>Gets or sets the certification string, for example <c>PG-13</c>.</summary>
    [JsonPropertyName("certification")]
    public string? Certification { get; set; }
}

/// <summary>
/// A TV detail object, including everything appended to it.
/// </summary>
public sealed class TmdbSeriesDetail
{
    /// <summary>Gets or sets the TMDB id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the series name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Gets or sets the original name.</summary>
    [JsonPropertyName("original_name")]
    public string? OriginalName { get; set; }

    /// <summary>Gets or sets the overview.</summary>
    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    /// <summary>Gets or sets the tagline.</summary>
    [JsonPropertyName("tagline")]
    public string? Tagline { get; set; }

    /// <summary>Gets or sets the first air date.</summary>
    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; set; }

    /// <summary>Gets or sets the last air date.</summary>
    [JsonPropertyName("last_air_date")]
    public string? LastAirDate { get; set; }

    /// <summary>Gets or sets the per-episode runtimes.</summary>
    [JsonPropertyName("episode_run_time")]
    public List<int> EpisodeRunTime { get; set; } = new();

    /// <summary>Gets or sets the mean vote score.</summary>
    [JsonPropertyName("vote_average")]
    public double VoteAverage { get; set; }

    /// <summary>Gets or sets the vote count.</summary>
    [JsonPropertyName("vote_count")]
    public int VoteCount { get; set; }

    /// <summary>Gets or sets the popularity score.</summary>
    [JsonPropertyName("popularity")]
    public double Popularity { get; set; }

    /// <summary>Gets or sets the status, for example <c>Returning Series</c>.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>Gets or sets the poster path.</summary>
    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    /// <summary>Gets or sets the backdrop path.</summary>
    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; set; }

    /// <summary>Gets or sets the genres.</summary>
    [JsonPropertyName("genres")]
    public List<TmdbGenre> Genres { get; set; } = new();

    /// <summary>Gets or sets the origin country codes.</summary>
    [JsonPropertyName("origin_country")]
    public List<string> OriginCountry { get; set; } = new();

    /// <summary>Gets or sets the original language code.</summary>
    [JsonPropertyName("original_language")]
    public string? OriginalLanguage { get; set; }

    /// <summary>Gets or sets the homepage.</summary>
    [JsonPropertyName("homepage")]
    public string? Homepage { get; set; }

    /// <summary>Gets or sets the network or channel the show is on.</summary>
    [JsonPropertyName("network")]
    public TmdbCompany? Network { get; set; }

    /// <summary>Gets or sets the season summaries.</summary>
    [JsonPropertyName("seasons")]
    public List<TmdbSeasonSummary> Seasons { get; set; } = new();

    /// <summary>Gets or sets the appended aggregate credits.</summary>
    [JsonPropertyName("aggregate_credits")]
    public TmdbCredits? AggregateCredits { get; set; }

    /// <summary>Gets or sets the appended videos.</summary>
    [JsonPropertyName("videos")]
    public TmdbVideos? Videos { get; set; }

    /// <summary>Gets or sets the appended images.</summary>
    [JsonPropertyName("images")]
    public TmdbImages? Images { get; set; }

    /// <summary>Gets or sets the appended recommendations.</summary>
    [JsonPropertyName("recommendations")]
    public TmdbPage<TmdbListItem>? Recommendations { get; set; }

    /// <summary>Gets or sets the appended similar titles.</summary>
    [JsonPropertyName("similar")]
    public TmdbPage<TmdbListItem>? Similar { get; set; }

    /// <summary>Gets or sets the appended watch providers.</summary>
    [JsonPropertyName("watch/providers")]
    public TmdbWatchProviders? WatchProviders { get; set; }

    /// <summary>Gets or sets the appended content ratings, used for certifications.</summary>
    [JsonPropertyName("content_ratings")]
    public TmdbContentRatings? ContentRatings { get; set; }

    /// <summary>Gets or sets the appended keywords, which TMDB returns under <c>results</c> for TV.</summary>
    [JsonPropertyName("results")]
    public TmdbPage<TmdbKeyword>? KeywordResults { get; set; }
}

/// <summary>
/// The <c>content_ratings</c> object.
/// </summary>
public sealed class TmdbContentRatings
{
    /// <summary>Gets or sets the per-country result map.</summary>
    [JsonPropertyName("results")]
    public Dictionary<string, TmdbContentRatingCountry> Results { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The rating for one country.
/// </summary>
public sealed class TmdbContentRatingCountry
{
    /// <summary>Gets or sets the rating string, for example <c>TV-MA</c>.</summary>
    [JsonPropertyName("rating")]
    public string? Rating { get; set; }
}

/// <summary>
/// A person detail object.
/// </summary>
public sealed class TmdbPersonDetail
{
    /// <summary>Gets or sets the person id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the person's name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Gets or sets the biography.</summary>
    [JsonPropertyName("biography")]
    public string? Biography { get; set; }

    /// <summary>Gets or sets the birthday.</summary>
    [JsonPropertyName("birthday")]
    public string? Birthday { get; set; }

    /// <summary>Gets or sets the death date.</summary>
    [JsonPropertyName("deathday")]
    public string? Deathday { get; set; }

    /// <summary>Gets or sets the place of birth.</summary>
    [JsonPropertyName("place_of_birth")]
    public string? PlaceOfBirth { get; set; }

    /// <summary>Gets or sets the profile image path.</summary>
    [JsonPropertyName("profile_path")]
    public string? ProfilePath { get; set; }

    /// <summary>Gets or sets the department the person is known for.</summary>
    [JsonPropertyName("known_for_department")]
    public string? KnownForDepartment { get; set; }

    /// <summary>Gets or sets the appended credits.</summary>
    [JsonPropertyName("combined_credits")]
    public TmdbPersonCredits? CombinedCredits { get; set; }
}

/// <summary>
/// The <c>combined_credits</c> object on a person.
/// </summary>
public sealed class TmdbPersonCredits
{
    /// <summary>Gets or sets the cast credits.</summary>
    [JsonPropertyName("cast")]
    public List<TmdbListItem> Cast { get; set; } = new();

    /// <summary>Gets or sets the crew credits.</summary>
    [JsonPropertyName("crew")]
    public List<TmdbListItem> Crew { get; set; } = new();
}

/// <summary>
/// The <c>/3/search/multi</c> response.
/// </summary>
public sealed class TmdbMultiSearchResult
{
    /// <summary>Gets or sets the page number.</summary>
    [JsonPropertyName("page")]
    public int Page { get; set; }

    /// <summary>Gets or sets the mixed results. Every entry carries <c>media_type</c>.</summary>
    [JsonPropertyName("results")]
    public List<TmdbListItem> Results { get; set; } = new();

    /// <summary>Gets or sets the total result count.</summary>
    [JsonPropertyName("total_results")]
    public int TotalResults { get; set; }
}

/// <summary>
/// The <c>/3/configuration</c> response, used to discover valid image sizes.
/// </summary>
public sealed class TmdbConfiguration
{
    /// <summary>Gets or sets the image size configuration.</summary>
    [JsonPropertyName("images")]
    public TmdbImageConfiguration Images { get; set; } = new();
}

/// <summary>
/// The image size configuration.
/// </summary>
public sealed class TmdbImageConfiguration
{
    /// <summary>Gets or sets the secure base URL, for example <c>https://image.tmdb.org/t/p/</c>.</summary>
    [JsonPropertyName("secure_base_url")]
    public string SecureBaseUrl { get; set; } = "https://image.tmdb.org/t/p/";

    /// <summary>Gets or sets the available poster sizes.</summary>
    [JsonPropertyName("poster_sizes")]
    public List<string> PosterSizes { get; set; } = new();

    /// <summary>Gets or sets the available backdrop sizes.</summary>
    [JsonPropertyName("backdrop_sizes")]
    public List<string> BackdropSizes { get; set; } = new();

    /// <summary>Gets or sets the available still sizes.</summary>
    [JsonPropertyName("still_sizes")]
    public List<string> StillSizes { get; set; } = new();

    /// <summary>Gets or sets the available profile sizes.</summary>
    [JsonPropertyName("profile_sizes")]
    public List<string> ProfileSizes { get; set; } = new();

    /// <summary>Gets or sets the available logo sizes.</summary>
    [JsonPropertyName("logo_sizes")]
    public List<string> LogoSizes { get; set; } = new();
}

/// <summary>
/// The documented TMDB status codes.
/// </summary>
public static class TmdbStatusCodes
{
    /// <summary>The API key is not valid.</summary>
    public const int InvalidApiKey = 401;

    /// <summary>The endpoint does not exist.</summary>
    public const int NotFound = 404;

    /// <summary>The request was rate limited.</summary>
    public const int RateLimited = 429;

    /// <summary>The API is temporarily unavailable.</summary>
    public const int ServerError = 503;
}
