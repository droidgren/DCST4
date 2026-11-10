using DCST4.Core;
using DCST4.Core.Network;
using DCST4.Core.Testing;
using FellowOakDicom;
using FellowOakDicom.Network;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Tests
{
    /// <summary>Target profiles, the test framework, the protocol timeline, the result files and the command line.</summary>
    internal static class TestSuiteTests
    {
        /// <summary>The fields allowed to differ between two identical runs. Everything else in a result file must match.</summary>
        private static readonly HashSet<string> VolatileJsonFields = new HashSet<string>(StringComparer.Ordinal) { "startedUtc", "endedUtc", "durationMs", "offsetMs", "reportAfterMs", "endedAfterMs" };
        private static readonly HashSet<string> VolatileCsvColumns = new HashSet<string>(StringComparer.Ordinal) { "run_started_utc", "run_ended_utc", "run_duration_ms", "started_utc", "ended_utc", "duration_ms" };

        private const string Thumbprint = "ABCDEF0123456789ABCDEF0123456789ABCDEF01";

        // ---------------------------------------------------------------- test doubles

        private sealed class StubTest : ITestCase
        {
            private readonly Func<CancellationToken, Task<TestResult>> _run;

            public StubTest(string id, Func<CancellationToken, Task<TestResult>> run, TestCategory category = TestCategory.Integrity)
            {
                Id = id;
                _run = run;
                Category = category;
            }

            public string Id { get; }
            public string Name => "Stub " + Id;
            public TestCategory Category { get; }
            public Task<TestResult> ExecuteAsync(TestContext context, CancellationToken token) => _run(token);
        }

        private static StubTest Passing(string id = "pass") =>
            new StubTest(id, _ => Task.FromResult(new TestResult { Result = TestResultClass.PASS, Explanation = "ok" }));

        // ---------------------------------------------------------------- helpers

        public static TargetProfile MockProfile(int port) => new TargetProfile
        {
            Name = "Mock",
            Endpoint = new DicomEndpoint { Host = "127.0.0.1", Port = port, CallingAe = "DCST4", CalledAe = "MOCKSCP" },
            Network = new NetworkOptions { ConnectionTimeoutSeconds = 5, AssociationTimeoutSeconds = 5, ResponseTimeoutSeconds = 30 },
        };

        private static string Types(IEnumerable<ProtocolEvent> events) => string.Join(" ", events.Select(e => e.Type));

        public static string CliPath
        {
            get
            {
                // The harness runs from tests\DCST4.Tests\bin\<configuration>\net48, and the CLI from its own output with libs\.
                var baseDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
                var configuration = new DirectoryInfo(baseDirectory).Parent.Name;
                return Path.GetFullPath(Path.Combine(baseDirectory, @"..\..\..\..\..\src\DCST4.Cli\bin", configuration, @"net48\DCST4.Cli.exe"));
            }
        }

        public sealed class CliRun
        {
            public int ExitCode;
            public string Output;
        }

        public static async Task<CliRun> RunCli(params string[] arguments)
        {
            var info = new ProcessStartInfo(CliPath, string.Join(" ", arguments.Select(a => "\"" + a + "\"")))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };

            using (var process = Process.Start(info))
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                var exited = await Task.Run(() => process.WaitForExit(60000));
                if (!exited)
                {
                    process.Kill();
                    return new CliRun { ExitCode = -1, Output = "Timed out" };
                }
                return new CliRun { ExitCode = process.ExitCode, Output = await output + await error };
            }
        }

        /// <summary>Where two normalized texts first differ, with some context, for a check that fails.</summary>
        public static string FirstDifference(string a, string b)
        {
            var i = 0;
            while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
            if (i == a.Length && i == b.Length) return null;
            var start = Math.Max(0, i - 120);
            string Around(string s) => s.Substring(start, Math.Min(s.Length - start, 240));
            return "\n    first: " + Around(a) + "\n    second: " + Around(b);
        }

        /// <summary>The JSON with volatile fields removed and everything else in a stable text form.</summary>
        public static string NormalizeJson(string json)
        {
            using (var document = JsonDocument.Parse(json))
            {
                var text = new StringBuilder();
                Write(document.RootElement, text);
                return text.ToString();
            }
        }

        private static void Write(JsonElement element, StringBuilder text)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    text.Append('{');
                    foreach (var property in element.EnumerateObject().Where(p => !VolatileJsonFields.Contains(p.Name)).OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        text.Append(property.Name).Append(':');
                        Write(property.Value, text);
                        text.Append(',');
                    }
                    text.Append('}');
                    break;
                case JsonValueKind.Array:
                    text.Append('[');
                    foreach (var item in element.EnumerateArray())
                    {
                        Write(item, text);
                        text.Append(',');
                    }
                    text.Append(']');
                    break;
                default:
                    text.Append(element.GetRawText());
                    break;
            }
        }

        /// <summary>RFC 4180 rows.</summary>
        public static List<List<string>> ParseCsv(string csv)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var field = new StringBuilder();
            var quoted = false;

            for (var i = 0; i < csv.Length; i++)
            {
                var c = csv[i];
                if (quoted)
                {
                    if (c != '"') field.Append(c);
                    else if (i + 1 < csv.Length && csv[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
                else if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n')
                {
                    i++;
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
                    row = new List<string>();
                }
                else field.Append(c);
            }
            return rows;
        }

        public static string NormalizeCsv(string csv)
        {
            var rows = ParseCsv(csv);
            var keep = Enumerable.Range(0, rows[0].Count).Where(i => !VolatileCsvColumns.Contains(rows[0][i])).ToList();
            return string.Join("\n", rows.Select(r => string.Join("|", keep.Select(i => r[i]))));
        }

        public static string CsvValue(string csv, string column, int row = 1)
        {
            var rows = ParseCsv(csv);
            return rows[row][rows[0].IndexOf(column)];
        }

        // ---------------------------------------------------------------- target profiles

        public static void TestProfiles(string temp, Action<string, bool, string> check)
        {
            Console.WriteLine("\nTarget profiles");

            check("A complete profile is valid", MockProfile(104).Validate() == null, MockProfile(104).Validate());

            var fresh = new TargetProfile();
            check("Defaults: local SCP and UID root", fresh.LocalScpAe == "DCST4" && fresh.LocalScpPort == TargetProfile.DefaultLocalScpPort && fresh.UidRoot == "2.25" && fresh.Network != null, fresh.UidRoot);

            var original = MockProfile(11112);
            original.Name = "Röntgen Åre";
            original.Endpoint.UseTls = true;
            original.Endpoint.TlsIgnoreCertErrors = true;
            original.Endpoint.TlsClientCertThumbprint = Thumbprint;
            original.Network.ResponseTimeoutSeconds = 7;
            original.Network.LogDimseDatasets = true;
            original.LocalScpAe = "CALLBACK";
            original.LocalScpPort = 4006;
            original.UidRoot = "1.2.3.4";

            var path = Path.Combine(temp, @"profiles\nested\target.json");
            original.Save(path);
            var loaded = TargetProfile.Load(path);
            check("Round trip: endpoint", loaded.Endpoint.Host == "127.0.0.1" && loaded.Endpoint.Port == 11112 && loaded.Endpoint.CallingAe == "DCST4" && loaded.Endpoint.CalledAe == "MOCKSCP", loaded.Endpoint.Host);
            check("Round trip: TLS, including the thumbprint", loaded.Endpoint.UseTls && loaded.Endpoint.TlsIgnoreCertErrors && loaded.Endpoint.TlsClientCertThumbprint == Thumbprint, loaded.Endpoint.TlsClientCertThumbprint);
            check("Round trip: network options", loaded.Network.ResponseTimeoutSeconds == 7 && loaded.Network.LogDimseDatasets && !loaded.Network.LogDataPdus, null);
            check("Round trip: local SCP and UID root", loaded.LocalScpAe == "CALLBACK" && loaded.LocalScpPort == 4006 && loaded.UidRoot == "1.2.3.4", loaded.LocalScpAe);
            check("Round trip: name with å, ä, ö", loaded.Name == "Röntgen Åre", loaded.Name);
            check("Round trip: schema version", loaded.SchemaVersion == TargetProfile.CurrentSchemaVersion, loaded.SchemaVersion.ToString());
            var bytes = File.ReadAllBytes(path);
            check("Profile file has no BOM", bytes[0] == (byte)'{', bytes[0].ToString());
            check("Profile file is readable text", Encoding.UTF8.GetString(bytes).Contains("Röntgen Åre"), null);

            Action<string, Action<TargetProfile>, string> invalid = (name, change, expected) =>
            {
                var profile = MockProfile(104);
                change(profile);
                var problem = profile.Validate();
                check("Invalid: " + name, problem != null && problem.Contains(expected), problem);
            };
            invalid("no endpoint section", p => p.Endpoint = null, "endpoint");
            invalid("null network section", p => p.Network = null, "network");
            invalid("empty host (from DicomEndpoint.Validate)", p => p.Endpoint.Host = " ", "host");
            invalid("port 0 (from DicomEndpoint.Validate)", p => p.Endpoint.Port = 0, "Port 0");
            invalid("port 65536 (from DicomEndpoint.Validate)", p => p.Endpoint.Port = 65536, "65536");
            invalid("empty called AE (from DicomEndpoint.Validate)", p => p.Endpoint.CalledAe = "", "Called AET");
            invalid("long calling AE (from DicomEndpoint.Validate)", p => p.Endpoint.CallingAe = new string('A', 17), "Calling AET");
            invalid("empty local AE", p => p.LocalScpAe = "", "Local SCP AET");
            invalid("long local AE", p => p.LocalScpAe = new string('A', 17), "Local SCP AET");
            invalid("backslash in local AE", p => p.LocalScpAe = "A\\B", "Local SCP AET");
            invalid("local port 0", p => p.LocalScpPort = 0, "Local SCP port");
            invalid("local port 65536", p => p.LocalScpPort = 65536, "Local SCP port");
            invalid("UID root with a letter", p => p.UidRoot = "1.2.a", "UID root");
            invalid("UID root with a leading zero", p => p.UidRoot = "1.02.3", "UID root");
            invalid("UID root ending in a dot", p => p.UidRoot = "1.2.", "UID root");
            invalid("empty UID root", p => p.UidRoot = "", "UID root");
            invalid("UID root over 40 characters", p => p.UidRoot = "1." + new string('1', 39), "UID root");
            invalid("negative timeout", p => p.Network.ConnectionTimeoutSeconds = -1, "negative");
            invalid("newer schema", p => p.SchemaVersion = TargetProfile.CurrentSchemaVersion + 1, "schema version");

            var zero = MockProfile(104);
            zero.Network.ResponseTimeoutSeconds = 0;
            check("Response timeout 0 (no limit) is valid", zero.Validate() == null, zero.Validate());

            var spaced = MockProfile(104);
            spaced.Endpoint.CalledAe = " PACS ";
            spaced.LocalScpAe = " LOCAL ";
            spaced.UidRoot = " 1.2 ";
            check("Trimmed removes surrounding spaces", spaced.Validate() == null && spaced.Trimmed().Endpoint.CalledAe == "PACS" && spaced.Trimmed().LocalScpAe == "LOCAL" && spaced.Trimmed().UidRoot == "1.2", spaced.Validate());

            Func<string, TargetProfileException> loadFailure = text =>
            {
                var file = Path.Combine(temp, Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllText(file, text);
                try { TargetProfile.Load(file); return null; }
                catch (TargetProfileException ex) { return ex; }
            };

            var broken = loadFailure("{ \"name\": ");
            check("Broken JSON: clear error", broken != null && broken.Message.Contains("not valid"), broken?.Message);
            var wrongType = loadFailure("{ \"endpoint\": { \"port\": \"high\" } }");
            check("Wrong value type: clear error", wrongType != null && wrongType.Message.Contains("not valid"), wrongType?.Message);
            var literalNull = loadFailure("null");
            check("A file holding null: clear error", literalNull != null && literalNull.Message.Contains("not valid"), literalNull?.Message);

            TargetProfileException missing = null;
            try { TargetProfile.Load(Path.Combine(temp, "does-not-exist.json")); }
            catch (TargetProfileException ex) { missing = ex; }
            check("Missing file: clear error", missing != null && missing.Message.Contains("Could not read"), missing?.Message);

            var handWritten = Path.Combine(temp, "hand-written.json");
            File.WriteAllText(handWritten, "// A comment\n{ \"ENDPOINT\": { \"host\": \"pacs\", \"Port\": 104, \"callingAe\": \"A\", \"calledAe\": \"B\", }, \"unknownField\": 1, }\n");
            var written = TargetProfile.Load(handWritten);
            check("Hand-written: comments, trailing commas, any case, unknown fields", written.Endpoint.Host == "pacs" && written.Validate() == null, written.Validate());
            check("Hand-written: omitted values get defaults", written.Network.ConnectionTimeoutSeconds == 10 && written.UidRoot == "2.25" && written.LocalScpAe == "DCST4", null);

            var noEndpoint = Path.Combine(temp, "no-endpoint.json");
            File.WriteAllText(noEndpoint, "{ \"name\": \"x\" }");
            var noEndpointProblem = TargetProfile.Load(noEndpoint).Validate();
            check("Missing endpoint section: loads and fails validation", noEndpointProblem != null && noEndpointProblem.Contains("endpoint"), noEndpointProblem);
        }

        // ---------------------------------------------------------------- framework

        public static async Task TestFramework(Action<string, bool, string> check)
        {
            Console.WriteLine("\nTest framework");
            var profile = MockProfile(1);

            check("Result class names are the ones in the result files",
                string.Join(",", Enum.GetNames(typeof(TestResultClass))) == "PASS,FAIL,NOT_SUPPORTED,EXPECTED_FAILURE,TIMEOUT,INDETERMINATE,TOOL_ERROR", null);
            check("Categories", string.Join(",", Enum.GetNames(typeof(TestCategory))) == "Capability,Integrity,Limits,Performance", null);
            check("Test ids are unique", TestCatalog.All.Select(t => t.Id).Distinct().Count() == TestCatalog.All.Count, null);

            var thrower = new StubTest("throws", async _ => { await Task.Yield(); throw new InvalidOperationException("Broken on purpose"); });
            var syncThrower = new StubTest("throws-at-once", _ => throw new ArgumentException("Thrown before any await"));
            var nothing = new StubTest("returns-null", _ => Task.FromResult<TestResult>(null));
            var strayCancel = new StubTest("stray-cancel", _ => throw new OperationCanceledException("Not from the run's token"));
            var last = Passing("after");

            var progress = new List<TestProgress>();
            var runner = new TestRunner();
            runner.Progress += p => { lock (progress) progress.Add(p); };
            runner.Progress += p => throw new InvalidOperationException("A progress handler that fails");

            var report = await runner.RunAsync(profile, new ITestCase[] { thrower, syncThrower, nothing, strayCancel, last }, CancellationToken.None);
            check("A test that throws gives TOOL_ERROR", report.Results[0].Result == TestResultClass.TOOL_ERROR && report.Results[0].Explanation.Contains("Broken on purpose"), report.Results[0].Explanation);
            check("TOOL_ERROR says it is our own error", report.Results[0].Explanation.Contains("DCST4 internal error"), report.Results[0].Explanation);
            check("A test that throws before its first await gives TOOL_ERROR", report.Results[1].Result == TestResultClass.TOOL_ERROR, report.Results[1].Result.ToString());
            check("A test that returns nothing gives TOOL_ERROR", report.Results[2].Result == TestResultClass.TOOL_ERROR, report.Results[2].Result.ToString());
            check("A cancel that did not come from the run gives TOOL_ERROR", report.Results[3].Result == TestResultClass.TOOL_ERROR, report.Results[3].Result.ToString());
            check("The run continues after an exception", report.Results.Count == 5 && report.Results[4].Result == TestResultClass.PASS, report.Results.Count.ToString());
            check("The runner stamps id, name and category", report.Results[4].TestId == "after" && report.Results[4].Name == "Stub after" && report.Results[4].Category == TestCategory.Integrity, report.Results[4].TestId);
            check("The runner stamps times", report.Results.All(r => r.StartedUtc.Kind == DateTimeKind.Utc && r.EndedUtc >= r.StartedUtc && r.DurationMs >= 0), null);
            check("Progress: started and completed for each, despite a failing handler", progress.Count == 10 && progress.Count(p => p.Stage == TestProgressStage.Completed && p.Result != null) == 5, progress.Count.ToString());
            check("Progress: position and total", progress[9].Index == 4 && progress[9].Total == 5, null);
            check("Summary counts every class", report.Summary["TOOL_ERROR"] == 4 && report.Summary["PASS"] == 1 && report.Summary["FAIL"] == 0 && report.Summary.Count == 7, null);
            check("Report: tool version comes from the build", report.ToolVersion == ProductInfo.Version && report.ToolVersion.Length > 0, report.ToolVersion);
            check("Report: not cancelled", !report.Cancelled, null);

            using (var cancellation = new CancellationTokenSource())
            {
                var cancelling = new StubTest("cancels", token => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(new TestResult()); });
                var never = 0;
                var neverRun = new StubTest("never", _ => { never++; return Task.FromResult(new TestResult { Result = TestResultClass.PASS }); });
                var cancelled = await new TestRunner().RunAsync(profile, new ITestCase[] { cancelling, neverRun }, cancellation.Token);
                check("Cancel: the running test is INDETERMINATE, not TOOL_ERROR", cancelled.Results[0].Result == TestResultClass.INDETERMINATE, cancelled.Results[0].Result.ToString());
                check("Cancel: later tests are not started, and are listed", never == 0 && cancelled.Results.Count == 2 && cancelled.Results[1].Result == TestResultClass.INDETERMINATE, cancelled.Results.Count.ToString());
                check("Cancel: the run says so", cancelled.Cancelled, null);
                check("Cancel: exit code", RunExitCodes.From(cancelled) == RunExitCodes.Cancelled, RunExitCodes.From(cancelled).ToString());
            }

            Func<TestResultClass, int> exitFor = c => RunExitCodes.From(new TestRunReport { Results = new List<TestResult> { new TestResult { Result = TestResultClass.PASS }, new TestResult { Result = c } } });
            check("Exit code: FAIL is non-zero", exitFor(TestResultClass.FAIL) == 1, null);
            check("Exit code: TOOL_ERROR is non-zero", exitFor(TestResultClass.TOOL_ERROR) == 1, null);
            foreach (var clean in new[] { TestResultClass.PASS, TestResultClass.NOT_SUPPORTED, TestResultClass.EXPECTED_FAILURE, TestResultClass.TIMEOUT, TestResultClass.INDETERMINATE })
            {
                check($"Exit code: {clean} is 0", exitFor(clean) == 0, null);
            }

            var capability = new StubTest("cap", _ => Task.FromResult(new TestResult()), TestCategory.Capability);
            var limits = new StubTest("lim", _ => Task.FromResult(new TestResult()), TestCategory.Limits);
            var catalog = new ITestCase[] { capability, limits };
            string error;
            check("Select: by id, ignoring case", TestCatalog.Select("LIM", out error, catalog).Single() == limits, error);
            check("Select: by category", TestCatalog.Select("capability", out error, catalog).Single() == capability, error);
            check("Select: all", TestCatalog.Select("all", out error, catalog).Count == 2, error);
            check("Select: catalog order, no duplicates", TestCatalog.Select("lim, cap, Limits", out error, catalog).SequenceEqual(catalog), error);
            check("Select: unknown names it and lists the choices", TestCatalog.Select("nope", out error, catalog) == null && error.Contains("nope") && error.Contains("cap") && error.Contains("Performance"), error);
            check("Select: empty is an error", TestCatalog.Select(" , ", out error, catalog) == null && error != null, error);
            check("Select: c-echo is in the real catalog", TestCatalog.Select("c-echo", out error).Single().Category == TestCategory.Capability, error);
        }

        // ---------------------------------------------------------------- timeline and C-ECHO

        private static async Task<Tuple<ConnectivityResult, List<ProtocolEvent>, AssociationTimeline>> Echo(int port, int responseTimeout = 30, CancellationToken token = default(CancellationToken))
        {
            var profile = MockProfile(port);
            profile.Network.ResponseTimeoutSeconds = responseTimeout;
            var timeline = new AssociationTimeline();
            var echo = await EchoService.EchoAsync(profile.Endpoint, profile.Network, token, timeline);
            return Tuple.Create(echo, timeline.Events, timeline);
        }

        public static async Task TestTimeline(int port, int closedPort, Action<string, bool, string> check)
        {
            Console.WriteLine("\nAssociation timeline and C-ECHO");

            Scp.Reset();
            var ok = await Echo(port);
            check("Normal: every step in order", Types(ok.Item2) == "A-ASSOCIATE-RQ A-ASSOCIATE-AC C-ECHO-RQ C-ECHO-RSP A-RELEASE-RQ A-RELEASE-RP", Types(ok.Item2));
            check("Normal: directions", string.Join(" ", ok.Item2.Select(e => e.Direction)) == "Sent Received Sent Received Sent Received", null);
            check("Normal: index follows the order", ok.Item2.Select(e => e.Index).SequenceEqual(Enumerable.Range(0, 6)), null);
            check("Normal: offsets do not go backwards", ok.Item2.Zip(ok.Item2.Skip(1), (a, b) => a.OffsetMs <= b.OffsetMs).All(x => x), string.Join(",", ok.Item2.Select(e => e.OffsetMs)));
            check("Normal: the response carries the raw status", ok.Item2[3].Status == "0x0000", ok.Item2[3].Status);
            check("Normal: request and response are paired", ok.Item2[2].DimseIndex == 1 && ok.Item2[3].DimseIndex == 1, null);
            check("Normal: AE titles are on the request", ok.Item2[0].Detail.Contains("DCST4") && ok.Item2[0].Detail.Contains("MOCKSCP"), ok.Item2[0].Detail);
            check("Normal: negotiated context", ok.Item3.Negotiation.Single().AbstractSyntaxUid == DicomUID.Verification.UID && ok.Item3.Negotiation.Single().AcceptedTransferSyntaxUid != null && ok.Item3.Negotiation.Single().Result == "Accept", null);
            check("Normal: outcome and raw status", ok.Item1.Outcome == ConnectivityOutcome.Success && ok.Item1.StatusCode == 0 && ok.Item1.Failure == ConnectivityFailure.None, ok.Item1.Message);

            Scp.Reset();
            Scp.RejectAssociation = true;
            var rejected = await Echo(port);
            check("Rejected: RQ then RJ, no release", Types(rejected.Item2) == "A-ASSOCIATE-RQ A-ASSOCIATE-RJ", Types(rejected.Item2));
            check("Rejected: result, source and reason", rejected.Item2[1].Detail.Contains("Permanent") && rejected.Item2[1].Detail.Contains("CalledAENotRecognized"), rejected.Item2[1].Detail);
            check("Rejected: told apart, still Failed", rejected.Item1.Outcome == ConnectivityOutcome.Failed && rejected.Item1.Failure == ConnectivityFailure.AssociationRejected && rejected.Item1.StatusCode == null, rejected.Item1.Failure.ToString());

            Scp.Reset();
            Scp.AbortOnEcho = true;
            var aborted = await Echo(port);
            check("Peer abort: ends in A-ABORT received, not a release", Types(aborted.Item2) == "A-ASSOCIATE-RQ A-ASSOCIATE-AC C-ECHO-RQ A-ABORT" && aborted.Item2[3].Direction == ProtocolEvent.Received, Types(aborted.Item2));
            check("Peer abort: source and reason", aborted.Item2.Last().Detail.Contains("ServiceProvider") && aborted.Item2.Last().Detail.Contains("UnexpectedPDU"), aborted.Item2.Last().Detail);
            check("Peer abort: told apart, still Failed", aborted.Item1.Outcome == ConnectivityOutcome.Failed && aborted.Item1.Failure == ConnectivityFailure.Aborted, aborted.Item1.Failure + " " + aborted.Item1.Message);

            Scp.Reset();
            Scp.EchoDelayMs = 4000;
            var timedOut = await Echo(port, responseTimeout: 1);
            check("Timeout: ends in A-ABORT sent, not a release", Types(timedOut.Item2) == "A-ASSOCIATE-RQ A-ASSOCIATE-AC C-ECHO-RQ A-ABORT" && timedOut.Item2[3].Direction == ProtocolEvent.Sent, Types(timedOut.Item2));
            check("Timeout: the reason is on the abort", timedOut.Item2.Last().Detail.Contains("no response within 1 s"), timedOut.Item2.Last().Detail);
            check("Timeout: told apart, still Failed", timedOut.Item1.Outcome == ConnectivityOutcome.Failed && timedOut.Item1.Failure == ConnectivityFailure.Timeout && timedOut.Item1.StatusCode == null, timedOut.Item1.Failure.ToString());
            check("Timeout: same message as before", timedOut.Item1.Message == "Echo failed: no response within 1 s (timed out).", timedOut.Item1.Message);

            Scp.Reset();
            Scp.EchoDelayMs = 4000;
            using (var cancellation = new CancellationTokenSource(300))
            {
                var cancelled = await Echo(port, token: cancellation.Token);
                check("Cancel: outcome and message unchanged", cancelled.Item1.Outcome == ConnectivityOutcome.Cancelled && cancelled.Item1.Message == "Echo cancelled.", cancelled.Item1.Message);
                check("Cancel: ends in A-ABORT sent", cancelled.Item2.Last().Type == "A-ABORT" && cancelled.Item2.Last().Direction == ProtocolEvent.Sent, Types(cancelled.Item2));
            }

            Scp.Reset();
            var refused = await Echo(closedPort);
            check("Refused connection: nothing was negotiated, so no steps", refused.Item2.Count == 0, Types(refused.Item2));
            check("Refused connection: told apart, still Failed", refused.Item1.Outcome == ConnectivityOutcome.Failed && refused.Item1.Failure == ConnectivityFailure.Connection, refused.Item1.Failure.ToString());

            Scp.Reset();
            Scp.AcceptedSyntaxes = new[] { DicomTransferSyntax.JPEGProcess14SV1 };
            var noContext = await Echo(port);
            check("No accepted context: no DIMSE on the wire", Types(noContext.Item2) == "A-ASSOCIATE-RQ A-ASSOCIATE-AC A-RELEASE-RQ A-RELEASE-RP", Types(noContext.Item2));
            check("No accepted context: fo-dicom's own answer is not a status", noContext.Item1.Failure == ConnectivityFailure.ContextNotAccepted && noContext.Item1.StatusCode == null && Scp.EchoCount == 0, noContext.Item1.Failure.ToString());
            check("No accepted context: the context shows why", noContext.Item3.Negotiation.Single().Result != "Accept" && noContext.Item3.Negotiation.Single().AcceptedTransferSyntaxUid == null, null);

            Scp.Reset();
            Scp.EchoStatus = new DicomStatus("C123", DicomState.Failure, "Vendor failure");
            var vendor = await Echo(port);
            check("Vendor status: raw code kept", vendor.Item1.StatusCode == 0xC123 && vendor.Item1.Failure == ConnectivityFailure.DimseStatus, vendor.Item1.StatusCode?.ToString("X4"));
            check("Vendor status: on the timeline", vendor.Item2[3].Status == "0xC123", vendor.Item2[3].Status);

            // ------------------------------------------------ the test case and its result classes
            Func<TargetProfile, Task<TestResult>> runEcho = async profile =>
                (await new TestRunner().RunAsync(profile, TestCatalog.Select("c-echo", out _), CancellationToken.None)).Results.Single();

            Scp.Reset();
            var pass = await runEcho(MockProfile(port));
            check("C-ECHO test: PASS", pass.Result == TestResultClass.PASS && pass.TestId == "c-echo", pass.Explanation);
            check("C-ECHO test: raw status recorded", pass.StatusCodes.Single().Code == "0x0000" && pass.StatusCodes.Single().Operation == "C-ECHO", null);
            check("C-ECHO test: explanation has no timing", !pass.Explanation.Contains(" ms"), pass.Explanation);
            check("C-ECHO test: negotiated context, UIDs and timeline recorded", pass.NegotiatedContexts.Count == 1 && pass.Uids["sopClassUid"] == DicomUID.Verification.UID && pass.Uids.ContainsKey("acceptedTransferSyntaxUid") && pass.Timeline.Count == 6, null);

            var closed = await runEcho(MockProfile(closedPort));
            check("C-ECHO test: closed port gives TIMEOUT or FAIL", closed.Result == TestResultClass.TIMEOUT || closed.Result == TestResultClass.FAIL, closed.Result.ToString());
            check("C-ECHO test: closed port has a clear message", closed.Explanation.Contains("127.0.0.1:" + closedPort) && !closed.Explanation.Contains("One or more errors") && !closed.Explanation.Contains("Ett eller flera fel"), closed.Explanation);

            Scp.Reset();
            Scp.RejectAssociation = true;
            var rejectedTest = await runEcho(MockProfile(port));
            check("C-ECHO test: rejected association is FAIL", rejectedTest.Result == TestResultClass.FAIL && rejectedTest.Details["failure"] == "AssociationRejected", rejectedTest.Explanation);

            Scp.Reset();
            Scp.AcceptedSyntaxes = new[] { DicomTransferSyntax.JPEGProcess14SV1 };
            var unsupported = await runEcho(MockProfile(port));
            check("C-ECHO test: no accepted context is NOT_SUPPORTED", unsupported.Result == TestResultClass.NOT_SUPPORTED && unsupported.StatusCodes.Count == 0, unsupported.Explanation);

            Scp.Reset();
            Scp.EchoDelayMs = 4000;
            var slowProfile = MockProfile(port);
            slowProfile.Network.ResponseTimeoutSeconds = 1;
            var slow = await runEcho(slowProfile);
            check("C-ECHO test: no response is TIMEOUT", slow.Result == TestResultClass.TIMEOUT && slow.Explanation.Contains("no response within 1 s"), slow.Explanation);

            Scp.Reset();
            Scp.EchoStatus = new DicomStatus("C123", DicomState.Failure, "Vendor failure");
            var failing = await runEcho(MockProfile(port));
            check("C-ECHO test: failure status is FAIL with the raw code", failing.Result == TestResultClass.FAIL && failing.Explanation.Contains("0xC123") && failing.StatusCodes.Single().Code == "0xC123", failing.Explanation);

            Scp.Reset();
            var missingCertificate = MockProfile(port);
            missingCertificate.Endpoint.UseTls = true;
            missingCertificate.Endpoint.TlsClientCertThumbprint = Thumbprint;
            var noCertificate = await runEcho(missingCertificate);
            check("C-ECHO test: missing client certificate is TOOL_ERROR", noCertificate.Result == TestResultClass.TOOL_ERROR && noCertificate.Explanation.Contains("local problem"), noCertificate.Explanation);
            check("C-ECHO test: thumbprint removed from the explanation", !noCertificate.Explanation.Contains(Thumbprint) && noCertificate.Explanation.Contains("[removed]"), noCertificate.Explanation);
            check("C-ECHO test: nothing reached the SCP", Scp.AssociationCount == 0, Scp.AssociationCount.ToString());
            Scp.Reset();
        }

        // ---------------------------------------------------------------- result files

        public static async Task TestResultFiles(int port, string temp, Action<string, bool, string> check)
        {
            Console.WriteLine("\nResult files");
            Scp.Reset();

            var profile = MockProfile(port);
            profile.Name = "Röntgen, \"test\"";
            profile.LocalScpAe = "CALLBACK";
            profile.UidRoot = "1.2.3";
            var runner = new TestRunner();
            var multiline = new StubTest("multiline", _ => Task.FromResult(new TestResult { Result = TestResultClass.FAIL, Explanation = "First, line\nSecond \"line\" åäö" }));
            var report = await runner.RunAsync(profile, new[] { TestCatalog.All[0], multiline }, CancellationToken.None);

            var folder = Path.Combine(temp, @"results\new");
            var files = ResultFileWriter.Write(report, folder);
            var json = File.ReadAllText(files.Json, Encoding.UTF8);
            var csv = File.ReadAllText(files.Csv, Encoding.UTF8);

            check("Files are created in a new folder", File.Exists(files.Json) && File.Exists(files.Csv), null);
            check("JSON and CSV have no BOM", File.ReadAllBytes(files.Json)[0] == (byte)'{' && File.ReadAllBytes(files.Csv)[0] == (byte)'s', null);

            var second = ResultFileWriter.Write(report, folder);
            check("A second write of the same run does not overwrite the first", second.Json != files.Json && File.Exists(files.Json) && File.Exists(second.Json), second.Json);

            using (var document = JsonDocument.Parse(json))
            {
                var root = document.RootElement;
                check("JSON: schema version", root.GetProperty("schemaVersion").GetInt32() == TestRunReport.CurrentSchemaVersion, null);
                check("JSON: tool version", root.GetProperty("toolVersion").GetString() == ProductInfo.Version, null);
                check("JSON: start and end time", root.GetProperty("startedUtc").GetDateTime() <= root.GetProperty("endedUtc").GetDateTime(), null);
                check("JSON: profile", root.GetProperty("profile").GetProperty("host").GetString() == "127.0.0.1" && root.GetProperty("profile").GetProperty("localScpAe").GetString() == "CALLBACK" && root.GetProperty("profile").GetProperty("uidRoot").GetString() == "1.2.3", null);
                var first = root.GetProperty("results")[0];
                check("JSON: result class as text", first.GetProperty("result").GetString() == "PASS" && first.GetProperty("category").GetString() == "Capability", null);
                check("JSON: full detail", first.GetProperty("statusCodes")[0].GetProperty("code").GetString() == "0x0000" && first.GetProperty("negotiatedContexts").GetArrayLength() == 1 && first.GetProperty("timeline").GetArrayLength() == 6, null);
                check("JSON: summary", root.GetProperty("summary").GetProperty("PASS").GetInt32() == 1 && root.GetProperty("summary").GetProperty("FAIL").GetInt32() == 1, null);
            }

            var rows = ParseCsv(csv);
            check("CSV: one row per result", rows.Count == 3, rows.Count.ToString());
            check("CSV: every row has every column", rows.All(r => r.Count == rows[0].Count), null);
            check("CSV: schema and tool version", CsvValue(csv, "schema_version") == TestRunReport.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture) && CsvValue(csv, "tool_version") == ProductInfo.Version, null);
            check("CSV: profile on each row", CsvValue(csv, "host", 2) == "127.0.0.1" && CsvValue(csv, "called_ae", 2) == "MOCKSCP" && CsvValue(csv, "uid_root", 2) == "1.2.3", null);
            check("CSV: start and end time", CsvValue(csv, "run_started_utc").EndsWith("Z") && CsvValue(csv, "run_ended_utc").EndsWith("Z"), CsvValue(csv, "run_started_utc"));
            check("CSV: result and raw status", CsvValue(csv, "result") == "PASS" && CsvValue(csv, "status_codes") == "C-ECHO=0x0000", CsvValue(csv, "status_codes"));
            check("CSV: quoting of commas and quotes", CsvValue(csv, "profile_name") == "Röntgen, \"test\"", CsvValue(csv, "profile_name"));
            check("CSV: line breaks and å, ä, ö survive", CsvValue(csv, "explanation", 2) == "First, line\nSecond \"line\" åäö", CsvValue(csv, "explanation", 2));

            var culture = CultureInfo.CurrentCulture;
            var uiCulture = CultureInfo.CurrentUICulture;
            string swedishJson;
            string swedishCsv;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("sv-SE");
                swedishJson = ResultFileWriter.ToJson(report);
                swedishCsv = ResultFileWriter.ToCsv(report);
            }
            finally
            {
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = uiCulture;
            }
            check("Same content under sv-SE: JSON", swedishJson == ResultFileWriter.ToJson(report), null);
            check("Same content under sv-SE: CSV", swedishCsv == ResultFileWriter.ToCsv(report), null);

            var secret = MockProfile(port);
            secret.Endpoint.UseTls = true;
            secret.Endpoint.TlsClientCertThumbprint = Thumbprint;
            var quoting = new StubTest("quotes-thumbprint", _ =>
            {
                var result = new TestResult { Result = TestResultClass.FAIL, Explanation = "Certificate " + Thumbprint };
                result.Details["note"] = Thumbprint;
                result.Timeline.Add(new ProtocolEvent { Type = "A-ABORT", Detail = Thumbprint });
                return Task.FromResult(result);
            });
            var secretReport = await new TestRunner().RunAsync(secret, new[] { TestCatalog.All[0], quoting }, CancellationToken.None);
            var secretJson = ResultFileWriter.ToJson(secretReport);
            var secretCsv = ResultFileWriter.ToCsv(secretReport);
            check("Thumbprint is in no JSON field", !secretJson.Contains(Thumbprint) && !secretJson.Contains("tlsClientCertThumbprint"), secretJson);
            check("Thumbprint is in no CSV field", !secretCsv.Contains(Thumbprint) && !ParseCsv(secretCsv)[0].Any(c => c.Contains("thumbprint")), secretCsv);

            var normalized = NormalizeJson(json);
            check("Normalizing removes only the volatile fields", !normalized.Contains("startedUtc") && !normalized.Contains("offsetMs") && normalized.Contains("schemaVersion") && normalized.Contains("0x0000") && normalized.Contains("A-ASSOCIATE-AC"), null);
            check("Normalizing notices a real difference", NormalizeJson(json) != NormalizeJson(json.Replace("\"PASS\",", "\"FAIL\",")), null);
            check("Normalizing the CSV keeps the content", NormalizeCsv(csv).Contains("MOCKSCP") && NormalizeCsv(csv).Contains("C-ECHO=0x0000") && !NormalizeCsv(csv).Contains("run_started_utc"), null);
        }

        // ---------------------------------------------------------------- command line

        public static async Task TestCli(int port, int closedPort, string temp, Action<string, bool, string> check)
        {
            Console.WriteLine("\nCommand line");
            Scp.Reset();
            var folder = Path.Combine(temp, "cli");
            var logs = Path.Combine(folder, "logs");
            Directory.CreateDirectory(folder);

            var realLogs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"DCST4\Logs");
            Func<string> realCliLogs = () => Directory.Exists(realLogs)
                ? string.Join(";", Directory.GetFiles(realLogs, "Cli-*").Select(f => f + File.GetLastWriteTimeUtc(f).Ticks))
                : string.Empty;
            var realBefore = realCliLogs();

            var version = await RunCli("--version");
            check("Starts from its own output with its DLLs in libs", version.ExitCode == 0 && version.Output.Trim() == ProductInfo.Version, version.Output);
            check("No DLL beside the exe", !Directory.GetFiles(Path.GetDirectoryName(CliPath), "*.dll").Any() && File.Exists(Path.Combine(Path.GetDirectoryName(CliPath), @"libs\fo-dicom.core.dll")), null);

            var profilePath = Path.Combine(folder, "target.json");
            MockProfile(port).Save(profilePath);

            var pass = await RunCli("run", "--profile", profilePath, "--tests", "c-echo", "--out", Path.Combine(folder, "pass"), "--log-dir", logs);
            check("Pass: exit code 0", pass.ExitCode == 0, pass.Output);
            check("Pass: reached the SCP", Scp.EchoCount == 1, Scp.EchoCount.ToString());
            check("Pass: JSON and CSV written", Directory.GetFiles(Path.Combine(folder, "pass"), "*.json").Length == 1 && Directory.GetFiles(Path.Combine(folder, "pass"), "*.csv").Length == 1, pass.Output);
            check("Pass: result shown on the console", pass.Output.Contains("c-echo: PASS"), pass.Output);

            var logFiles = Directory.GetFiles(logs, "Cli-*.log");
            var logText = logFiles.Length == 1 ? ReadShared(logFiles[0]) : string.Empty;
            check("Its own log file, in the folder given", logFiles.Length == 1, string.Join(";", logFiles));
            check("The log has Core's messages", logText.Contains("C-ECHO ->") && logText.Contains("[DCST4.Core.Network.EchoService]"), logText);
            check("The log has fo-dicom's messages", logText.Contains("A-ASSOCIATE") || logText.Contains("Association"), null);

            // The negative tests expect a refusal, so this SCP only knows the profile's own AE titles.
            Scp.RejectWhen = (association, number) => association.CallingAE == "DCST4" && association.CalledAE == "MOCKSCP" ? null : new ScpRejection();
            Scp.ContextTable = new ScpContextTable { Default = ScpAnswer.Reject(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported) };
            Scp.ContextTable.Set(DicomUID.Verification, ScpAnswer.Accept);
            var byCategory = await RunCli("run", "--profile", profilePath, "--tests", "Capability", "--out", Path.Combine(folder, "category"), "--log-dir", logs);
            Scp.RejectWhen = null;
            Scp.ContextTable = null;
            check("Tests can be selected by category", byCategory.ExitCode == 0 && byCategory.Output.Contains("c-echo: PASS") && byCategory.Output.Contains("negotiation-unknown-calling-ae: EXPECTED_FAILURE"), byCategory.Output);

            var closedPath = Path.Combine(folder, "closed.json");
            MockProfile(closedPort).Save(closedPath);
            var closed = await RunCli("run", "--profile", closedPath, "--tests", "c-echo", "--out", Path.Combine(folder, "closed"), "--log-dir", logs);
            var closedCsv = File.ReadAllText(Directory.GetFiles(Path.Combine(folder, "closed"), "*.csv").Single(), Encoding.UTF8);
            var closedResult = CsvValue(closedCsv, "result");
            check("Closed port: FAIL or TIMEOUT, not a crash", closedResult == "FAIL" || closedResult == "TIMEOUT", closedResult);
            check("Closed port: exit code follows the result", closed.ExitCode == (closedResult == "FAIL" ? 1 : 0), closed.ExitCode + " " + closed.Output);
            check("Closed port: clear message", CsvValue(closedCsv, "explanation").Contains("127.0.0.1:" + closedPort) && !CsvValue(closedCsv, "explanation").Contains("One or more errors"), CsvValue(closedCsv, "explanation"));

            Func<string, CliRun, string, bool> refused = (name, run, expected) =>
            {
                var ok = run.ExitCode == RunExitCodes.Usage && run.Output.Contains(expected);
                check(name, ok, run.ExitCode + " " + run.Output);
                return ok;
            };
            refused("No arguments: exit code 2", await RunCli(), "Usage");
            refused("Unknown command: exit code 2", await RunCli("walk"), "Usage");
            refused("Unknown option: exit code 2", await RunCli("run", "--wat", "x"), "--wat");
            var named = await RunCli("run", "--profile", profilePath, "--tests", "c-echo", "--out", Path.Combine(folder, "named"), "--log-dir", logs, "--patient-naming", "MarkedTestName");
            check("--patient-naming is accepted on run", named.ExitCode == 0 && named.Output.Contains("c-echo: PASS"), named.Output);
            refused("Unknown --patient-naming: exit code 2", await RunCli("run", "--profile", profilePath, "--tests", "c-echo", "--out", folder, "--patient-naming", "Bad"), "Bad");
            refused("Missing --profile: exit code 2", await RunCli("run", "--tests", "c-echo", "--out", folder), "--profile");
            refused("Option without a value: exit code 2", await RunCli("run", "--profile"), "--profile");
            refused("Missing profile file: exit code 2", await RunCli("run", "--profile", Path.Combine(folder, "none.json"), "--tests", "c-echo", "--out", folder, "--log-dir", logs), "Could not read");
            refused("Unknown test: exit code 2, with the choices", await RunCli("run", "--profile", profilePath, "--tests", "nope", "--out", folder, "--log-dir", logs), "c-echo");

            var invalidPath = Path.Combine(folder, "invalid.json");
            var invalid = MockProfile(port);
            invalid.UidRoot = "x";
            invalid.Save(invalidPath);
            var countBefore = Scp.AssociationCount;
            refused("Invalid profile: exit code 2", await RunCli("run", "--profile", invalidPath, "--tests", "c-echo", "--out", folder, "--log-dir", logs), "UID root");
            check("Invalid profile: nothing is sent", Scp.AssociationCount == countBefore, null);

            var blocked = Path.Combine(folder, "a-file");
            File.WriteAllText(blocked, "not a folder");
            refused("Output folder cannot be used: exit code 2", await RunCli("run", "--profile", profilePath, "--tests", "c-echo", "--out", blocked, "--log-dir", logs), "output folder");
            check("Output folder cannot be used: nothing is sent", Scp.AssociationCount == countBefore, null);

            check("Nothing was written to the user's log folder", realCliLogs() == realBefore, null);
            Scp.Reset();
        }

        private static string ReadShared(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }
    }
}
