using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Reko.Services;

/// <summary>
/// A small TTL cache with single-flight reads, so a rail opened by several users at once costs one
/// upstream TMDB call instead of one per user.
/// </summary>
/// <typeparam name="TKey">The cache key type.</typeparam>
/// <typeparam name="TValue">The cached value type.</typeparam>
public sealed class TtlCache<TKey, TValue>
    where TKey : notnull
{
    /// <summary>
    /// How long a single production may run before it is abandoned.
    /// </summary>
    /// <remarks>
    /// A production is shared work, so it cannot be tied to the request that happened to trigger it.
    /// It does still need a ceiling: without one, a hung upstream call holds its slot for the whole
    /// TTL and every later caller waits on it too.
    /// </remarks>
    private static readonly TimeSpan ProductionTimeout = TimeSpan.FromSeconds(45);

    private readonly Dictionary<TKey, Entry> _entries = new();
    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="TtlCache{TKey, TValue}"/> class.
    /// </summary>
    /// <param name="timeProvider">The time provider used for expiry, injectable for tests.</param>
    public TtlCache(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Gets the number of live entries, after purging expired ones.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                Purge();
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Returns the cached value for <paramref name="key"/>, invoking <paramref name="factory"/> on a miss.
    /// Concurrent misses for the same key share a single invocation.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="ttl">How long a produced value stays fresh.</param>
    /// <param name="factory">Produces the value on a miss.</param>
    /// <param name="cancellationToken">
    /// Token for abandoning this caller's wait. It does not cancel the shared production, because the
    /// caller that missed is not the only one waiting and the value is still worth producing.
    /// </param>
    /// <returns>The cached or freshly produced value.</returns>
    public async Task<TValue> GetOrAddAsync(
        TKey key,
        TimeSpan ttl,
        Func<CancellationToken, Task<TValue>> factory,
        CancellationToken cancellationToken)
    {
        Task<TValue> task;
        TaskCompletionSource<TValue>? completion = null;

        // The lock only guards dictionary access. Nothing is awaited, and the factory is never
        // invoked under it: running caller code while holding a lock is how a cache becomes a
        // deadlock, because that code may itself come back to this cache.
        lock (_gate)
        {
            Purge();
            if (_entries.TryGetValue(key, out var existing))
            {
                task = existing.Task;
            }
            else
            {
                completion = new TaskCompletionSource<TValue>(TaskCreationOptions.RunContinuationsAsynchronously);
                task = completion.Task;
                _entries[key] = new Entry(task, _timeProvider.GetUtcNow() + ttl);
            }
        }

        if (completion is not null)
        {
            // Hop to the pool so the factory runs with no lock held.
            var pending = completion;
            _ = Task.Run(() => ProduceAsync(this, key, pending, factory), CancellationToken.None);
        }

        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stores a value directly, bypassing the factory.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="ttl">How long the value stays fresh.</param>
    public void Set(TKey key, TValue value, TimeSpan ttl)
    {
        lock (_gate)
        {
            _entries[key] = new Entry(Task.FromResult(value), _timeProvider.GetUtcNow() + ttl);
        }
    }

    /// <summary>
    /// Removes every entry whose string key starts with <paramref name="prefix"/>.
    /// </summary>
    /// <param name="prefix">The key prefix to invalidate.</param>
    public void InvalidatePrefix(string prefix)
    {
        lock (_gate)
        {
            List<TKey>? doomed = null;
            foreach (var pair in _entries)
            {
                if (pair.Key is string s && s.StartsWith(prefix, StringComparison.Ordinal))
                {
                    (doomed ??= new List<TKey>()).Add(pair.Key);
                }
            }

            if (doomed is not null)
            {
                foreach (var key in doomed)
                {
                    _entries.Remove(key);
                }
            }
        }
    }

    /// <summary>
    /// Removes every entry.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    private static async Task ProduceAsync(
        TtlCache<TKey, TValue> cache,
        TKey key,
        TaskCompletionSource<TValue> completion,
        Func<CancellationToken, Task<TValue>> factory)
    {
        // The cache's own timeout, not any caller's request token. Tying production to the request
        // that happened to miss means every caller after the first inherits that request's lifetime:
        // when it ends, the shared work is cancelled, the entry is evicted as a failure, and the next
        // caller starts the whole thing again. A rail that fails to parse then re-fetches on every
        // rebuild instead of once, which is how one bad response becomes a thousand upstream calls.
        using var timeout = new CancellationTokenSource(ProductionTimeout);

        try
        {
            completion.SetResult(await factory(timeout.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            completion.SetCanceled();
            cache.Evict(key, completion.Task);
        }
        catch (Exception ex)
        {
            completion.SetException(ex);

            // A failed refresh must not be cached, or one transient outage would poison the entry
            // until the TTL expires. Evicted here rather than by a waiter, because every waiter may
            // already have given up.
            cache.Evict(key, completion.Task);
        }
    }

    private void Evict(TKey key, Task<TValue> task)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry) && ReferenceEquals(entry.Task, task))
            {
                _entries.Remove(key);
            }
        }
    }

    private void Purge()
    {
        var now = _timeProvider.GetUtcNow();
        List<TKey>? doomed = null;
        foreach (var pair in _entries)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                (doomed ??= new List<TKey>()).Add(pair.Key);
            }
        }

        if (doomed is not null)
        {
            foreach (var key in doomed)
            {
                _entries.Remove(key);
            }
        }
    }

    private sealed class Entry
    {
        public Entry(Task<TValue> task, DateTimeOffset expiresAt)
        {
            Task = task;
            ExpiresAt = expiresAt;
        }

        public Task<TValue> Task { get; }

        public DateTimeOffset ExpiresAt { get; }
    }
}
