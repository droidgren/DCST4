using DCST4.Core.Testing;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DCST4.Core.Reporting
{
    /// <summary>What the front-ends call to put a run or a comparison on disk with its HTML report.</summary>
    public static class ReportFiles
    {
        /// <summary>
        /// Writes the result files of a run and its report beside them. The JSON and CSV come first, so a report that cannot be
        /// written loses nothing: <see cref="ResultFilePaths.Html"/> is then null and <paramref name="reportProblem"/> says why.
        /// Throws what <see cref="ResultFileWriter.Write"/> throws.
        /// </summary>
        public static ResultFilePaths WriteRun(TestRunReport report, string directory, out string reportProblem)
        {
            var paths = ResultFileWriter.Write(report, directory);
            reportProblem = null;
            try
            {
                paths.Html = WriteReportFor(report, paths.Json);
            }
            catch (Exception ex) when (RunFile.IsFileProblem(ex))
            {
                reportProblem = ex.Message;
            }
            return paths;
        }

        /// <summary>DCST4-run-x.json gives DCST4-run-x.html in the same folder.</summary>
        public static string ReportPathFor(string jsonPath) => Path.ChangeExtension(Path.GetFullPath(jsonPath), ".html");

        /// <summary>Writes the report of a run beside its JSON file, or at <paramref name="htmlPath"/>, and returns where.</summary>
        public static string WriteReportFor(TestRunReport report, string jsonPath, string htmlPath = null)
        {
            var path = htmlPath == null ? ReportPathFor(jsonPath) : Path.GetFullPath(htmlPath);
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            RunReportWriter.Write(report, path, new RunReportOptions { RunFolder = Path.GetDirectoryName(Path.GetFullPath(jsonPath)), RunName = Path.GetFileName(jsonPath) });
            return path;
        }

        /// <summary>DCST4-compare-&lt;UTC time&gt;.html in <paramref name="directory"/>, with a number after it if that is taken.</summary>
        public static string NewComparisonPath(string directory)
        {
            var stem = Path.Combine(Path.GetFullPath(directory), "DCST4-compare-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
            var unique = stem;
            for (var n = 2; File.Exists(unique + ".html"); n++) unique = stem + "-" + n.ToString(CultureInfo.InvariantCulture);
            return unique + ".html";
        }

        /// <summary>Writes a comparison, and returns where.</summary>
        public static string WriteComparison(ComparisonResult comparison, string htmlPath)
        {
            var path = Path.GetFullPath(htmlPath);
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            ComparisonReportWriter.Write(comparison, path);
            return path;
        }

        /// <summary>
        /// Loads the runs and gives each a name that tells it from the others. Two files of the same name in different folders
        /// get their folder in front, and the same file given twice gets a number.
        /// </summary>
        public static List<NamedRun> LoadRuns(IEnumerable<string> paths, System.Threading.CancellationToken token = default(System.Threading.CancellationToken))
        {
            var loaded = paths.Select(p =>
            {
                // Between two files, so that a cancel does not have to wait for all of them.
                token.ThrowIfCancellationRequested();
                return new { Path = Path.GetFullPath(p), Report = RunFile.Load(p) };
            }).ToList();
            var original = loaded.Select(l => Path.GetFileNameWithoutExtension(l.Path)).ToList();
            var names = original.ToList();
            for (var i = 0; i < loaded.Count; i++)
            {
                if (original.Count(n => string.Equals(n, original[i], StringComparison.OrdinalIgnoreCase)) < 2) continue;
                var folder = Path.GetFileName(Path.GetDirectoryName(loaded[i].Path) ?? string.Empty);
                var samePath = loaded.Count(l => string.Equals(l.Path, loaded[i].Path, StringComparison.OrdinalIgnoreCase)) > 1;
                names[i] = samePath || string.IsNullOrEmpty(folder) ? original[i] : folder + "\\" + original[i];
            }
            // Whatever is still shared, such as the same file twice, is told apart by its place in the list.
            var settled = names.ToList();
            for (var i = 0; i < names.Count; i++)
            {
                if (settled.Count(n => string.Equals(n, settled[i], StringComparison.OrdinalIgnoreCase)) > 1) names[i] = settled[i] + " (" + (i + 1).ToString(CultureInfo.InvariantCulture) + ")";
            }
            return loaded.Select((l, i) => new NamedRun(names[i], l.Report, l.Path)).ToList();
        }
    }
}
