using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seerr = Jellyfin.Plugin.Reko.Services.Seerr;
using Tmdb = Jellyfin.Plugin.Reko.Services.Tmdb;

namespace Jellyfin.Plugin.Reko.Services;

/// <summary>
/// Injects Reko's stylesheet and bootstrap script into the jellyfin-web index page.
/// </summary>
/// <remarks>
/// Jellyfin 12 serves <c>index.html</c> as a plain static file with no injection hook, and it has no
/// SPA fallback for unknown <c>/web/*</c> paths, so a plugin cannot serve a page that the client
/// renders inside its own shell. Rewriting the index response in middleware is the only way to get
/// code into the page without writing to the web folder, which would be wiped on every jellyfin-web
/// update and needs a writable mount under a read-only container.
/// <para>
/// The alternative used by other plugins, patching the minified bundle with a regex, is deliberately
/// not used here: it breaks on any minifier or JSX-runtime change and cannot reach the Modern
/// layout's React tree at all. This approach only depends on the response containing
/// <c>&lt;/head&gt;</c> and <c>&lt;/body&gt;</c>.
/// </para>
/// </remarks>
public sealed class ScriptInjectionStartupFilter : IStartupFilter
{
    private readonly ILogger<ScriptInjectionStartupFilter> _logger;
    private readonly string _versionToken;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScriptInjectionStartupFilter"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public ScriptInjectionStartupFilter(ILogger<ScriptInjectionStartupFilter> logger)
    {
        _logger = logger;
        _versionToken = BuildVersionToken();
    }

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        // Outermost, so this runs before static file handling writes the body.
        return app =>
        {
            app.Use(InvokeAsync);
            next(app);
        };
    }

    private async Task InvokeAsync(HttpContext context, Func<Task> nextMiddleware)
    {
        if (!IsIndexRequest(context.Request))
        {
            await nextMiddleware().ConfigureAwait(false);
            return;
        }

        // Reading the body requires an unencoded, unbuffered response.
        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("Range");
        context.Request.Headers.Remove("If-Range");
        context.Request.Headers.Remove("If-Modified-Since");
        context.Request.Headers.Remove("If-None-Match");

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await nextMiddleware().ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        if (context.Response.StatusCode != StatusCodes.Status200OK)
        {
            await WriteThroughAsync(originalBody, buffer, context).ConfigureAwait(false);
            return;
        }

        buffer.Position = 0;
        string html;
        using (var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true))
        {
            html = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        if (html.Contains("reko-client", StringComparison.OrdinalIgnoreCase))
        {
            // Already injected. This can happen if two filters ran, and rewriting twice would add
            // the script twice.
            await WriteThroughAsync(originalBody, buffer, context).ConfigureAwait(false);
            return;
        }

        var injected = Inject(html);

        var bytes = Encoding.UTF8.GetBytes(injected);
        context.Response.ContentLength = bytes.Length;
        context.Response.Headers.Remove("ETag");
        context.Response.Headers.Remove("Last-Modified");
        context.Response.Headers.Remove("Accept-Ranges");
        context.Response.Headers.Remove("Content-Range");
        context.Response.Headers.ContentType = "text/html; charset=utf-8";

        await originalBody.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    private string Inject(string html)
    {
        var version = "?v=" + _versionToken;

        // Relative URLs so this works under a Jellyfin BaseUrl such as /jellyfin.
        //
        // early.js is a classic, non-deferred script placed at the very top of <head>. That is
        // deliberate: jellyfin-web creates its hash router while its own deferred bundles evaluate,
        // and the router reads the URL at that moment, so only a script that runs during parsing can
        // stop Jellyfin from acting on a tab index that no button backs yet. See Web/early.js.
        var early = "<script src=\"../Reko/early.js" + version + "\"></script>";
        var head = "<link rel=\"stylesheet\" href=\"../Reko/reko.css" + version + "\">";
        var body = "<script type=\"module\" src=\"../Reko/client.js" + version + "\" "
                   + "data-reko-plugin=\"Reko\"></script>";

        var result = html;

        if (result.Contains("</head>", StringComparison.OrdinalIgnoreCase))
        {
            result = Regex.Replace(
                result,
                "<head(\\s[^>]*)?>",
                match => "<head" + (match.Groups[1].Value ?? string.Empty) + ">" + early + head,
                RegexOptions.IgnoreCase,
                TimeSpan.FromMilliseconds(250));
        }
        else if (result.Contains("</body>", StringComparison.OrdinalIgnoreCase))
        {
            // A document with no head still honours a script placed at the very top of the body, which
            // still runs before the deferred list.
            result = Regex.Replace(
                result,
                "<body(\\s[^>]*)?>",
                match => "<body" + (match.Groups[1].Value ?? string.Empty) + ">" + early + head,
                RegexOptions.IgnoreCase,
                TimeSpan.FromMilliseconds(250));
        }
        else
        {
            _logger.LogDebug("Reko could not find a <head> or <body> to inject into.");
        }

        if (result.Contains("</body>", StringComparison.OrdinalIgnoreCase))
        {
            result = Regex.Replace(
                result,
                "</body>",
                body + "</body>",
                RegexOptions.IgnoreCase,
                TimeSpan.FromMilliseconds(250));
        }
        else
        {
            // A minified single-line page still has a body close; if it genuinely does not, appending
            // is better than dropping the tab entirely.
            _logger.LogDebug("Reko could not find a </body>; appending its script at the end of the document.");
            result += body;
        }

        return result;
    }

    private static async Task WriteThroughAsync(Stream target, MemoryStream buffer, HttpContext context)
    {
        buffer.Position = 0;
        await buffer.CopyToAsync(target, context.RequestAborted).ConfigureAwait(false);
    }

    private static bool IsIndexRequest(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method))
        {
            return false;
        }

        var path = request.Path.Value;
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        // Works with a BaseUrl, because the check is a suffix rather than an equality test.
        return path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith("/web", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildVersionToken()
    {
        try
        {
            var assembly = typeof(ScriptInjectionStartupFilter).Assembly;
            var version = assembly.GetName().Version?.ToString() ?? "0";
            var stamp = File.GetLastWriteTimeUtc(assembly.Location).Ticks.ToString(CultureInfo.InvariantCulture);
            return version + "-" + stamp;
        }
        catch (Exception)
        {
            return "0";
        }
    }
}

