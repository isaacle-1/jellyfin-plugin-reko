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
    /// <param name="cancellationToken">Token used to abandon the wait.</param>
    /// <returns>The cached or freshly produced value.</returns>
    public async Task<TValue> GetOrAddAsync(
        TKey key,
        TimeSpan ttl,
        Func<CancellationToken, Task<TValue>> factory,
        CancellationToken cancellationToken)
    {
        Task<TValue> task;
        bool isOwner;
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
                isOwner = false;
            }
            else
            {
                completion = new TaskCompletionSource<TValue>(TaskCreationOptions.RunContinuationsAsynchronously);
                task = completion.Task;
                _entries[key] = new Entry(task, _timeProvider.GetUtcNow() + ttl);
                isOwner = true;
            }
        }

        if (completion is not null)
        {
            // Hop to the pool so the factory runs with no lock held.
            var pending = completion;
            _ = Task.Run(() => ProduceAsync(pending, factory, cancellationToken), CancellationToken.None);
        }

        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A failed refresh must not be cached, or one transient outage would poison the entry
            // until the TTL expires. Only the caller that created the entry may evict it.
            if (isOwner)
            {
                Evict(key, task);
            }

            throw;
        }
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
        TaskCompletionSource<TValue> completion,
        Func<CancellationToken, Task<TValue>> factory,
        CancellationToken cancellationToken)
    {
        try
        {
            completion.SetResult(await factory(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            completion.SetCanceled(cancellationToken);
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
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
