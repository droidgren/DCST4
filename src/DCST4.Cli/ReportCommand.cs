using DCST4.Core.Reporting;
using DCST4.Core.Testing;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DCST4.Cli
{
    /// <summary>
    /// "report" and "compare": both read saved result files and write one HTML file. Nothing is sent anywhere, so they need
    /// no profile and no log.
    /// </summary>
    internal static class ReportCommand
    {
        /// <summary>report --run &lt;file&gt; [--out &lt;file&gt;]</summary>
        public static int Report(string[] args, Func<string, int> usageError, Func<string, int> refuse)
        {
            string run = null, output = null;
            for (var i = 1; i < args.Length; i++)
            {
                var name = args[i].ToLowerInvariant();
                if (name != "--run" && name != "--out") return usageError($"Unknown option {args[i]}.");
                if (i + 1 >= args.Length) return usageError($"{args[i]} needs a value.");
                if (name == "--run") run = args[++i]; else output = args[++i];
            }
            if (string.IsNullOrWhiteSpace(run)) return usageError("report needs --run <file>, the DCST4-run-<time>.json file of a run.");

            try
            {
                var report = RunFile.Load(run);
                var path = ReportFiles.WriteReportFor(report, run, string.IsNullOrWhiteSpace(output) ? null : output);
                Console.WriteLine("HTML: " + path);
                return RunExitCodes.Clean;
            }
            catch (RunFileException ex)
            {
                return refuse(ex.Message);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                return refuse($"The report could not be written: {ex.Message}");
            }
        }

        /// <summary>compare --runs &lt;a&gt; &lt;b&gt; [&lt;c&gt; ...] [--threshold 10] [--out &lt;file&gt;]</summary>
        public static int Compare(string[] args, Func<string, int> usageError, Func<string, int> refuse)
        {
            var runs = new List<string>();
            string output = null;
            var options = new ComparisonOptions();
            for (var i = 1; i < args.Length; i++)
            {
                var name = args[i].ToLowerInvariant();
                if (name == "--runs")
                {
                    // Every argument up to the next option is a run.
                    while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) runs.Add(args[++i]);
                    continue;
                }
                if (name != "--threshold" && name != "--out") return usageError($"Unknown option {args[i]}.");
                if (i + 1 >= args.Length) return usageError($"{args[i]} needs a value.");
                if (name == "--out")
                {
                    output = args[++i];
                    continue;
                }
                if (!double.TryParse(args[++i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var threshold)) return usageError($"--threshold must be a number of percent, not '{args[i]}'.");
                options.ThresholdPercent = threshold;
            }

            if (runs.Count < 2) return usageError("compare needs --runs with at least two result files. The first is the baseline.");
            var problem = options.Validate();
            if (problem != null) return usageError(problem);

            try
            {
                var loaded = ReportFiles.LoadRuns(runs);
                var comparison = RunComparer.Compare(loaded, options);

                Console.WriteLine("Baseline: " + Describe(comparison.Baseline));
                foreach (var delta in comparison.Runs)
                {
                    Console.WriteLine();
                    Console.WriteLine("Compared: " + Describe(delta.Run));
                    if (delta.Findings.Count == 0) Console.WriteLine("  No differences.");
                    foreach (var finding in delta.Findings.OrderBy(f => f.Kind))
                    {
                        Console.WriteLine($"  {Label(finding.Kind)}  {finding}");
                    }
                }
                Console.WriteLine();
                Console.WriteLine(comparison.Summary);

                var path = ReportFiles.WriteComparison(comparison, string.IsNullOrWhiteSpace(output) ? ReportFiles.NewComparisonPath(Path.GetDirectoryName(loaded[loaded.Count - 1].Path)) : output);
                Console.WriteLine("HTML: " + path);
                return comparison.Regressions > 0 ? RunExitCodes.Failures : RunExitCodes.Clean;
            }
            catch (RunFileException ex)
            {
                return refuse(ex.Message);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                return refuse($"The comparison could not be written: {ex.Message}");
            }
        }

        private static string Describe(NamedRun run)
        {
            var profile = run.Report.Profile;
            return $"{run.Name}: {profile.CalledAe} at {profile.Host}:{profile.Port.ToString(CultureInfo.InvariantCulture)}, " +
                   $"{run.Report.StartedUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC, DCST4 {run.Report.ToolVersion}, {run.Report.Results.Count} tests";
        }

        private static string Label(FindingKind kind)
        {
            switch (kind)
            {
                case FindingKind.Regression: return "REGRESSION    ";
                case FindingKind.Improvement: return "improvement   ";
                case FindingKind.Difference: return "difference    ";
                default: return "not comparable";
            }
        }
    }
}
