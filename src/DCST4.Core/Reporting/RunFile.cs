using DCST4.Core.Testing;
using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DCST4.Core.Reporting
{
    /// <summary>Thrown when a result file cannot be read. The message is ready to show to the user.</summary>
    public sealed class RunFileException : Exception
    {
        public RunFileException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>A run that was read back from its JSON result file, with where it came from.</summary>
    public sealed class NamedRun
    {
        public NamedRun(string name, TestRunReport report, string path = null)
        {
            Name = name;
            Report = report ?? throw new ArgumentNullException(nameof(report));
            Path = path;
        }

        /// <summary>What the run is called in a comparison: its file name without the extension.</summary>
        public string Name { get; }
        public TestRunReport Report { get; }
        /// <summary>The JSON file. Null for a run that was never saved.</summary>
        public string Path { get; }
    }

    /// <summary>Reads the JSON result file that <see cref="ResultFileWriter"/> writes.</summary>
    public static class RunFile
    {
        /// <summary>Throws <see cref="RunFileException"/> if the file is not a result file this version can read.</summary>
        public static TestRunReport Load(string path)
        {
            string json;
            try
            {
                json = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception ex) when (IsFileProblem(ex))
            {
                throw new RunFileException($"Could not read {path}: {ex.Message}", ex);
            }

            TestRunReport report;
            try
            {
                report = JsonSerializer.Deserialize<TestRunReport>(json, ResultJson.Options) ?? throw new JsonException("The file holds no run.");
            }
            catch (JsonException ex)
            {
                throw new RunFileException($"{path} is not a DCST4 result file: {ex.Message}", ex);
            }

            if (report.SchemaVersion > TestRunReport.CurrentSchemaVersion)
            {
                throw new RunFileException($"{path} has schema version {report.SchemaVersion}, and this version of DCST4 reads up to {TestRunReport.CurrentSchemaVersion}.");
            }

            // A capability matrix or a profile is valid JSON too, and would otherwise load as a run with nothing in it.
            if (report.SchemaVersion < 1 || report.Profile == null || report.Results == null || !json.Contains("\"results\""))
            {
                throw new RunFileException($"{path} is not a DCST4 result file: it has no profile and results. Choose the DCST4-run-<time>.json file of a run.");
            }

            report.Results.RemoveAll(r => r == null);
            return report;
        }

        public static NamedRun LoadNamed(string path) => new NamedRun(System.IO.Path.GetFileNameWithoutExtension(path), Load(path), System.IO.Path.GetFullPath(path));

        internal static bool IsFileProblem(Exception ex) =>
            ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException;
    }
}
