using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Reko.Services.Library;

/// <summary>
/// Keeps the library index fresh.
/// </summary>
/// <remarks>
/// The index is the input to every "in library" badge, the continue watching row and the seeds for
/// personalisation, so it is built once at startup and then maintained two ways: immediately on
/// <see cref="ILibraryManager"/> events, and periodically as a safety net in case an event is missed
/// or the library changes underneath the server.
/// </remarks>
public sealed class LibraryIndexService : BackgroundService
{
    private static readonly TimeSpan RebuildInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(10);

    private readonly LibraryIndex _index;
    private readonly LibraryIndexBuilder _builder;
    private readonly ILibraryManager _libraryManager;
    private readonly PluginConfigurationAccessor _config;
    private readonly ILogger<LibraryIndexService> _logger;

    private readonly SemaphoreSlim _rebuildGate = new(1, 1);
    private CancellationTokenSource _debounce = new();
    private int _debounceGeneration;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryIndexService"/> class.
    /// </summary>
    /// <param name="index">The index to maintain.</param>
    /// <param name="builder">The builder.</param>
    /// <param name="libraryManager">The library manager, used for change events.</param>
    /// <param name="config">The configuration accessor.</param>
    /// <param name="logger">The logger.</param>
    public LibraryIndexService(
        LibraryIndex index,
        LibraryIndexBuilder builder,
        ILibraryManager libraryManager,
        PluginConfigurationAccessor config,
        ILogger<LibraryIndexService> logger)
    {
        _index = index;
        _builder = builder;
        _libraryManager = libraryManager;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        _libraryManager.ItemRemoved += OnItemChanged;

        try
        {
            await RebuildAsync(stoppingToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(RebuildInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RebuildAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // An unhandled exception here would stop the host, which is not something a browsing tab
            // is allowed to do to a media server.
            _logger.LogError(ex, "The Reko library index service stopped unexpectedly.");
        }
        finally
        {
            _libraryManager.ItemAdded -= OnItemChanged;
            _libraryManager.ItemUpdated -= OnItemChanged;
            _libraryManager.ItemRemoved -= OnItemChanged;
            await _debounce.CancelAsync().ConfigureAwait(false);
            _debounce.Dispose();
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _debounce.Dispose();
        _rebuildGate.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// Rebuilds the index now, if a rebuild is not already running.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the rebuild has finished or been skipped.</returns>
    public async Task RebuildAsync(CancellationToken cancellationToken)
    {
        if (!await _rebuildGate.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false))
        {
            _logger.LogDebug("Skipping a Reko library index rebuild because one is already running.");
            return;
        }

        try
        {
            if (_libraryManager.IsScanRunning)
            {
                // Rebuilding mid-scan produces a partial index that then looks stale. The periodic
                // timer will pick it up once the scan settles.
                _logger.LogDebug("Deferring the Reko library index rebuild because a scan is running.");
                return;
            }

            var started = DateTimeOffset.UtcNow;
            var entries = await _builder.BuildAsync(cancellationToken).ConfigureAwait(false);
            _index.Replace(entries);

            if (_config.Current.EnableDebugLogging)
            {
                _logger.LogInformation(
                    "Reko indexed {Count} TMDB items in {Elapsed} ms.",
                    entries.Count,
                    (DateTimeOffset.UtcNow - started).TotalMilliseconds);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not rebuild the Reko library index.");
        }
        finally
        {
            _rebuildGate.Release();
        }
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        var kind = e?.Item?.GetBaseItemKind();
        if (kind is not (BaseItemKind.Movie or BaseItemKind.Series))
        {
            return;
        }

        // A scan touches thousands of items. Debounce so that one rebuild follows the burst rather
        // than one rebuild per item.
        var generation = Interlocked.Increment(ref _debounceGeneration);
        _debounce.Cancel();
        _debounce.Dispose();
        _debounce = new CancellationTokenSource();

        var token = _debounce.Token;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(DebounceDelay, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (generation != Volatile.Read(ref _debounceGeneration))
                {
                    return;
                }

                await RebuildAsync(CancellationToken.None).ConfigureAwait(false);
            },
            CancellationToken.None);
    }
}
