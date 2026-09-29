using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Reko.Services;
using Jellyfin.Plugin.Reko.Services.Library;
using Jellyfin.Plugin.Reko.Services.Seerr;
using Jellyfin.Plugin.Reko.Services.Tmdb;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Reko;

/// <summary>
/// Registers Reko's services with the server's dependency injection container.
/// </summary>
/// <remarks>
/// This is the 12.x replacement for the removed <c>IServerEntryPoint</c>. The registrator is
/// discovered by reflection and must have a parameterless constructor, and the container is not
/// usable yet inside <see cref="RegisterServices"/>, which is why the logger comes from the
/// application host rather than from DI.
/// </remarks>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        _ = applicationHost;

        RekoHttpClients.AddTmdb(serviceCollection);
        RekoHttpClients.AddSeerr(serviceCollection);

        // The index is a singleton because it is a shared cache: every card in every rail reads it.
        serviceCollection.AddSingleton<LibraryIndex>();
        serviceCollection.AddSingleton<LibraryIndexBuilder>();
        serviceCollection.AddSingleton<PluginConfigurationAccessor>();
        serviceCollection.AddSingleton<RekoCacheInvalidator>();
        serviceCollection.AddSingleton<RekoConfigurationWatcher>();

        serviceCollection.AddSingleton<TmdbClient>();
        serviceCollection.AddSingleton<SeerrClient>();
        serviceCollection.AddSingleton<RekoPayloadBuilder>();

        serviceCollection.AddSingleton<IStartupFilter, ScriptInjectionStartupFilter>();

        // AddSingleton then AddHostedService(p => GetRequiredService) rather than AddHostedService<T>,
        // so the index and the service that maintains it are the same instances.
        serviceCollection.AddSingleton<LibraryIndexService>();
        serviceCollection.AddHostedService(p => p.GetRequiredService<LibraryIndexService>());
        serviceCollection.AddHostedService(p => p.GetRequiredService<RekoConfigurationWatcher>());
    }
}

/// <summary>
/// Watches the plugin configuration and clears the TMDB and Seerr caches when it changes.
/// </summary>
/// <remarks>
/// Jellyfin has no plugin-level "configuration saved" event, so this polls a fingerprint of the
/// fields that affect cached data. At one comparison every 30 seconds the cost is nil, and the
/// payoff is that changing a TMDB key or a region takes effect immediately rather than after a
/// server restart.
/// </remarks>
public sealed class RekoConfigurationWatcher : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly RekoCacheInvalidator _invalidator;
    private readonly ILogger<RekoConfigurationWatcher> _logger;
    private readonly Lock _gate = new();
    private string _fingerprint;

    /// <summary>
    /// Initializes a new instance of the <see cref="RekoConfigurationWatcher"/> class.
    /// </summary>
    /// <param name="invalidator">The invalidator.</param>
    /// <param name="tmdb">The TMDB client.</param>
    /// <param name="seerr">The Seerr client.</param>
    /// <param name="logger">The logger.</param>
    public RekoConfigurationWatcher(
        RekoCacheInvalidator invalidator,
        TmdbClient tmdb,
        SeerrClient seerr,
        ILogger<RekoConfigurationWatcher> logger)
    {
        _invalidator = invalidator;
        _logger = logger;
        _fingerprint = BuildFingerprint();

        invalidator.Register(tmdb.Invalidate);
        invalidator.Register(seerr.Invalidate);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                Poll();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private void Poll()
    {
        var current = BuildFingerprint();
        lock (_gate)
        {
            if (string.Equals(current, _fingerprint, StringComparison.Ordinal))
            {
                return;
            }

            _fingerprint = current;
        }

        _logger.LogInformation("Reko configuration changed; clearing TMDB and Seerr caches.");
        _invalidator.InvalidateAll();
    }

    private static string BuildFingerprint()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return string.Empty;
        }

        // Only the fields that change what is cached, not every presentation toggle.
        return string.Join(
            '|',
            config.TmdbApiKey,
            config.TmdbBaseUrl,
            config.TmdbImageBaseUrl,
            config.TmdbLanguage,
            config.TmdbImageLanguage,
            config.TmdbRegion,
            config.CacheMinutes,
            config.TrendingCacheMinutes,
            config.DetailCacheHours,
            config.EnableSeerr,
            config.SeerrUrl,
            config.SeerrApiKey,
            config.SeerrMapJellyfinUsers);
    }
}
