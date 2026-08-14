using System;

namespace Jellyfin.Plugin.Chaperone.Providers
{
    /// <summary>
    /// Result of a music rating lookup: the rating (or null), plus whether the lookup couldn't be
    /// completed because of a transient failure (rate limit / network) rather than a genuine miss.
    /// </summary>
    public readonly record struct MusicRatingResult(string? Rating, bool TransientFailure);

    /// <summary>
    /// Thrown when an external lookup can't complete for a transient reason (a rate limit or network
    /// error), as opposed to returning a definitive "no match". Callers use this to avoid marking a
    /// track unidentifiable when the request simply didn't get through.
    /// </summary>
    internal sealed class TransientLookupException : Exception
    {
        public TransientLookupException(string message)
            : base(message)
        {
        }

        public TransientLookupException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
