using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DCST4.Core.Testing
{
    /// <summary>The one JSON setup for profiles and result files.</summary>
    internal static class ResultJson
    {
        public static readonly JsonSerializerOptions Options = Create();

        private static JsonSerializerOptions Create()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                // Keeps å, ä and ö readable. The files are data on disk, never embedded in HTML by this class.
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            options.Converters.Add(new JsonStringEnumConverter());
            options.Converters.Add(new ShortDoubleConverter());
            return options;
        }
    }

    /// <summary>
    /// Writes a double with at most 15 significant digits, so a latency of 21.504 ms reads 21.504 and not
    /// 21.504000000000001, which is what .NET Framework's round-trip format gives.
    /// </summary>
    internal sealed class ShortDoubleConverter : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetDouble();

        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > 1e28)
            {
                writer.WriteNumberValue(value);
                return;
            }
            writer.WriteNumberValue((decimal)value);
        }
    }

    public sealed class ResultFilePaths
    {
        public string Json { get; set; }
        public string Csv { get; set; }
        /// <summary>Null when the run has no capability matrix.</summary>
        public string CapabilityJson { get; set; }
        public string CapabilityCsv { get; set; }
        /// <summary>Null when no store test ran.</summary>
        public string ChainCsv { get; set; }
    }

    /// <summary>
    /// Writes a run as JSON (full detail) and CSV (one row per result), both UTF-8 without BOM and independent of the
    /// culture. Later versions compare these files, so change a field only together with <see cref="TestRunReport.CurrentSchemaVersion"/>.
    /// </summary>
    public static class ResultFileWriter
    {
        private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private static readonly string[] CsvColumns =
        {
            "schema_version", "tool_version", "run_started_utc", "run_ended_utc", "run_duration_ms", "run_cancelled",
            "profile_name", "host", "port", "calling_ae", "called_ae", "use_tls", "tls_ignore_cert_errors",
            "local_scp_ae", "local_scp_port", "uid_root", "connection_timeout_s", "association_timeout_s", "response_timeout_s",
            "test_id", "test_name", "category", "result", "status_codes", "uids", "details", "started_utc", "ended_utc", "duration_ms", "explanation",
        };

        /// <summary>Throws IOException, UnauthorizedAccessException or the like if the folder cannot be written.</summary>
        public static ResultFilePaths Write(TestRunReport report, string directory)
        {
            Directory.CreateDirectory(directory);

            var stem = Path.Combine(directory, "DCST4-run-" + report.StartedUtc.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
            var unique = stem;
            for (var n = 2; File.Exists(unique + ".json") || File.Exists(unique + ".csv"); n++) unique = stem + "-" + n.ToString(CultureInfo.InvariantCulture);

            var paths = new ResultFilePaths { Json = unique + ".json", Csv = unique + ".csv" };
            File.WriteAllText(paths.Json, ToJson(report), Utf8NoBom);
            File.WriteAllText(paths.Csv, ToCsv(report), Utf8NoBom);

            var matrix = report.Results.Select(r => r.CapabilityMatrix).FirstOrDefault(m => m != null);
            if (matrix != null)
            {
                paths.CapabilityJson = unique + "-capability.json";
                paths.CapabilityCsv = unique + "-capability.csv";
                File.WriteAllText(paths.CapabilityJson, matrix.ToJson(), Utf8NoBom);
                File.WriteAllText(paths.CapabilityCsv, matrix.ToCsv(), Utf8NoBom);
            }

            if (report.Chain != null)
            {
                paths.ChainCsv = unique + "-chain.csv";
                File.WriteAllText(paths.ChainCsv, ChainToCsv(report), Utf8NoBom);
            }
            return paths;
        }

        /// <summary>One row per object type: the chain has no place in the CSV with one row per test.</summary>
        public static string ChainToCsv(TestRunReport report)
        {
            var text = new StringBuilder();
            AppendRow(text, new[] { "object_type", "display_name", "transfer_syntax", "accepted", "indexed", "retrievable", "intact", "committed", "explanation" });

            string ResultOf(ChainStep step) => step == null ? string.Empty : step.Result.ToString();

            foreach (var row in report.Chain)
            {
                var worst = new[] { row.Committed, row.Intact, row.Retrievable, row.Indexed, row.Accepted }
                    .Where(s => s != null)
                    .OrderBy(s => s.Result == TestResultClass.PASS ? 1 : 0)
                    .FirstOrDefault();
                AppendRow(text, new[]
                {
                    row.ObjectType, row.DisplayName, row.TransferSyntaxUid,
                    ResultOf(row.Accepted), ResultOf(row.Indexed), ResultOf(row.Retrievable), ResultOf(row.Intact), ResultOf(row.Committed),
                    worst?.Explanation,
                });
            }
            return text.ToString();
        }

        public static string ToJson(TestRunReport report) => JsonSerializer.Serialize(report, ResultJson.Options);

        public static string ToCsv(TestRunReport report)
        {
            var text = new StringBuilder();
            AppendRow(text, CsvColumns);

            foreach (var result in report.Results)
            {
                var profile = report.Profile;
                AppendRow(text, new[]
                {
                    Number(report.SchemaVersion), report.ToolVersion, Time(report.StartedUtc), Time(report.EndedUtc), Number(report.DurationMs), Flag(report.Cancelled),
                    profile.Name, profile.Host, Number(profile.Port), profile.CallingAe, profile.CalledAe, Flag(profile.UseTls), Flag(profile.TlsIgnoreCertErrors),
                    profile.LocalScpAe, Number(profile.LocalScpPort), profile.UidRoot,
                    Number(profile.ConnectionTimeoutSeconds), Number(profile.AssociationTimeoutSeconds), Number(profile.ResponseTimeoutSeconds),
                    result.TestId, result.Name, result.Category.ToString(), result.Result.ToString(),
                    string.Join(";", result.StatusCodes.Select(s => s.Operation + "=" + s.Code)),
                    Pairs(result.Uids), Pairs(result.Details),
                    Time(result.StartedUtc), Time(result.EndedUtc), Number(result.DurationMs), result.Explanation,
                });
            }
            return text.ToString();
        }

        private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
        private static string Flag(bool value) => value ? "true" : "false";
        private static string Time(DateTime value) => value.ToString(TimeFormat, CultureInfo.InvariantCulture);
        private static string Pairs(IDictionary<string, string> values) => string.Join(";", values.Select(p => p.Key + "=" + p.Value));

        internal static void AppendRow(StringBuilder text, IEnumerable<string> values)
        {
            text.Append(string.Join(",", values.Select(Quote))).Append("\r\n");
        }

        /// <summary>RFC 4180.</summary>
        private static string Quote(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }
    }
}
