using DCST4.Core.Capability;
using DCST4.Core.Network;
using DCST4.Core.Testing;
using DCST4.Core.Verification;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DCST4.Core.Reporting
{
    /// <summary>One thing a run found out about how the system under test behaves.</summary>
    public sealed class FingerprintItem
    {
        /// <summary>Stable across versions: two runs are compared by it.</summary>
        public string Key { get; set; }
        public string Group { get; set; }
        public string Label { get; set; }
        /// <summary>Null when no test of the run could say.</summary>
        public string Value { get; set; }
        /// <summary>The test the value came from.</summary>
        public string Source { get; set; }
        /// <summary>A measured time, which differs between two runs against the same system. Shown, and never counted as a difference.</summary>
        public bool Measured { get; set; }
    }

    /// <summary>
    /// What a run says about the system under test as a product: who made it and how it behaves, as opposed to whether each
    /// test passed. Every item is read from the result file, so a saved run gives the same fingerprint as a new one.
    /// </summary>
    public static class VendorFingerprint
    {
        public const string NotTested = "not tested";

        private const string Identity = "Identity";
        private const string Negotiation = "Negotiation";
        private const string Storage = "Storage and retrieve";
        private const string Commitment = "Storage Commitment";
        private const string Limits = "Limits and faults";
        private const string Performance = "Performance";

        public static List<FingerprintItem> From(TestRunReport report)
        {
            var items = new List<FingerprintItem>();
            Func<string, TestResult> result = id => report.Results.FirstOrDefault(r => r.TestId == id);
            Action<string, string, string, string, string, bool> add = (key, group, label, value, source, measured) =>
                items.Add(new FingerprintItem { Key = key, Group = group, Label = label, Value = string.IsNullOrWhiteSpace(value) ? null : value.Trim(), Source = source, Measured = measured });

            // ---- identity
            var probe = report.Results.FirstOrDefault(r => r.CapabilityMatrix != null);
            var matrix = probe?.CapabilityMatrix;
            string classUid = matrix?.RemoteImplementationClassUid, versionName = matrix?.RemoteImplementationVersionName, identitySource = probe?.TestId;
            var maxPdu = matrix?.RemoteMaxPduLength?.ToString(CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(classUid))
            {
                // Every accepted association says who answered, so a run without the probe still knows.
                foreach (var candidate in report.Results)
                {
                    if (!TryReadAssociateAc(candidate.Timeline, out var pdu, out var uid, out var version)) continue;
                    classUid = uid;
                    versionName = version;
                    maxPdu = maxPdu ?? pdu;
                    identitySource = candidate.TestId;
                    break;
                }
            }
            add("implementation-class-uid", Identity, "Implementation Class UID", classUid, identitySource, false);
            add("implementation-version-name", Identity, "Implementation Version Name", classUid == null ? null : (string.IsNullOrEmpty(versionName) ? "(none sent)" : versionName), identitySource, false);
            add("max-pdu", Identity, "Maximum PDU length announced", maxPdu == null ? null : (maxPdu == "0" ? "0 (no limit)" : maxPdu), identitySource, false);

            // ---- negotiation
            if (matrix != null)
            {
                var answered = matrix.AllCells().Where(c => CapabilityMatrix.WasAnswered(c.Item2.Result)).ToList();
                var accepted = answered.Where(c => c.Item2.Result == CapabilityResult.Accepted).ToList();
                add("sop-classes-accepted", Negotiation, "SOP classes accepted", answered.Count == 0 ? null :
                    $"{accepted.Select(c => c.Item1.SopClassUid).Distinct().Count()} of {answered.Select(c => c.Item1.SopClassUid).Distinct().Count()} asked about", probe.TestId, false);
                add("combinations-accepted", Negotiation, "SOP class and transfer syntax combinations accepted", answered.Count == 0 ? null : $"{accepted.Count} of {answered.Count}", probe.TestId, false);

                var names = matrix.TransferSyntaxes.ToDictionary(s => s.Uid, s => s.ShortName ?? s.Name ?? s.Uid);
                var syntaxes = accepted.Select(c => c.Item2.TransferSyntaxUid).Distinct().OrderBy(u => matrix.TransferSyntaxes.FindIndex(s => s.Uid == u))
                    .Select(u => names.TryGetValue(u, out var name) ? name : u).ToList();
                add("transfer-syntaxes-accepted", Negotiation, "Transfer syntaxes accepted for at least one SOP class", answered.Count == 0 ? null : (syntaxes.Count == 0 ? "none" : string.Join(", ", syntaxes)), probe.TestId, false);

                var broken = answered.Count(c => c.Item2.Result == CapabilityResult.NotAnswered || c.Item2.Result == CapabilityResult.AcceptedOtherSyntax);
                add("negotiation-rule-breaks", Negotiation, "Answers that break the negotiation rules", answered.Count == 0 ? null : (broken == 0 ? "none" : broken.ToString(CultureInfo.InvariantCulture)), probe.TestId, false);

                if (matrix.RoleSelectionReplies.Count > 0)
                {
                    var replied = matrix.RoleSelectionReplies.Count(r => r.Replied);
                    var scp = matrix.RoleSelectionReplies.Count(r => r.Replied && r.ScpRole == true);
                    add("role-selection", Negotiation, "SCP/SCU role selection, which C-GET needs", replied == 0 ? "not answered" :
                        $"answered for {replied} of {matrix.RoleSelectionReplies.Count} storage classes, SCP role accepted for {scp}", probe.TestId, false);
                }
                else add("role-selection", Negotiation, "SCP/SCU role selection, which C-GET needs", null, null, false);

                if (matrix.ExtendedNegotiationReplies.Count > 0)
                {
                    var replied = matrix.ExtendedNegotiationReplies.Count(r => r.Replied);
                    add("extended-negotiation", Negotiation, "Extended negotiation for Query/Retrieve", replied == 0 ? "not answered" :
                        $"answered for {replied} of {matrix.ExtendedNegotiationReplies.Count} SOP classes", probe.TestId, false);
                }
                else add("extended-negotiation", Negotiation, "Extended negotiation for Query/Retrieve", null, null, false);
            }
            else
            {
                foreach (var pair in new[]
                {
                    Tuple.Create("sop-classes-accepted", "SOP classes accepted"), Tuple.Create("combinations-accepted", "SOP class and transfer syntax combinations accepted"),
                    Tuple.Create("transfer-syntaxes-accepted", "Transfer syntaxes accepted for at least one SOP class"), Tuple.Create("negotiation-rule-breaks", "Answers that break the negotiation rules"),
                    Tuple.Create("role-selection", "SCP/SCU role selection, which C-GET needs"), Tuple.Create("extended-negotiation", "Extended negotiation for Query/Retrieve"),
                })
                {
                    add(pair.Item1, Negotiation, pair.Item2, null, null, false);
                }
            }

            Func<string, string, string, string> verdict = (id, good, bad) =>
            {
                var r = result(id);
                if (r == null) return null;
                switch (r.Result)
                {
                    case TestResultClass.EXPECTED_FAILURE: return good;
                    case TestResultClass.PASS: return good;
                    case TestResultClass.FAIL: return bad;
                    case TestResultClass.NOT_SUPPORTED: return "not supported";
                    case TestResultClass.TIMEOUT: return "no answer";
                    default: return null;
                }
            };
            add("unknown-calling-ae", Negotiation, "An unknown calling AE title", verdict("negotiation-unknown-calling-ae", "refused", "accepted, or not refused with an A-ASSOCIATE-RJ"), "negotiation-unknown-calling-ae", false);
            add("wrong-called-ae", Negotiation, "A wrong called AE title", verdict("negotiation-wrong-called-ae", "refused", "accepted, or not refused with an A-ASSOCIATE-RJ"), "negotiation-wrong-called-ae", false);

            // ---- storage and retrieve
            var integrity = report.Results.FirstOrDefault(r => r.Integrity != null);
            var instances = integrity?.Integrity.Sets.SelectMany(s => s.Instances.Select(i => Tuple.Create(s, i))).ToList() ?? new List<Tuple<IntegritySet, InstanceIntegrity>>();
            if (instances.Count > 0)
            {
                var withSyntax = instances.Where(i => !string.IsNullOrEmpty(i.Item2.TransferSyntaxSent) && !string.IsNullOrEmpty(i.Item2.TransferSyntaxReturned)).ToList();
                var changed = withSyntax.Where(i => i.Item2.TransferSyntaxSent != i.Item2.TransferSyntaxReturned).ToList();
                string transcoding = null;
                if (withSyntax.Count > 0)
                {
                    transcoding = changed.Count == 0
                        ? $"every object came back in the transfer syntax it was sent in ({withSyntax.Count} compared)"
                        : $"{changed.Count} of {withSyntax.Count} objects came back in another transfer syntax: " + string.Join("; ", changed
                            .Select(i => SyntaxName(i.Item2.TransferSyntaxSent) + " \u2192 " + SyntaxName(i.Item2.TransferSyntaxReturned)).Distinct().OrderBy(t => t, StringComparer.Ordinal).Take(6));
                }
                add("transcoding", Storage, "Transfer syntax on retrieve", transcoding, integrity.TestId, false);
                add("transcoding-inference", Storage, "Where it transcodes, as far as can be told", string.Join(" ", instances.Select(i => i.Item2.TranscodingInference)
                    .Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().OrderBy(t => t, StringComparer.Ordinal).Take(3)), integrity.TestId, false);

                var differences = instances.SelectMany(i => i.Item2.Differences).ToList();
                Func<AttributeChangeClass, int> count = c => differences.Count(d => d.Class == c);
                var coercion = new List<string>();
                foreach (var pair in new[]
                {
                    Tuple.Create(AttributeChangeClass.DocumentedCoercion, "documented in Original Attributes Sequence"),
                    Tuple.Create(AttributeChangeClass.SilentModification, "changed silently"),
                    Tuple.Create(AttributeChangeClass.Truncated, "truncated"),
                    Tuple.Create(AttributeChangeClass.PrivateDropped, "private dropped"),
                    Tuple.Create(AttributeChangeClass.EmptyDropped, "empty dropped"),
                    Tuple.Create(AttributeChangeClass.VrLost, "VR lost"),
                })
                {
                    if (count(pair.Item1) > 0) coercion.Add($"{count(pair.Item1)} {pair.Item2}");
                }
                add("coercion", Storage, "Attributes changed between store and retrieve", coercion.Count == 0 ? "none" : string.Join(", ", coercion), integrity.TestId, false);

                var added = differences.Where(d => d.Class == AttributeChangeClass.Added).Select(d => d.Name ?? d.Path).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
                add("attributes-added", Storage, "Attributes the system adds", added.Count == 0 ? "none" : string.Join(", ", added.Take(12)) + (added.Count > 12 ? $" and {added.Count - 12} more" : string.Empty), integrity.TestId, false);

                var privateKept = instances.Sum(i => i.Item2.PrivateKept.Count);
                var privateDropped = instances.Sum(i => i.Item2.PrivateDropped.Count);
                add("private-tags", Storage, "Private attributes", privateKept + privateDropped == 0 ? null : (privateDropped == 0 ? "kept" : $"{privateDropped} of {privateKept + privateDropped} dropped"), integrity.TestId, false);
                add("character-set", Storage, "Specific Character Set", instances.Any(i => i.Item2.CharacterSetChanged)
                    ? "changed: " + string.Join("; ", instances.Where(i => i.Item2.CharacterSetChanged).Select(i => $"{Shown(i.Item2.CharacterSetSent)} \u2192 {Shown(i.Item2.CharacterSetReturned)}").Distinct().OrderBy(t => t, StringComparer.Ordinal).Take(4))
                    : "kept", integrity.TestId, false);

                var pixels = instances.Select(i => i.Item2.Pixel).Where(p => p != PixelVerdict.NoPixelData).ToList();
                add("pixel-data", Storage, "Pixel data", pixels.Count == 0 ? null : string.Join(", ", pixels.GroupBy(p => p).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")), integrity.TestId, false);
            }
            else
            {
                foreach (var pair in new[]
                {
                    Tuple.Create("transcoding", "Transfer syntax on retrieve"), Tuple.Create("transcoding-inference", "Where it transcodes, as far as can be told"),
                    Tuple.Create("coercion", "Attributes changed between store and retrieve"), Tuple.Create("attributes-added", "Attributes the system adds"),
                    Tuple.Create("private-tags", "Private attributes"), Tuple.Create("character-set", "Specific Character Set"), Tuple.Create("pixel-data", "Pixel data"),
                })
                {
                    add(pair.Item1, Storage, pair.Item2, null, null, false);
                }
            }

            var chain = report.Chain ?? new List<ChainRow>();
            Func<Func<ChainRow, ChainStep>, string> share = step =>
            {
                var ran = chain.Select(step).Where(s => s != null && s.Result != TestResultClass.INDETERMINATE && s.Result != TestResultClass.TOOL_ERROR).ToList();
                return ran.Count == 0 ? null : $"{ran.Count(s => s.Result == TestResultClass.PASS)} of {ran.Count} object types";
            };
            add("chain-accepted", Storage, "Object types accepted", share(r => r.Accepted), "store-*", false);
            add("chain-retrievable", Storage, "Object types that can be retrieved", share(r => r.Retrievable), "retrieve-cget, retrieve-cmove", false);
            add("retrieve-cget", Storage, "C-GET", verdict("retrieve-cget", "works", "fails"), "retrieve-cget", false);
            add("retrieve-cmove", Storage, "C-MOVE", verdict("retrieve-cmove", "works", "fails"), "retrieve-cmove", false);

            add("duplicate-identical", Storage, "The same SOP Instance UID sent twice", DuplicateVerdict(result("duplicate-identical")), "duplicate-identical", false);
            var modified = result("duplicate-modified");
            string kept = null;
            if (modified != null && (modified.Result == TestResultClass.PASS || modified.Result == TestResultClass.FAIL || modified.Result == TestResultClass.EXPECTED_FAILURE))
            {
                modified.Details.TryGetValue("attributeVersionKept", out var attributeVersion);
                modified.Details.TryGetValue("pixelVersionKept", out var pixelVersion);
                kept = modified.Result == TestResultClass.EXPECTED_FAILURE ? "refused"
                    : attributeVersion == null && pixelVersion == null ? DuplicateVerdict(modified)
                    : $"attributes of the {attributeVersion ?? "?"} version, pixels of the {pixelVersion ?? "?"} version";
            }
            add("duplicate-modified", Storage, "The same SOP Instance UID with changed content", kept, "duplicate-modified", false);
            add("conflict-demographics", Storage, "Two patients under one Study Instance UID", verdict("conflict-demographics", "refused or reconciled", "both kept unchanged"), "conflict-demographics", false);

            // ---- storage commitment
            var commit = result("commit-stored")?.Commitment;
            var commitResult = result("commit-stored");
            add("commitment-supported", Commitment, "Storage Commitment Push Model", commitResult == null ? null
                : commitResult.Result == TestResultClass.NOT_SUPPORTED ? "not supported"
                : commitResult.Result == TestResultClass.TIMEOUT ? "N-ACTION accepted, no report in time"
                : commit?.ReportVia != null ? "supported" : null, "commit-stored", false);
            add("commitment-style", Commitment, "Where the N-EVENT-REPORT comes", commit == null ? null
                : commit.ReportVia == "SameAssociation" ? "on the association of the N-ACTION"
                : commit.ReportVia == "SeparateAssociation" ? "on a new association that the system opens"
                : commit.Late ? "late, " + (commit.LateVia == "SameAssociation" ? "on the association of the N-ACTION" : "on a new association") : null, "commit-stored", false);
            add("commitment-linger", Commitment, "The association was kept open for", commit == null ? null : commit.LingerSeconds.ToString(CultureInfo.InvariantCulture) + " s", "commit-stored", false);
            add("commitment-time", Commitment, "Time from N-ACTION to the report", commit?.ReportAfterMs == null ? null : ReportFormat.Number(commit.ReportAfterMs.Value) + " ms", "commit-stored", true);
            add("commitment-unknown-instance", Commitment, "An instance that was never stored", verdict("commit-unknown-instance", "reported as failed", "reported as committed"), "commit-unknown-instance", false);

            // ---- limits and faults
            var associations = result("limit-max-associations")?.Limits;
            add("association-limit", Limits, "Simultaneous associations", associations == null ? null
                : associations.AssociationLimit.HasValue ? associations.AssociationLimit.Value.ToString(CultureInfo.InvariantCulture)
                : associations.Ceiling.HasValue && associations.AssociationsOpened >= associations.Ceiling ? $"at least {associations.Ceiling.Value.ToString(CultureInfo.InvariantCulture)}, where the test stopped" : null,
                "limit-max-associations", false);
            add("association-limit-refusal", Limits, "How the association over the limit is refused", associations?.AssociationLimit == null ? null
                : associations.RefusedWith + (associations.Rejection == null ? string.Empty : $" ({associations.Rejection})"), "limit-max-associations", false);

            var idle = result("limit-idle-timeout")?.Limits;
            add("idle-ending", Limits, "An idle association", idle?.EndedWith == null ? null : (idle.EndedWith == "still-open" ? $"still open after {idle.WaitSeconds} s" : "ended with " + idle.EndedWith), "limit-idle-timeout", false);
            add("idle-time", Limits, "Idle association ended after", idle?.EndedAfterMs == null ? null : ReportFormat.Duration(idle.EndedAfterMs.Value), "limit-idle-timeout", true);
            var artim = result("limit-artim")?.Limits;
            add("artim-ending", Limits, "A connection without an association request", artim?.EndedWith == null ? null : (artim.EndedWith == "still-open" ? $"still open after {artim.WaitSeconds} s" : "ended with " + artim.EndedWith), "limit-artim", false);
            add("artim-time", Limits, "Such a connection ended after", artim?.EndedAfterMs == null ? null : ReportFormat.Duration(artim.EndedAfterMs.Value), "limit-artim", true);

            add("max-pdu-kept", Limits, "Keeps to the maximum PDU length DCST4 announces", verdict("limit-max-pdu", "yes", "no"), "limit-max-pdu", false);
            add("oversized-pdu", Limits, "A PDU longer than it announced", verdict("limit-oversized-pdu", "refused", "stored"), "limit-oversized-pdu", false);

            foreach (var id in new[] { "fault-abort-mid-dataset", "fault-drop-mid-dataset", "fault-drop-before-response" })
            {
                var fault = result(id)?.Limits?.Fault;
                add(id, Limits, FaultLabel(id), fault == null ? null : $"kept {Words(fault.StateAfterFault.ToString())}, retry {Words(fault.Retry.ToString())}", id, false);
            }

            var tls = report.Results.Select(r => r.Limits?.Tls).FirstOrDefault(t => t != null && t.HandshakeCompleted);
            add("tls", Limits, "TLS", tls == null ? (report.Profile != null && !report.Profile.UseTls ? "not used by this profile" : null) : $"{tls.Protocol}, {tls.CipherAlgorithm} {tls.CipherStrength}", "tls-association", false);
            add("tls-client-certificate", Limits, "Client certificate from an unknown authority", verdict("tls-client-cert-wrong-ca", "refused", "accepted"), "tls-client-cert-wrong-ca", false);

            // ---- performance
            var load = result("load-throughput")?.Performance;
            add("throughput", Performance, "Throughput", load?.Summary == null || load.Summary.Stored == 0 ? null
                : $"{ReportFormat.Decimal(load.Summary.StoredPerSecond)} stored/s on {load.Settings?.Associations.ToString(CultureInfo.InvariantCulture) ?? "?"} associations, mix {load.Mix}", "load-throughput", true);
            add("latency-p95", Performance, "C-STORE latency p95", load?.Summary?.Latency == null || load.Summary.Latency.Count == 0 ? null : ReportFormat.Ms(load.Summary.Latency.P95Ms), "load-throughput", true);
            var ramp = result("load-ramp")?.Performance;
            add("saturation", Performance, "Saturation", ramp?.Saturation == null ? null
                : ramp.Saturation.Found ? $"at {ramp.Saturation.Associations} associations ({ramp.Saturation.Reason}), best throughput at {ramp.Saturation.BestAssociations}"
                : $"not reached, best throughput at {ramp.Saturation.BestAssociations} associations", "load-ramp", true);

            return items;
        }

        /// <summary>
        /// Reads "Max PDU n, implementation uid version" from the first A-ASSOCIATE-AC that the system under test sent, which is
        /// how <see cref="AssociationTimeline"/> and <see cref="RawAssociation"/> both write it.
        /// </summary>
        internal static bool TryReadAssociateAc(IEnumerable<ProtocolEvent> timeline, out string maxPdu, out string classUid, out string versionName)
        {
            maxPdu = classUid = versionName = null;
            if (timeline == null) return false;
            const string marker = ", implementation ";
            foreach (var step in timeline)
            {
                if (step == null || step.Type != "A-ASSOCIATE-AC" || step.Direction != ProtocolEvent.Received || step.Detail == null) continue;
                var at = step.Detail.LastIndexOf(marker, StringComparison.Ordinal);
                var pduAt = step.Detail.LastIndexOf("Max PDU ", at < 0 ? step.Detail.Length - 1 : at, StringComparison.Ordinal);
                if (at < 0 || pduAt < 0) continue;

                var rest = step.Detail.Substring(at + marker.Length).Trim();
                var space = rest.IndexOf(' ');
                var uid = space < 0 ? rest : rest.Substring(0, space);
                if (uid.Length == 0 || !uid.All(c => (c >= '0' && c <= '9') || c == '.')) continue;

                classUid = uid;
                versionName = space < 0 ? null : rest.Substring(space + 1).Trim();
                maxPdu = step.Detail.Substring(pduAt + "Max PDU ".Length, at - pduAt - "Max PDU ".Length).Trim();
                return true;
            }
            return false;
        }

        private static string DuplicateVerdict(TestResult result)
        {
            if (result == null) return null;
            switch (result.Result)
            {
                case TestResultClass.EXPECTED_FAILURE: return "refused";
                case TestResultClass.PASS: return "accepted";
                case TestResultClass.FAIL: return "handled in a way the test does not accept";
                default: return null;
            }
        }

        private static string FaultLabel(string id)
        {
            switch (id)
            {
                case "fault-abort-mid-dataset": return "After an A-ABORT in the middle of a dataset";
                case "fault-drop-mid-dataset": return "After a connection lost in the middle of a dataset";
                default: return "After a connection lost before the C-STORE response";
            }
        }

        private static string Shown(string value) => string.IsNullOrEmpty(value) ? "(none)" : value;

        internal static string SyntaxName(string uid) => string.IsNullOrEmpty(uid) ? "(none)" : CapabilityMatrix.DisplayName(uid);

        /// <summary>StoredWithWarning as "stored with warning".</summary>
        internal static string Words(string pascal)
        {
            var text = new System.Text.StringBuilder();
            foreach (var c in pascal)
            {
                if (char.IsUpper(c) && text.Length > 0) text.Append(' ');
                text.Append(char.ToLowerInvariant(c));
            }
            return text.ToString();
        }
    }
}
