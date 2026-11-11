using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DCST4.Core.Performance
{
    /// <summary>
    /// Finds whether a soak got worse over time. The first window is a warm-up. The baseline is the mean of the next 10 %
    /// of the windows, at least one, and the end the mean of the last 10 %. Degraded when the least-squares trend of p95
    /// latency rises by more than the threshold over the run and the end is above the baseline by as much, and by at least
    /// <see cref="MinRiseMs"/>, when the
    /// end's throughput is below the baseline by more than the drop threshold, or when errors appear after an error-free
    /// baseline.
    /// </summary>
    public static class DegradationDetector
    {
        public const double DefaultLatencyRisePercent = 50;
        public const double DefaultThroughputDropPercent = 20;
        private const double EdgeShare = 0.1;
        /// <summary>A rise smaller than this is not counted, however large in percent: 1 ms to 2 ms is timer noise, not degradation.</summary>
        public const double MinRiseMs = 5;

        public static DegradationVerdict Detect(IReadOnlyList<SoakWindow> windows, double latencyRisePercent = DefaultLatencyRisePercent, double throughputDropPercent = DefaultThroughputDropPercent)
        {
            var verdict = new DegradationVerdict();
            var measured = (windows ?? new SoakWindow[0]).Skip(1).Where(w => w.Summary != null).ToList();
            if (measured.Count < 2)
            {
                verdict.TooShort = true;
                verdict.Description = "Too few snapshot windows after the first to say whether the system got worse. Run longer, or with a shorter snapshot interval.";
                return verdict;
            }

            var edge = Math.Max(1, (int)Math.Ceiling(measured.Count * EdgeShare));
            var baseline = measured.Take(edge).ToList();
            var end = measured.Skip(measured.Count - edge).ToList();

            verdict.BaselineP95Ms = Round(baseline.Average(w => P95(w)));
            verdict.EndP95Ms = Round(end.Average(w => P95(w)));
            verdict.BaselineStoredPerSecond = Round(baseline.Average(w => w.Summary.StoredPerSecond));
            verdict.EndStoredPerSecond = Round(end.Average(w => w.Summary.StoredPerSecond));

            var slope = Slope(measured.Select(P95).ToList());
            if (verdict.BaselineP95Ms > 0) verdict.P95TrendPercent = Round(slope * (measured.Count - 1) / verdict.BaselineP95Ms * 100);

            var factor = 1 + latencyRisePercent / 100.0;
            if (verdict.BaselineP95Ms > 0 && verdict.P95TrendPercent > latencyRisePercent && verdict.EndP95Ms > verdict.BaselineP95Ms * factor
                && verdict.EndP95Ms - verdict.BaselineP95Ms >= MinRiseMs)
            {
                verdict.Reasons.Add("p95-trend");
            }
            if (verdict.BaselineStoredPerSecond > 0 && verdict.EndStoredPerSecond < verdict.BaselineStoredPerSecond * (1 - throughputDropPercent / 100.0))
            {
                verdict.Reasons.Add("throughput-drop");
            }
            if (baseline.All(w => Problems(w) == 0) && measured.Skip(edge).Any(w => Problems(w) > 0))
            {
                verdict.Reasons.Add("errors");
            }

            verdict.Degraded = verdict.Reasons.Count > 0;
            verdict.Description = verdict.Degraded
                ? "The system got worse during the soak: " + string.Join(", ", verdict.Reasons.Select(r => Words(r, latencyRisePercent, throughputDropPercent))) + "."
                : "No degradation was found over the soak.";
            return verdict;
        }

        /// <summary>Least squares of y against 0, 1, 2 ...</summary>
        internal static double Slope(IReadOnlyList<double> values)
        {
            var n = values.Count;
            if (n < 2) return 0;
            var meanX = (n - 1) / 2.0;
            var meanY = values.Average();
            double num = 0, den = 0;
            for (var i = 0; i < n; i++)
            {
                num += (i - meanX) * (values[i] - meanY);
                den += (i - meanX) * (i - meanX);
            }
            return den == 0 ? 0 : num / den;
        }

        private static double P95(SoakWindow window) => window.Summary.Latency?.P95Ms ?? 0;

        private static long Problems(SoakWindow window) => window.Summary.Errors?.Problems ?? 0;

        private static double Round(double value) => Math.Round(value, 3);

        private static string Words(string reason, double rise, double drop)
        {
            switch (reason)
            {
                case "p95-trend": return $"p95 latency rose by more than {Number(rise)} %";
                case "throughput-drop": return $"throughput fell by more than {Number(drop)} %";
                default: return "errors appeared after an error-free start";
            }
        }

        private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
