using DCST4.Core.Testing;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace DCST4.Core.Reporting
{
    /// <summary>A comparison as one HTML file, with the same shell and the same encoding rule as the run report.</summary>
    public static class ComparisonReportWriter
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>Throws IOException, UnauthorizedAccessException or the like if the file cannot be written.</summary>
        public static void Write(ComparisonResult comparison, string path) => File.WriteAllText(path, Render(comparison), Utf8NoBom);

        public static string Render(ComparisonResult comparison)
        {
            if (comparison == null) throw new ArgumentNullException(nameof(comparison));
            var threshold = comparison.Options.ThresholdPercent.ToString("0.##", CultureInfo.InvariantCulture) + " %";

            var html = HtmlPage.Begin("DCST4 comparison of " + (comparison.Runs.Count + 1).ToString(CultureInfo.InvariantCulture) + " runs");
            html.Element("h1", "DCST4 run comparison").Line();
            html.Element("p", $"Each run against the baseline, {comparison.Baseline.Name}.", "class", "sub").Line();

            html.Open("section", "id", "verdict").Line();
            html.Element("p", comparison.Summary, "class", "verdict " + (comparison.Regressions > 0 ? "bad" : "ok")).Line();
            html.Element("p", "A regression is a test, a chain step or a combination of SOP class and transfer syntax that did worse than in the baseline, or a load test whose " +
                $"throughput fell or whose p95 latency or time-to-available rose by more than {threshold}, or whose error rate rose by more than " +
                $"{comparison.Options.ErrorRatePoints.ToString("0.##", CultureInfo.InvariantCulture)} percentage point. A latency must also have risen by at least " +
                $"{comparison.Options.LatencyFloorMs.ToString("0.##", CultureInfo.InvariantCulture)} ms. Times, UIDs, the seed and the wording of explanations are never compared.", "class", "sub").Line();
            html.Close("section").Line();

            WriteRuns(html, comparison);
            foreach (var delta in comparison.Runs)
            {
                html.Open("section").Line().Element("h2", $"{delta.Run.Name} against {comparison.Baseline.Name}").Line();
                WriteFindings(html, delta);
                WritePerformance(html, delta, threshold);
                html.Close("section").Line();
            }
            WriteFingerprints(html, comparison);
            return HtmlPage.End(html);
        }

        private static void WriteRuns(HtmlWriter html, ComparisonResult comparison)
        {
            html.Open("section", "id", "runs").Line().Element("h2", "Runs").Line();
            html.Open("div", "class", "scroll").Open("table").HeaderRow("Run", "Role", "Target", "Started", "DCST4 version", "Tests#", "Results", "Regressions#").Open("tbody");
            var all = new[] { Tuple.Create(comparison.Baseline, (RunDelta)null) }.Concat(comparison.Runs.Select(r => Tuple.Create(r.Run, r)));
            foreach (var entry in all)
            {
                var report = entry.Item1.Report;
                var profile = report.Profile ?? new ResultProfile();
                var counts = report.Results.GroupBy(r => r.Result).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}");
                html.Open("tr").Cell(entry.Item1.Name, "wrap").Cell(entry.Item2 == null ? "baseline" : "compared")
                    .Cell($"{profile.CalledAe} at {profile.Host}:{profile.Port.ToString(CultureInfo.InvariantCulture)}").Cell(ReportFormat.Time(report.StartedUtc)).Cell(report.ToolVersion)
                    .Num(report.Results.Count.ToString(CultureInfo.InvariantCulture)).Cell(string.Join(", ", counts) + (report.Cancelled ? " (cancelled)" : string.Empty))
                    .Num(entry.Item2 == null ? string.Empty : entry.Item2.Regressions.ToString(CultureInfo.InvariantCulture)).Close("tr").Line();
            }
            html.Close("tbody").Close("table").Close("div").Close("section").Line();
        }

        private static void WriteFindings(HtmlWriter html, RunDelta delta)
        {
            if (delta.Findings.Count == 0)
            {
                html.Element("p", "No differences.").Line();
                return;
            }

            foreach (var kind in new[] { FindingKind.Regression, FindingKind.Improvement, FindingKind.Difference, FindingKind.NotComparable })
            {
                var findings = delta.Findings.Where(f => f.Kind == kind).ToList();
                if (findings.Count == 0) continue;
                html.Element("h3", Heading(kind) + ": " + findings.Count.ToString(CultureInfo.InvariantCulture)).Line();
                if (kind == FindingKind.NotComparable)
                {
                    html.Element("p", "One of the runs has no verdict here: the test was not run, was cancelled, ended in a tool error, or was measured with other settings. This says nothing about the system.", "class", "sub").Line();
                }
                html.Open("div", "class", "scroll").Open("table").HeaderRow("Area", "What", "Baseline", "This run", "Note").Open("tbody");
                foreach (var f in findings)
                {
                    html.Open("tr", "class", kind == FindingKind.Regression ? "regression" : kind == FindingKind.Improvement ? "improved" : null)
                        .Cell(f.Area).Cell(f.Subject, "wrap").Cell(f.Baseline, "wrap").Cell(f.Value, "wrap").Cell(f.Note, "wrap").Close("tr").Line();
                }
                html.Close("tbody").Close("table").Close("div").Line();
            }
        }

        private static string Heading(FindingKind kind)
        {
            switch (kind)
            {
                case FindingKind.Regression: return "Regressions";
                case FindingKind.Improvement: return "Improvements";
                case FindingKind.Difference: return "Other differences";
                default: return "Not comparable";
            }
        }

        private static void WritePerformance(HtmlWriter html, RunDelta delta, string threshold)
        {
            if (delta.Performance.Count == 0) return;
            html.Element("h3", "Performance").Line();
            html.Element("p", $"Rows marked as a regression are worse by more than {threshold}. \"Worse\" without that word is beyond the threshold in a number that is shown for context, or in a test whose settings differ.", "class", "sub").Line();
            html.Open("div", "class", "scroll").Open("table").HeaderRow("Test", "Window", "Metric", "Baseline#", "This run#", "Change#", "Verdict").Open("tbody");
            foreach (var row in delta.Performance)
            {
                html.Open("tr", "class", row.Regression ? "regression" : row.Better && row.Decisive ? "improved" : null);
                html.Open("td", "class", "id").Element("code", row.TestId).Close("td").Cell(row.Scope).Cell(row.Metric);
                html.Num(RunComparer.Shown(row.Baseline, row.Unit)).Num(RunComparer.Shown(row.Value, row.Unit)).Num(RunComparer.DeltaText(row));
                html.Element("td", row.Regression ? "Regression" : row.Worse ? "Worse" : row.Better ? "Better" : string.Empty, "class", row.Regression ? "flag" : null).Close("tr").Line();
            }
            html.Close("tbody").Close("table").Close("div").Line();
        }

        private static void WriteFingerprints(HtmlWriter html, ComparisonResult comparison)
        {
            var runs = new[] { comparison.Baseline }.Concat(comparison.Runs.Select(r => r.Run)).ToList();
            var prints = runs.Select(r => VendorFingerprint.From(r.Report)).ToList();

            html.Open("section", "id", "fingerprint").Line().Element("h2", "Vendor fingerprint, side by side").Line();
            html.Element("p", "How each system behaves, read from the tests of each run. Rows where no run could tell are left out. A measured time is shown and never counted as a difference.", "class", "sub").Line();
            html.Open("div", "class", "scroll").Open("table").HeaderRow(new[] { "Behaviour" }.Concat(runs.Select(r => r.Name)).ToArray());
            foreach (var group in prints[0].GroupBy(i => i.Group))
            {
                var rows = group.Where(item => prints.Any(p => p.Any(i => i.Key == item.Key && i.Value != null))).ToList();
                if (rows.Count == 0) continue;
                html.Open("tbody").Open("tr").Element("th", group.Key, "colspan", (runs.Count + 1).ToString(CultureInfo.InvariantCulture)).Close("tr");
                foreach (var item in rows)
                {
                    var values = prints.Select(p => p.FirstOrDefault(i => i.Key == item.Key)?.Value).ToList();
                    var differs = !item.Measured && values.Where(v => v != null).Distinct(StringComparer.Ordinal).Count() > 1;
                    html.Open("tr").Cell(item.Label + (item.Measured ? " (measured)" : string.Empty));
                    foreach (var value in values)
                    {
                        if (value == null) html.Element("td", VendorFingerprint.NotTested, "class", "muted");
                        else html.Element("td", value, "class", differs ? "wrap flag" : "wrap");
                    }
                    html.Close("tr").Line();
                }
                html.Close("tbody");
            }
            html.Close("table").Close("div").Close("section").Line();
        }
    }
}
