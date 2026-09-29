using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Reko.Services.Library;

/// <summary>
/// One library item Reko cares about, reduced to the minimum needed to decide whether a TMDB title
/// can be played and to label it.
/// </summary>
/// <param name="ItemId">The Jellyfin item id, used as the playback target.</param>
/// <param name="TmdbId">The TMDB id from the item's provider ids.</param>
/// <param name="Kind">The item kind.</param>
/// <param name="Name">The item name.</param>
/// <param name="RunTimeTicks">The runtime, for progress maths.</param>
/// <param name="PremiereDate">The premiere date.</param>
/// <param name="CommunityRating">The community rating.</param>
/// <param name="Genres">The item's genre names, used as personalisation seeds.</param>
public readonly record struct LibraryEntry(
    Guid ItemId,
    int TmdbId,
    BaseItemKind Kind,
    string Name,
    long? RunTimeTicks,
    DateTime? PremiereDate,
    double? CommunityRating,
    IReadOnlyList<string> Genres)
{
    /// <summary>
    /// Gets a value indicating whether this entry is a movie.
    /// </summary>
    public bool IsMovie => Kind == BaseItemKind.Movie;

    /// <summary>
    /// Gets a value indicating whether this entry is a series.
    /// </summary>
    public bool IsSeries => Kind == BaseItemKind.Series;
}

/// <summary>
/// A server-wide map from TMDB id to the Jellyfin item that carries it.
/// </summary>
/// <remarks>
/// Everything library-related in Reko reads from this one structure: "in library" badges, playback
/// targets, and the seeds for personalisation. It is built once by a background service and
/// invalidated by <see cref="ILibraryManager"/> events, which keeps the hot path free of per-card
/// Jellyfin queries.
/// </remarks>
public sealed class LibraryIndex
{
    /// <summary>
    /// The provider id key Jellyfin uses for TMDB, from <see cref="MetadataProvider"/>.
    /// </summary>
    public const string TmdbProviderKey = nameof(MetadataProvider.Tmdb);

    private readonly Lock _gate = new();
    private Dictionary<int, LibraryEntry> _byTmdbId = new();
    private volatile bool _isReady;

    /// <summary>
    /// Gets a value indicating whether the index has been built at least once.
    /// </summary>
    public bool IsReady => _isReady;

    /// <summary>
    /// Gets the number of indexed items.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _byTmdbId.Count;
            }
        }
    }

    /// <summary>
    /// Gets the TMDB id to entry map. Treat the result as read-only.
    /// </summary>
    public IReadOnlyDictionary<int, LibraryEntry> Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<int, LibraryEntry>(_byTmdbId);
            }
        }
    }

    /// <summary>
    /// Looks up a single TMDB id.
    /// </summary>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <returns>The entry, or null when the title is not in the library.</returns>
    public LibraryEntry? Find(int tmdbId)
    {
        lock (_gate)
        {
            return _byTmdbId.TryGetValue(tmdbId, out var entry) ? entry : null;
        }
    }

    /// <summary>
    /// Looks up several TMDB ids at once, which is what a rail of cards needs.
    /// </summary>
    /// <param name="tmdbIds">The TMDB ids.</param>
    /// <returns>Only the ids that are present in the library.</returns>
    public Dictionary<int, LibraryEntry> FindMany(IEnumerable<int> tmdbIds)
    {
        lock (_gate)
        {
            var result = new Dictionary<int, LibraryEntry>();
            foreach (var id in tmdbIds)
            {
                if (_byTmdbId.TryGetValue(id, out var entry))
                {
                    result[id] = entry;
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Replaces the index contents.
    /// </summary>
    /// <param name="entries">The new entries.</param>
    public void Replace(IEnumerable<LibraryEntry> entries)
    {
        var map = new Dictionary<int, LibraryEntry>();
        foreach (var entry in entries)
        {
            // A library can legitimately contain the same TMDB id twice (alternate versions are
            // grouped by Jellyfin, but a film can appear in two libraries). First wins.
            if (!map.ContainsKey(entry.TmdbId))
            {
                map[entry.TmdbId] = entry;
            }
        }

        lock (_gate)
        {
            _byTmdbId = map;
            _isReady = true;
        }
    }
}

/// <summary>
/// Builds the <see cref="LibraryIndex"/> from the Jellyfin library.
/// </summary>
public sealed class LibraryIndexBuilder
{
    private const int PageSize = 2000;

    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryIndexBuilder"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    public LibraryIndexBuilder(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Walks every movie and series in the library and returns the TMDB-indexed subset.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The indexed entries.</returns>
    public async Task<List<LibraryEntry>> BuildAsync(CancellationToken cancellationToken)
    {
        var results = new List<LibraryEntry>();
        var startIndex = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                Recursive = true,
                IsFolder = false,
                OrderBy = new[] { (ItemSortBy.Name, SortOrder.Ascending) },
                StartIndex = startIndex,
                Limit = PageSize,
                DtoOptions = new MediaBrowser.Controller.Dto.DtoOptions(true)
            };

            var page = _libraryManager.GetItemList(query);
            if (page is null || page.Count == 0)
            {
                break;
            }

            foreach (var item in page)
            {
                var entry = ToEntry(item);
                if (entry.HasValue)
                {
                    results.Add(entry.Value);
                }
            }

            if (page.Count < PageSize)
            {
                break;
            }

            startIndex += PageSize;

            // Yield between pages so a large library does not monopolise the thread pool.
            await Task.Yield();
        }

        return results;
    }

    private static LibraryEntry? ToEntry(BaseItem item)
    {
        if (item.ProviderIds is null
            || !item.ProviderIds.TryGetValue(LibraryIndex.TmdbProviderKey, out var raw)
            || !int.TryParse(raw, out var tmdbId)
            || tmdbId <= 0)
        {
            return null;
        }

        return new LibraryEntry(
            item.Id,
            tmdbId,
            item.GetBaseItemKind(),
            item.Name,
            item.RunTimeTicks,
            item.PremiereDate,
            item.CommunityRating,
            item.Genres ?? Array.Empty<string>());
    }
}
