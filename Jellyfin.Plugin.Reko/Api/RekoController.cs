using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Reko.Api;
using Jellyfin.Plugin.Reko.Services;
using Jellyfin.Plugin.Reko.Services.Library;
using Jellyfin.Plugin.Reko.Services.Seerr;
using Jellyfin.Plugin.Reko.Services.Tmdb;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Reko.Api;

/// <summary>
/// Reko's HTTP surface.
/// </summary>
/// <remarks>
/// Every data endpoint requires an authenticated Jellyfin user. The TMDB and Seerr credentials never
/// leave the server: the browser only ever talks to this controller on its own Jellyfin instance.
/// <para>
/// The static asset endpoints are deliberately unauthenticated. They are the same embedded resources
/// that Jellyfin serves anonymously through <c>/web/ConfigurationPage</c>, they contain no user data,
/// and the browser must be able to load them before the app shell finishes booting. Without this,
/// script injection into <c>index.html</c> could not work.
/// </para>
/// </remarks>
[ApiController]
[Route("Reko")]
[Produces(MediaTypeNames.Application.Json)]
public class RekoController : ControllerBase, IExceptionFilter
{
    private const string ResourcePrefix = "Jellyfin.Plugin.Reko.";

    /// <summary>
    /// Claim types that may carry the Jellyfin user id, in preference order.
    /// </summary>
    private static readonly string[] UserIdClaims =
    [
        "Jellyfin-UserId",
        System.Security.Claims.ClaimTypes.NameIdentifier,
        "sub"
    ];

