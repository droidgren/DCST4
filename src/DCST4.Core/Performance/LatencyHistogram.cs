using System;

namespace DCST4.Core.Performance
{
    /// <summary>
    /// Durations in microseconds, in log-linear buckets: exact below 512 µs, and above that 256 buckets per power of two,
    /// so a percentile is at most 0.4 % below the true value. The maximum is kept exactly. Memory is fixed whatever the
    /// number of samples, which is what a soak test of many hours needs. Not thread-safe: the collector locks around it.
    /// </summary>
    public sealed class LatencyHistogram
    {
        private const int ExactBelow = 512;
        private const int SubBuckets = 256;
        private const int ExactBits = 9;
        /// <summary>Values are clamped to 2^33 µs, more than two hours.</summary>
        private const int TopBit = 33;
        private const int BucketCount = ExactBelow + (TopBit - ExactBits + 1) * SubBuckets;

        private readonly long[] _counts = new long[BucketCount];

        public long Count { get; private set; }
        public long MaxMicros { get; private set; }
        public long MinMicros { get; private set; } = long.MaxValue;
        public double SumMicros { get; private set; }

        public void Record(long micros)
        {
            if (micros < 0) micros = 0;
            var clamped = Math.Min(micros, (1L << (TopBit + 1)) - 1);
            _counts[Index(clamped)]++;
            Count++;
            SumMicros += micros;
            if (micros > MaxMicros) MaxMicros = micros;
            if (micros < MinMicros) MinMicros = micros;
        }

        public void Add(LatencyHistogram other)
        {
            if (other == null || other.Count == 0) return;
            for (var i = 0; i < BucketCount; i++) _counts[i] += other._counts[i];
            Count += other.Count;
            SumMicros += other.SumMicros;
            if (other.MaxMicros > MaxMicros) MaxMicros = other.MaxMicros;
            if (other.MinMicros < MinMicros) MinMicros = other.MinMicros;
        }

        public void Clear()
        {
            Array.Clear(_counts, 0, _counts.Length);
            Count = 0;
            SumMicros = 0;
            MaxMicros = 0;
            MinMicros = long.MaxValue;
        }

        public LatencyHistogram Clone()
        {
            var copy = new LatencyHistogram();
            copy.Add(this);
            return copy;
        }

        /// <summary>
        /// Nearest rank: the smallest recorded value such that at least <paramref name="percent"/> % of the samples are at or
        /// below it, as the lower edge of its bucket. 100 gives the exact maximum. 0 if there are no samples.
        /// </summary>
        public long Percentile(double percent)
        {
            if (Count == 0) return 0;
            if (percent >= 100) return MaxMicros;
            var rank = (long)Math.Ceiling(Math.Max(percent, 0) / 100.0 * Count);
            if (rank < 1) rank = 1;

            long seen = 0;
            for (var i = 0; i < BucketCount; i++)
            {
                seen += _counts[i];
                if (seen >= rank) return Math.Min(Math.Max(LowerEdge(i), MinMicros), MaxMicros);
            }
            return MaxMicros;
        }

        public double MeanMicros => Count == 0 ? 0 : SumMicros / Count;

        private static int Index(long value)
        {
            if (value < ExactBelow) return (int)value;
            var bit = 63 - LeadingZeros((ulong)value);
            var top = (int)(value >> (bit - (ExactBits - 1)));
            return ExactBelow + (bit - ExactBits) * SubBuckets + (top - SubBuckets);
        }

        private static long LowerEdge(int index)
        {
            if (index < ExactBelow) return index;
            var octave = (index - ExactBelow) / SubBuckets;
            var top = (index - ExactBelow) % SubBuckets + SubBuckets;
            return (long)top << (octave + 1);
        }

        private static int LeadingZeros(ulong value)
        {
            var n = 0;
            if (value == 0) return 64;
            while ((value & 0x8000000000000000UL) == 0)
            {
                value <<= 1;
                n++;
            }
            return n;
        }
    }
}
