using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DCST4.Core.Performance
{
    /// <summary>
    /// Finds the ramp step where more associations stop helping: the first step whose throughput rises less than the minimum
    /// gain over the best step before it, whose p95 latency is more than a factor above that of step 1, or whose error rate
    /// rises by more than some percentage points over the step before, or that lost an association.
    /// </summary>
    public static class SaturationDetector
    {
        public const double DefaultMinGainPercent = 10;
        public const double DefaultLatencyFactor = 2;
        public const double DefaultErrorRisePoints = 1;

        public static SaturationVerdict Detect(IReadOnlyList<RampStep> steps, double minGainPercent = DefaultMinGainPercent, double latencyFactor = DefaultLatencyFactor, double errorRisePoints = DefaultErrorRisePoints)
        {
            var verdict = new SaturationVerdict();
            if (steps == null || steps.Count == 0)
            {
                verdict.Description = "No step was measured.";
                return verdict;
            }

            var best = steps[0];
            var baseP95 = steps[0].Summary.Latency?.P95Ms ?? 0;
            for (var k = 0; k < steps.Count && !verdict.Found; k++)
            {
                var step = steps[k];
                var summary = step.Summary;
                string reason = null;

                if (summary.Errors != null && summary.Errors.AssociationLost > 0) reason = "errors";
                if (reason == null && k > 0)
                {
                    var bestEarlier = steps.Take(k).Max(s => s.Summary.StoredPerSecond);
                    var previousRate = steps[k - 1].Summary.ErrorRate;
                    if (summary.StoredPerSecond < bestEarlier * (1 + minGainPercent / 100.0)) reason = "throughput";
                    else if (baseP95 > 0 && (summary.Latency?.P95Ms ?? 0) > baseP95 * latencyFactor) reason = "latency";
                    else if ((summary.ErrorRate - previousRate) * 100 > errorRisePoints) reason = "errors";
                }
                else if (reason == null && summary.ErrorRate * 100 > errorRisePoints)
                {
                    reason = "errors";
                }

                if (reason != null)
                {
                    verdict.Found = true;
                    verdict.Step = step.Step;
                    verdict.Associations = step.Associations;
                    verdict.Reason = reason;
                    step.Saturated = true;
                }
            }

            foreach (var step in steps)
            {
                if (step.Summary.StoredPerSecond > best.Summary.StoredPerSecond) best = step;
            }
            verdict.BestStep = best.Step;
            verdict.BestAssociations = best.Associations;
            verdict.Description = verdict.Found
                ? $"Saturated at step {Int(verdict.Step)} with {Int(verdict.Associations)} associations: {Words(verdict.Reason, minGainPercent, latencyFactor, errorRisePoints)}. The highest throughput was at step {Int(verdict.BestStep)}, with {Int(verdict.BestAssociations)} associations."
                : $"No saturation up to {Int(steps[steps.Count - 1].Associations)} associations.";
            return verdict;
        }

        private static string Words(string reason, double gain, double factor, double points)
        {
            switch (reason)
            {
                case "throughput": return $"throughput rose less than {Number(gain)} % over the best step before it";
                case "latency": return $"p95 latency was more than {Number(factor)} times that of step 1";
                default: return $"the error rate rose more than {Number(points)} percentage point{(points == 1 ? string.Empty : "s")}, or an association was lost";
            }
        }

        private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
        private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
