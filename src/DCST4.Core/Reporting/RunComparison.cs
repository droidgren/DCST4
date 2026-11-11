using DCST4.Core.Capability;
using DCST4.Core.Performance;
using DCST4.Core.Testing;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DCST4.Core.Reporting
{
    public enum FindingKind
    {
        /// <summary>Worse than the baseline. Makes the command line return 1.</summary>
        Regression,
        Improvement,
        /// <summary>Not the same as the baseline, and neither better nor worse by any rule here.</summary>
        Difference,
        /// <summary>One side has no verdict to compare with: not run, cancelled, a tool error or settings that differ. Never blamed on the system under test.</summary>
        NotComparable,
    }

    /// <summary>One thing that is not the same in a run as in the baseline.</summary>
    public sealed class ComparisonFinding
    {
        public FindingKind Kind { get; set; }
        /// <summary>Result, Capability, Chain, Performance, Behaviour, Negotiation or Status codes.</summary>
        public string Area { get; set; }
        public string Subject { get; set; }
        public string Baseline { get; set; }
        public string Value { get; set; }
        public string Note { get; set; }

        public override string ToString() =>
            $"{Area}: {Subject}: {Baseline ?? "-"} -> {Value ?? "-"}" + (string.IsNullOrEmpty(Note) ? string.Empty : $" ({Note})");
    }

    /// <summary>One measured number of a load test in the baseline and in a run.</summary>
    public sealed class PerformanceDelta
    {
        public string TestId { get; set; }
        /// <summary>"run", or "4 associations" for a ramp step.</summary>
        public string Scope { get; set; }
        public string Metric { get; set; }
        public string Unit { get; set; }
        public double Baseline { get; set; }
        public double Value { get; set; }
        /// <summary>(value - baseline) / baseline, in percent. Null when the baseline is 0 and for the error rate, which is compared in percentage points.</summary>
        public double? DeltaPercent { get; set; }
        /// <summary>Only for the error rate: the change in percentage points.</summary>
        public double? DeltaPoints { get; set; }
        public bool HigherIsBetter { get; set; }
        /// <summary>One of the metrics that can make a regression. The others are shown for context, since they are noisy in a short run.</summary>
        public bool Decisive { get; set; }
        /// <summary>Worse than the baseline by more than the threshold.</summary>
        public bool Worse { get; set; }
        /// <summary>Better than the baseline by more than the threshold.</summary>
        public bool Better { get; set; }
        /// <summary>Worse, decisive and measured with the same settings.</summary>
        public bool Regression { get; set; }
    }

    /// <summary>A run against the baseline.</summary>
    public sealed class RunDelta
    {
        public NamedRun Run { get; set; }
        public List<ComparisonFinding> Findings { get; } = new List<ComparisonFinding>();
        public List<PerformanceDelta> Performance { get; } = new List<PerformanceDelta>();
        public int Regressions => Findings.Count(f => f.Kind == FindingKind.Regression);
    }

    public sealed class ComparisonOptions
    {
        public const double DefaultThresholdPercent = 10;
        public const double LargestThresholdPercent = 1000;

        /// <summary>How much worse a throughput or a latency may be, in percent of the baseline, before it is a regression.</summary>
        public double ThresholdPercent { get; set; } = DefaultThresholdPercent;
        /// <summary>A latency must also rise by this much. Below it, a percentage says more about the clock than about the system.</summary>
        public double LatencyFloorMs { get; set; } = 1;
        /// <summary>The error rate may rise by this many percentage points.</summary>
        public double ErrorRatePoints { get; set; } = 1;

        /// <summary>What is wrong with the options, or null.</summary>
        public string Validate()
        {
            if (double.IsNaN(ThresholdPercent) || ThresholdPercent <= 0 || ThresholdPercent > LargestThresholdPercent)
            {
                return $"The threshold must be more than 0 and at most {LargestThresholdPercent.ToString(CultureInfo.InvariantCulture)} percent.";
            }
            return null;
        }
    }

    public sealed class ComparisonResult
    {
        public NamedRun Baseline { get; set; }
        public List<RunDelta> Runs { get; } = new List<RunDelta>();
        public ComparisonOptions Options { get; set; }
        public int Regressions => Runs.Sum(r => r.Regressions);
        public bool HasDifferences => Runs.Any(r => r.Findings.Count > 0);

        /// <summary>"2 regressions beyond 10 %", for a status bar or the last line on a console.</summary>
        public string Summary
        {
            get
            {
                var threshold = Options.ThresholdPercent.ToString("0.##", CultureInfo.InvariantCulture) + " %";
                if (!HasDifferences) return "No differences.";
                var others = Runs.Sum(r => r.Findings.Count) - Regressions;
                var other = others == 0 ? string.Empty : others == 1 ? ", and 1 other difference" : $", and {others} other differences";
                if (Regressions == 0) return $"No regressions with a threshold of {threshold}{other}.";
                return (Regressions == 1 ? "1 regression" : $"{Regressions} regressions") + $" with a threshold of {threshold}{other}.";
            }
        }
    }

    /// <summary>
    /// Compares runs by what they found, never by what belongs to one run alone: timestamps, durations, offsets, the seed,
    /// UIDs, file paths and the wording of an explanation are not looked at. The first run is the baseline.
    /// </summary>
    public static class RunComparer
    {
        // Counts that the capability and chain findings already report, and settings of DCST4 itself.
        private static readonly HashSet<string> FingerprintKeysLeftOut = new HashSet<string>(StringComparer.Ordinal)
        {
            "sop-classes-accepted", "combinations-accepted", "transfer-syntaxes-accepted", "chain-accepted", "chain-retrievable", "commitment-linger",
        };

        public static ComparisonResult Compare(IReadOnlyList<NamedRun> runs, ComparisonOptions options = null)
        {
            if (runs == null || runs.Count < 2) throw new ArgumentException("A comparison needs at least two runs.", nameof(runs));
            options = options ?? new ComparisonOptions();
            var problem = options.Validate();
            if (problem != null) throw new ArgumentException(problem, nameof(options));

            var result = new ComparisonResult { Baseline = runs[0], Options = options };
            foreach (var run in runs.Skip(1))
            {
                var delta = new RunDelta { Run = run };
                CompareResults(runs[0].Report, run.Report, delta);
                CompareCapabilities(runs[0].Report, run.Report, delta);
                CompareChain(runs[0].Report, run.Report, delta);
                CompareBehaviour(runs[0].Report, run.Report, delta);
                ComparePerformance(runs[0].Report, run.Report, delta, options);
                result.Runs.Add(delta);
            }
            return result;
        }

        /// <summary>2 for a result the test accepts, 1 for not supported, 0 for one it does not accept, and null when there is no verdict on the system.</summary>
        internal static int? Rank(TestResultClass result)
        {
            switch (result)
            {
                case TestResultClass.PASS:
                case TestResultClass.EXPECTED_FAILURE: return 2;
                case TestResultClass.NOT_SUPPORTED: return 1;
                case TestResultClass.FAIL:
                case TestResultClass.TIMEOUT: return 0;
                default: return null;
            }
        }

        private static FindingKind KindOf(TestResultClass baseline, TestResultClass value)
        {
            var before = Rank(baseline);
            var after = Rank(value);
            if (before == null || after == null) return FindingKind.NotComparable;
            if (after < before) return FindingKind.Regression;
            return after > before ? FindingKind.Improvement : FindingKind.Difference;
        }

        private static void CompareResults(TestRunReport baseline, TestRunReport run, RunDelta delta)
        {
            var after = new Dictionary<string, TestResult>(StringComparer.Ordinal);
            foreach (var r in run.Results) if (r.TestId != null) after[r.TestId] = r;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var before in baseline.Results.Where(r => r.TestId != null))
            {
                if (!seen.Add(before.TestId)) continue;
                if (!after.TryGetValue(before.TestId, out var now))
                {
                    delta.Findings.Add(new ComparisonFinding { Kind = FindingKind.NotComparable, Area = "Result", Subject = before.TestId, Baseline = before.Result.ToString(), Value = "not run" });
                    continue;
                }

                if (before.Result != now.Result)
                {
                    delta.Findings.Add(new ComparisonFinding
                    {
                        Kind = KindOf(before.Result, now.Result), Area = "Result", Subject = before.TestId,
                        Baseline = before.Result.ToString(), Value = now.Result.ToString(), Note = now.Explanation,
                    });
                    continue;
                }

                // The same verdict from other statuses is worth a line: a warning in place of a success, or another failure code.
                var codesBefore = Codes(before);
                var codesNow = Codes(now);
                if (codesBefore != codesNow && Rank(before.Result) != null && before.Performance == null)
                {
                    delta.Findings.Add(new ComparisonFinding { Kind = FindingKind.Difference, Area = "Status codes", Subject = before.TestId, Baseline = codesBefore, Value = codesNow });
                }
            }

            foreach (var now in run.Results.Where(r => r.TestId != null && !seen.Contains(r.TestId)))
            {
                if (!seen.Add(now.TestId)) continue;
                delta.Findings.Add(new ComparisonFinding { Kind = FindingKind.NotComparable, Area = "Result", Subject = now.TestId, Baseline = "not run", Value = now.Result.ToString() });
            }
        }

        /// <summary>The raw statuses of a test as a set with counts, so that their order does not matter.</summary>
        private static string Codes(TestResult result)
        {
            var codes = (result.StatusCodes ?? new List<DimseStatusRecord>()).Where(s => s != null)
                .GroupBy(s => s.Operation + "=" + s.Code, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Count() == 1 ? g.Key : g.Key + " x" + g.Count().ToString(CultureInfo.InvariantCulture)).ToList();
            return codes.Count == 0 ? "none" : string.Join(", ", codes);
        }

        private static CapabilityMatrix MatrixOf(TestRunReport report) => report.Results.Select(r => r.CapabilityMatrix).FirstOrDefault(m => m != null);

        private static void CompareCapabilities(TestRunReport baseline, TestRunReport run, RunDelta delta)
        {
            var before = MatrixOf(baseline);
            var after = MatrixOf(run);
            if (before == null && after == null) return;
            if (before == null || after == null)
            {
                delta.Findings.Add(new ComparisonFinding
                {
                    Kind = FindingKind.NotComparable, Area = "Capability", Subject = "Capability matrix",
                    Baseline = before == null ? "none" : "probed", Value = after == null ? "none" : "probed", Note = "Only one of the runs has a capability matrix.",
                });
                return;
            }

            var syntaxNames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var s in after.TransferSyntaxes.Concat(before.TransferSyntaxes)) if (s?.Uid != null) syntaxNames[s.Uid] = s.ShortName ?? s.Name ?? s.Uid;
            Func<string, string> syntax = uid => syntaxNames.TryGetValue(uid, out var name) ? name : uid;

            var unanswered = 0;
            foreach (var row in before.Rows)
            {
                var lost = new List<string>();
                var gained = new List<string>();
                var changed = new List<string>();
                foreach (var cell in row.Cells)
                {
                    var now = after.Find(row.SopClassUid, cell.TransferSyntaxUid);
                    if (now == null || !CapabilityMatrix.WasAnswered(cell.Result) || !CapabilityMatrix.WasAnswered(now.Result))
                    {
                        if (now == null || now.Result != cell.Result) unanswered++;
                        continue;
                    }
                    if (cell.Result == now.Result) continue;
                    if (cell.Result == CapabilityResult.Accepted) lost.Add($"{syntax(cell.TransferSyntaxUid)} ({now.Result})");
                    else if (now.Result == CapabilityResult.Accepted) gained.Add($"{syntax(cell.TransferSyntaxUid)} (was {cell.Result})");
                    else changed.Add($"{syntax(cell.TransferSyntaxUid)} ({cell.Result} \u2192 {now.Result})");
                }

                var name = row.SopClassName ?? row.SopClassUid;
                if (lost.Count > 0) delta.Findings.Add(new ComparisonFinding { Kind = FindingKind.Regression, Area = "Capability", Subject = name, Baseline = "Accepted", Value = "no longer accepted: " + string.Join(", ", lost), Note = row.SopClassUid });
                if (gained.Count > 0) delta.Findings.Add(new ComparisonFinding { Kind = FindingKind.Improvement, Area = "Capability", Subject = name, Baseline = "not accepted", Value = "now accepted: " + string.Join(", ", gained), Note = row.SopClassUid });
                if (changed.Count > 0) delta.Findings.Add(new ComparisonFinding { Kind = FindingKind.Difference, Area = "Capability", Subject = name, Baseline = "rejected", Value = "rejected for another reason: " + string.Join(", ", changed), Note = row.SopClassUid });
            }

            var onlyAfter = after.Rows.Count(r => before.Rows.All(b => b.SopClassUid != r.SopClassUid));
            if (unanswered > 0 || onlyAfter > 0)
            {
                var parts = new List<string>();
                if (unanswered > 0) parts.Add($"{unanswered} combinations were not answered in one of the runs");
                if (onlyAfter > 0) parts.Add($"{onlyAfter} SOP classes were only asked about in this run");
                delta.Findings.Add(new ComparisonFinding { Kind = FindingKind.NotComparable, Area = "Capability", Subject = "Capability matrix", Note = string.Join(", and ", parts) + "." });
            }

            var roles = Changed(before.RoleSelectionReplies, after.RoleSelectionReplies, r => r.SopClassUid, r => r.Replied + "/" + r.ScuRole + "/" + r.ScpRole);
            if (roles > 0) delta.Findings.Add(new ComparisonFinding { Kind = FindingKind.Difference, Area = "Negotiation", Subject = "SCP/SCU role selection", Note = $"The reply differs for {roles} SOP classes." });
            var extended = Changed(before.ExtendedNegotiationReplies, after.ExtendedNegotiationReplies, r => r.SopClassUid, r => r.Replied + "/" + r.Accepted);
            if (extended > 0) delta.Findings.Add(new ComparisonFinding { Kind = FindingKind.Difference, Area = "Negotiation", Subject = "Extended negotiation", Note = $"The reply differs for {extended} SOP classes." });
        }

        private static int Changed<T>(IEnumerable<T> before, IEnumerable<T> after, Func<T, string> key, Func<T, string> value)
        {
            var now = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in after ?? Enumerable.Empty<T>()) if (item != null && key(item) != null) now[key(item)] = value(item);
            return (before ?? Enumerable.Empty<T>()).Count(item => item != null && key(item) != null && now.TryGetValue(key(item), out var other) && other != value(item));
        }

        private static void CompareChain(TestRunReport baseline, TestRunReport run, RunDelta delta)
        {
            if (baseline.Chain == null || run.Chain == null) return;
            var steps = new[]
            {
                Tuple.Create("accepted", (Func<ChainRow, ChainStep>)(r => r.Accepted)), Tuple.Create("indexed", (Func<ChainRow, ChainStep>)(r => r.Indexed)),
                Tuple.Create("retrievable", (Func<ChainRow, ChainStep>)(r => r.Retrievable)), Tuple.Create("intact", (Func<ChainRow, ChainStep>)(r => r.Intact)),
                Tuple.Create("committed", (Func<ChainRow, ChainStep>)(r => r.Committed)),
            };

            foreach (var before in baseline.Chain.Where(r => r?.ObjectType != null))
            {
                var now = run.Chain.FirstOrDefault(r => r?.ObjectType == before.ObjectType);
                if (now == null) continue;
                foreach (var step in steps)
                {
                    var a = step.Item2(before);
                    var b = step.Item2(now);
                    if (a == null || b == null || a.Result == b.Result) continue;
                    delta.Findings.Add(new ComparisonFinding
                    {
                        Kind = KindOf(a.Result, b.Result), Area = "Chain", Subject = $"{before.DisplayName ?? before.ObjectType}: {step.Item1}",
                        Baseline = a.Result.ToString(), Value = b.Result.ToString(), Note = b.Explanation,
                    });
                }
            }
        }

        private static void CompareBehaviour(TestRunReport baseline, TestRunReport run, RunDelta delta)
        {
            var after = VendorFingerprint.From(run).ToDictionary(i => i.Key, StringComparer.Ordinal);
            foreach (var before in VendorFingerprint.From(baseline))
            {
                if (before.Measured || before.Value == null || FingerprintKeysLeftOut.Contains(before.Key)) continue;
                if (!after.TryGetValue(before.Key, out var now) || now.Value == null || now.Value == before.Value) continue;
                delta.Findings.Add(new ComparisonFinding { Kind = FindingKind.Difference, Area = "Behaviour", Subject = before.Label, Baseline = before.Value, Value = now.Value, Note = before.Source });
            }
        }

        // ---------------------------------------------------------------- performance

        private static void ComparePerformance(TestRunReport baseline, TestRunReport run, RunDelta delta, ComparisonOptions options)
        {
            foreach (var before in baseline.Results.Where(r => r.Performance != null && r.TestId != null))
            {
                var now = run.Results.FirstOrDefault(r => r.TestId == before.TestId);
                if (now?.Performance == null) continue;

                var a = before.Performance;
                var b = now.Performance;
                var settings = SettingsThatDiffer(a, b);
                var comparable = settings.Count == 0;
                if (!comparable)
                {
                    delta.Findings.Add(new ComparisonFinding
                    {
                        Kind = FindingKind.NotComparable, Area = "Performance", Subject = before.TestId, Baseline = "measured", Value = "measured with other settings",
                        Note = "The settings differ, so the numbers are shown and not judged: " + string.Join(", ", settings) + ".",
                    });
                }

                var first = delta.Performance.Count;
                if (a.Mode == LoadMode.Ramp)
                {
                    foreach (var step in a.Steps ?? new List<RampStep>())
                    {
                        var other = (b.Steps ?? new List<RampStep>()).FirstOrDefault(s => s.Associations == step.Associations);
                        if (other == null) continue;
                        AddSummary(delta, before.TestId, step.Associations.ToString(CultureInfo.InvariantCulture) + (step.Associations == 1 ? " association" : " associations"), step.Summary, other.Summary, options);
                    }
                }
                else
                {
                    AddSummary(delta, before.TestId, "run", a.Summary, b.Summary, options);
                }

                var availableBefore = a.TimeToAvailable?.Available;
                var availableNow = b.TimeToAvailable?.Available;
                if (availableBefore != null && availableNow != null && availableBefore.Count > 0 && availableNow.Count > 0)
                {
                    delta.Performance.Add(Relative(before.TestId, "run", "Time-to-available p95", "ms", availableBefore.P95Ms, availableNow.P95Ms, false, true, options, 0));
                }

                foreach (var row in delta.Performance.Skip(first))
                {
                    row.Regression = row.Worse && row.Decisive && comparable;
                    if (!row.Regression) continue;
                    delta.Findings.Add(new ComparisonFinding
                    {
                        Kind = FindingKind.Regression, Area = "Performance", Subject = $"{row.TestId}, {row.Scope}: {row.Metric}",
                        Baseline = Shown(row.Baseline, row.Unit), Value = Shown(row.Value, row.Unit), Note = DeltaText(row),
                    });
                }
            }
        }

        private static List<string> SettingsThatDiffer(PerformanceResult a, PerformanceResult b)
        {
            var differ = new List<string>();
            Action<string, object, object> check = (name, x, y) =>
            {
                var left = Convert.ToString(x, CultureInfo.InvariantCulture) ?? string.Empty;
                var right = Convert.ToString(y, CultureInfo.InvariantCulture) ?? string.Empty;
                if (left != right) differ.Add($"{name} {(left.Length == 0 ? "-" : left)} and {(right.Length == 0 ? "-" : right)}");
            };

            check("mode", a.Mode, b.Mode);
            check("mix", a.Mix, b.Mix);
            check("transfer syntax", a.TransferSyntaxUid, b.TransferSyntaxUid);
            check("maximum PDU", a.MaxPdu, b.MaxPdu);
            var s = a.Settings;
            var t = b.Settings;
            if (s == null || t == null || a.Mode != b.Mode) return differ;

            check("instances per association", s.InstancesPerAssociation, t.InstancesPerAssociation);
            check("warm-up", s.WarmUpSeconds, t.WarmUpSeconds);
            switch (a.Mode)
            {
                case LoadMode.Load:
                    check("associations", s.Associations, t.Associations);
                    check("rate", s.RatePerSecond, t.RatePerSecond);
                    check("count", s.InstanceCount, t.InstanceCount);
                    if (s.InstanceCount == 0) check("duration", s.DurationSeconds, t.DurationSeconds);
                    break;
                case LoadMode.Ramp:
                    check("step duration", s.RampStepSeconds, t.RampStepSeconds);
                    break;
                default:
                    check("associations", s.Associations, t.Associations);
                    check("rate", s.SoakRatePerSecond, t.SoakRatePerSecond);
                    check("duration", s.SoakSeconds, t.SoakSeconds);
                    break;
            }
            return differ;
        }

        private static void AddSummary(RunDelta delta, string testId, string scope, LoadSummary a, LoadSummary b, ComparisonOptions options)
        {
            // Nothing stored on one side is a result of its own, which the result class already says.
            if (a == null || b == null || a.Stored == 0 || b.Stored == 0) return;

            delta.Performance.Add(Relative(testId, scope, "Stored per second", "/s", a.StoredPerSecond, b.StoredPerSecond, true, true, options, 0));
            delta.Performance.Add(Relative(testId, scope, "MB per second", "MB/s", a.MegabytesPerSecond, b.MegabytesPerSecond, true, false, options, 0));
            if (a.Latency != null && b.Latency != null && a.Latency.Count > 0 && b.Latency.Count > 0)
            {
                delta.Performance.Add(Relative(testId, scope, "Latency p50", "ms", a.Latency.P50Ms, b.Latency.P50Ms, false, false, options, options.LatencyFloorMs));
                delta.Performance.Add(Relative(testId, scope, "Latency p95", "ms", a.Latency.P95Ms, b.Latency.P95Ms, false, true, options, options.LatencyFloorMs));
                delta.Performance.Add(Relative(testId, scope, "Latency p99", "ms", a.Latency.P99Ms, b.Latency.P99Ms, false, false, options, options.LatencyFloorMs));
                delta.Performance.Add(Relative(testId, scope, "Latency maximum", "ms", a.Latency.MaxMs, b.Latency.MaxMs, false, false, options, options.LatencyFloorMs));
            }
            if (a.AssociationSetup != null && b.AssociationSetup != null && a.AssociationSetup.Count > 0 && b.AssociationSetup.Count > 0)
            {
                delta.Performance.Add(Relative(testId, scope, "Association setup p95", "ms", a.AssociationSetup.P95Ms, b.AssociationSetup.P95Ms, false, false, options, options.LatencyFloorMs));
            }

            var points = (b.ErrorRate - a.ErrorRate) * 100;
            delta.Performance.Add(new PerformanceDelta
            {
                TestId = testId, Scope = scope, Metric = "Error rate", Unit = "%", Baseline = a.ErrorRate * 100, Value = b.ErrorRate * 100, DeltaPoints = points,
                HigherIsBetter = false, Decisive = true, Worse = points > options.ErrorRatePoints, Better = points < -options.ErrorRatePoints,
            });
        }

        private static PerformanceDelta Relative(string testId, string scope, string metric, string unit, double baseline, double value, bool higherIsBetter, bool decisive, ComparisonOptions options, double floor)
        {
            var row = new PerformanceDelta { TestId = testId, Scope = scope, Metric = metric, Unit = unit, Baseline = baseline, Value = value, HigherIsBetter = higherIsBetter, Decisive = decisive };
            if (!(baseline > 0)) return row;

            var percent = (value - baseline) / baseline * 100;
            row.DeltaPercent = Math.Round(percent, 1);
            var worse = higherIsBetter ? -percent : percent;
            var far = Math.Abs(value - baseline) >= floor;
            row.Worse = worse > options.ThresholdPercent && far;
            row.Better = -worse > options.ThresholdPercent && far;
            return row;
        }

        internal static string Shown(double value, string unit) => ReportFormat.Decimal(value) + (unit == "%" ? " %" : " " + unit);

        internal static string DeltaText(PerformanceDelta row)
        {
            if (row.DeltaPoints.HasValue)
            {
                var points = Math.Round(row.DeltaPoints.Value, 2);
                return (points > 0 ? "+" : string.Empty) + points.ToString("0.##", CultureInfo.InvariantCulture).Replace("-", "\u2212") + " percentage points";
            }
            return row.DeltaPercent.HasValue ? ReportFormat.Percent(row.DeltaPercent.Value, true) : "no baseline";
        }
    }
}