/// <summary>
/// A minimal named-client set for Reko's outbound HTTP calls.
/// </summary>
internal static class RekoHttpClients
{
    /// <summary>
    /// Configures the TMDB client.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static void AddTmdb(IServiceCollection services)
        => services.AddHttpClient(Tmdb.TmdbClient.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-Reko/1.0 (+https://github.com/isaacle-1/jellyfin-plugin-reko)");
        });

    /// <summary>
    /// Configures the Seerr client.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static void AddSeerr(IServiceCollection services)
        => services.AddHttpClient(Seerr.SeerrClient.HttpClientName, client =>
        {
            // Seerr is usually on the LAN or behind a reverse proxy on the same host, so be generous.
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-Reko/1.0 (+https://github.com/isaacle-1/jellyfin-plugin-reko)");
        });
}

/// <summary>
/// Holds the TMDB and Seerr caches so a configuration change can be picked up centrally.
/// </summary>
public sealed class RekoCacheInvalidator
{
    private readonly ConcurrentBag<Action> _actions = new();

    /// <summary>
    /// Registers a callback to run when the configuration changes.
    /// </summary>
    /// <param name="action">The callback.</param>
    public void Register(Action action) => _actions.Add(action);

    /// <summary>
    /// Runs every registered callback.
    /// </summary>
    public void InvalidateAll()
    {
        foreach (var action in _actions)
        {
            try
            {
                action();
            }
            catch (Exception)
            {
                // A failing invalidator must not stop the others.
            }
        }
    }
}
