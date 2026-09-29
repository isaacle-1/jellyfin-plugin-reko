using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Reko.Services;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.Reko.Tests;

/// <summary>
/// Asserts the cache's behaviour when callers come and go.
/// </summary>
/// <remarks>
/// Production is shared: the first caller to miss starts it and everyone else waits on the same
/// task. Tying that production to the first caller's request token means every later caller inherits
/// that request's lifetime — when it ends, the shared work is cancelled, the entry is evicted as a
/// failure, and the next caller starts over. With a rail that fails to parse, that turns one bad
/// response into a re-fetch on every rebuild, which is how a single upstream error became a couple
/// of hundred calls in two minutes.
/// </remarks>
public class TtlCacheTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task ConcurrentMissesShareOneProduction()
    {
        var cache = new TtlCache<string, int>();
        var started = 0;

        var tasks = new List<Task<int>>();
        for (var i = 0; i < 10; i++)
        {
            tasks.Add(cache.GetOrAddAsync("k", Ttl, _ =>
            {
                Interlocked.Increment(ref started);
                Thread.Sleep(50);
                return Task.FromResult(42);
            }, CancellationToken.None));
        }

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal(42, r));
        Assert.Equal(1, Volatile.Read(ref started));
    }

    [Fact]
    public async Task ACallerGivingUpDoesNotCancelTheSharedProduction()
    {
        var cache = new TtlCache<string, int>();
        var release = new TaskCompletionSource();
        var productionStarted = new TaskCompletionSource();
        var productionCancelled = false;

        // A caller that will walk away, and that is the one that happens to miss.
        using (var abandoned = new CancellationTokenSource())
        {
            var first = cache.GetOrAddAsync("k", Ttl, async token =>
            {
                productionStarted.SetResult();
                try
                {
                    await release.Task.WaitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    productionCancelled = true;
                    throw;
                }

                return 7;
            }, abandoned.Token);

            await productionStarted.Task;
            await abandoned.CancelAsync();

            // The abandoned caller is released with a cancellation, which is correct: it is no longer
            // waiting for anything.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        }

        release.SetResult();
        await WaitForAsync(() => cache.Count == 1 || !productionCancelled);

        Assert.False(productionCancelled, "the shared production was cancelled by a departing caller");

        // And a second caller still gets the value that was produced for it.
        Assert.Equal(7, await cache.GetOrAddAsync("k", Ttl, _ => Task.FromResult(-1), CancellationToken.None));
    }

    [Fact]
    public async Task AFailedProductionIsNotCached()
    {
        var cache = new TtlCache<string, int>();
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetOrAddAsync("k", Ttl, _ =>
        {
            attempts++;
            throw new InvalidOperationException("nope");
        }, CancellationToken.None));

        // Retried, rather than the failure being remembered for the whole TTL: one transient outage
        // must not poison a rail for hours.
        var value = await cache.GetOrAddAsync("k", Ttl, _ =>
        {
            attempts++;
            return Task.FromResult(5);
        }, CancellationToken.None);

        Assert.Equal(5, value);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task AnExpiredEntryIsProducedAgain()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var cache = new TtlCache<string, int>(time);
        var attempts = 0;

        Task<int> Produce()
            => cache.GetOrAddAsync("k", Ttl, _ =>
            {
                attempts++;
                return Task.FromResult(attempts);
            }, CancellationToken.None);

        Assert.Equal(1, await Produce());
        Assert.Equal(1, await Produce());

        time.Advance(Ttl + TimeSpan.FromSeconds(1));

        Assert.Equal(2, await Produce());
    }

    /// <summary>
    /// Polls until a condition holds, so a test does not depend on how quickly a pooled task happens
    /// to be scheduled.
    /// </summary>
    /// <param name="condition">The condition to wait for.</param>
    /// <returns>A task that completes when the condition holds.</returns>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("the condition never held");
    }
}
