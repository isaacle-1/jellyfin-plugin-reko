using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Reko.Services.Tmdb;

/// <summary>
/// A token bucket rate limiter used to stay inside TMDB's request budget.
/// </summary>
/// <remarks>
/// TMDB documents a limit "somewhere in the 40 requests per second range" and returns no rate-limit
/// headers, so the budget has to be enforced on this side rather than read from the response.
/// </remarks>
public sealed class TmdbRateLimiter
{
    private readonly object _lock = new();
    private readonly int _capacity;
    private readonly TimeSpan _refillPeriod;
    private double _tokens;
    private long _lastRefillTicks;

    /// <summary>
    /// Initializes a new instance of the <see cref="TmdbRateLimiter"/> class.
    /// </summary>
    /// <param name="requestsPerSecond">Sustained request rate allowed.</param>
    /// <param name="burst">Maximum burst size.</param>
    public TmdbRateLimiter(int requestsPerSecond, int burst)
    {
        _capacity = burst;
        _refillPeriod = TimeSpan.FromSeconds(1);
        _tokens = burst;
        _lastRefillTicks = Environment.TickCount64;
        RequestsPerSecond = requestsPerSecond;
    }

    /// <summary>
    /// Gets the sustained request rate allowed.
    /// </summary>
    public int RequestsPerSecond { get; }

    /// <summary>
    /// Waits until a request may be issued, or until the token is cancelled.
    /// </summary>
    /// <param name="cancellationToken">Token used to abandon the wait.</param>
    /// <returns>A task that completes when the caller may proceed.</returns>
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TimeSpan wait;
            lock (_lock)
            {
                Refill();
                if (_tokens >= 1)
                {
                    _tokens -= 1;
                    return;
                }

                var deficit = 1 - _tokens;
                wait = TimeSpan.FromSeconds(deficit / Math.Max(1, RequestsPerSecond));
            }

            // Clamp so a burst of waiters does not hammer the lock.
            var delay = wait < TimeSpan.FromMilliseconds(5) ? TimeSpan.FromMilliseconds(5) : wait;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Records a rejection so the limiter backs off after a 429.
    /// </summary>
    /// <param name="retryAfter">How long TMDB asked us to wait, if it said.</param>
    public void Penalise(TimeSpan retryAfter)
    {
        lock (_lock)
        {
            _tokens = 0;
            var floor = Environment.TickCount64 + (long)Math.Max(retryAfter.TotalMilliseconds, 0);
            if (floor > _lastRefillTicks)
            {
                _lastRefillTicks = floor;
            }
        }
    }

    private void Refill()
    {
        var now = Environment.TickCount64;
        var elapsed = now - _lastRefillTicks;
        if (elapsed <= 0)
        {
            return;
        }

        _lastRefillTicks = now;
        var elapsedSeconds = elapsed / 1000d;
        _tokens = Math.Min(_capacity, _tokens + (elapsedSeconds * RequestsPerSecond));
    }
}