    /// <summary>
    /// Claim types that may carry the Jellyfin username, in preference order.
    /// </summary>
    private static readonly string[] UsernameClaims =
    [
        "Jellyfin-Username",
        System.Security.Claims.ClaimTypes.Name
    ];

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".html"] = "text/html; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png"
    };

    private readonly RekoPayloadBuilder _builder;
    private readonly SeerrClient _seerr;
    private readonly LibraryIndex _libraryIndex;
    private readonly IUserManager _userManager;
    private readonly ILogger<RekoController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RekoController"/> class.
    /// </summary>
    /// <param name="builder">The payload builder.</param>
    /// <param name="seerr">The Seerr client.</param>
    /// <param name="libraryIndex">The library index.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="logger">The logger.</param>
    public RekoController(
        RekoPayloadBuilder builder,
        SeerrClient seerr,
        LibraryIndex libraryIndex,
        IUserManager userManager,
        ILogger<RekoController> logger)
    {
        _builder = builder;
        _seerr = seerr;
        _libraryIndex = libraryIndex;
        _userManager = userManager;
        _logger = logger;
    }

    // ---------------------------------------------------------------------------------------
    // Static assets. Anonymous on purpose, see the class remarks.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Serves the Reko stylesheet.
    /// </summary>
    /// <returns>The stylesheet.</returns>
    [HttpGet("reko.css")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetStylesheet() => ServeResource("Web.reko.css");

    /// <summary>
    /// Serves Reko's pre-boot script.
    /// </summary>
    /// <remarks>
    /// Deliberately not a module. It is injected as a classic, non-deferred script at the top of
    /// <c>&lt;head&gt;</c> so it runs before jellyfin-web's deferred bundles create the hash router.
    /// </remarks>
    /// <returns>The pre-boot script.</returns>
    [HttpGet("early.js")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetEarlyScript() => ServeResource("Web.early.js");

    /// <summary>
    /// Serves the Reko bootstrap module.
    /// </summary>
    /// <returns>The bootstrap module.</returns>
    [HttpGet("client.js")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetClient() => ServeResource("Web.client.js");

    /// <summary>
    /// Serves the stylesheet for the dashboard configuration page.
    /// </summary>
    /// <returns>The stylesheet.</returns>
    [HttpGet("config.css")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetConfigStylesheet() => ServeResource("Configuration.configPage.css");

    /// <summary>
    /// Serves a Reko client module. Modules are requested as real ES modules by the browser, so
    /// they must be served from distinct URLs with a JavaScript content type.
    /// </summary>
    /// <param name="module">The module name, without the extension.</param>
    /// <returns>The module.</returns>
    [HttpGet("js/{module}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetModule([FromRoute] string module)
    {
        // The client's import specifiers carry the extension, so "/Reko/js/utils.js" arrives with
        // "utils.js" in the route value. Accepting and stripping it keeps the URLs natural in the
        // browser's network panel and in devtools, and the allowlist below still bounds what can be
        // resolved out of the assembly.
        if (module.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
        {
            module = module[..^3];
        }

        if (!IsSafeModuleName(module))
        {
            return NotFound();
        }

        return ServeResource("Web.js." + module + ".js");
    }

    // ---------------------------------------------------------------------------------------
    // Data
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Returns the client configuration and the state of Reko's dependencies.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The bootstrap payload.</returns>
    [HttpGet("bootstrap")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<RekoBootstrap>> GetBootstrap(CancellationToken cancellationToken)
    {
        return new RekoBootstrap
        {
            Config = await _builder.BuildClientConfigAsync(cancellationToken).ConfigureAwait(false),
            TmdbConfigured = !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.TmdbApiKey),
            LibraryIndexed = _libraryIndex.IsReady,
            LibrarySize = _libraryIndex.Count
        };
    }

    /// <summary>
    /// Returns the whole home page payload.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The home payload.</returns>
    [HttpGet("home")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<RekoHomePayload>> GetHome(CancellationToken cancellationToken)
    {
        var user = ResolveUser();
        return await _builder.BuildHomeAsync(user, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns a single rail, for lazy loading.
    /// </summary>
    /// <param name="railId">The rail id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The rail, or 404 when the id is unknown.</returns>
    [HttpGet("rail/{railId}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RekoRail>> GetRail([FromRoute] string railId, CancellationToken cancellationToken)
    {
        var user = ResolveUser();
        var rail = await _builder.BuildRailByIdAsync(railId, user, cancellationToken).ConfigureAwait(false);
        return rail is null ? NotFound() : rail;
    }

    /// <summary>
    /// Returns a full title page.
    /// </summary>
    /// <param name="id">The TMDB id.</param>
    /// <param name="type">Either <c>movie</c> or <c>tv</c>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The title payload.</returns>
    [HttpGet("title")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RekoTitlePayload>> GetTitle(
        [FromQuery] int id,
        [FromQuery] string type,
        CancellationToken cancellationToken)
    {
        if (id <= 0 || !IsValidMediaType(type))
        {
            return BadRequest(new RekoError { Message = "Provide a positive id and a type of movie or tv." });
        }

        var user = ResolveUser();
        var seerrUserId = await _builder.ResolveSeerrUserAsync(user, cancellationToken).ConfigureAwait(false);

        var payload = await _builder
            .BuildTitleAsync(id, type, user, seerrUserId, cancellationToken)
            .ConfigureAwait(false);

        return payload is null ? NotFound() : payload;
    }

    /// <summary>
    /// Swallows a cancellation so that a person closing the tab is not logged as a server error.
    /// </summary>
    /// <param name="context">The exception context.</param>
    /// <remarks>
    /// The default behaviour turns an <see cref="OperationCanceledException"/> into a 500 and a full
    /// stack trace, with a second copy from the outer middleware. That is the wrong shape for a
    /// request nobody is waiting for any more: it is noise, and on a tab that is rebuilt on every
    /// navigation it buries the failures that do matter. 499 is nginx's "client closed request", and
    /// says the same thing without pretending the server did anything wrong.
    /// </remarks>
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not OperationCanceledException
            || !context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            return;
        }

        _logger.LogDebug("A Reko request was abandoned by the client: {Path}", context.HttpContext.Request.Path);
        context.Result = new StatusCodeResult(StatusCodes.Status499ClientClosedRequest);
        context.ExceptionHandled = true;
    }

    /// <summary>
    /// Returns the episode list for a season.
    /// </summary>
    /// <param name="seriesId">The TMDB series id.</param>
    /// <param name="season">The season number. Zero is the specials season.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The season payload.</returns>
    [HttpGet("season")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RekoSeasonPayload>> GetSeason(
        [FromQuery] int seriesId,
        [FromQuery] int season,
        CancellationToken cancellationToken)
    {
        if (seriesId <= 0 || season < 0)
        {
            return BadRequest(new RekoError { Message = "Provide a positive seriesId and a season of zero or more." });
        }

        var payload = await _builder.BuildSeasonAsync(seriesId, season, cancellationToken).ConfigureAwait(false);
        return payload is null ? NotFound() : payload;
    }

    /// <summary>
    /// Returns a person page.
    /// </summary>
    /// <param name="id">The TMDB person id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The person payload.</returns>
    [HttpGet("person")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RekoPersonPayload>> GetPerson(
        [FromQuery] int id,
        CancellationToken cancellationToken)
    {
        if (id <= 0)
        {
            return BadRequest(new RekoError { Message = "Provide a positive person id." });
        }

        var payload = await _builder.BuildPersonAsync(id, ResolveUser(), cancellationToken).ConfigureAwait(false);
        return payload is null ? NotFound() : payload;
    }

    /// <summary>
    /// Searches TMDB.
    /// </summary>
    /// <param name="query">The search text.</param>
    /// <param name="page">The 1-based page number.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The search results.</returns>
    [HttpGet("search")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<RekoBrowsePayload>> Search(
        [FromQuery] string query,
        [FromQuery] int page,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new RekoBrowsePayload { Page = 1 };
        }

        // Bounded so a single request cannot drive an unbounded upstream crawl.
        var bounded = Math.Clamp(page <= 0 ? 1 : page, 1, 10);
        return await _builder
            .SearchAsync(query, bounded, ResolveUser(), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a discover browse for a category page.
    /// </summary>
    /// <param name="type">Either <c>movie</c> or <c>tv</c>.</param>
    /// <param name="genreId">An optional TMDB genre id.</param>
    /// <param name="sort">The sort expression, for example <c>popularity.desc</c>.</param>
    /// <param name="year">An optional release year.</param>
    /// <param name="yearFrom">An optional earliest release year.</param>
    /// <param name="yearTo">An optional latest release year.</param>
    /// <param name="page">The 1-based page number.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The browse results.</returns>
    [HttpGet("browse")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<RekoBrowsePayload>> Browse(
        [FromQuery] string type,
        [FromQuery] int? genreId,
        [FromQuery] string? sort,
        [FromQuery] int? year,
        [FromQuery] int? yearFrom,
        [FromQuery] int? yearTo,
        [FromQuery] int page,
        CancellationToken cancellationToken)
    {
        if (!IsValidMediaType(type))
        {
            return BadRequest(new RekoError { Message = "Provide a type of movie or tv." });
        }

        var mediaType = type == "tv" ? "tv" : "movie";
        var query = new Dictionary<string, string>(StringComparer.Ordinal);

        if (genreId is > 0)
        {
            query["with_genres"] = genreId.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(sort) && IsAllowedSort(sort))
        {
            query["sort_by"] = sort;
        }
        else
        {
            query["sort_by"] = "popularity.desc";
        }

        var dateField = mediaType == "tv" ? "first_air_date" : "primary_release_date";

        if (yearFrom is > 0)
        {
            query[dateField + ".gte"] = string.Create(CultureInfo.InvariantCulture, $"{yearFrom}-01-01");
        }

        if (yearTo is > 0)
        {
            query[dateField + ".lte"] = string.Create(CultureInfo.InvariantCulture, $"{yearTo}-12-31");
        }
        else if (year is > 0)
        {
            // A specific year is a range of one year, not a single point.
            query[dateField + ".gte"] = string.Create(CultureInfo.InvariantCulture, $"{year}-01-01");
            query[dateField + ".lte"] = string.Create(CultureInfo.InvariantCulture, $"{year}-12-31");
        }

        var bounded = Math.Clamp(page <= 0 ? 1 : page, 1, 20);

        return await _builder
            .BrowseAsync(mediaType, query, bounded, ResolveUser(), cancellationToken)
            .ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------------------
    // Seerr
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Reports whether Seerr is reachable and whether the signed-in user is mapped to a Seerr account.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The Seerr status.</returns>
    [HttpGet("seerr/status")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<RekoSeerrStatus>> GetSeerrStatus(CancellationToken cancellationToken)
    {
        var result = new RekoSeerrStatus { Enabled = _seerr.IsConfigured };

        if (!_seerr.IsConfigured)
        {
            result.Warning = "Overseerr/Jellyseerr is not configured. Add its URL and API key in Dashboard > Plugins > Reko > Settings.";
            return result;
        }

        var settings = await _seerr.GetPublicSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (settings is null)
        {
            result.Warning = "Reko could not reach Overseerr/Jellyseerr. Check the URL in Dashboard > Plugins > Reko > Settings.";
            return result;
        }

        result.Reachable = true;
        result.Initialized = settings.Initialized;
        result.Product = string.IsNullOrWhiteSpace(settings.ApplicationTitle) ? "Overseerr" : settings.ApplicationTitle;
        result.Version = await _seerr.GetVersionAsync(cancellationToken).ConfigureAwait(false);
        result.MediaServer = settings.MediaServerType switch
        {
            SeerrMediaServerType.Jellyfin => "Jellyfin",
            SeerrMediaServerType.Plex => "Plex",
            SeerrMediaServerType.Emby => "Emby",
            _ => "not configured"
        };

        if (!settings.Initialized)
        {
            result.Warning = "Overseerr/Jellyseerr has not finished its setup wizard, so requests will fail.";
        }
        else if (settings.MediaServerType == SeerrMediaServerType.NotConfigured)
        {
            result.Warning = "No media server is linked in Overseerr/Jellyseerr, so requests will fail.";
        }

        var user = ResolveUser();
        if (user is not null)
        {
            var seerrUserId = await _builder.ResolveSeerrUserAsync(user, cancellationToken).ConfigureAwait(false);
            if (seerrUserId is not null)
            {
                result.UserMapped = true;
                result.SeerrUser = user.Username;
            }
            else if (Plugin.Instance?.Configuration.SeerrMapJellyfinUsers == true)
            {
                result.Warning = (result.Warning is null ? string.Empty : result.Warning + " ")
                    + "Your Jellyfin account has not signed in to Overseerr/Jellyseerr yet, so requests will be attributed to the API key owner.";
            }
        }

        return result;
    }

    /// <summary>
    /// Submits a request through Seerr.
    /// </summary>
    /// <param name="body">The request body.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome.</returns>
    [HttpPost("seerr/request")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RekoRequestResultDto>> RequestMedia(
        [FromBody] RekoRequestInput body,
        CancellationToken cancellationToken)
    {
        if (body is null || body.Id <= 0 || !IsValidMediaType(body.Type))
        {
            return BadRequest(new RekoError { Message = "Provide a positive id and a type of movie or tv." });
        }

        if (!_seerr.IsConfigured)
        {
            return BadRequest(new RekoError { Message = "Overseerr/Jellyseerr is not configured." });
        }

        if (body.Type == "tv" && body.Seasons is { Count: 0 })
        {
            return BadRequest(new RekoError { Message = "Choose at least one season." });
        }

        var user = ResolveUser();
        var seerrUserId = await _builder.ResolveSeerrUserAsync(user, cancellationToken).ConfigureAwait(false);

        var request = new SeerrRequestBody
        {
            MediaType = body.Type,
            MediaId = body.Id,
            Seasons = body.Type == "tv" ? body.Seasons?.Distinct().OrderBy(s => s).ToList() : null
        };

        try
        {
            var result = await _seerr.RequestAsync(request, seerrUserId, cancellationToken).ConfigureAwait(false);
            return new RekoRequestResultDto
            {
                Outcome = result.Outcome.ToString().ToLowerInvariant(),
                Message = result.Message,
                RequestId = result.Request?.Id
            };
        }
        catch (SeerrException ex)
        {
            _logger.LogInformation("A Reko request for {Type} {Id} failed: {Reason}", body.Type, body.Id, ex.Message);
            return BadRequest(new RekoError { Message = ex.Message });
        }
    }

    /// <summary>
    /// Returns the live Seerr state for a set of titles, so badges can be refreshed without
    /// reloading a whole rail.
    /// </summary>
    /// <param name="ids">A comma separated list of TMDB ids.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The request states.</returns>
    [HttpGet("seerr/states")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<RekoRequestStates>> GetRequestStates(
        [FromQuery] string ids,
        CancellationToken cancellationToken)
    {
        var result = new RekoRequestStates();

        if (!_seerr.IsConfigured || string.IsNullOrWhiteSpace(ids))
        {
            return result;
        }

        var parsed = ids
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0)
            .Where(value => value > 0)
            .Take(60)
            .ToList();

        var index = await _builder.GetRequestIndexAsync(cancellationToken).ConfigureAwait(false);
        foreach (var id in parsed)
        {
            if (!index.TryGetValue(id, out var info))
            {
                continue;
            }

            var state = new RekoRequestState
            {
                Id = id,
                Type = string.Equals(info.MediaType, "tv", StringComparison.Ordinal) ? "tv" : "movie",
                Status = CardFactory.DescribeMediaStatus(info.Status)
            };

            var newest = info.Requests
                .OrderByDescending(r => r.CreatedAt, StringComparer.Ordinal)
                .FirstOrDefault();

            if (newest is not null)
            {
                state.RequestStatus = CardFactory.DescribeRequestStatus(newest.Status);
            }

            foreach (var season in info.Seasons)
            {
                var name = CardFactory.DescribeMediaStatus(season.Status);
                if (name is not null)
                {
                    state.Seasons[season.SeasonNumber] = name;
                }
            }

            result.Items.Add(state);
        }

        return result;
    }

    // ---------------------------------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------------------------------
    private User? ResolveUser()
    {
        var principal = User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        // Jellyfin's ClaimsPrincipal extension methods live in Jellyfin.Api, which a plugin cannot
        // reference, and the claim names are not part of the plugin contract. Rather than guess at
        // an internal, the id is read from the well-known claim names and then validated against
        // IUserManager, so a wrong guess degrades to a username lookup instead of a wrong user.
        foreach (var claimType in UserIdClaims)
        {
            var value = principal.FindFirst(claimType)?.Value;
            if (Guid.TryParse(value, out var userId) && TryGetUser(userId) is { } byId)
            {
                return byId;
            }
        }

        foreach (var claimType in UsernameClaims)
        {
            var value = principal.FindFirst(claimType)?.Value;
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            try
            {
                return _userManager.GetUserByName(value);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Reko could not resolve a user by the {Claim} claim.", claimType);
            }
        }

        return null;
    }

    private User? TryGetUser(Guid userId)
    {
        try
        {
            return _userManager.GetUserById(userId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reko could not resolve a user by id.");
            return null;
        }
    }

    private IActionResult ServeResource(string resourceName)
    {
        var assembly = typeof(RekoController).Assembly;

        Stream? stream;
        try
        {
            stream = assembly.GetManifestResourceStream(ResourcePrefix + resourceName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reko could not open the embedded resource {Resource}.", resourceName);
            return NotFound();
        }

        if (stream is null)
        {
            _logger.LogError("Reko could not find the embedded resource {Resource}.", resourceName);
            return NotFound();
        }

        byte[] bytes;
        using (stream)
        {
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            bytes = buffer.ToArray();
        }

        // The bytes are copied rather than returning the manifest stream itself. MVC writes a
        // FileStreamResult after this method returns, so a stream that is disposed on the way out is
        // a closed stream by the time it is read.
        var contentType = ContentTypes.TryGetValue(Path.GetExtension(resourceName), out var known)
            ? known
            : "application/octet-stream";

        // The rewrite runs on every module, not just the entry point: a module is only ever loaded
        // because some other module imported it, so versioning the entry alone leaves the
        // sub-modules shared between builds. A stale card.js then runs against a fresh inject.js.
        if (Path.GetExtension(resourceName).Equals(".js", StringComparison.OrdinalIgnoreCase))
        {
            bytes = AddVersionToImports(bytes);
        }

        // Every module URL now carries a build-specific query, so a long cache is safe: a plugin
        // update produces different URLs and cannot be served from a stale cache. Without the
        // rewrite below the sub-modules would be shared between builds, and a 24 hour cache would
        // leave users running yesterday's client after installing today's plugin.
        Response.Headers.CacheControl = "public, max-age=86400";
        return File(bytes, contentType);
    }

    /// <summary>
    /// Rewrites a module's relative imports so each one carries the build version.
    /// </summary>
    /// <param name="bytes">The module's bytes.</param>
    /// <returns>The rewritten bytes.</returns>
    private static byte[] AddVersionToImports(byte[] bytes)
    {
        // Fully qualified: ControllerBase has a File method, so an unqualified File is the controller
        // action rather than System.IO.File.
        var assembly = typeof(RekoController).Assembly;
        var version = string.Format(
            CultureInfo.InvariantCulture,
            "v={0}-{1}",
            assembly.GetName().Version?.ToString() ?? "0",
            System.IO.File.GetLastWriteTimeUtc(assembly.Location).Ticks);

        var source = Encoding.UTF8.GetString(bytes);

        // One pass over the whole file rather than index bookkeeping, which is easy to get subtly
        // wrong and silently leaves later imports unversioned. The query goes *inside* the quotes:
        // appending after them produces a specifier that is no longer valid JavaScript.
        var rewritten = ImportPattern.Replace(
            source,
            match => string.Format(
                CultureInfo.InvariantCulture,
                "from '{0}?{1}'",
                match.Groups["specifier"].Value,
                version));

        return Encoding.UTF8.GetBytes(rewritten);
    }

    /// <summary>
    /// Matches the relative module specifiers in a module, at any depth.
    /// </summary>
    /// <remarks>
    /// The entry script sits at the root of the served path and imports <c>./js/name.js</c>, while a
    /// module in <c>js/</c> imports its siblings as <c>./name.js</c>. Both are covered by anchoring
    /// on the leading dot and requiring a <c>.js</c> ending rather than on a fixed directory.
    /// </remarks>
    private static readonly Regex ImportPattern = new(
        @"from '(?<specifier>\.[^']+\.js)'",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static bool IsSafeModuleName(string module)
    {
        if (string.IsNullOrWhiteSpace(module) || module.Length > 40)
        {
            return false;
        }

        foreach (var c in module)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidMediaType(string? type)
        => string.Equals(type, "movie", StringComparison.OrdinalIgnoreCase)
           || string.Equals(type, "tv", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Restricts the sort expression to a known-safe allowlist, so the parameter cannot be used to
    /// probe arbitrary TMDB behaviour.
    /// </summary>
    /// <param name="sort">The requested sort.</param>
    /// <returns>True when the sort is allowed.</returns>
    private static bool IsAllowedSort(string sort)
    {
        ReadOnlySpan<string> allowed =
        [
            "popularity.asc", "popularity.desc",
            "vote_average.asc", "vote_average.desc",
            "primary_release_date.asc", "primary_release_date.desc",
            "first_air_date.asc", "first_air_date.desc",
            "revenue.asc", "revenue.desc",
            "title.asc", "title.desc",
            "name.asc", "name.desc"
        ];

        foreach (var candidate in allowed)
        {
            if (string.Equals(candidate, sort, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// The payload behind <c>GET /Reko/bootstrap</c>.
/// </summary>
public sealed class RekoBootstrap
{
    /// <summary>Gets or sets the client configuration.</summary>
    [JsonPropertyName("config")]
    public RekoClientConfig Config { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether a TMDB key is configured.</summary>
    [JsonPropertyName("tmdbConfigured")]
    public bool TmdbConfigured { get; set; }

    /// <summary>Gets or sets a value indicating whether the library index has been built.</summary>
    [JsonPropertyName("libraryIndexed")]
    public bool LibraryIndexed { get; set; }

    /// <summary>Gets or sets the number of indexed library items.</summary>
    [JsonPropertyName("librarySize")]
    public int LibrarySize { get; set; }
}

/// <summary>
/// A simple error body, so the client can show the server's own message.
/// </summary>
public sealed class RekoError
{
    /// <summary>Gets or sets the message.</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// The body accepted by <c>POST /Reko/seerr/request</c>.
/// </summary>
public sealed class RekoRequestInput
{
    /// <summary>Gets or sets the TMDB id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the media type, either <c>movie</c> or <c>tv</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "movie";

    /// <summary>Gets or sets the seasons to request. TV only.</summary>
    [JsonPropertyName("seasons")]
    public List<int>? Seasons { get; set; }
}

/// <summary>
/// The result of a request submission.
/// </summary>
public sealed class RekoRequestResultDto
{
    /// <summary>Gets or sets the outcome name: <c>created</c>, <c>alreadySatisfied</c> or <c>duplicate</c>.</summary>
    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = string.Empty;

    /// <summary>Gets or sets a human-readable message.</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets the created request id, when one was created.</summary>
    [JsonPropertyName("requestId")]
    public int? RequestId { get; set; }
}

/// <summary>
/// A batch of Seerr request states.
/// </summary>
public sealed class RekoRequestStates
{
    /// <summary>Gets or sets the states.</summary>
    [JsonPropertyName("items")]
    public List<RekoRequestState> Items { get; set; } = new();
}
