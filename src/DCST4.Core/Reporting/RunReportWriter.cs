using DCST4.Core.Capability;
using DCST4.Core.Network;
using DCST4.Core.Performance;
using DCST4.Core.Testing;
using DCST4.Core.Verification;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace DCST4.Core.Reporting
{
    public sealed class RunReportOptions
    {
        /// <summary>The folder of the JSON file, where the series file of a load test is looked for when its recorded path is gone.</summary>
        public string RunFolder { get; set; }
        /// <summary>The name of the JSON file, shown in the header.</summary>
        public string RunName { get; set; }
    }

    /// <summary>
    /// A run as one HTML file that is complete in itself. Everything in it goes through <see cref="HtmlWriter"/>, which
    /// encodes it, because much of a result comes from the system under test: Error Comments, AE titles, version names and
    /// attribute values.
    /// </summary>
    public static class RunReportWriter
    {
        /// <summary>A failed test against a system that sends thousands of responses must not make a file of hundreds of megabytes.</summary>
        internal const int TimelineLimit = 300;
        internal const int DifferenceLimit = 200;

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>Throws IOException, UnauthorizedAccessException or the like if the file cannot be written.</summary>
        public static void Write(TestRunReport report, string path, RunReportOptions options = null)
        {
            options = options ?? new RunReportOptions();
            if (options.RunFolder == null) options.RunFolder = Path.GetDirectoryName(Path.GetFullPath(path));
            File.WriteAllText(path, Render(report, options), Utf8NoBom);
        }

        public static string Render(TestRunReport report, RunReportOptions options = null)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            options = options ?? new RunReportOptions();
            var profile = report.Profile ?? new ResultProfile();
            var results = (report.Results ?? new List<TestResult>()).Where(r => r != null).ToList();

            var html = HtmlPage.Begin($"DCST4 report: {profile.CalledAe} at {profile.Host}:{profile.Port}");
            html.Element("h1", ProductInfo.FullName + ": test report").Line();
            html.Element("p", $"{profile.CalledAe} at {profile.Host}:{profile.Port.ToString(CultureInfo.InvariantCulture)}, {ReportFormat.Time(report.StartedUtc)}", "class", "sub").Line();

            var matrix = results.Select(r => r.CapabilityMatrix).FirstOrDefault(m => m != null);
            var integrity = results.Where(r => r.Integrity != null).ToList();
            var limits = results.Where(r => r.Limits != null).ToList();
            var commitments = results.Where(r => r.Commitment != null).ToList();
            var performance = results.Where(r => r.Performance != null).ToList();
            var failed = results.Where(r => r.Result == TestResultClass.FAIL || r.Result == TestResultClass.TIMEOUT || r.Result == TestResultClass.TOOL_ERROR).ToList();

            html.Open("nav");
            Action<string, string, bool> link = (id, text, present) => { if (present) html.Element("a", text, "href", "#" + id); };
            link("summary", "Summary", true);
            link("fingerprint", "Vendor fingerprint", true);
            link("capability", "Capability matrix", matrix != null);
            link("chain", "Chain", report.Chain != null);
            link("integrity", "Integrity", integrity.Count > 0);
            link("limits", "Limits", limits.Count > 0);
            link("commitment", "Storage Commitment", commitments.Count > 0);
            link("performance", "Performance", performance.Count > 0);
            link("timelines", "Timelines of failed tests", failed.Count > 0);
            html.Close("nav").Line();

            WriteTarget(html, report, profile, options);
            WriteSummary(html, report, results);
            WriteFingerprint(html, VendorFingerprint.From(report));
            if (matrix != null) WriteCapability(html, matrix);
            if (report.Chain != null) WriteChain(html, report.Chain);
            if (integrity.Count > 0) WriteIntegrity(html, integrity);
            if (limits.Count > 0) WriteLimits(html, limits);
            if (commitments.Count > 0) WriteCommitment(html, commitments);
            if (performance.Count > 0) WritePerformance(html, performance, options);
            if (failed.Count > 0) WriteTimelines(html, failed);

            return HtmlPage.End(html);
        }

        private static void Section(HtmlWriter html, string id, string heading) => html.Open("section", "id", id).Line().Element("h2", heading).Line();

        // ---------------------------------------------------------------- target and summary

        private static void WriteTarget(HtmlWriter html, TestRunReport report, ResultProfile profile, RunReportOptions options)
        {
            Section(html, "target", "Target and run");
            html.Open("table", "class", "kv");
            html.Pair("Profile", profile.Name);
            html.Pair("Called AE title", profile.CalledAe);
            html.Pair("Calling AE title", profile.CallingAe);
            html.Pair("Host and port", $"{profile.Host}:{profile.Port.ToString(CultureInfo.InvariantCulture)}");
            html.Pair("TLS", profile.UseTls ? (profile.TlsIgnoreCertErrors ? "yes, certificate errors accepted" : "yes") : "no");
            html.Pair("Local SCP", $"{profile.LocalScpAe} on port {profile.LocalScpPort.ToString(CultureInfo.InvariantCulture)}");
            html.Pair("UID root", profile.UidRoot);
            html.Pair("Timeouts", $"connection {profile.ConnectionTimeoutSeconds} s, association {profile.AssociationTimeoutSeconds} s, response {profile.ResponseTimeoutSeconds} s");
            html.Pair("DCST4 version", report.ToolVersion);
            html.Pair("Result file", options.RunName);
            html.Pair("Schema version", report.SchemaVersion.ToString(CultureInfo.InvariantCulture));
            html.Pair("Started", ReportFormat.Time(report.StartedUtc));
            html.Pair("Ended", ReportFormat.Time(report.EndedUtc));
            html.Pair("Duration", ReportFormat.Duration(report.DurationMs));
            html.Pair("Seed", report.Seed?.ToString(CultureInfo.InvariantCulture));
            if (report.Cancelled) html.Pair("Cancelled", "yes, the run was cancelled before it finished");
            html.Close("table").Close("section").Line();
        }

        private static void WriteSummary(HtmlWriter html, TestRunReport report, List<TestResult> results)
        {
            Section(html, "summary", "Summary");
            html.Open("div", "class", "tiles");
            foreach (TestResultClass value in Enum.GetValues(typeof(TestResultClass)))
            {
                var count = results.Count(r => r.Result == value);
                html.Open("div", "class", "tile " + HtmlPage.ResultClass(value))
                    .Element("div", count.ToString(CultureInfo.InvariantCulture), "class", "value").Element("div", value.ToString(), "class", "label").Close("div");
            }
            html.Close("div").Line();
            if (results.Count == 0) html.Element("p", "The run has no results.", "class", "muted");

            html.Open("div", "class", "scroll").Open("table").HeaderRow("Test", "Name", "Category", "Result", "Raw status", "Duration#", "Explanation");
            html.Open("tbody");
            foreach (var r in results)
            {
                html.Open("tr");
                html.Open("td", "class", "id").Element("code", r.TestId).Close("td");
                html.Cell(r.Name).Cell(r.Category.ToString());
                html.Open("td").Badge(r.Result).Close("td");
                html.Cell(string.Join(", ", (r.StatusCodes ?? new List<DimseStatusRecord>()).Where(s => s != null).Select(s => s.Operation + " " + s.Code).Distinct().Take(8)), "mono");
                html.Num(ReportFormat.Duration(r.DurationMs));
                html.Open("td", "class", "wrap").Text(r.Explanation);
                var details = ReportFormat.Ordered(r.Details).ToList();
                var uids = ReportFormat.Ordered(r.Uids).ToList();
                if (details.Count + uids.Count > 0)
                {
                    html.Open("details").Element("summary", "Details").Open("table", "class", "kv");
                    foreach (var pair in details) html.Pair(pair.Key, pair.Value);
                    foreach (var pair in uids) html.Pair(pair.Key, pair.Value);
                    html.Close("table").Close("details");
                }
                html.Close("td").Close("tr").Line();
            }
            html.Close("tbody").Close("table").Close("div").Close("section").Line();
        }

        private static void WriteFingerprint(HtmlWriter html, List<FingerprintItem> items)
        {
            Section(html, "fingerprint", "Vendor fingerprint");
            html.Element("p", "Who answered and how it behaves, read from the tests of this run. A row says \"not tested\" when no test of the run could tell. Measured times differ from run to run.", "class", "sub");
            html.Open("table");
            foreach (var group in items.GroupBy(i => i.Group))
            {
                html.Open("tbody").Open("tr").Element("th", group.Key, "colspan", "3").Close("tr");
                foreach (var item in group)
                {
                    html.Open("tr").Cell(item.Label);
                    if (item.Value == null) html.Element("td", VendorFingerprint.NotTested, "class", "muted");
                    else html.Cell(item.Value + (item.Measured ? " (measured)" : string.Empty), "wrap");
                    html.Open("td", "class", "muted").Text(item.Value == null ? string.Empty : item.Source).Close("td").Close("tr").Line();
                }
                html.Close("tbody");
            }
            html.Close("table").Close("section").Line();
        }

        // ---------------------------------------------------------------- capability

        internal static string CellSymbol(CapabilityResult result)
        {
            switch (result)
            {
                case CapabilityResult.Accepted: return "\u2713";
                case CapabilityResult.AbstractSyntaxNotSupported: return "A";
                case CapabilityResult.TransferSyntaxNotSupported: return "T";
                case CapabilityResult.UserRejection: return "U";
                case CapabilityResult.NoReason: return "N";
                case CapabilityResult.NotAnswered: return "!";
                case CapabilityResult.AcceptedOtherSyntax: return "\u2260";
                case CapabilityResult.AssociationRejected: return "R";
                case CapabilityResult.AssociationFailed: return "F";
                default: return "\u00B7";
            }
        }

        internal static string CellClass(CapabilityResult result)
        {
            switch (result)
            {
                case CapabilityResult.Accepted: return "c-accepted";
                case CapabilityResult.AbstractSyntaxNotSupported:
                case CapabilityResult.TransferSyntaxNotSupported:
                case CapabilityResult.UserRejection:
                case CapabilityResult.NoReason: return "c-rejected";
                case CapabilityResult.NotAnswered:
                case CapabilityResult.AcceptedOtherSyntax: return "c-broken";
                default: return "c-unknown";
            }
        }

        private static string CellMeaning(CapabilityResult result)
        {
            switch (result)
            {
                case CapabilityResult.Accepted: return "Accepted";
                case CapabilityResult.AbstractSyntaxNotSupported: return "Rejected: abstract syntax not supported";
                case CapabilityResult.TransferSyntaxNotSupported: return "Rejected: transfer syntax not supported";
                case CapabilityResult.UserRejection: return "Rejected by the user";
                case CapabilityResult.NoReason: return "Rejected, no reason given";
                case CapabilityResult.NotAnswered: return "Not answered, which the standard requires";
                case CapabilityResult.AcceptedOtherSyntax: return "Accepted with another syntax than proposed";
                case CapabilityResult.AssociationRejected: return "The whole association was refused";
                case CapabilityResult.AssociationFailed: return "The association failed before an answer";
                default: return "Not probed";
            }
        }

        private static void WriteCapability(HtmlWriter html, CapabilityMatrix matrix)
        {
            Section(html, "capability", "Capability matrix");
            html.Element("p", "What the system accepted in association negotiation, per SOP class and transfer syntax. No image was sent to find this out.", "class", "sub");

            html.Open("div", "class", "legend");
            foreach (CapabilityResult value in Enum.GetValues(typeof(CapabilityResult)))
            {
                html.Open("span").Element("span", CellSymbol(value), "class", "key " + CellClass(value)).Text(CellMeaning(value)).Close("span");
            }
            html.Close("div").Line();

            var syntaxes = matrix.TransferSyntaxes.Where(s => s?.Uid != null).ToList();
            foreach (var uid in matrix.Rows.SelectMany(r => r.Cells).Select(c => c.TransferSyntaxUid).Distinct())
            {
                if (syntaxes.All(s => s.Uid != uid)) syntaxes.Add(new MatrixSyntax { Uid = uid, Name = uid, ShortName = uid });
            }

            html.Open("div", "class", "scroll").Open("table", "class", "matrix").Open("thead").Open("tr").Element("th", "SOP class");
            foreach (var syntax in syntaxes) html.Element("th", syntax.ShortName ?? syntax.Name ?? syntax.Uid, "class", "syntax", "title", $"{syntax.Name} ({syntax.Uid})");
            html.Close("tr").Close("thead").Line().Open("tbody");
            foreach (var row in matrix.Rows)
            {
                html.Open("tr").Element("td", row.SopClassName ?? row.SopClassUid, "title", row.SopClassUid);
                foreach (var syntax in syntaxes)
                {
                    var cell = row.Cells.FirstOrDefault(c => c.TransferSyntaxUid == syntax.Uid);
                    if (cell == null)
                    {
                        html.Element("td", string.Empty, "class", "cell");
                        continue;
                    }
                    var title = CellMeaning(cell.Result) + (cell.Result == CapabilityResult.AcceptedOtherSyntax && cell.AcceptedTransferSyntaxUid != null ? ": " + VendorFingerprint.SyntaxName(cell.AcceptedTransferSyntaxUid) : string.Empty);
                    html.Element("td", CellSymbol(cell.Result), "class", "cell " + CellClass(cell.Result), "title", title);
                }
                html.Close("tr").Line();
            }
            html.Close("tbody").Close("table").Close("div").Line();

            if (matrix.Associations.Count > 0)
            {
                html.Open("details").Element("summary", $"The {matrix.Associations.Count} associations of the probe");
                html.Open("table").HeaderRow("Association#", "Contexts proposed#", "Ended", "Rejection", "Implementation Class UID", "Version Name", "Max PDU#", "Detail").Open("tbody");
                foreach (var a in matrix.Associations)
                {
                    html.Open("tr").Num(a.Index.ToString(CultureInfo.InvariantCulture) + (a.IsFeatureProbe ? " (roles and extended negotiation)" : string.Empty))
                        .Num(a.ProposedContexts.ToString(CultureInfo.InvariantCulture)).Cell(a.Ending.ToString())
                        .Cell(a.Rejection == null ? string.Empty : $"result {a.Rejection.Result}, source {a.Rejection.Source}, reason {a.Rejection.Reason}")
                        .Cell(a.RemoteImplementationClassUid, "mono").Cell(a.RemoteImplementationVersionName).Num(ReportFormat.Number(a.RemoteMaxPduLength)).Cell(a.Detail, "wrap").Close("tr").Line();
                }
                html.Close("tbody").Close("table").Close("details").Line();
            }

            if (matrix.RoleSelectionReplies.Count > 0)
            {
                html.Open("details").Element("summary", $"SCP/SCU role selection: answered for {matrix.RoleSelectionReplies.Count(r => r.Replied)} of {matrix.RoleSelectionReplies.Count} SOP classes");
                html.Open("table").HeaderRow("SOP class", "Replied", "SCU role", "SCP role").Open("tbody");
                foreach (var r in matrix.RoleSelectionReplies)
                {
                    html.Open("tr").Element("td", r.SopClassName ?? r.SopClassUid, "title", r.SopClassUid).Cell(ReportFormat.YesNo(r.Replied))
                        .Cell(r.ScuRole.HasValue ? ReportFormat.YesNo(r.ScuRole.Value) : string.Empty).Cell(r.ScpRole.HasValue ? ReportFormat.YesNo(r.ScpRole.Value) : string.Empty).Close("tr").Line();
                }
                html.Close("tbody").Close("table").Close("details").Line();
            }

            if (matrix.ExtendedNegotiationReplies.Count > 0)
            {
                html.Open("details").Element("summary", $"Extended negotiation: answered for {matrix.ExtendedNegotiationReplies.Count(r => r.Replied)} of {matrix.ExtendedNegotiationReplies.Count} SOP classes");
                html.Open("table").HeaderRow("SOP class", "Requested", "Replied", "Accepted").Open("tbody");
                foreach (var r in matrix.ExtendedNegotiationReplies)
                {
                    html.Open("tr").Element("td", r.SopClassName ?? r.SopClassUid, "title", r.SopClassUid).Cell(r.Requested, "mono").Cell(ReportFormat.YesNo(r.Replied)).Cell(r.Accepted, "mono").Close("tr").Line();
                }
                html.Close("tbody").Close("table").Close("details").Line();
            }
            html.Close("section").Line();
        }

        // ---------------------------------------------------------------- chain and integrity

        private static void WriteChain(HtmlWriter html, List<ChainRow> chain)
        {
            Section(html, "chain", "Store, query and retrieve chain");
            html.Element("p", "Per object type: was it accepted, can it be found, does it come back, is it what was sent, and did the system commit to it.", "class", "sub");
            html.Open("div", "class", "scroll").Open("table").HeaderRow("Object type", "Transfer syntax", "Accepted", "Indexed", "Retrievable", "Intact", "Committed", "What did not pass").Open("tbody");
            foreach (var row in chain.Where(r => r != null))
            {
                var steps = new[]
                {
                    Tuple.Create("Accepted", row.Accepted), Tuple.Create("Indexed", row.Indexed), Tuple.Create("Retrievable", row.Retrievable),
                    Tuple.Create("Intact", row.Intact), Tuple.Create("Committed", row.Committed),
                };
                html.Open("tr").Element("td", row.DisplayName ?? row.ObjectType, "title", row.ObjectType).Cell(VendorFingerprint.SyntaxName(row.TransferSyntaxUid));
                foreach (var step in steps) html.Open("td").Badge(step.Item2?.Result).Close("td");
                html.Open("td", "class", "wrap");
                foreach (var step in steps.Where(s => s.Item2 != null && s.Item2.Result != TestResultClass.PASS))
                {
                    html.Open("p").Element("strong", step.Item1 + ": ").Text(step.Item2.Explanation).Close("p");
                }
                html.Close("td").Close("tr").Line();
            }
            html.Close("tbody").Close("table").Close("div").Close("section").Line();
        }

        private static void WriteIntegrity(HtmlWriter html, List<TestResult> results)
        {
            Section(html, "integrity", "Integrity findings");
            html.Element("p", "What came back compared with what was sent, per object type and retrieve method. Attributes that the system only added are counted and not listed.", "class", "sub");

            foreach (var set in results.SelectMany(r => r.Integrity.Sets).Where(s => s != null))
            {
                html.Open("h3").Text($"{set.ObjectType}, {set.Method}: ").Badge(set.Result).Close("h3").Line();
                html.Open("table").HeaderRow("Level", "Result", "Explanation").Open("tbody");
                foreach (var level in set.Levels.Where(l => l != null))
                {
                    html.Open("tr").Cell(level.Level.ToString()).Open("td").Badge(level.Result).Close("td").Cell(level.Explanation, "wrap").Close("tr").Line();
                }
                html.Close("tbody").Close("table");

                html.Open("table", "class", "kv");
                html.Pair("Instances", $"{set.ReturnedCount} returned of {set.ExpectedCount} sent");
                html.Pair("Missing", string.Join(", ", set.Missing.Take(20)) + (set.Missing.Count > 20 ? $" and {set.Missing.Count - 20} more" : string.Empty));
                html.Pair("Not sent, but returned", string.Join(", ", set.Extra.Take(20)) + (set.Extra.Count > 20 ? $" and {set.Extra.Count - 20} more" : string.Empty));
                var instances = set.Instances.Where(i => i != null).ToList();
                html.Pair("Identity changes", string.Join("; ", instances.SelectMany(i => i.IdentityChanges).Take(20)));
                html.Pair("Pixel data", string.Join(", ", instances.Where(i => i.Pixel != PixelVerdict.NoPixelData).GroupBy(i => i.Pixel).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")));
                var lossy = instances.Where(i => i.MaxAbsoluteDifference.HasValue).ToList();
                if (lossy.Count > 0)
                {
                    html.Pair("Pixel difference", $"largest {ReportFormat.Decimal(lossy.Max(i => i.MaxAbsoluteDifference.Value))}, mean {ReportFormat.Decimal(lossy.Average(i => i.MeanAbsoluteDifference ?? 0))}");
                }
                html.Pair("Pixel detail", string.Join(" ", instances.Select(i => i.PixelDetail).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().Take(3)));
                html.Pair("Transfer syntax", string.Join("; ", instances.Where(i => i.TransferSyntaxSent != null)
                    .Select(i => $"sent {VendorFingerprint.SyntaxName(i.TransferSyntaxSent)}, returned {VendorFingerprint.SyntaxName(i.TransferSyntaxReturned)}").Distinct().Take(4)));
                html.Pair("Transcoding", string.Join(" ", instances.Select(i => i.TranscodingInference).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().Take(3)));
                html.Pair("Character set", string.Join("; ", instances.Where(i => i.CharacterSetChanged).Select(i => $"{i.CharacterSetSent} \u2192 {i.CharacterSetReturned}").Distinct().Take(4)));
                var kept = instances.Sum(i => i.PrivateKept.Count);
                var dropped = instances.SelectMany(i => i.PrivateDropped).ToList();
                if (kept + dropped.Count > 0) html.Pair("Private attributes", $"{kept} kept, {dropped.Count} dropped" + (dropped.Count > 0 ? ": " + string.Join(", ", dropped.Distinct().Take(20)) : string.Empty));
                var added = instances.SelectMany(i => i.Differences).Count(d => d.Class == AttributeChangeClass.Added);
                if (added > 0) html.Pair("Attributes added by the system", added.ToString(CultureInfo.InvariantCulture));
                html.Close("table").Line();

                var differences = instances.SelectMany(i => i.Differences.Where(d => d != null && d.Class != AttributeChangeClass.Added).Select(d => Tuple.Create(i, d))).ToList();
                if (differences.Count == 0) continue;
                html.Open("details").Element("summary", $"{differences.Count} attribute differences");
                html.Open("div", "class", "scroll").Open("table").HeaderRow("Attribute", "Name", "Class", "Sent", "Returned").Open("tbody");
                foreach (var entry in differences.Take(DifferenceLimit))
                {
                    var d = entry.Item2;
                    html.Open("tr").Cell(d.Path, "mono").Cell(d.Name).Cell(d.Class.ToString()).Cell(d.Original, "wrap").Cell(d.Retrieved, "wrap").Close("tr").Line();
                }
                html.Close("tbody").Close("table").Close("div");
                if (differences.Count > DifferenceLimit) html.Element("p", $"{differences.Count - DifferenceLimit} more are in the JSON file.", "class", "muted");
                html.Close("details").Line();
            }
            html.Close("section").Line();
        }

        // ---------------------------------------------------------------- limits and commitment

        private static void WriteLimits(HtmlWriter html, List<TestResult> results)
        {
            Section(html, "limits", "Limits, faults and TLS");
            foreach (var r in results)
            {
                var l = r.Limits;
                html.Open("h3").Text(r.Name + ": ").Badge(r.Result).Close("h3").Line();
                html.Open("table", "class", "kv");
                if (l.AssociationsOpened.HasValue || l.AssociationLimit.HasValue)
                {
                    html.Pair("Association limit", l.AssociationLimit.HasValue ? l.AssociationLimit.Value.ToString(CultureInfo.InvariantCulture) : $"not reached, {l.AssociationsOpened} were open when the test stopped at its ceiling of {l.Ceiling}");
                    html.Pair("The next association was refused with", l.RefusedWith + (l.Rejection == null ? string.Empty : $" ({l.Rejection})"));
                    html.Pair("Still answering a C-ECHO at the end", l.AliveAtEnd?.ToString(CultureInfo.InvariantCulture));
                }
                if (l.EndedWith != null)
                {
                    html.Pair("Waited at most", l.WaitSeconds.HasValue ? l.WaitSeconds.Value.ToString(CultureInfo.InvariantCulture) + " s" : null);
                    html.Pair("Ended with", l.EndedWith);
                    html.Pair("Ended after", l.EndedAfterMs.HasValue ? ReportFormat.Number(l.EndedAfterMs.Value) + " ms" : null);
                }
                if (l.Fault != null)
                {
                    var f = l.Fault;
                    html.Pair("Fault", f.Kind);
                    html.Pair("Sent before the fault", $"{ReportFormat.Number(f.DatasetBytesSent)} of {ReportFormat.Number(f.DatasetBytes)} bytes, in PDUs of {ReportFormat.Number(f.PduLength)}");
                    if (f.ResponseArrivedUnread.HasValue) html.Pair("A response arrived unread", ReportFormat.YesNo(f.ResponseArrivedUnread.Value));
                    html.Pair("State after the fault", f.StateAfterFault.ToString());
                    html.Pair("Retry", f.Retry + (f.RetryStatus == null ? string.Empty : " " + f.RetryStatus));
                    html.Pair("State after the retry", f.StateAfterRetry.ToString());
                }
                if (l.Tls != null)
                {
                    var t = l.Tls;
                    html.Pair("TLS handshake", t.HandshakeCompleted ? "completed" : "not completed");
                    html.Pair("Protocol and cipher", t.HandshakeCompleted ? $"{t.Protocol}, {t.CipherAlgorithm} {t.CipherStrength}, key exchange {t.KeyExchangeAlgorithm}, hash {t.HashAlgorithm}" : null);
                    html.Pair("Client certificate requested", ReportFormat.YesNo(t.ClientCertificateRequested));
                    html.Pair("Server certificate", t.ServerSubject == null ? null : $"{t.ServerSubject}, issued by {t.ServerIssuer}");
                    html.Pair("Valid", t.ServerNotBeforeUtc.HasValue && t.ServerNotAfterUtc.HasValue ? $"{ReportFormat.Time(t.ServerNotBeforeUtc.Value)} to {ReportFormat.Time(t.ServerNotAfterUtc.Value)}" : null);
                    html.Pair("Certificate problems", string.Join(", ", new[] { t.PolicyErrors == "None" ? null : t.PolicyErrors, t.ChainStatus }.Where(x => !string.IsNullOrWhiteSpace(x))));
                }
                html.Pair("Client certificate presented", l.ClientCertificate);
                html.Close("table").Line();

                if (l.PduAttempts != null && l.PduAttempts.Count > 0)
                {
                    html.Open("table").HeaderRow("DCST4 announced#", "System announced#", "PDU length sent#", "Longest sent#", "Longest received#", "Status", "Outcome").Open("tbody");
                    foreach (var a in l.PduAttempts.Where(a => a != null))
                    {
                        html.Open("tr").Num(ReportFormat.Number(a.OurMaxPdu)).Num(ReportFormat.Number(a.PeerMaxPdu)).Num(ReportFormat.Number(a.PduLengthSent))
                            .Num(ReportFormat.Number(a.LargestPduSent)).Num(ReportFormat.Number(a.LargestPduReceived)).Cell(a.Status, "mono").Cell(a.Outcome).Close("tr").Line();
                    }
                    html.Close("tbody").Close("table").Line();
                }
                html.Element("p", r.Explanation, "class", "sub").Line();
            }
            html.Close("section").Line();
        }

        private static void WriteCommitment(HtmlWriter html, List<TestResult> results)
        {
            Section(html, "commitment", "Storage Commitment");
            foreach (var r in results)
            {
                var c = r.Commitment;
                html.Open("h3").Text(r.Name + ": ").Badge(r.Result).Close("h3").Line();
                html.Open("table", "class", "kv");
                html.Pair("N-ACTION response", c.ActionStatus ?? "none");
                html.Pair("The report came", c.ReportVia == "SameAssociation" ? "on the association of the N-ACTION" : c.ReportVia == "SeparateAssociation" ? "on a new association that the system opened" : "not in time");
                html.Pair("Sent by", c.ReportCallingAe);
                html.Pair("Event Type", c.EventTypeId.HasValue ? c.EventTypeId.Value + (c.EventTypeId == 1 ? " (all committed)" : c.EventTypeId == 2 ? " (failures)" : string.Empty) : null);
                html.Pair("From N-ACTION to the report", c.ReportAfterMs.HasValue ? ReportFormat.Number(c.ReportAfterMs.Value) + " ms" : null);
                if (c.Late) html.Pair("Late", "yes, after the wait" + (c.LateVia == null ? string.Empty : ", " + (c.LateVia == "SameAssociation" ? "on the association of the N-ACTION" : "on a new association")));
                html.Pair("Settings", $"association kept open {c.LingerSeconds} s, wait {c.WaitSeconds} s, then listening {c.GraceSeconds} s");
                var instances = c.Instances.Where(i => i != null).ToList();
                html.Pair("Instances", string.Join(", ", instances.GroupBy(i => i.Outcome).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")));
                html.Pair("Reported, but never asked for", c.UnrequestedInstances.Count == 0 ? null : c.UnrequestedInstances.Count.ToString(CultureInfo.InvariantCulture));
                html.Pair("Repeated reports", c.RepeatedReports == 0 ? null : c.RepeatedReports.ToString(CultureInfo.InvariantCulture));
                html.Close("table").Line();

                var notCommitted = instances.Where(i => i.Outcome != CommitmentInstanceOutcome.Committed).ToList();
                if (notCommitted.Count > 0)
                {
                    html.Open("table").HeaderRow("Object type", "SOP Instance UID", "Outcome", "Failure Reason").Open("tbody");
                    foreach (var i in notCommitted.Take(DifferenceLimit))
                    {
                        html.Open("tr").Cell(i.NeverStored ? "(never stored)" : i.ObjectType).Cell(i.SopInstanceUid, "mono").Cell(i.Outcome.ToString())
                            .Cell(i.FailureReason == null ? string.Empty : $"{i.FailureReason} {i.FailureReasonText}").Close("tr").Line();
                    }
                    html.Close("tbody").Close("table").Line();
                }

                if (c.RejectedReports.Count > 0)
                {
                    html.Element("p", "Reports that DCST4 refused:");
                    html.Open("table").HeaderRow("Came on", "From", "Event Type#", "Answered", "Problem").Open("tbody");
                    foreach (var rejected in c.RejectedReports.Where(x => x != null))
                    {
                        html.Open("tr").Cell(rejected.Via).Cell(rejected.CallingAe).Num(rejected.EventTypeId.ToString(CultureInfo.InvariantCulture)).Cell(rejected.AnsweredStatus, "mono").Cell(rejected.Problem, "wrap").Close("tr").Line();
                    }
                    html.Close("tbody").Close("table").Line();
                }
                html.Element("p", r.Explanation, "class", "sub").Line();
            }
            html.Close("section").Line();
        }

        // ---------------------------------------------------------------- performance

        private static void WritePerformance(HtmlWriter html, List<TestResult> results, RunReportOptions options)
        {
            Section(html, "performance", "Performance");
            foreach (var r in results)
            {
                var p = r.Performance;
                html.Open("h3").Text(r.Name + ": ").Badge(r.Result).Close("h3").Line();
                html.Element("p", r.Explanation, "class", "sub").Line();

                html.Open("table", "class", "kv");
                html.Pair("Mix", $"{p.Mix}: {p.MixDescription}");
                html.Pair("Transfer syntax", VendorFingerprint.SyntaxName(p.TransferSyntaxUid));
                html.Pair("Mean object on the wire", ReportFormat.Number(p.AverageObjectBytes) + " bytes");
                html.Pair("Maximum PDU length", ReportFormat.Number(p.MaxPdu));
                html.Pair("Settings", DescribeSettings(p));
                html.Pair("Planned", $"at most {ReportFormat.Number(p.PlannedInstances)} instances, about {ReportFormat.Decimal(p.PlannedMegabytes)} MB");
                var problems = p.Summary?.Errors?.DescribeProblems();
                if (problems != null) html.Pair("Problems", problems);
                html.Pair("Stopped", p.DescribeStop());
                if (r.Details != null && r.Details.TryGetValue("firstProblem", out var firstProblem)) html.Pair("First problem", firstProblem);
                if (p.Saturation != null) html.Pair("Saturation", p.Saturation.Description);
                if (p.Degradation != null) html.Pair("Degradation", p.Degradation.Description);
                if (p.Client != null)
                {
                    html.Pair("This computer", $"processor mean {ReportFormat.Decimal(p.Client.MeanCpuPercent)} %, highest {ReportFormat.Decimal(p.Client.MaxCpuPercent)} %, on {p.Client.LogicalProcessors} logical processors. " +
                        $"Garbage collections {p.Client.Gen0Collections}/{p.Client.Gen1Collections}/{p.Client.Gen2Collections}, {(p.Client.ServerGc ? "server" : "workstation")} GC, {p.Client.GcLatencyMode}");
                }
                html.Close("table").Line();

                html.Open("div", "class", "charts");
                if (p.Mode == LoadMode.Ramp && p.Steps != null && p.Steps.Count > 0) WriteRampCharts(html, p);
                else if (p.Mode == LoadMode.Soak && p.Windows != null && p.Windows.Count > 1) WriteSoakCharts(html, p);
                else
                {
                    var series = ReadSeries(p, options);
                    if (series != null && series.Count > 0) WriteSeriesCharts(html, series);
                    else html.Element("p", "The per-second series file of this test was not found beside the result file, so there is no chart of the run over time.", "class", "muted");
                }
                html.Close("div").Line();

                var rows = new List<Tuple<string, LoadSummary, bool>>();
                if (p.Mode == LoadMode.Ramp && p.Steps != null) rows.AddRange(p.Steps.Where(s => s?.Summary != null).Select(s => Tuple.Create($"Step {s.Step}: {s.Associations} associations", s.Summary, s.Saturated)));
                else if (p.Summary != null) rows.Add(Tuple.Create(p.Mode == LoadMode.Soak ? "Soak" : "Run", p.Summary, false));
                if (p.Mode == LoadMode.Soak && p.Windows != null) rows.AddRange(p.Windows.Where(w => w?.Summary != null).Select(w => Tuple.Create($"Window {w.Window}: {w.StartSecond}-{w.EndSecond} s", w.Summary, false)));
                WriteSummaryTable(html, rows);

                if (p.TimeToAvailable != null) WriteTimeToAvailable(html, p.TimeToAvailable);
            }
            html.Close("section").Line();
        }

        private static string DescribeSettings(PerformanceResult p)
        {
            var s = p.Settings;
            if (s == null) return null;
            Func<double, string> seconds = v => v.ToString("0.##", CultureInfo.InvariantCulture) + " s";
            var parts = new List<string>();
            switch (p.Mode)
            {
                case LoadMode.Ramp:
                    parts.Add("steps of " + string.Join(", ", s.RampSteps ?? new List<int>()) + " associations");
                    parts.Add("each for " + seconds(s.RampStepSeconds));
                    break;
                case LoadMode.Soak:
                    parts.Add($"{s.Associations} associations");
                    parts.Add("for " + seconds(s.SoakSeconds));
                    parts.Add($"at {s.SoakRatePerSecond.ToString("0.##", CultureInfo.InvariantCulture)} per second");
                    parts.Add("a snapshot every " + seconds(s.SnapshotSeconds));
                    break;
                default:
                    parts.Add($"{s.Associations} associations");
                    parts.Add(s.InstanceCount > 0 ? $"{ReportFormat.Number(s.InstanceCount)} instances" : "for " + seconds(s.DurationSeconds));
                    parts.Add(s.RatePerSecond > 0 ? $"at {s.RatePerSecond.ToString("0.##", CultureInfo.InvariantCulture)} per second" : "as fast as answered");
                    break;
            }
            if (s.InstancesPerAssociation > 0) parts.Add($"a new association every {s.InstancesPerAssociation} instances");
            parts.Add("warm-up " + seconds(s.WarmUpSeconds));
            return string.Join(", ", parts);
        }

        private static void WriteSummaryTable(HtmlWriter html, List<Tuple<string, LoadSummary, bool>> rows)
        {
            if (rows.Count == 0) return;
            html.Open("div", "class", "scroll").Open("table")
                .HeaderRow("Window", "Stored#", "Stored/s#", "MB/s#", "Latency p50#", "p95#", "p99#", "Max#", "Response wait p95#", "Association setup p95#", "Not stored#", "Error rate#", "Raw statuses").Open("tbody");
            foreach (var row in rows)
            {
                var s = row.Item2;
                var latency = s.Latency ?? new LatencySummary();
                html.Open("tr").Cell(row.Item1 + (row.Item3 ? " (saturated)" : string.Empty));
                html.Num(ReportFormat.Number(s.Stored)).Num(ReportFormat.Decimal(s.StoredPerSecond)).Num(ReportFormat.Decimal(s.MegabytesPerSecond));
                html.Num(ReportFormat.Ms(latency.P50Ms)).Num(ReportFormat.Ms(latency.P95Ms)).Num(ReportFormat.Ms(latency.P99Ms)).Num(ReportFormat.Ms(latency.MaxMs));
                html.Num(s.ResponseWait == null ? string.Empty : ReportFormat.Ms(s.ResponseWait.P95Ms)).Num(s.AssociationSetup == null ? string.Empty : ReportFormat.Ms(s.AssociationSetup.P95Ms));
                var errors = s.Errors ?? new StatusClassCounts();
                var problems = new List<string>();
                if (errors.Failure > 0) problems.Add($"{errors.Failure} failure status");
                if (errors.Cancel > 0) problems.Add($"{errors.Cancel} cancel status");
                if (errors.NoResponse > 0) problems.Add($"{errors.NoResponse} no response");
                if (errors.AssociationLost > 0) problems.Add($"{errors.AssociationLost} association lost");
                if (errors.AssociationRejected > 0) problems.Add($"{errors.AssociationRejected} association rejected");
                if (errors.ConnectFailed > 0) problems.Add($"{errors.ConnectFailed} connection failed");
                html.Num(problems.Count == 0 ? "0" : string.Join(", ", problems)).Num(ReportFormat.Percent(s.ErrorRate * 100));
                html.Cell(string.Join(", ", ReportFormat.Ordered(errors.ByCode).Select(c => $"{c.Key} x{ReportFormat.Number(c.Value)}")), "mono").Close("tr").Line();
            }
            html.Close("tbody").Close("table").Close("div").Line();
        }

        private static void WriteTimeToAvailable(HtmlWriter html, TimeToAvailable t)
        {
            html.Open("table", "class", "kv");
            var text = t.QuerySupported == false ? "C-FIND was not accepted, so it could not be measured"
                : $"{t.Found} of {t.Sampled} sampled instances were found, {t.NeverFound} never" + (t.NotSettled > 0 ? $", {t.NotSettled} still being asked for when the run ended" : string.Empty);
            html.Pair("Time-to-available", text);
            if (t.Available != null && t.Available.Count > 0)
            {
                html.Pair("From C-STORE response to found", $"p50 {ReportFormat.Ms(t.Available.P50Ms)}, p95 {ReportFormat.Ms(t.Available.P95Ms)}, maximum {ReportFormat.Ms(t.Available.MaxMs)}, asked every {t.IntervalSeconds.ToString("0.##", CultureInfo.InvariantCulture)} s");
            }
            html.Pair("Note", t.Note);
            html.Close("table").Line();
        }

        private static void WriteRampCharts(HtmlWriter html, PerformanceResult p)
        {
            var steps = p.Steps.Where(s => s?.Summary != null).ToList();
            var marked = steps.FindIndex(s => s.Saturated);
            Func<string, Func<LoadSummary, double>, Func<double, string>, string, ColumnChart> chart = (title, value, label, colour) => new ColumnChart
            {
                Title = title, XLabel = "Associations", Colour = colour, Marked = marked, MarkedLabel = "saturated",
                Categories = steps.Select(s => s.Associations.ToString(CultureInfo.InvariantCulture)).ToList(),
                Values = steps.Select(s => value(s.Summary)).ToList(),
                ValueLabels = steps.Select(s => label(value(s.Summary))).ToList(),
                Hovers = steps.Select(s => $"Step {s.Step}, {s.Associations} associations: {ReportFormat.Decimal(s.Summary.StoredPerSecond)} stored/s, latency p95 {ReportFormat.Ms(s.Summary.Latency?.P95Ms ?? 0)}, error rate {ReportFormat.Percent(s.Summary.ErrorRate * 100)}").ToList(),
            };
            SvgChart.Write(html, chart("Stored per second, by ramp step", s => s.StoredPerSecond, ReportFormat.Decimal, "1"));
            SvgChart.Write(html, chart("Latency p95 in ms, by ramp step", s => s.Latency?.P95Ms ?? 0, ReportFormat.Decimal, "2"));
        }

        private static void WriteSoakCharts(HtmlWriter html, PerformanceResult p)
        {
            var windows = p.Windows.Where(w => w?.Summary != null).ToList();
            Func<SoakWindow, double> x = w => (w.StartSecond + w.EndSecond) / 2.0 / 60;
            var bands = new List<ChartBand> { new ChartBand { From = windows[0].StartSecond / 60.0, To = windows[0].EndSecond / 60.0, Label = "warm-up" } };
            var hovers = windows.Select(w => new ChartHover
            {
                From = w.StartSecond / 60.0, To = w.EndSecond / 60.0,
                Text = $"Window {w.Window}, {w.StartSecond}-{w.EndSecond} s: {ReportFormat.Decimal(w.Summary.StoredPerSecond)} stored/s, latency p50 {ReportFormat.Ms(w.Summary.Latency?.P50Ms ?? 0)}, p95 {ReportFormat.Ms(w.Summary.Latency?.P95Ms ?? 0)}",
            }).ToList();

            SvgChart.Write(html, new LineChart
            {
                Title = "Stored per second, by soak window", XLabel = "Minutes into the soak", Bands = bands, Hovers = hovers,
                Series = { new ChartSeries { Name = "Stored per second", Colour = "1", Runs = { windows.Select(w => new ChartPoint(x(w), w.Summary.StoredPerSecond)).ToList() } } },
            });
            SvgChart.Write(html, new LineChart
            {
                Title = "Latency in ms, by soak window", XLabel = "Minutes into the soak", Bands = bands, Hovers = hovers,
                Series =
                {
                    new ChartSeries { Name = "p50", Colour = "1", Runs = { windows.Select(w => new ChartPoint(x(w), w.Summary.Latency?.P50Ms ?? 0)).ToList() } },
                    new ChartSeries { Name = "p95", Colour = "2", Runs = { windows.Select(w => new ChartPoint(x(w), w.Summary.Latency?.P95Ms ?? 0)).ToList() } },
                },
            });
        }

        private static void WriteSeriesCharts(HtmlWriter html, List<LoadSecond> seconds)
        {
            // A last second that was cut short is not a rate per second, so it is left out of the lines, as on the Performance tab.
            var whole = seconds.Where(s => !s.Partial).ToList();
            if (whole.Count == 0) whole = seconds;

            // More points than pixels says nothing more. The mean of the counts and the highest p95 of each bucket keep a peak.
            var bucket = Math.Max(1, (int)Math.Ceiling(whole.Count / 600.0));
            var points = whole.GroupBy(s => s.Second / bucket).OrderBy(g => g.Key).Select(g => new LoadSecond
            {
                Second = g.Min(s => s.Second), Stored = (long)Math.Round(g.Average(s => s.Stored)), Failed = (long)Math.Round(g.Average(s => s.Failed)),
                LatencyP50Ms = g.Average(s => s.LatencyP50Ms), LatencyP95Ms = g.Max(s => s.LatencyP95Ms), OpenAssociations = g.Max(s => s.OpenAssociations),
            }).ToList();

            var bands = new List<ChartBand>();
            var warm = seconds.Where(s => s.WarmUp).ToList();
            if (warm.Count > 0) bands.Add(new ChartBand { From = warm.Min(s => s.Second), To = warm.Max(s => s.Second) + 1, Label = "warm-up" });
            var hovers = points.Select(s => new ChartHover
            {
                From = s.Second, To = s.Second + bucket,
                Text = $"Second {s.Second + 1}: {s.Stored} stored, {s.Failed} failed, latency p50 {ReportFormat.Ms(s.LatencyP50Ms)}, p95 {ReportFormat.Ms(s.LatencyP95Ms)}, {s.OpenAssociations} associations open",
            }).ToList();
            Func<Func<LoadSecond, double>, List<ChartPoint>> run = value => points.Select(s => new ChartPoint(s.Second + bucket / 2.0, value(s))).ToList();

            var throughput = new LineChart { Title = "Stored and failed per second", XLabel = "Seconds into the run", Bands = bands, Hovers = hovers };
            throughput.Series.Add(new ChartSeries { Name = "Stored", Colour = "1", Runs = { run(s => s.Stored) } });
            if (points.Any(s => s.Failed > 0)) throughput.Series.Add(new ChartSeries { Name = "Failed", Colour = "bad", Runs = { run(s => s.Failed) } });
            SvgChart.Write(html, throughput);

            SvgChart.Write(html, new LineChart
            {
                Title = "Latency in ms", XLabel = "Seconds into the run", Bands = bands, Hovers = hovers,
                Series =
                {
                    new ChartSeries { Name = "p50", Colour = "1", Runs = { run(s => s.LatencyP50Ms) } },
                    new ChartSeries { Name = "p95", Colour = "2", Runs = { run(s => s.LatencyP95Ms) } },
                },
            });
        }

        /// <summary>
        /// The per-second series of a load test: kept in memory when the report follows the run, otherwise from the file it
        /// wrote while it ran. Null if neither is there or the file cannot be read.
        /// </summary>
        internal static List<LoadSecond> ReadSeries(PerformanceResult p, RunReportOptions options)
        {
            if (p.SeriesInMemory != null && p.SeriesInMemory.Count > 0) return p.SeriesInMemory;
            if (string.IsNullOrEmpty(p.SeriesFile)) return null;
            var candidates = new List<string> { p.SeriesFile };
            try
            {
                if (options?.RunFolder != null) candidates.Add(Path.Combine(options.RunFolder, Path.GetFileName(p.SeriesFile)));
            }
            catch (ArgumentException)
            {
                // A path in the file that this computer cannot make sense of.
            }

            foreach (var path in candidates)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    string[] lines;
                    // The test may still hold the file open for writing when the report of a cancelled run is made.
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        lines = reader.ReadToEnd().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    }
                    if (lines.Length < 2) continue;

                    var columns = lines[0].Split(',').Select((name, index) => new { name, index }).ToDictionary(c => c.name.Trim(), c => c.index, StringComparer.Ordinal);
                    if (!columns.ContainsKey("second") || !columns.ContainsKey("stored")) continue;
                    var rows = new List<LoadSecond>();
                    foreach (var line in lines.Skip(1))
                    {
                        var fields = line.Split(',');
                        Func<string, double> number = name =>
                            columns.TryGetValue(name, out var index) && index < fields.Length && double.TryParse(fields[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
                        rows.Add(new LoadSecond
                        {
                            Second = (int)number("second"), Step = (int)number("step"), WarmUp = number("warm_up") != 0, Partial = number("partial") != 0,
                            OpenAssociations = (int)number("open_associations"), Stored = (long)number("stored"), Failed = (long)number("failed"),
                            LatencyP50Ms = number("latency_p50_ms"), LatencyP95Ms = number("latency_p95_ms"), LatencyMaxMs = number("latency_max_ms"),
                        });
                    }
                    return rows;
                }
                catch (Exception ex) when (RunFile.IsFileProblem(ex))
                {
                    // The chart is an extra. The numbers of the run are in the table either way.
                }
            }
            return null;
        }

        // ---------------------------------------------------------------- timelines

        private static void WriteTimelines(HtmlWriter html, List<TestResult> failed)
        {
            Section(html, "timelines", "Protocol timelines of failed tests");
            html.Element("p", "Every step of the associations of each test that ended in FAIL, TIMEOUT or TOOL_ERROR, with the raw status of each response.", "class", "sub");
            foreach (var r in failed)
            {
                html.Open("h3").Element("code", r.TestId).Text(" ").Badge(r.Result).Close("h3").Line();
                html.Element("p", r.Explanation).Line();
                var timeline = (r.Timeline ?? new List<ProtocolEvent>()).Where(e => e != null).ToList();
                if (timeline.Count == 0)
                {
                    html.Element("p", "No protocol step was recorded: nothing reached the system, or the test does not record a timeline.", "class", "muted").Line();
                    continue;
                }

                var many = timeline.Any(e => e.Association.HasValue);
                html.Open("div", "class", "scroll").Open("table");
                if (many) html.HeaderRow("Offset#", "Association#", "Direction", "Step", "Status", "Detail");
                else html.HeaderRow("Offset#", "Direction", "Step", "Status", "Detail");
                html.Open("tbody");
                foreach (var e in timeline.Take(TimelineLimit))
                {
                    html.Open("tr").Num(ReportFormat.Number(e.OffsetMs) + " ms");
                    if (many) html.Num(e.Association?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
                    html.Cell(e.Direction).Cell(e.Type + (e.DimseIndex.HasValue ? $" #{e.DimseIndex}" : string.Empty), "id").Cell(e.Status, "mono").Cell(e.Detail, "wrap").Close("tr").Line();
                }
                html.Close("tbody").Close("table").Close("div");
                if (timeline.Count > TimelineLimit) html.Element("p", $"{timeline.Count - TimelineLimit} more steps are in the JSON file.", "class", "muted");
                html.Line();
            }
            html.Close("section").Line();
        }
    }
}
