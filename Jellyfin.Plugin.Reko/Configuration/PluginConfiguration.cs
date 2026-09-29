using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Reko.Configuration;

/// <summary>
/// Reko plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        TabLabel = "Reko";
        TmdbApiKey = string.Empty;
        TmdbBaseUrl = string.Empty;
        TmdbImageBaseUrl = string.Empty;
        TmdbLanguage = "en-US";
        TmdbRegion = "US";
        TmdbImageLanguage = string.Empty;

        EnableSeerr = true;
        SeerrUrl = string.Empty;
        SeerrApiKey = string.Empty;
        SeerrMapJellyfinUsers = true;

        HeroCount = 5;
        HeroRotationSeconds = 9;
        ItemsPerRail = 20;
        ContinueWatchingCount = 12;
        PersonalizedRailCount = 4;

        EnableContinueWatching = true;
        EnablePersonalizedRows = true;
        EnableSearch = true;
        ShowWatchProviders = true;
        ShowTrailers = true;
        ShowCast = true;

        IncludeMovies = true;
        IncludeSeries = true;

        HideWatchedFromPersonalized = true;
        MinimumWatchedForPersonalized = 3;
        PersonalizedCacheMinutes = 720;

        RailOrder = string.Empty;
        CacheMinutes = 180;
        TrendingCacheMinutes = 45;
        DetailCacheHours = 12;

        EnableDebugLogging = false;
    }

    /// <summary>
    /// Gets or sets the label shown on the Reko home tab.
    /// </summary>
    public string TabLabel { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the tab is added to the home page at all.
    /// </summary>
    public bool EnableTab { get; set; } = true;

    /// <summary>
    /// Gets or sets the TMDB API Read Access Token. Stays on the server; never sent to a browser.
    /// </summary>
    public string TmdbApiKey { get; set; }

    /// <summary>
    /// Gets or sets an alternative TMDB API base URL, including the <c>/3/</c> suffix.
    /// </summary>
    /// <remarks>
    /// An advanced escape hatch for networks that can only reach TMDB through a caching proxy or
    /// mirror. Leave it blank to use TMDB directly. Nothing else in the plugin assumes this host, and
    /// image URLs are unaffected because they come from TMDB's own image configuration.
    /// </remarks>
    public string TmdbBaseUrl { get; set; }

    /// <summary>
    /// Gets or sets an alternative TMDB image base URL, up to but excluding the size token.
    /// </summary>
    /// <remarks>
    /// Set this alongside <see cref="TmdbBaseUrl"/> when pointing Reko at a mirror, because a mirror
    /// that proxies the API but not the image CDN produces cards with broken artwork.
    /// </remarks>
    public string TmdbImageBaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the TMDB language tag used for text metadata, for example <c>en-US</c>.
    /// </summary>
    public string TmdbLanguage { get; set; }

    /// <summary>
    /// Gets or sets the TMDB region code used for watch providers, for example <c>US</c>.
    /// </summary>
    public string TmdbRegion { get; set; }

    /// <summary>
    /// Gets or sets the TMDB image language code. Falls back to the primary language when empty.
    /// </summary>
    public string TmdbImageLanguage { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Seerr requests are offered.
    /// </summary>
    public bool EnableSeerr { get; set; }

    /// <summary>
    /// Gets or sets the base URL of the Overseerr/Jellyseerr instance, for example <c>http://localhost:5055</c>.
    /// </summary>
    public string SeerrUrl { get; set; }

    /// <summary>
    /// Gets or sets the Seerr API key.
    /// </summary>
    public string SeerrApiKey { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the signed-in Jellyfin user is resolved to their
    /// matching Seerr user so that requests and quotas are attributed correctly.
    /// </summary>
    public bool SeerrMapJellyfinUsers { get; set; }

    /// <summary>
    /// Gets or sets the number of titles in the rotating hero.
    /// </summary>
    public int HeroCount { get; set; }

    /// <summary>
    /// Gets or sets the hero rotation interval in seconds.
    /// </summary>
    public int HeroRotationSeconds { get; set; }

    /// <summary>
    /// Gets or sets how many titles each rail requests.
    /// </summary>
    public int ItemsPerRail { get; set; }

    /// <summary>
    /// Gets or sets how many items the continue watching row shows.
    /// </summary>
    public int ContinueWatchingCount { get; set; }

    /// <summary>
    /// Gets or sets how many personalised rows are generated from playback history.
    /// </summary>
    public int PersonalizedRailCount { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the continue watching row is shown.
    /// </summary>
    public bool EnableContinueWatching { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether rows are personalised from playback history.
    /// </summary>
    public bool EnablePersonalizedRows { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the in-tab search is enabled.
    /// </summary>
    public bool EnableSearch { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether watch providers are shown on title pages.
    /// </summary>
    public bool ShowWatchProviders { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether trailers are shown on title pages.
    /// </summary>
    public bool ShowTrailers { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether cast rows are shown on title pages.
    /// </summary>
    public bool ShowCast { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether movies appear in browsing.
    /// </summary>
    public bool IncludeMovies { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether series appear in browsing.
    /// </summary>
    public bool IncludeSeries { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether already watched items are excluded from personalised rows.
    /// </summary>
    public bool HideWatchedFromPersonalized { get; set; }

    /// <summary>
    /// Gets or sets how many watched items are required before personalised rows are generated.
    /// </summary>
    public int MinimumWatchedForPersonalized { get; set; }

    /// <summary>
    /// Gets or sets how long a computed personalised profile is cached, in minutes.
    /// </summary>
    public int PersonalizedCacheMinutes { get; set; }

    /// <summary>
    /// Gets or sets a comma separated list of rail ids defining rail order. Empty means the default order.
    /// </summary>
    public string RailOrder { get; set; }

    /// <summary>
    /// Gets or sets the default cache lifetime for rails, in minutes.
    /// </summary>
    public int CacheMinutes { get; set; }

    /// <summary>
    /// Gets or sets the cache lifetime for trending rails, in minutes.
    /// </summary>
    public int TrendingCacheMinutes { get; set; }

    /// <summary>
    /// Gets or sets the cache lifetime for title detail payloads, in hours.
    /// </summary>
    public int DetailCacheHours { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether verbose plugin logging is enabled.
    /// </summary>
    public bool EnableDebugLogging { get; set; }
}
