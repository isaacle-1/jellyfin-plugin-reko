using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Reko.Services.Seerr;

/// <summary>
/// The <c>GET /api/v1/status</c> response.
/// </summary>
public sealed class SeerrStatus
{
    /// <summary>Gets or sets the Seerr version.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>Gets or sets the commit tag.</summary>
    [JsonPropertyName("commitTag")]
    public string? CommitTag { get; set; }
}

/// <summary>
/// The <c>GET /api/v1/settings/public</c> response. Only the fields Reko acts on are modelled.
/// </summary>
public sealed class SeerrPublicSettings
{
    /// <summary>Gets or sets a value indicating whether the setup wizard has been completed.</summary>
    [JsonPropertyName("initialized")]
    public bool Initialized { get; set; }

    /// <summary>Gets or sets the configured application title, for example <c>Jellyseerr</c>.</summary>
    [JsonPropertyName("applicationTitle")]
    public string? ApplicationTitle { get; set; }

    /// <summary>Gets or sets the configured application URL.</summary>
    [JsonPropertyName("applicationUrl")]
    public string? ApplicationUrl { get; set; }

    /// <summary>Gets or sets the linked media server type. 1 is Plex, 2 is Jellyfin, 3 is Emby, 4 is unset.</summary>
    [JsonPropertyName("mediaServerType")]
    public int MediaServerType { get; set; }

    /// <summary>Gets or sets the discover region.</summary>
    [JsonPropertyName("discoverRegion")]
    public string? DiscoverRegion { get; set; }

    /// <summary>Gets or sets the linked Jellyfin server name.</summary>
    [JsonPropertyName("jellyfinServerName")]
    public string? JellyfinServerName { get; set; }

    /// <summary>Gets or sets a value indicating whether individual seasons may be requested.</summary>
    [JsonPropertyName("partialRequestsEnabled")]
    public bool PartialRequestsEnabled { get; set; }
}

/// <summary>
/// The media server type sentinels used by Seerr's <c>settings.public</c>.
/// </summary>
public static class SeerrMediaServerType
{
    /// <summary>Plex.</summary>
    public const int Plex = 1;

    /// <summary>Jellyfin.</summary>
    public const int Jellyfin = 2;

    /// <summary>Emby.</summary>
    public const int Emby = 3;

    /// <summary>No media server has been linked.</summary>
    public const int NotConfigured = 4;
}

/// <summary>
/// A Seerr user record, as returned by the Jellyfin user lookup.
/// </summary>
public sealed class SeerrUser
{
    /// <summary>Gets or sets the Seerr user id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the permission bitmask. Bit 2 is ADMIN, which satisfies every check.</summary>
    [JsonPropertyName("permissions")]
    public long Permissions { get; set; }

    /// <summary>Gets or sets the account type. 3 is Jellyfin.</summary>
    [JsonPropertyName("userType")]
    public int UserType { get; set; }

    /// <summary>Gets or sets the display name.</summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    /// <summary>Gets or sets the Jellyfin username.</summary>
    [JsonPropertyName("jellyfinUsername")]
    public string? JellyfinUsername { get; set; }

    /// <summary>Gets or sets the Jellyfin user GUID, dash-stripped and lower-cased.</summary>
    [JsonPropertyName("jellyfinUserId")]
    public string? JellyfinUserId { get; set; }
}

/// <summary>
/// A single request against a Seerr season.
/// </summary>
public sealed class SeerrRequest
{
    /// <summary>Gets or sets the request id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the request status. See <see cref="SeerrRequestStatus"/>.</summary>
    [JsonPropertyName("status")]
    public int Status { get; set; }

    /// <summary>Gets or sets the requested seasons, empty for movies.</summary>
    [JsonPropertyName("seasons")]
    public List<SeerrRequestSeason> Seasons { get; set; } = new();

    /// <summary>Gets or sets when the request was created.</summary>
    [JsonPropertyName("createdAt")]
    public string? CreatedAt { get; set; }

    /// <summary>Gets or sets when the request was last modified.</summary>
    [JsonPropertyName("updatedAt")]
    public string? UpdatedAt { get; set; }
}

