using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Chaperone.Providers
{
    /// <summary>
    /// A simple global rate limiter that guarantees a minimum interval between calls.
    /// Used to respect the MusicBrainz (1 req/s) and Jikan (~2 req/s) rate limits even
    /// when many Jellyfin metadata refreshes run concurrently.
    /// </summary>
    internal sealed class Throttle
    {
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        private readonly TimeSpan _minInterval;
        private DateTime _lastCallUtc = DateTime.MinValue;

        public Throttle(TimeSpan minInterval)
        {
            _minInterval = minInterval;
        }

        /// <summary>
        /// Runs <paramref name="action"/> while holding a global lock and enforcing the minimum
        /// spacing between successive calls.
        /// </summary>
        public async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
        {
            await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var elapsed = DateTime.UtcNow - _lastCallUtc;
                var wait = _minInterval - elapsed;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    return await action().ConfigureAwait(false);
                }
                finally
                {
                    _lastCallUtc = DateTime.UtcNow;
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }
}
