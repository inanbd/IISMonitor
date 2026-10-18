using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace IISMonitor.Website.Site;

/// <summary>
/// Limits how many contact messages one visitor can send per hour. Checked by the page itself
/// rather than by rate-limiting middleware, so a visitor over the limit keeps what they typed.
/// </summary>
public sealed class ContactThrottle(IOptionsMonitor<EmailOptions> options) : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(visitor =>
        RateLimitPartition.GetFixedWindowLimiter(visitor, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, options.CurrentValue.MaxMessagesPerHour),
            Window = TimeSpan.FromHours(1),
            QueueLimit = 0,
        }));

    /// <summary>Counts one message for this visitor; false when they are over the limit.</summary>
    public bool TryAcquire(string visitor)
    {
        using var lease = _limiter.AttemptAcquire(visitor);
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}