/// <summary>
/// The season entries of a request.
/// </summary>
public sealed class SeerrRequestSeason
{
    /// <summary>Gets or sets the season number.</summary>
    [JsonPropertyName("seasonNumber")]
    public int SeasonNumber { get; set; }

    /// <summary>Gets or sets the per-season status. See <see cref="SeerrMediaStatus"/>.</summary>
    [JsonPropertyName("status")]
    public int Status { get; set; }
}

/// <summary>
/// A season's state within a Seerr media record.
/// </summary>
public sealed class SeerrMediaSeason
{
    /// <summary>Gets or sets the season number.</summary>
    [JsonPropertyName("seasonNumber")]
    public int SeasonNumber { get; set; }

    /// <summary>Gets or sets the season status. See <see cref="SeerrMediaStatus"/>.</summary>
    [JsonPropertyName("status")]
    public int Status { get; set; }
}

/// <summary>
/// The <c>mediaInfo</c> object of a Seerr detail response.
/// </summary>
public sealed class SeerrMediaInfo
{
    /// <summary>Gets or sets the Seerr media row id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the TMDB id.</summary>
    [JsonPropertyName("tmdbId")]
    public int TmdbId { get; set; }

    /// <summary>Gets or sets the media type, either <c>movie</c> or <c>tv</c>.</summary>
    [JsonPropertyName("mediaType")]
    public string? MediaType { get; set; }

    /// <summary>Gets or sets the media status. See <see cref="SeerrMediaStatus"/>.</summary>
    [JsonPropertyName("status")]
    public int Status { get; set; }

    /// <summary>Gets or sets the requests made against this media.</summary>
    [JsonPropertyName("requests")]
    public List<SeerrRequest> Requests { get; set; } = new();

    /// <summary>Gets or sets the per-season state, TV only.</summary>
    [JsonPropertyName("seasons")]
    public List<SeerrMediaSeason> Seasons { get; set; } = new();

    /// <summary>Gets or sets the status of any service error, when one occurred.</summary>
    [JsonPropertyName("status4k")]
    public int Status4k { get; set; }
}

/// <summary>
/// A Seerr movie detail response.
/// </summary>
public sealed class SeerrMovieDetail
{
    /// <summary>Gets or sets the media record.</summary>
    [JsonPropertyName("mediaInfo")]
    public SeerrMediaInfo? MediaInfo { get; set; }

    /// <summary>Gets or sets the credits, present on the detail route.</summary>
    [JsonPropertyName("credits")]
    public SeerrCredits? Credits { get; set; }
}

/// <summary>
/// A Seerr series detail response.
/// </summary>
public sealed class SeerrSeriesDetail
{
    /// <summary>Gets or sets the media record.</summary>
    [JsonPropertyName("mediaInfo")]
    public SeerrMediaInfo? MediaInfo { get; set; }

    /// <summary>Gets or sets the credits, present on the detail route.</summary>
    [JsonPropertyName("credits")]
    public SeerrCredits? Credits { get; set; }
}

/// <summary>
/// A Seerr cast list.
/// </summary>
public sealed class SeerrCredits
{
    /// <summary>Gets or sets the cast members.</summary>
    [JsonPropertyName("cast")]
    public List<SeerrCastMember> Cast { get; set; } = new();

    /// <summary>Gets or sets the crew members.</summary>
    [JsonPropertyName("crew")]
    public List<SeerrCastMember> Crew { get; set; } = new();
}

/// <summary>
/// A Seerr cast or crew member.
/// </summary>
public sealed class SeerrCastMember
{
    /// <summary>Gets or sets the person id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the person's name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Gets or sets the character name.</summary>
    [JsonPropertyName("character")]
    public string? Character { get; set; }

    /// <summary>Gets or sets the profile image path.</summary>
    [JsonPropertyName("profilePath")]
    public string? ProfilePath { get; set; }
}

/// <summary>
/// The body sent to <c>POST /api/v1/request</c>.
/// </summary>
/// <remarks>
/// Seerr runs <c>express-openapi-validator</c> with <c>validateRequests: true</c>, so this must contain
/// exactly the documented fields. Extra properties are rejected with a 400 before routing.
/// </remarks>
public sealed class SeerrRequestBody
{
    /// <summary>Gets or sets the media type, either <c>movie</c> or <c>tv</c>.</summary>
    [JsonPropertyName("mediaType")]
    public string MediaType { get; set; } = "movie";

