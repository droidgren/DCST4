using DCST4.Core.Network;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace DCST4.Core.Performance
{
    public enum LoadFailure
    {
        /// <summary>No response within the response timeout. Counts as an attempt.</summary>
        NoResponse,
        /// <summary>Aborted or closed while a request was outstanding. Counts as an attempt.</summary>
        AssociationLost,
        /// <summary>A-ASSOCIATE-RJ. No attempt was made.</summary>
        AssociationRejected,
        /// <summary>No connection or no answer to the A-ASSOCIATE-RQ. No attempt was made.</summary>
        ConnectFailed,
    }

    /// <summary>
    /// What the workers of one run, ramp step or soak record. Responses go in the second in which they arrived, on the
    /// monotonic clock from <see cref="StartTicks"/>. Throughput and latency leave the warm-up out, errors count from the
    /// start. One lock: a record is a few additions, far less than a C-STORE.
    /// </summary>
    public sealed class MetricsCollector
    {
        private sealed class Bucket
        {
            public readonly LatencyHistogram Latency = new LatencyHistogram();
            public long Attempts;
            public long Stored;
            public long Failed;
            public long Bytes;
        }

        private readonly object _sync = new object();
        private readonly long _warmUpEndTicks;
        private readonly int _step;
        private readonly Dictionary<int, Bucket> _seconds = new Dictionary<int, Bucket>();
        private int _closedBefore;

        private readonly LatencyHistogram _latency = new LatencyHistogram();
        private readonly LatencyHistogram _responseWait = new LatencyHistogram();
        private readonly LatencyHistogram _setup = new LatencyHistogram();
        private readonly LatencyHistogram _connect = new LatencyHistogram();
        private readonly StatusClassCounts _errors = new StatusClassCounts();
        private long _attempts;
        private long _measuredStored;
        private long _measuredBytes;
        private long _measuredAttempts;
        private long _lastMeasuredTicks;
        private int _associationsOpened;
        private int _open;

        // The soak window since the last CloseWindow.
        private readonly LatencyHistogram _windowLatency = new LatencyHistogram();
        private readonly LatencyHistogram _windowWait = new LatencyHistogram();
        private StatusClassCounts _windowErrors = new StatusClassCounts();
        private long _windowAttempts;
        private long _windowStored;
        private long _windowBytes;
        private long _windowStartTicks;

        public MetricsCollector(long startTicks, double warmUpSeconds, int step = 0)
        {
            StartTicks = startTicks;
            _warmUpEndTicks = startTicks + (long)(Math.Max(0, warmUpSeconds) * Stopwatch.Frequency);
            _windowStartTicks = startTicks;
            _step = step;
        }

        public long StartTicks { get; }
        public int OpenAssociations => Volatile.Read(ref _open);

        /// <summary>Stored instances, warm-up included, for the poller's sampling and the live status.</summary>
        public long TotalStored { get { lock (_sync) return _errors.Success + _errors.Warning; } }

        public void AssociationOpened(long connectTicks, long setupTicks)
        {
            Interlocked.Increment(ref _open);
            lock (_sync)
            {
                _associationsOpened++;
                _connect.Record(Micros(connectTicks));
                _setup.Record(Micros(setupTicks));
            }
        }

        public void AssociationClosed() => Interlocked.Decrement(ref _open);

        /// <summary>A C-STORE that got a response.</summary>
        /// <param name="firstByteTicks">Just before the first byte of the command was written.</param>
        /// <param name="lastByteTicks">When the last byte of the dataset was handed to the socket.</param>
        /// <param name="arrivedTicks">When the read loop had the whole response command.</param>
        public void RecordResponse(long firstByteTicks, long lastByteTicks, long arrivedTicks, ushort status, long bytes)
        {
            var state = StoreStatus.Classify(status);
            var stored = state == StoreState.Success || state == StoreState.Warning;
            var latency = Micros(arrivedTicks - firstByteTicks);
            var wait = Micros(arrivedTicks - lastByteTicks);
            var code = "0x" + status.ToString("X4", CultureInfo.InvariantCulture);

            lock (_sync)
            {
                _attempts++;
                switch (state)
                {
                    case StoreState.Success: _errors.Success++; break;
                    case StoreState.Warning: _errors.Warning++; break;
                    case StoreState.Cancel: _errors.Cancel++; break;
                    default: _errors.Failure++; break;
                }
                _errors.ByCode.TryGetValue(code, out var count);
                _errors.ByCode[code] = count + 1;

                var bucket = BucketAt(arrivedTicks);
                bucket.Attempts++;
                if (stored)
                {
                    bucket.Stored++;
                    bucket.Bytes += bytes;
                    bucket.Latency.Record(latency);
                }
                else
                {
                    bucket.Failed++;
                }

                if (arrivedTicks >= _warmUpEndTicks)
                {
                    _measuredAttempts++;
                    if (stored)
                    {
                        _measuredStored++;
                        _measuredBytes += bytes;
                        _latency.Record(latency);
                        _responseWait.Record(wait);
                    }
                    if (arrivedTicks > _lastMeasuredTicks) _lastMeasuredTicks = arrivedTicks;
                }

                _windowAttempts++;
                if (stored)
                {
                    _windowStored++;
                    _windowBytes += bytes;
                    _windowLatency.Record(latency);
                    _windowWait.Record(wait);
                }
                if (state == StoreState.Success) _windowErrors.Success++;
                else if (state == StoreState.Warning) _windowErrors.Warning++;
                else if (state == StoreState.Cancel) _windowErrors.Cancel++;
                else _windowErrors.Failure++;
            }
        }

        public void RecordFailure(LoadFailure failure, long atTicks)
        {
            lock (_sync)
            {
                var attempt = failure == LoadFailure.NoResponse || failure == LoadFailure.AssociationLost;
                Count(_errors, failure);
                Count(_windowErrors, failure);
                if (!attempt) return;

                _attempts++;
                _windowAttempts++;
                var bucket = BucketAt(atTicks);
                bucket.Attempts++;
                bucket.Failed++;
                if (atTicks >= _warmUpEndTicks) _measuredAttempts++;
            }
        }

        private static void Count(StatusClassCounts counts, LoadFailure failure)
        {
            switch (failure)
            {
                case LoadFailure.NoResponse: counts.NoResponse++; break;
                case LoadFailure.AssociationLost: counts.AssociationLost++; break;
                case LoadFailure.AssociationRejected: counts.AssociationRejected++; break;
                default: counts.ConnectFailed++; break;
            }
        }

        /// <summary>The seconds before <paramref name="second"/> that are not closed yet, in order. Empty seconds are included.</summary>
        public List<LoadSecond> CloseSecondsBefore(int second, double clientCpuPercent, int secondOffset = 0)
        {
            var rows = new List<LoadSecond>();
            lock (_sync)
            {
                for (var s = _closedBefore; s < second; s++)
                {
                    _seconds.TryGetValue(s, out var bucket);
                    _seconds.Remove(s);
                    var startOfSecond = StartTicks + (long)s * Stopwatch.Frequency;
                    rows.Add(new LoadSecond
                    {
                        Second = s + secondOffset,
                        Step = _step,
                        WarmUp = startOfSecond < _warmUpEndTicks,
                        OpenAssociations = OpenAssociations,
                        Attempts = bucket?.Attempts ?? 0,
                        Stored = bucket?.Stored ?? 0,
                        Failed = bucket?.Failed ?? 0,
                        Bytes = bucket?.Bytes ?? 0,
                        MegabytesPerSecond = Math.Round((bucket?.Bytes ?? 0) / 1000000.0, 3),
                        LatencyP50Ms = LatencySummary.Ms(bucket?.Latency.Percentile(50) ?? 0),
                        LatencyP95Ms = LatencySummary.Ms(bucket?.Latency.Percentile(95) ?? 0),
                        LatencyMaxMs = LatencySummary.Ms(bucket?.Latency.MaxMicros ?? 0),
                        ClientCpuPercent = Math.Round(clientCpuPercent, 1),
                    });
                }
                if (second > _closedBefore) _closedBefore = second;
            }
            return rows;
        }

        /// <summary>Closes every second up to and including the one of <paramref name="endTicks"/>.</summary>
        public List<LoadSecond> CloseAll(long endTicks, double clientCpuPercent, int secondOffset = 0)
        {
            int last;
            lock (_sync)
            {
                last = SecondOf(endTicks);
                if (_seconds.Count > 0) last = Math.Max(last, _seconds.Keys.Max());
            }
            var rows = CloseSecondsBefore(last + 1, clientCpuPercent, secondOffset);
            if (rows.Count > 0) rows[rows.Count - 1].Partial = true;
            return rows;
        }

        /// <summary>What the run gave. Throughput and latency over the window after the warm-up, errors over the whole run.</summary>
        public LoadSummary Summarize()
        {
            lock (_sync)
            {
                var windowTicks = Math.Max(0, _lastMeasuredTicks - _warmUpEndTicks);
                var seconds = (double)windowTicks / Stopwatch.Frequency;
                var notStored = _errors.NotStored;
                return new LoadSummary
                {
                    WindowMs = (long)(seconds * 1000),
                    Attempts = _attempts,
                    Stored = _measuredStored,
                    Bytes = _measuredBytes,
                    StoredPerSecond = seconds > 0 ? Math.Round(_measuredStored / seconds, 3) : 0,
                    MegabytesPerSecond = seconds > 0 ? Math.Round(_measuredBytes / seconds / 1000000.0, 3) : 0,
                    Latency = LatencySummary.From(_latency),
                    ResponseWait = LatencySummary.From(_responseWait),
                    AssociationSetup = LatencySummary.From(_setup),
                    Connect = LatencySummary.From(_connect),
                    AssociationsOpened = _associationsOpened,
                    Errors = _errors.Clone(),
                    ErrorRate = _attempts > 0 ? Math.Round((double)notStored / _attempts, 6) : 0,
                };
            }
        }

        /// <summary>What happened since the last call, or since the start, for a soak snapshot. No warm-up applies.</summary>
        public LoadSummary CloseWindow(long nowTicks)
        {
            lock (_sync)
            {
                var seconds = (double)(nowTicks - _windowStartTicks) / Stopwatch.Frequency;
                var summary = new LoadSummary
                {
                    WindowMs = (long)(seconds * 1000),
                    Attempts = _windowAttempts,
                    Stored = _windowStored,
                    Bytes = _windowBytes,
                    StoredPerSecond = seconds > 0 ? Math.Round(_windowStored / seconds, 3) : 0,
                    MegabytesPerSecond = seconds > 0 ? Math.Round(_windowBytes / seconds / 1000000.0, 3) : 0,
                    Latency = LatencySummary.From(_windowLatency),
                    ResponseWait = LatencySummary.From(_windowWait),
                    Errors = _windowErrors,
                    ErrorRate = _windowAttempts > 0 ? Math.Round((double)_windowErrors.NotStored / _windowAttempts, 6) : 0,
                };
                _windowLatency.Clear();
                _windowWait.Clear();
                _windowErrors = new StatusClassCounts();
                _windowAttempts = 0;
                _windowStored = 0;
                _windowBytes = 0;
                _windowStartTicks = nowTicks;
                return summary;
            }
        }

        public int SecondOf(long ticks) => (int)Math.Max(0, (ticks - StartTicks) / Stopwatch.Frequency);

        private Bucket BucketAt(long ticks)
        {
            var second = Math.Max(SecondOf(ticks), _closedBefore);
            if (!_seconds.TryGetValue(second, out var bucket))
            {
                bucket = new Bucket();
                _seconds[second] = bucket;
            }
            return bucket;
        }

        internal static long Micros(long ticks) => ticks <= 0 ? 0 : (long)(ticks * 1000000.0 / Stopwatch.Frequency);
    }
}
