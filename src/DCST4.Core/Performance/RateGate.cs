using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Core.Performance
{
    /// <summary>
    /// Spreads sends over time at a rate shared by all workers. Each send gets the next slot, one interval after the one
    /// before, but never a slot in the past: a worker that falls behind sends at once and does not burst to catch up.
    /// </summary>
    public sealed class RateGate
    {
        private readonly object _sync = new object();
        private readonly long _intervalTicks;
        private readonly Func<long> _now;
        private long _next = long.MinValue;

        /// <param name="ratePerSecond">0 or less lets everything through at once.</param>
        /// <param name="now">The clock in <see cref="Stopwatch"/> ticks. Null is <see cref="Stopwatch.GetTimestamp"/>.</param>
        public RateGate(double ratePerSecond, Func<long> now = null)
        {
            _now = now ?? Stopwatch.GetTimestamp;
            _intervalTicks = ratePerSecond > 0 ? (long)(Stopwatch.Frequency / ratePerSecond) : 0;
        }

        public bool Unlimited => _intervalTicks == 0;

        /// <summary>When the next send may go, in clock ticks.</summary>
        public long NextSlot()
        {
            var now = _now();
            if (Unlimited) return now;
            lock (_sync)
            {
                var slot = Math.Max(_next, now);
                _next = slot + _intervalTicks;
                return slot;
            }
        }

        /// <summary>Takes a slot and waits for it.</summary>
        public async Task WaitAsync(CancellationToken token)
        {
            if (Unlimited) return;
            var slot = NextSlot();
            var wait = slot - _now();
            if (wait <= 0) return;
            await Task.Delay(TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency), token).ConfigureAwait(false);
        }
    }
}
