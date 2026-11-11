using DCST4.Core;
using DCST4.Core.Capability;
using DCST4.Core.Network;
using DCST4.Core.Performance;
using DCST4.Core.Reporting;
using DCST4.Core.TestData;
using DCST4.Core.Testing;
using DCST4.Core.Verification;
using FellowOakDicom;
using FellowOakDicom.Network;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Tests
{
    /// <summary>
    /// The HTML report, the comparison of runs and the vendor fingerprint: on reports made by hand, where every field is known,
    /// and on real runs against the mock SCP, through the command line where the exit code matters.
    /// </summary>
    internal static class ReportingTests
    {
        private const int ListenPort = 41222;
        private const string Root = "1.2.826.0.1.3680043.10.999";

        /// <summary>What a hostile system could put in an Error Comment: markup that would run or fetch if it were not encoded, and a control character.</summary>
        private const string Evil = "<script>alert('x')</script><img src=\"https://example.invalid/x.png\" onerror=\"alert(1)\">";

        /// <summary>The same idea within the 64 characters that an Error Comment (0000,0902) may have.</summary>
        private const string EvilComment = "<script>alert('x')</script><img src=\"https://x.invalid/x.png\">";
        private const string EvilCommentAsText = "&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt;&lt;img src=&quot;https://x.invalid/x.png&quot;&gt;";

        private static int _port;
        private static string _temp;
        private static Action<string, bool, string> _check;

        private static void Check(string name, bool ok, string detail = null) => _check(name, ok, detail);

        public static async Task Run(int port, int closedPort, string temp, Action<string, bool, string> check)
        {
            _port = port;
            _temp = Path.Combine(temp, "reporting");
            _check = check;
            Directory.CreateDirectory(_temp);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            TestEncoding();
            TestComparisonRules();
            TestLoader();
            await TestRichRun();
            await TestErrorCommentEndToEnd();
            await TestSelfCompare();
            await TestScriptedRegression();
            Scp.Reset();
            Console.WriteLine($"  (reporting section: {clock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s)");
        }

        // ---------------------------------------------------------------- the scanner

        private static readonly HashSet<string> AllowedTags = new HashSet<string>(StringComparer.Ordinal)
        {
            "html", "head", "meta", "title", "style", "body", "main", "h1", "h2", "h3", "p", "section", "nav", "a", "table", "thead", "tbody", "tr", "th", "td",
            "code", "span", "div", "details", "summary", "strong", "footer", "figure", "figcaption", "svg", "line", "text", "rect", "polyline", "circle", "path",
        };

        private static readonly HashSet<string> AllowedAttributes = new HashSet<string>(StringComparer.Ordinal)
        {
            "lang", "charset", "http-equiv", "content", "name", "class", "id", "href", "colspan", "title", "viewBox", "width", "height", "role", "aria-label",
            "x", "y", "x1", "x2", "y1", "y2", "text-anchor", "points", "cx", "cy", "r", "d", "rx",
        };

        /// <summary>
        /// Reads a page the way a browser would find its elements: every &lt; starts a tag. Returns what is wrong with it: a tag
        /// or attribute that is not one of ours, a link that leaves the page, or a style sheet that fetches something.
        /// </summary>
        internal static List<string> Scan(string html, out HashSet<string> tags)
        {
            var problems = new List<string>();
            tags = new HashSet<string>(StringComparer.Ordinal);
            const string doctype = "<!DOCTYPE html>";
            var i = html.StartsWith(doctype, StringComparison.Ordinal) ? doctype.Length : 0;
            if (i == 0) problems.Add("no doctype");

            while ((i = html.IndexOf('<', i)) >= 0)
            {
                var end = html.IndexOf('>', i);
                if (end < 0) { problems.Add("unclosed tag at " + i); break; }
                var inner = html.Substring(i + 1, end - i - 1);
                i = end + 1;
                if (inner.StartsWith("/", StringComparison.Ordinal))
                {
                    if (!AllowedTags.Contains(inner.Substring(1))) problems.Add("end tag " + inner);
                    continue;
                }

                var nameLength = 0;
                while (nameLength < inner.Length && (char.IsLetterOrDigit(inner[nameLength]) || inner[nameLength] == '-')) nameLength++;
                var name = inner.Substring(0, nameLength);
                if (!AllowedTags.Contains(name)) { problems.Add("tag <" + (inner.Length > 40 ? inner.Substring(0, 40) : inner) + ">"); continue; }
                tags.Add(name);

                var rest = inner.Substring(nameLength).TrimEnd('/');
                var at = 0;
                while (at < rest.Length)
                {
                    while (at < rest.Length && rest[at] == ' ') at++;
                    if (at >= rest.Length) break;
                    var equals = rest.IndexOf("=\"", at, StringComparison.Ordinal);
                    if (equals < 0) { problems.Add($"<{name}>: attribute without a quoted value: {rest.Substring(at)}"); break; }
                    var attribute = rest.Substring(at, equals - at);
                    var close = rest.IndexOf('"', equals + 2);
                    if (close < 0) { problems.Add($"<{name}>: unclosed value of {attribute}"); break; }
                    var value = rest.Substring(equals + 2, close - equals - 2);
                    at = close + 1;

                    if (!AllowedAttributes.Contains(attribute)) problems.Add($"<{name}>: attribute {attribute}");
                    if (attribute == "href" && !value.StartsWith("#", StringComparison.Ordinal)) problems.Add($"<{name}>: href leaves the page: {value}");
                }

                if (name == "style")
                {
                    var styleEnd = html.IndexOf("</style>", i, StringComparison.Ordinal);
                    if (styleEnd < 0) { problems.Add("unclosed style"); break; }
                    var css = html.Substring(i, styleEnd - i);
                    foreach (var banned in new[] { "url(", "@import", "http:", "https:", "expression(", "<" })
                    {
                        if (css.IndexOf(banned, StringComparison.OrdinalIgnoreCase) >= 0) problems.Add("style sheet holds " + banned);
                    }
                    i = styleEnd;
                }
            }
            return problems;
        }

        private static bool HasPolicy(string html) =>
            html.Contains("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src &#39;none&#39;; style-src &#39;unsafe-inline&#39;; img-src data:\"/>");

        // ---------------------------------------------------------------- reports made by hand

        private static LoadSummary Summary(double perSecond, double p95, double errorRate = 0, long stored = 1000) => new LoadSummary
        {
            WindowMs = 10000, Attempts = stored, Stored = stored, Bytes = stored * 130000, StoredPerSecond = perSecond, MegabytesPerSecond = perSecond * 0.13,
            Latency = new LatencySummary { Count = stored, P50Ms = p95 / 2, P95Ms = p95, P99Ms = p95 * 1.2, MaxMs = p95 * 2, MeanMs = p95 / 2 },
            ResponseWait = new LatencySummary { Count = stored, P50Ms = 1, P95Ms = 2, P99Ms = 3, MaxMs = 4, MeanMs = 1 },
            AssociationSetup = new LatencySummary { Count = 4, P50Ms = 3, P95Ms = 5, P99Ms = 5, MaxMs = 6, MeanMs = 3 },
            Connect = new LatencySummary { Count = 4, P50Ms = 1, P95Ms = 2, P99Ms = 2, MaxMs = 2, MeanMs = 1 },
            AssociationsOpened = 4, Errors = new StatusClassCounts { Success = stored }, ErrorRate = errorRate,
        };

        private static TestResult Load(double perSecond, double p95, double errorRate = 0, Action<LoadSettings> settings = null)
        {
            var s = new LoadSettings { Associations = 4, DurationSeconds = 10, Mix = "ct" };
            settings?.Invoke(s);
            return new TestResult
            {
                TestId = "load-throughput", Name = "Load: throughput and latency", Category = TestCategory.Performance, Result = TestResultClass.PASS, Explanation = "Stored everything.",
                Performance = new PerformanceResult
                {
                    Mode = LoadMode.Load, Settings = s, Mix = s.Mix, MixDescription = "CT", TransferSyntaxUid = DicomTransferSyntax.ExplicitVRLittleEndian.UID.UID, MaxPdu = 262144,
                    PlannedInstances = 1000, PlannedMegabytes = 135, StoppedBy = "duration", Summary = Summary(perSecond, p95, errorRate), Seconds = 10, Client = new ClientHealth { LogicalProcessors = 8 },
                },
            };
        }

        private static TestResult Simple(string id, TestResultClass result, string status = "0x0000") => new TestResult
        {
            TestId = id, Name = id, Category = TestCategory.Capability, Result = result, Explanation = "As it was.",
            StatusCodes = { new DimseStatusRecord { Operation = "C-ECHO", Code = status, Description = "Success" } },
        };

        private static CapabilityMatrix Matrix(params CapabilityResult[] cells)
        {
            var syntaxes = new[] { DicomTransferSyntax.ExplicitVRLittleEndian, DicomTransferSyntax.JPEGProcess14SV1, DicomTransferSyntax.JPEG2000Lossless };
            var matrix = new CapabilityMatrix
            {
                ToolVersion = "1.10.0", RemoteImplementationClassUid = "1.2.3.4", RemoteImplementationVersionName = "MOCK_1", RemoteMaxPduLength = 16384,
                TransferSyntaxes = syntaxes.Select(s => new MatrixSyntax { Uid = s.UID.UID, Name = s.UID.Name, ShortName = s.UID.Name.Split(' ')[0] }).ToList(),
            };
            var row = new MatrixRow { SopClassUid = DicomUID.CTImageStorage.UID, SopClassName = "CT Image Storage" };
            for (var i = 0; i < cells.Length; i++) row.Cells.Add(new MatrixCell { TransferSyntaxUid = syntaxes[i].UID.UID, Result = cells[i], Association = 1, PresentationContextId = 2 * i + 1 });
            matrix.Rows.Add(row);
            return matrix;
        }

        private static TestResult Probe(params CapabilityResult[] cells)
        {
            var result = Simple("negotiation-probe", TestResultClass.PASS);
            result.CapabilityMatrix = Matrix(cells);
            return result;
        }

        private static TestRunReport Report(params TestResult[] results)
        {
            var report = new TestRunReport
            {
                ToolVersion = "1.10.0", StartedUtc = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc), EndedUtc = new DateTime(2026, 11, 1, 8, 1, 0, DateTimeKind.Utc), DurationMs = 60000,
                Profile = new ResultProfile { Name = "Mock", Host = "127.0.0.1", Port = 104, CallingAe = "DCST4", CalledAe = "MOCKSCP", LocalScpAe = "DCST4", LocalScpPort = 11113, UidRoot = Root },
                Seed = 7,
            };
            report.Results.AddRange(results);
            foreach (TestResultClass value in Enum.GetValues(typeof(TestResultClass))) report.Summary[value.ToString()] = results.Count(r => r.Result == value);
            return report;
        }

        private static ComparisonResult Compare(TestRunReport a, TestRunReport b, double threshold = 10) =>
            RunComparer.Compare(new[] { new NamedRun("a", a), new NamedRun("b", b) }, new ComparisonOptions { ThresholdPercent = threshold });

        private static string Findings(ComparisonResult comparison) => string.Join(" | ", comparison.Runs.SelectMany(r => r.Findings).Select(f => f.Kind + " " + f));

        /// <summary>The same run as it comes back from its own JSON file, so that a change can be made to one copy only.</summary>
        private static TestRunReport Copy(TestRunReport report)
        {
            var path = Path.Combine(_temp, "copy-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, ResultFileWriter.ToJson(report), new UTF8Encoding(false));
            return RunFile.Load(path);
        }

        // ---------------------------------------------------------------- encoding

        private static TestRunReport HostileReport()
        {
            var bad = Evil + "\u0001";
            var failed = Simple("store-ct", TestResultClass.FAIL, "0xA700");
            failed.Name = bad;
            failed.Explanation = "Refused with 0xA700, comment: " + bad;
            failed.StatusCodes[0].Description = bad;
            failed.StatusCodes[0].Operation = bad;
            failed.Details["firstProblem"] = bad;
            failed.Details[bad] = bad;
            failed.Uids[bad] = bad;
            failed.Timeline.Add(new ProtocolEvent { Index = 0, OffsetMs = 3, Direction = ProtocolEvent.Received, Type = "A-ASSOCIATE-AC", Detail = "Max PDU 16384, implementation 1.2.3 " + bad });
            failed.Timeline.Add(new ProtocolEvent { Index = 1, OffsetMs = 9, Direction = ProtocolEvent.Received, Type = bad, Detail = bad, Status = bad, DimseIndex = 1, Association = 1 });

            var probe = Probe(CapabilityResult.Accepted, CapabilityResult.TransferSyntaxNotSupported, CapabilityResult.AcceptedOtherSyntax);
            probe.CapabilityMatrix.RemoteImplementationVersionName = bad;
            probe.CapabilityMatrix.RemoteImplementationClassUid = bad;
            probe.CapabilityMatrix.Rows[0].SopClassName = bad;
            probe.CapabilityMatrix.Rows[0].SopClassUid = bad;
            probe.CapabilityMatrix.Rows[0].Cells[2].AcceptedTransferSyntaxUid = "1.2.840.10008.1.2";
            probe.CapabilityMatrix.TransferSyntaxes[0].ShortName = bad;
            probe.CapabilityMatrix.TransferSyntaxes[0].Name = bad;
            probe.CapabilityMatrix.Associations.Add(new ProbeAssociationRecord
            {
                Index = 1, ProposedContexts = 3, Ending = AssociationEnding.Rejected, Detail = bad, RemoteImplementationClassUid = bad, RemoteImplementationVersionName = bad,
                Rejection = new AssociationRejectionRecord { Result = bad, Source = bad, Reason = bad },
            });
            probe.CapabilityMatrix.RoleSelectionReplies.Add(new RoleSelectionReply { SopClassUid = bad, SopClassName = bad, Replied = true, ScpRole = true });
            probe.CapabilityMatrix.ExtendedNegotiationReplies.Add(new ExtendedNegotiationReply { SopClassUid = bad, SopClassName = bad, Requested = bad, Replied = true, Accepted = bad });

            var integrity = Simple("verify-integrity", TestResultClass.FAIL);
            integrity.Integrity = new IntegrityReport
            {
                Sets =
                {
                    new IntegritySet
                    {
                        ObjectType = bad, Method = bad, ExpectedCount = 1, ReturnedCount = 1, Result = TestResultClass.FAIL, Missing = { bad }, Extra = { bad },
                        Levels = { new IntegrityLevelResult { Level = IntegrityLevel.Attributes, Result = TestResultClass.FAIL, Explanation = bad } },
                        Instances =
                        {
                            new InstanceIntegrity
                            {
                                SopInstanceUid = bad, IdentityChanges = { bad }, PrivateDropped = { bad }, CharacterSetSent = bad, CharacterSetReturned = bad, CharacterSetChanged = true,
                                Pixel = PixelVerdict.LossyChanged, PixelDetail = bad, MaxAbsoluteDifference = 3, MeanAbsoluteDifference = 1, TransferSyntaxSent = bad, TransferSyntaxReturned = bad, TranscodingInference = bad,
                                Differences = { new AttributeDifference { Path = bad, Name = bad, Class = AttributeChangeClass.SilentModification, Original = bad, Retrieved = bad } },
                            },
                        },
                    },
                },
            };

            var commit = Simple("commit-stored", TestResultClass.FAIL);
            commit.Commitment = new CommitmentResult
            {
                TransactionUid = bad, ActionStatus = bad, ReportVia = "SeparateAssociation", ReportCallingAe = bad, EventTypeId = 2, ReportAfterMs = 12,
                Instances = { new CommitmentInstanceResult { ObjectType = bad, SopInstanceUid = bad, Outcome = CommitmentInstanceOutcome.Failed, FailureReason = bad, FailureReasonText = bad } },
                UnrequestedInstances = { bad },
                RejectedReports = { new RejectedCommitmentReport { Via = bad, CallingAe = bad, TransactionUid = bad, AnsweredStatus = bad, Problem = bad } },
            };

            var limit = Simple("limit-max-associations", TestResultClass.FAIL);
            limit.Limits = new LimitsResult
            {
                Ceiling = 10, AssociationsOpened = 3, AssociationLimit = 3, RefusedWith = bad, Rejection = new AssociationRejection { Result = bad, Source = bad, Reason = bad }, EndedWith = bad, WaitSeconds = 5,
                PduAttempts = new List<PduAttempt> { new PduAttempt { OurMaxPdu = 4096, Status = bad, Outcome = bad, SopInstanceUid = bad } },
                Fault = new FaultRecord { Kind = bad, RetryStatus = bad },
                Tls = new TlsSessionInfo { HandshakeCompleted = true, Protocol = bad, CipherAlgorithm = bad, ServerSubject = bad, ServerIssuer = bad, PolicyErrors = bad, ChainStatus = bad },
                ClientCertificate = bad,
            };

            var load = Load(100, 20);
            load.Result = TestResultClass.FAIL;
            load.Explanation = bad;
            load.Details["firstProblem"] = "0xA700 Error Comment \"" + bad + "\"";
            load.Performance.Mix = bad;
            load.Performance.MixDescription = bad;
            load.Performance.StoppedBy = bad;
            load.Performance.Summary.Errors.ByCode[bad] = 3;
            load.Performance.TimeToAvailable = new TimeToAvailable { Sampled = 2, Found = 1, NeverFound = 1, Note = bad, Available = new LatencySummary { Count = 1, P50Ms = 2000, P95Ms = 2000, MaxMs = 2000 } };

            var ramp = Load(100, 20);
            ramp.TestId = "load-ramp";
            ramp.Performance.Mode = LoadMode.Ramp;
            ramp.Performance.Steps = new List<RampStep>
            {
                new RampStep { Step = 1, Associations = 1, Summary = Summary(40, 20) }, new RampStep { Step = 2, Associations = 2, Summary = Summary(75, 22) },
                new RampStep { Step = 3, Associations = 4, Summary = Summary(78, 60), Saturated = true },
            };
            ramp.Performance.Saturation = new SaturationVerdict { Found = true, Step = 3, Associations = 4, Reason = bad, BestStep = 3, BestAssociations = 4, Description = bad };

            var soak = Load(2, 20);
            soak.TestId = "load-soak";
            soak.Performance.Mode = LoadMode.Soak;
            soak.Performance.Windows = Enumerable.Range(1, 6).Select(n => new SoakWindow { Window = n, StartSecond = (n - 1) * 60, EndSecond = n * 60, Summary = Summary(2, 20 + n) }).ToList();
            soak.Performance.Degradation = new DegradationVerdict { Degraded = false, Description = bad };

            var report = Report(failed, probe, integrity, commit, limit, load, ramp, soak);
            report.Profile.Name = bad;
            report.Profile.CalledAe = bad;
            report.Profile.Host = bad;
            report.ToolVersion = bad;
            report.Chain = new List<ChainRow>
            {
                new ChainRow { ObjectType = bad, DisplayName = bad, TransferSyntaxUid = bad, Accepted = ChainStep.Of(TestResultClass.FAIL, bad), Indexed = ChainStep.Of(TestResultClass.INDETERMINATE, bad) },
            };
            return report;
        }

        private static void TestEncoding()
        {
            Console.WriteLine("\nReporting: everything is encoded, and the page is complete in itself");

            Check("Encode: markup becomes text", HtmlWriter.Encode("<b a=\"1\" c='2'>&") == "&lt;b a=&quot;1&quot; c=&#39;2&#39;&gt;&amp;", HtmlWriter.Encode("<b a=\"1\" c='2'>&"));
            Check("Encode: a control character is shown as U+FFFD, and a line break is kept", HtmlWriter.Encode("a\u0001b\u0000c\nd\u009Fe") == "a\uFFFDb\uFFFDc\nd\uFFFDe", null);
            Check("Encode: Swedish letters and a pair of surrogates become character references, and a lone surrogate U+FFFD", HtmlWriter.Encode("åäö \U0001F600 \uD83D") == "&#229;&#228;&#246; &#128512; \uFFFD", HtmlWriter.Encode("åäö \U0001F600 \uD83D"));
            var named = false;
            try { new HtmlWriter().Open("a b"); } catch (ArgumentException) { named = true; }
            Check("A tag name that is not a name is refused", named, null);

            var html = RunReportWriter.Render(HostileReport(), new RunReportOptions { RunName = Evil });
            var problems = Scan(html, out var tags);
            Check("Run report: only our own tags and attributes, no link out of the page", problems.Count == 0, string.Join(" | ", problems.Take(6)));
            Check("Run report: no script, image, frame, object or link element", !new[] { "script", "img", "iframe", "object", "embed", "link", "base", "form" }.Any(t => html.IndexOf("<" + t, StringComparison.OrdinalIgnoreCase) >= 0), null);
            Check("Run report: the markup is there as text", html.Contains("&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt;&lt;img src=&quot;https://example.invalid/x.png&quot; onerror=&quot;alert(1)&quot;&gt;"), null);
            Check("Run report: no control character", !html.Any(c => c < 0x20 && c != '\n' && c != '\r' && c != '\t'), null);
            Check("Run report: has the Content-Security-Policy", HasPolicy(html), null);
            Check("Run report: every section of a full run", new[] { "target", "summary", "fingerprint", "capability", "chain", "integrity", "limits", "commitment", "performance", "timelines" }
                .All(id => html.Contains("<section id=\"" + id + "\">")), null);
            Check("Run report: charts as inline SVG, for the ramp and the soak", tags.Contains("svg") && tags.Contains("path") && tags.Contains("polyline") && html.Contains("saturated"), null);
            Check("Run report: no chart of the run without its series file, and it says so", html.Contains("series file of this test was not found"), null);

            var comparison = RunComparer.Compare(new[] { new NamedRun(Evil, HostileReport()), new NamedRun(Evil + "2", Report(Simple("store-ct", TestResultClass.PASS), Load(50, 80))) });
            var page = ComparisonReportWriter.Render(comparison);
            var pageProblems = Scan(page, out _);
            Check("Comparison: only our own tags and attributes", pageProblems.Count == 0, string.Join(" | ", pageProblems.Take(6)));
            Check("Comparison: the markup is there as text, and the policy too", page.Contains("&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt;") && page.IndexOf("<script", StringComparison.OrdinalIgnoreCase) < 0 && HasPolicy(page), null);

            var empty = RunReportWriter.Render(new TestRunReport { Profile = new ResultProfile(), Cancelled = true });
            Check("A run with no results still gives a page", Scan(empty, out _).Count == 0 && empty.Contains("The run has no results.") && empty.Contains("cancelled"), null);

            Check("Scanner: finds a script, an image and a link out", Scan("<!DOCTYPE html><html><script>x</script></html>", out _).Count > 0
                && Scan("<!DOCTYPE html><p><img src=\"https://example.invalid/x\"/></p>", out _).Count > 0
                && Scan("<!DOCTYPE html><a href=\"https://example.invalid\">x</a>", out _).Count > 0
                && Scan("<!DOCTYPE html><p onclick=\"x\">x</p>", out _).Count > 0
                && Scan("<!DOCTYPE html><style>p{background:url(x)}</style>", out _).Count > 0
                && Scan("<!DOCTYPE html><p>fine</p>", out _).Count == 0, null);
        }

        // ---------------------------------------------------------------- comparison rules

        private static void TestComparisonRules()
        {
            Console.WriteLine("\nReporting: what counts as a regression");

            var baseline = Report(Simple("c-echo", TestResultClass.PASS), Probe(CapabilityResult.Accepted, CapabilityResult.Accepted, CapabilityResult.TransferSyntaxNotSupported), Load(100, 20));
            var same = Compare(baseline, Copy(baseline));
            Check("The same run twice: no differences", !same.HasDifferences && same.Regressions == 0 && same.Summary == "No differences.", Findings(same));

            // Everything that belongs to one run alone.
            var later = Copy(baseline);
            later.StartedUtc = later.StartedUtc.AddDays(30);
            later.EndedUtc = later.EndedUtc.AddDays(30);
            later.DurationMs = 99999;
            later.Seed = 12345;
            later.ToolVersion = "9.9.9";
            later.Profile.Host = "other.example.org";
            later.Profile.CalledAe = "OTHER";
            foreach (var r in later.Results)
            {
                r.StartedUtc = r.StartedUtc.AddDays(30);
                r.DurationMs += 777;
                r.Explanation = "Worded differently.";
                r.Uids["sopInstanceUid"] = "1.2.3.999";
                r.Details["runNonce"] = "abc";
                r.Timeline.Add(new ProtocolEvent { OffsetMs = 123, Type = "A-RELEASE-RQ", Direction = ProtocolEvent.Sent });
                if (r.Performance != null) r.Performance.SeriesFile = @"C:\elsewhere\series.csv";
            }
            var ignored = Compare(baseline, later);
            Check("Times, durations, seed, UIDs, paths, wording and the target are not differences", !ignored.HasDifferences, Findings(ignored));

            Func<double, double, double, ComparisonResult> perf = (perSecond, p95, threshold) => Compare(Report(Load(100, 20)), Report(Load(perSecond, p95)), threshold);
            Check("p95 latency +9 % at a threshold of 10: no regression", perf(100, 21.8, 10).Regressions == 0, Findings(perf(100, 21.8, 10)));
            var slower = perf(100, 22.2, 10);
            Check("p95 latency +11 % at a threshold of 10: a regression, with the percentage", slower.Regressions == 1 && slower.Runs[0].Findings.Single().Subject.Contains("Latency p95") && slower.Runs[0].Findings.Single().Note == "+11.0 %", Findings(slower));
            Check("The same at a threshold of 20: no regression", perf(100, 22.2, 20).Regressions == 0, null);
            var throughput = perf(85, 20, 10);
            Check("Throughput -15 %: a regression, with a minus sign", throughput.Regressions == 1 && throughput.Runs[0].Findings.Single().Subject.Contains("Stored per second") && throughput.Runs[0].Findings.Single().Note == "\u221215.0 %", Findings(throughput));
            Check("Throughput -9 %: no regression", perf(91, 20, 10).Regressions == 0, null);
            Check("Faster and higher throughput: no regression, and no finding", !perf(150, 10, 10).HasDifferences && perf(150, 10, 10).Runs[0].Performance.Any(p => p.Better), null);

            var tiny = Compare(Report(Load(100, 0.5)), Report(Load(100, 0.9)));
            Check("A latency that rises by less than 1 ms is not a regression, whatever the percentage", tiny.Regressions == 0 && tiny.Runs[0].Performance.Single(p => p.Metric == "Latency p95").DeltaPercent == 80, Findings(tiny));

            var context = Compare(Report(Load(100, 20)), Report(Load(100, 20)));
            var rows = context.Runs[0].Performance;
            Check("The performance rows: decisive and context metrics", rows.Count == 8 && rows.Count(p => p.Decisive) == 3 && rows.All(p => !p.Worse && !p.Better), string.Join(",", rows.Select(p => p.Metric)));

            var errors = Compare(Report(Load(100, 20, 0.001)), Report(Load(100, 20, 0.02)));
            Check("Error rate up by more than 1 percentage point: a regression", errors.Regressions == 1 && errors.Runs[0].Findings.Single().Note.Contains("percentage points"), Findings(errors));
            Check("Error rate up by half a point: no regression", Compare(Report(Load(100, 20, 0.001)), Report(Load(100, 20, 0.006))).Regressions == 0, null);

            var otherSettings = Compare(Report(Load(100, 20)), Report(Load(40, 90, 0, s => s.Associations = 1)));
            Check("Other settings: shown, not comparable and not a regression", otherSettings.Regressions == 0 && otherSettings.Runs[0].Findings.Single().Kind == FindingKind.NotComparable
                && otherSettings.Runs[0].Findings.Single().Note.Contains("associations 4 and 1") && otherSettings.Runs[0].Performance.Any(p => p.Worse && !p.Regression), Findings(otherSettings));

            Func<TestResultClass, TestResultClass, ComparisonFinding> change = (before, after) => Compare(Report(Simple("x", before)), Report(Simple("x", after))).Runs[0].Findings.SingleOrDefault(f => f.Area == "Result");
            Check("PASS to FAIL and to TIMEOUT: regressions", change(TestResultClass.PASS, TestResultClass.FAIL).Kind == FindingKind.Regression && change(TestResultClass.EXPECTED_FAILURE, TestResultClass.TIMEOUT).Kind == FindingKind.Regression, null);
            Check("PASS to NOT_SUPPORTED, and NOT_SUPPORTED to FAIL: regressions", change(TestResultClass.PASS, TestResultClass.NOT_SUPPORTED).Kind == FindingKind.Regression && change(TestResultClass.NOT_SUPPORTED, TestResultClass.FAIL).Kind == FindingKind.Regression, null);
            Check("FAIL to PASS: an improvement", change(TestResultClass.FAIL, TestResultClass.PASS).Kind == FindingKind.Improvement, null);
            Check("PASS to EXPECTED_FAILURE, FAIL to TIMEOUT: differences", change(TestResultClass.PASS, TestResultClass.EXPECTED_FAILURE).Kind == FindingKind.Difference && change(TestResultClass.FAIL, TestResultClass.TIMEOUT).Kind == FindingKind.Difference, null);
            Check("To TOOL_ERROR or INDETERMINATE: not comparable, never a regression", change(TestResultClass.PASS, TestResultClass.TOOL_ERROR).Kind == FindingKind.NotComparable
                && change(TestResultClass.PASS, TestResultClass.INDETERMINATE).Kind == FindingKind.NotComparable && change(TestResultClass.TOOL_ERROR, TestResultClass.FAIL).Kind == FindingKind.NotComparable, null);

            var added = Compare(Report(Simple("a", TestResultClass.PASS)), Report(Simple("b", TestResultClass.FAIL)));
            Check("A test in only one run: not comparable, both ways", added.Regressions == 0 && added.Runs[0].Findings.Count == 2 && added.Runs[0].Findings.All(f => f.Kind == FindingKind.NotComparable), Findings(added));
            var codes = Compare(Report(Simple("x", TestResultClass.PASS, "0x0000")), Report(Simple("x", TestResultClass.PASS, "0xB000")));
            Check("The same result from another raw status: a difference", codes.Regressions == 0 && codes.Runs[0].Findings.Single().Area == "Status codes" && codes.Runs[0].Findings.Single().Value.Contains("0xB000"), Findings(codes));

            Func<CapabilityResult, CapabilityResult, ComparisonResult> cell = (before, after) =>
                Compare(Report(Probe(CapabilityResult.Accepted, before)), Report(Probe(CapabilityResult.Accepted, after)));
            var lost = cell(CapabilityResult.Accepted, CapabilityResult.TransferSyntaxNotSupported);
            Check("A combination that is no longer accepted: a regression that names it", lost.Regressions == 1 && lost.Runs[0].Findings.Single(f => f.Kind == FindingKind.Regression).Subject == "CT Image Storage"
                && lost.Runs[0].Findings.Single(f => f.Kind == FindingKind.Regression).Value.Contains("JPEG"), Findings(lost));
            Check("A combination that is newly accepted: an improvement", cell(CapabilityResult.AbstractSyntaxNotSupported, CapabilityResult.Accepted).Runs[0].Findings.Any(f => f.Kind == FindingKind.Improvement && f.Area == "Capability"), null);
            Check("Accepted with another syntax than proposed counts as lost", cell(CapabilityResult.Accepted, CapabilityResult.AcceptedOtherSyntax).Regressions == 1, null);
            var notProbed = cell(CapabilityResult.Accepted, CapabilityResult.NotProbed);
            Check("Not probed, or an association that failed: not comparable, not a loss", notProbed.Regressions == 0 && notProbed.Runs[0].Findings.Any(f => f.Kind == FindingKind.NotComparable && f.Area == "Capability")
                && cell(CapabilityResult.Accepted, CapabilityResult.AssociationFailed).Regressions == 0, Findings(notProbed));
            Check("Rejected for another reason: a difference", cell(CapabilityResult.AbstractSyntaxNotSupported, CapabilityResult.TransferSyntaxNotSupported).Runs[0].Findings.Any(f => f.Kind == FindingKind.Difference && f.Area == "Capability"), null);
            var oneMatrix = Compare(Report(Probe(CapabilityResult.Accepted)), Report(Simple("negotiation-probe", TestResultClass.PASS)));
            Check("A matrix in only one run: not comparable", oneMatrix.Regressions == 0 && oneMatrix.Runs[0].Findings.Any(f => f.Area == "Capability" && f.Kind == FindingKind.NotComparable), Findings(oneMatrix));

            var chainBefore = Report(Simple("store-ct", TestResultClass.PASS));
            chainBefore.Chain = new List<ChainRow> { new ChainRow { ObjectType = "ct", DisplayName = "CT", Accepted = ChainStep.Of(TestResultClass.PASS, "ok"), Intact = ChainStep.Of(TestResultClass.PASS, "ok") } };
            var chainAfter = Report(Simple("store-ct", TestResultClass.PASS));
            chainAfter.Chain = new List<ChainRow> { new ChainRow { ObjectType = "ct", DisplayName = "CT", Accepted = ChainStep.Of(TestResultClass.PASS, "ok"), Intact = ChainStep.Of(TestResultClass.FAIL, "Pixels changed.") } };
            var chain = Compare(chainBefore, chainAfter);
            Check("A chain step that got worse: a regression that names the object type and the step", chain.Regressions == 1 && chain.Runs[0].Findings.Single().Subject == "CT: intact", Findings(chain));

            var three = RunComparer.Compare(new[] { new NamedRun("a", Report(Load(100, 20))), new NamedRun("b", Report(Load(100, 20))), new NamedRun("c", Report(Load(50, 20))) });
            Check("Three runs: each against the first", three.Runs.Count == 2 && three.Runs[0].Regressions == 0 && three.Runs[1].Regressions == 1 && three.Regressions == 1 && three.Summary.StartsWith("1 regression with a threshold of 10 %"), three.Summary);

            Check("Threshold: 0, a negative number and more than 1000 are refused", new ComparisonOptions { ThresholdPercent = 0 }.Validate() != null && new ComparisonOptions { ThresholdPercent = -1 }.Validate() != null
                && new ComparisonOptions { ThresholdPercent = 1001 }.Validate() != null && new ComparisonOptions { ThresholdPercent = 0.5 }.Validate() == null, null);

            var ramp = new Func<double, TestResult>(p95At4 =>
            {
                var r = Load(100, 20);
                r.TestId = "load-ramp";
                r.Performance.Mode = LoadMode.Ramp;
                r.Performance.Steps = new List<RampStep> { new RampStep { Step = 1, Associations = 1, Summary = Summary(40, 20) }, new RampStep { Step = 2, Associations = 4, Summary = Summary(90, p95At4) } };
                return r;
            });
            var rampDelta = Compare(Report(ramp(25)), Report(ramp(60)));
            Check("A ramp is compared step by step, by the number of associations", rampDelta.Regressions == 1 && rampDelta.Runs[0].Findings.Single().Subject.Contains("4 associations"), Findings(rampDelta));

            // The fingerprint.
            var echo = Simple("c-echo", TestResultClass.PASS);
            echo.Timeline.Add(new ProtocolEvent { Direction = ProtocolEvent.Received, Type = "A-ASSOCIATE-AC", Detail = "Accepted 1 of 1. Max PDU 16384, implementation 1.2.826.0.1.3680043.9.1 ORTHANC 1.12" });
            var print = VendorFingerprint.From(Report(echo));
            Func<List<FingerprintItem>, string, string> value = (items, key) => items.Single(i => i.Key == key).Value;
            Check("Fingerprint: without the probe, the implementation comes from an A-ASSOCIATE-AC", value(print, "implementation-class-uid") == "1.2.826.0.1.3680043.9.1"
                && value(print, "implementation-version-name") == "ORTHANC 1.12" && value(print, "max-pdu") == "16384" && print.Single(i => i.Key == "implementation-class-uid").Source == "c-echo", value(print, "implementation-version-name"));
            Check("Fingerprint: what no test of the run could tell has no value", value(print, "association-limit") == null && value(print, "commitment-style") == null && value(print, "transcoding") == null, null);
            var probed = VendorFingerprint.From(Report(Probe(CapabilityResult.Accepted, CapabilityResult.Accepted, CapabilityResult.TransferSyntaxNotSupported)));
            Check("Fingerprint: with the probe, from the matrix", value(probed, "implementation-class-uid") == "1.2.3.4" && value(probed, "implementation-version-name") == "MOCK_1"
                && value(probed, "combinations-accepted") == "2 of 3" && value(probed, "sop-classes-accepted") == "1 of 1 asked about", value(probed, "combinations-accepted"));
            Check("Fingerprint: keys are unique", print.Select(i => i.Key).Distinct().Count() == print.Count, null);

            var vendorA = Report(Probe(CapabilityResult.Accepted));
            var vendorB = Report(Probe(CapabilityResult.Accepted));
            vendorB.Results[0].CapabilityMatrix.RemoteImplementationVersionName = "OTHER_2";
            var vendors = Compare(vendorA, vendorB);
            Check("Another Implementation Version Name: a difference in behaviour, not a regression", vendors.Regressions == 0 && vendors.Runs[0].Findings.Single().Area == "Behaviour"
                && vendors.Runs[0].Findings.Single().Value == "OTHER_2", Findings(vendors));
        }

        // ---------------------------------------------------------------- loader

        private static void TestLoader()
        {
            Console.WriteLine("\nReporting: reading a result file back");

            var report = HostileReport();
            var path = Path.Combine(_temp, "round-trip.json");
            File.WriteAllText(path, ResultFileWriter.ToJson(report), new UTF8Encoding(false));
            var loaded = RunFile.Load(path);
            Check("A run comes back from its JSON file as it was written", ResultFileWriter.ToJson(loaded) == ResultFileWriter.ToJson(report), TestSuiteTests.FirstDifference(ResultFileWriter.ToJson(loaded), ResultFileWriter.ToJson(report)));

            Func<string, string, string> refused = (name, content) =>
            {
                var file = Path.Combine(_temp, name);
                if (content != null) File.WriteAllText(file, content, new UTF8Encoding(false));
                try { RunFile.Load(file); return null; } catch (RunFileException ex) { return ex.Message; }
            };
            var newer = refused("newer.json", ResultFileWriter.ToJson(report).Replace("\"schemaVersion\": 6", "\"schemaVersion\": 7"));
            Check("A newer schema version is refused, and the message says which", newer != null && newer.Contains("schema version 7") && newer.Contains("up to 6"), newer);
            Check("A file that is not JSON is refused", (refused("text.json", "not json at all") ?? string.Empty).Contains("is not a DCST4 result file"), null);
            Check("A capability matrix file is refused as a run", (refused("matrix.json", Matrix(CapabilityResult.Accepted).ToJson()) ?? string.Empty).Contains("is not a DCST4 result file"), refused("matrix.json", null));
            Check("A profile file is refused as a run", (refused("profile.json", "{ \"name\": \"x\", \"endpoint\": { \"host\": \"h\", \"port\": 104 } }") ?? string.Empty).Contains("is not a DCST4 result file"), null);
            Check("A missing file is refused", (refused("none.json", null) ?? string.Empty).Contains("Could not read"), null);
            var older = refused("older.json", "{ \"schemaVersion\": 1, \"toolVersion\": \"1.3.0\", \"profile\": { \"host\": \"h\", \"port\": 104 }, \"results\": [ { \"testId\": \"c-echo\", \"result\": \"PASS\" } ] }");
            Check("A file of schema version 1 is read, and gives a page", older == null && Scan(RunReportWriter.Render(RunFile.Load(Path.Combine(_temp, "older.json"))), out _).Count == 0, older);

            var names = ReportFiles.LoadRuns(new[] { path, path }).Select(r => r.Name).ToList();
            Check("The same file twice gets two names", names.Distinct().Count() == 2, string.Join(", ", names));
        }

        // ---------------------------------------------------------------- real runs

        private static TargetProfile Profile() => new TargetProfile
        {
            Name = "Mock",
            Endpoint = new DicomEndpoint { Host = "127.0.0.1", Port = _port, CallingAe = "DCST4", CalledAe = "MOCKSCP" },
            Network = new NetworkOptions { ConnectionTimeoutSeconds = 5, AssociationTimeoutSeconds = 5, ResponseTimeoutSeconds = 15 },
            LocalScpAe = "DCST4",
            LocalScpPort = ListenPort,
            UidRoot = Root,
        };

        private static LoadSettings SmallLoad(int count) => new LoadSettings
        {
            MixOverride = new DataMix("ct64", "CT 64 x 64 for the tests", DicomTransferSyntax.ExplicitVRLittleEndian, new DataMixEntry("ct", 64, 64, 16)) { PoolPerEntry = 2 },
            Mix = "ct64", SampleEvery = 0, WarmUpSeconds = 0, Associations = 2, InstanceCount = count,
        };

        private static async Task<ResultFilePaths> RunAndSave(string tests, string folder, TestRunOptions options)
        {
            var selected = TestCatalog.Select(tests, out var error);
            if (selected == null) throw new InvalidOperationException(error);
            options.MetricsFolder = folder;
            var report = await new TestRunner().RunAsync(Profile(), selected, CancellationToken.None, TestPatientNaming.RegistrySwedishName, options);
            var files = ReportFiles.WriteRun(report, folder, out var problem);
            if (problem != null) throw new InvalidOperationException(problem);
            return files;
        }

        private static async Task TestRichRun()
        {
            Console.WriteLine("\nReporting: the report of a real run against the mock SCP");

            Scp.Reset();
            Scp.KeepStored = true;
            Scp.MoveDestinations["DCST4"] = Tuple.Create("127.0.0.1", ListenPort);
            Scp.Commit = CommitMode.SameIfOpen;
            Scp.CommitDestinations["DCST4"] = Tuple.Create("127.0.0.1", ListenPort, "DCST4");
            Scp.MaxAssociations = 3;

            var folder = Path.Combine(_temp, "rich");
            var files = await RunAndSave("negotiation-probe,negotiation-unknown-calling-ae,store-ct,query-index,retrieve-cget,verify-integrity,commit-stored,limit-max-associations,load-throughput", folder,
                new TestRunOptions { Seed = 7, IndexWaitSeconds = 1, CommitLingerSeconds = 1, CommitWaitSeconds = 3, CommitGraceSeconds = 1, MaxAssociations = 6, Load = SmallLoad(30) });
            Scp.Reset();

            var report = RunFile.Load(files.Json);
            var classes = string.Join(", ", report.Results.Select(r => r.TestId + " " + r.Result));
            Check("The run did what the report needs: a matrix, a chain, integrity, commitment, a limit, a load and one failure",
                report.Results.Any(r => r.CapabilityMatrix != null) && report.Chain != null && report.Results.Any(r => r.Integrity != null) && report.Results.Any(r => r.Commitment?.ReportVia != null)
                && report.Results.Any(r => r.Limits?.AssociationLimit == 3) && report.Results.Any(r => r.Performance?.Summary?.Stored == 30)
                && report.Results.Single(r => r.TestId == "negotiation-unknown-calling-ae").Result == TestResultClass.FAIL, classes);

            Check("The report is written beside the JSON file, with the same name", files.Html == Path.ChangeExtension(files.Json, ".html") && File.Exists(files.Html), files.Html);
            var html = File.ReadAllText(files.Html, Encoding.UTF8);
            var problems = Scan(html, out var tags);
            Check("No external references: only our own tags and attributes, and no link out of the page", problems.Count == 0, string.Join(" | ", problems.Take(6)));
            Check("No external references: no script, style sheet, image or frame from outside", !new[] { "<script", "<link", "<img", "<iframe", "<object", "<embed", "<base", "@import", "url(" }
                .Any(t => html.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) && HasPolicy(html), null);
            Check("The file is UTF-8 without a byte order mark", File.ReadAllBytes(files.Html).Take(3).SequenceEqual(Encoding.ASCII.GetBytes("<!D")), null);
            Check("Every section is there", new[] { "target", "summary", "fingerprint", "capability", "chain", "integrity", "limits", "commitment", "performance", "timelines" }
                .All(id => html.Contains("<section id=\"" + id + "\">")), null);
            Check("The target, the tool version and the result classes", html.Contains("MOCKSCP") && html.Contains("127.0.0.1:" + _port) && html.Contains(ProductInfo.Version)
                && html.Contains("<div class=\"label\">EXPECTED_FAILURE</div>") && html.Contains("<div class=\"label\">TOOL_ERROR</div>"), null);
            Check("The matrix has a row for CT with accepted cells", html.Contains("CT Image Storage") && html.Contains("class=\"cell c-accepted\""), null);
            Check("The chart of the run is drawn from the series file", tags.Contains("svg") && (tags.Contains("polyline") || tags.Contains("circle")) && html.Contains("Stored and failed per second") && html.Contains("<title>Second 1: "), null);
            Check("The timeline is there for the failed test, and only for it", html.Contains("<h3><code>negotiation-unknown-calling-ae</code>") && html.Contains("A-ASSOCIATE-AC") && !html.Contains("<h3><code>store-ct</code>"), null);
            Check("Limits and Storage Commitment say what was found", html.Contains("Association limit") && html.Contains("LocalLimitExceeded") && html.Contains("on the association of the N-ACTION"), null);

            var print = VendorFingerprint.From(report);
            Func<string, string> value = key => print.Single(i => i.Key == key).Value;
            Check("Fingerprint of the mock: fo-dicom's implementation, the limit, the commitment style and the transcoding",
                !string.IsNullOrEmpty(value("implementation-class-uid")) && value("association-limit") == "3" && value("association-limit-refusal").StartsWith("A-ASSOCIATE-RJ")
                && value("commitment-style") == "on the association of the N-ACTION" && value("transcoding").StartsWith("every object came back in the transfer syntax it was sent in")
                && value("coercion") != null && value("unknown-calling-ae").StartsWith("accepted"), string.Join(" | ", print.Where(i => i.Value != null).Select(i => i.Key + "=" + i.Value)));
            Check("The loaded run gives the same fingerprint and the same sections as the page that was written", Scan(RunReportWriter.Render(report, new RunReportOptions { RunFolder = folder }), out var again).Count == 0 && again.SetEquals(tags), null);

            // Without the series file the report still stands, and says what is missing.
            File.Delete(report.Results.Single(r => r.Performance != null).Performance.SeriesFile);
            var without = RunReportWriter.Render(report, new RunReportOptions { RunFolder = folder });
            Check("Without the series file: no chart, a note, and the table", !without.Contains("Stored and failed per second") && without.Contains("series file of this test was not found") && without.Contains("Latency p50"), null);
        }

        private static async Task TestErrorCommentEndToEnd()
        {
            Console.WriteLine("\nReporting: markup in an Error Comment, through the command line");

            Scp.Reset();
            Scp.StatusFor = _ => DicomStatus.StorageStorageOutOfResources;
            Scp.ErrorCommentFor = _ => EvilComment;
            var folder = Path.Combine(_temp, "comment");
            Directory.CreateDirectory(folder);
            var profilePath = Path.Combine(folder, "target.json");
            Profile().Save(profilePath);

            var run = await TestSuiteTests.RunCli("run", "--profile", profilePath, "--tests", "store-ct", "--out", folder, "--log-dir", Path.Combine(folder, "logs"), "--seed", "3");
            Scp.Reset();
            var json = Directory.GetFiles(folder, "DCST4-run-*.json").SingleOrDefault();
            var jsonText = json == null ? string.Empty : File.ReadAllText(json, Encoding.UTF8);
            Check("The run: FAIL, exit code 1, and the Error Comment is in the result file as it was sent", run.ExitCode == 1 && run.Output.Contains("store-ct: FAIL") && jsonText.Contains("<script>alert('x')</script>"), run.Output);
            Check("The run writes the report by itself and says where", json != null && File.Exists(Path.ChangeExtension(json, ".html")) && run.Output.Contains("HTML: " + Path.ChangeExtension(json, ".html")), run.Output);
            if (json == null) return;

            var target = Path.Combine(folder, "by-report", "report.html");
            var made = await TestSuiteTests.RunCli("report", "--run", json, "--out", target);
            Check("report: exit code 0, and it says where the file is", made.ExitCode == 0 && made.Output.Contains("HTML: " + target) && File.Exists(target), made.Output);

            foreach (var path in new[] { Path.ChangeExtension(json, ".html"), target })
            {
                var html = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;
                var problems = Scan(html, out var tags);
                var name = path == target ? "report" : "run";
                Check($"{name}: the Error Comment is shown as text", html.Contains(EvilCommentAsText), null);
                Check($"{name}: and is no element of the page", problems.Count == 0 && !tags.Contains("script") && !tags.Contains("img") && html.IndexOf("<script", StringComparison.OrdinalIgnoreCase) < 0
                    && html.IndexOf("<img", StringComparison.OrdinalIgnoreCase) < 0, string.Join(" | ", problems.Take(4)));
            }

            var byDefault = await TestSuiteTests.RunCli("report", "--run", json);
            Check("report without --out: beside the JSON file", byDefault.ExitCode == 0 && byDefault.Output.Contains("HTML: " + Path.ChangeExtension(json, ".html")), byDefault.Output);

            Func<string, TestSuiteTests.CliRun, string, bool> refused = (name, result, expected) =>
            {
                var ok = result.ExitCode == RunExitCodes.Usage && result.Output.Contains(expected);
                Check(name, ok, result.ExitCode + " " + result.Output);
                return ok;
            };
            refused("report without --run: exit code 2", await TestSuiteTests.RunCli("report"), "--run");
            refused("report on a missing file: exit code 2", await TestSuiteTests.RunCli("report", "--run", Path.Combine(folder, "none.json")), "Could not read");
            refused("compare with one run: exit code 2", await TestSuiteTests.RunCli("compare", "--runs", json), "at least two");
            refused("compare with a threshold of 0: exit code 2", await TestSuiteTests.RunCli("compare", "--runs", json, json, "--threshold", "0"), "threshold");
        }

        private static async Task TestSelfCompare()
        {
            Console.WriteLine("\nReporting: a run compared with itself, and with a second identical run");

            var folder = Path.Combine(_temp, "self");
            Scp.Reset();
            var first = await RunAndSave("c-echo,negotiation-probe,store-ct", Path.Combine(folder, "first"), new TestRunOptions { Seed = 5 });
            Scp.Reset();
            var second = await RunAndSave("c-echo,negotiation-probe,store-ct", Path.Combine(folder, "second"), new TestRunOptions { Seed = 5 });
            Scp.Reset();

            var target = Path.Combine(folder, "self.html");
            var self = await TestSuiteTests.RunCli("compare", "--runs", first.Json, first.Json, "--out", target);
            Check("A run with itself: exit code 0 and no differences", self.ExitCode == 0 && self.Output.Contains("No differences.") && !self.Output.Contains("REGRESSION"), self.Output);
            var page = File.Exists(target) ? File.ReadAllText(target, Encoding.UTF8) : string.Empty;
            Check("A run with itself: the page says so and is complete in itself", page.Contains("No differences.") && page.Contains("class=\"verdict ok\"") && Scan(page, out _).Count == 0 && HasPolicy(page), null);

            var a = RunFile.Load(first.Json);
            var b = RunFile.Load(second.Json);
            Check("The two runs do differ in what belongs to one run", a.StartedUtc != b.StartedUtc && File.ReadAllText(first.Json) != File.ReadAllText(second.Json), null);
            var twice = await TestSuiteTests.RunCli("compare", "--runs", first.Json, second.Json);
            Check("Two identical runs: exit code 0 and no differences", twice.ExitCode == 0 && twice.Output.Contains("No differences."), twice.Output);
            Check("Without --out the page goes in the folder of the last run", Directory.GetFiles(Path.Combine(folder, "second"), "DCST4-compare-*.html").Length == 1 && twice.Output.Contains("HTML: " + Path.Combine(folder, "second", "DCST4-compare-")), twice.Output);
        }

        private static async Task TestScriptedRegression()
        {
            Console.WriteLine("\nReporting: a scripted capability loss and a slower p95, through the command line");

            Func<bool, ScpContextTable> table = withLossless =>
            {
                var t = new ScpContextTable { Default = ScpAnswer.Reject(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported) };
                t.Set(DicomUID.Verification, ScpAnswer.Accept);
                t.Set(DicomUID.CTImageStorage, ScpAnswer.Reject(DicomPresentationContextResult.RejectTransferSyntaxesNotSupported));
                t.Set(DicomUID.CTImageStorage, DicomTransferSyntax.ExplicitVRLittleEndian, ScpAnswer.Accept);
                t.Set(DicomUID.CTImageStorage, DicomTransferSyntax.ImplicitVRLittleEndian, ScpAnswer.Accept);
                if (withLossless) t.Set(DicomUID.CTImageStorage, DicomTransferSyntax.JPEGProcess14SV1, ScpAnswer.Accept);
                return t;
            };

            var folder = Path.Combine(_temp, "regression");
            Func<string, bool, int, Task<ResultFilePaths>> run = async (name, withLossless, delayMs) =>
            {
                Scp.Reset();
                Scp.RecordReceived = false;
                Scp.ReceiveInMemory = true;
                Scp.ContextTable = table(withLossless);
                Scp.ResponseDelayMs = delayMs;
                // Collected first, so that a full collection left over from the sections before does not land in one of the two runs.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                return await RunAndSave("negotiation-probe,load-throughput", Path.Combine(folder, name), new TestRunOptions { Seed = 9, Load = SmallLoad(24) });
            };

            // 30 ms and 90 ms: three times slower, which a threshold of 500 % lets through and the default does not.
            var before = await run("before", true, 30);
            var after = await run("after", false, 90);
            var slowOnly = await run("slow-only", true, 90);
            Scp.Reset();

            var a = RunFile.Load(before.Json);
            var b = RunFile.Load(after.Json);
            var p95Before = a.Results.Single(r => r.Performance != null).Performance.Summary.Latency.P95Ms;
            var p95After = b.Results.Single(r => r.Performance != null).Performance.Summary.Latency.P95Ms;
            Check("The script worked: the lossless cell is gone and p95 is at least 60 ms higher",
                a.Results[0].CapabilityMatrix.Find(DicomUID.CTImageStorage.UID, DicomTransferSyntax.JPEGProcess14SV1.UID.UID).Result == CapabilityResult.Accepted
                && b.Results[0].CapabilityMatrix.Find(DicomUID.CTImageStorage.UID, DicomTransferSyntax.JPEGProcess14SV1.UID.UID).Result == CapabilityResult.TransferSyntaxNotSupported
                && a.Results.All(r => r.Result == TestResultClass.PASS) && b.Results.All(r => r.Result == TestResultClass.PASS) && p95After - p95Before >= 55 && p95After < p95Before * 5,
                $"p95 {p95Before} and {p95After}, " + string.Join(", ", b.Results.Select(r => r.TestId + " " + r.Result + " " + r.Explanation)));

            var target = Path.Combine(folder, "compare.html");
            var compared = await TestSuiteTests.RunCli("compare", "--runs", before.Json, after.Json, "--out", target);
            Check("Capability loss and slower p95: exit code 1", compared.ExitCode == 1, compared.ExitCode + " " + compared.Output);
            Check("The capability loss is named as a regression", compared.Output.Split('\n').Any(l => l.Contains("REGRESSION") && l.Contains("Capability: CT Image Storage") && l.Contains("no longer accepted") && l.Contains("TransferSyntaxNotSupported")), compared.Output);
            Check("The slower p95 is named as a regression, with its percentage", compared.Output.Split('\n').Any(l => l.Contains("REGRESSION") && l.Contains("load-throughput, run: Latency p95") && l.Contains("+") && l.Contains("%")), compared.Output);
            Check("The last line counts them against the threshold", compared.Output.Contains("regressions with a threshold of 10 %"), compared.Output);

            var page = File.Exists(target) ? File.ReadAllText(target, Encoding.UTF8) : string.Empty;
            Check("The page: the verdict, the regressions marked, and complete in itself", page.Contains("class=\"verdict bad\"") && page.Contains("<tr class=\"regression\">") && page.Contains("Latency p95")
                && page.Contains("no longer accepted") && page.Contains("Vendor fingerprint, side by side") && Scan(page, out _).Count == 0 && HasPolicy(page), string.Join(" | ", Scan(page, out _).Take(4)));

            var high = await TestSuiteTests.RunCli("compare", "--runs", before.Json, after.Json, "--threshold", "500", "--out", Path.Combine(folder, "high.html"));
            Check("A threshold of 500 %: the capability loss still gives exit code 1, and the latency no longer counts", high.ExitCode == 1 && high.Output.Contains("Capability: CT Image Storage")
                && !high.Output.Split('\n').Any(l => l.Contains("REGRESSION") && l.Contains("Latency p95")), high.Output);

            var slow = await TestSuiteTests.RunCli("compare", "--runs", before.Json, slowOnly.Json, "--out", Path.Combine(folder, "slow.html"));
            Check("Only slower: exit code 1 at the default threshold, for the latency and the throughput", slow.ExitCode == 1 && !slow.Output.Contains("Capability:") && slow.Output.Contains("Latency p95") && slow.Output.Contains("Stored per second"), slow.Output);
            var tolerant = await TestSuiteTests.RunCli("compare", "--runs", before.Json, slowOnly.Json, "--threshold", "500", "--out", Path.Combine(folder, "tolerant.html"));
            Check("Only slower, with a threshold of 500 %: no regression, exit code 0", tolerant.ExitCode == 0 && !tolerant.Output.Contains("REGRESSION") && tolerant.Output.Contains("No differences."), tolerant.ExitCode + " " + tolerant.Output);

        }
    }
}