    /// <summary>Gets or sets the TMDB id.</summary>
    [JsonPropertyName("mediaId")]
    public int MediaId { get; set; }

    /// <summary>
    /// Gets or sets the seasons to request. TV only.
    /// </summary>
    /// <remarks>
    /// Omitted entirely when null. Seerr validates its own request bodies with
    /// <c>express-openapi-validator</c>, and <c>seasons</c> is a <c>oneOf</c> of array, string and
    /// the literal <c>"all"</c>. A JSON <c>null</c> matches none of those, so sending the property at
    /// all for a movie — which is what happens the moment it is serialised without this attribute —
    /// makes every movie request fail with a 400 before it reaches the route.
    /// </remarks>
    [JsonPropertyName("seasons")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? Seasons { get; set; }

    /// <summary>Gets or sets a value indicating whether this is a 4K request.</summary>
    [JsonPropertyName("is4k")]
    public bool Is4k { get; set; }
}

/// <summary>
/// The <c>GET /api/v1/request</c> response envelope.
/// </summary>
public sealed class SeerrRequestPage
{
    /// <summary>Gets or sets the paging information.</summary>
    [JsonPropertyName("pageInfo")]
    public SeerrPageInfo PageInfo { get; set; } = new();

    /// <summary>Gets or sets the requests on this page.</summary>
    [JsonPropertyName("results")]
    public List<SeerrRequestRow> Results { get; set; } = new();
}

/// <summary>
/// Paging information on a list response.
/// </summary>
public sealed class SeerrPageInfo
{
    /// <summary>Gets or sets the current page.</summary>
    [JsonPropertyName("page")]
    public int Page { get; set; }

    /// <summary>Gets or sets the page count.</summary>
    [JsonPropertyName("pages")]
    public int Pages { get; set; }

    /// <summary>Gets or sets the total result count.</summary>
    [JsonPropertyName("results")]
    public int Results { get; set; }

    /// <summary>Gets or sets the page size.</summary>
    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }
}

/// <summary>
/// One row of the request list. The media object is embedded rather than referenced.
/// </summary>
public sealed class SeerrRequestRow
{
    /// <summary>Gets or sets the request id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the request status. See <see cref="SeerrRequestStatus"/>.</summary>
    [JsonPropertyName("status")]
    public int Status { get; set; }

    /// <summary>Gets or sets the media the request is for.</summary>
    [JsonPropertyName("media")]
    public SeerrMediaInfo? Media { get; set; }

    /// <summary>Gets or sets the requested seasons.</summary>
    [JsonPropertyName("seasons")]
    public List<SeerrRequestSeason> Seasons { get; set; } = new();
}

/// <summary>
/// A <c>MediaRequestStatus</c> enum values. Verified against <c>server/constants/media.ts</c>.
/// </summary>
public static class SeerrRequestStatus
{
    /// <summary>Awaiting approval.</summary>
    public const int Pending = 1;

    /// <summary>Accepted and sent to the *arr server.</summary>
    public const int Approved = 2;

    /// <summary>Rejected by an administrator.</summary>
    public const int Declined = 3;

    /// <summary>The send to the *arr server failed. Retryable.</summary>
    public const int Failed = 4;

    /// <summary>Fully available in the library.</summary>
    public const int Completed = 5;
}

/// <summary>
/// The <c>MediaStatus</c> enum values. There is no UNAVAILABLE member and DELETED is 7, not 6.
/// </summary>
public static class SeerrMediaStatus
{
    /// <summary>Not in the library and not requested.</summary>
    public const int Unknown = 1;

    /// <summary>Requested but not yet picked up.</summary>
    public const int Pending = 2;

    /// <summary>Downloading.</summary>
    public const int Processing = 3;

    /// <summary>Some requested seasons are present.</summary>
    public const int PartiallyAvailable = 4;

    /// <summary>Present in the library.</summary>
    public const int Available = 5;

    /// <summary>Blocklisted by an administrator.</summary>
    public const int Blocklisted = 6;

    /// <summary>Was in the library and has since been removed.</summary>
    public const int Deleted = 7;
}
