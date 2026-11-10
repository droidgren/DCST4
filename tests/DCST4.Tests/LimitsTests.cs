using DCST4.Core.Network;
using DCST4.Core.Testing;
using DCST4.ViewModels;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.IO.Buffer;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Tls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Tests
{
    /// <summary>Phase 6: limits, broken transfers and TLS refusals against the mock, which sees the connections through <see cref="ObservingAcceptor"/>.</summary>
    internal static class LimitsTests
    {
        private const int LimitsPort = 41218;
        private const int TlsPort = 41219;
        private const int ClosedPort = 41213;
        private const string ExpiredThumbprint = "00AA11BB22CC33DD44EE55FF66AA77BB88CC99DD";

        private static string _temp;
        private static Action<string, bool, string> _check;

        public static async Task Run(string temp, Action<string, bool, string> check)
        {
            _temp = temp;
            _check = check;

            using (DicomServerFactory.Create<MockStorageScp>(LimitsPort, new ObservingAcceptor()))
            {
                await TestCeiling();
                await TestTimeouts();
                await TestPdu();
                await TestFaults();
                await TestCancel();
                await TestFrontEnds();
            }
            await TestTls();
        }

        // ---------------------------------------------------------------- helpers

        private static TargetProfile Profile(int port = LimitsPort) => new TargetProfile
        {
            Name = "Mock",
            Endpoint = new DicomEndpoint { Host = "127.0.0.1", Port = port, CallingAe = "DCST4", CalledAe = "MOCKSCP" },
            Network = new NetworkOptions { ConnectionTimeoutSeconds = 5, AssociationTimeoutSeconds = 5, ResponseTimeoutSeconds = 10 },
            UidRoot = "1.2.826.0.1.3680043.10.999",
        };

        private static TestRunOptions Options(int ceiling = 10, int wait = 3, int seed = 21) => new TestRunOptions
        {
            Seed = seed,
            IndexWaitSeconds = 1,
            MaxAssociations = ceiling,
            IdleWaitSeconds = wait,
            ArtimWaitSeconds = wait,
            FaultSettleSeconds = 0,
        };

        private static async Task<TestResult> One(string test, TestRunOptions options = null, TargetProfile profile = null, CancellationToken token = default(CancellationToken))
        {
            var selected = TestCatalog.Select(test, out var error);
            if (selected == null) throw new InvalidOperationException(error);
            var report = await new TestRunner().RunAsync(profile ?? Profile(), selected, token, DCST4.Core.TestData.TestPatientNaming.RegistrySwedishName, options ?? Options());
            return report.Results.Single();
        }

        private static string Show(TestResult result) => result.Result + ": " + result.Explanation;

        private static async Task<bool> WaitFor(Func<bool> condition, int milliseconds = 3000)
        {
            var clock = Stopwatch.StartNew();
            while (!condition() && clock.ElapsedMilliseconds < milliseconds) await Task.Delay(20);
            return condition();
        }

        private static string Types(TestResult result) => string.Join(" ", result.Timeline.Select(e => e.Type));

        // ---------------------------------------------------------------- association ceiling

        private static async Task TestCeiling()
        {
            Console.WriteLine("\nLimits: simultaneous associations");

            foreach (var scripted in new[] { 4, 7 })
            {
                Scp.Reset();
                Scp.MaxAssociations = scripted;
                var found = await One("limit-max-associations");
                _check($"Ceiling {scripted}: found exactly", found.Limits?.AssociationLimit == scripted && found.Limits.AssociationsOpened == scripted, Show(found));
                _check($"Ceiling {scripted}: a proper rejection is EXPECTED_FAILURE", found.Result == TestResultClass.EXPECTED_FAILURE, Show(found));
                _check($"Ceiling {scripted}: result, source and reason recorded",
                    found.Limits?.RefusedWith == "A-ASSOCIATE-RJ" && found.Limits.Rejection?.Result == "Transient" && found.Limits.Rejection.Source == "ServiceProviderPresentation" && found.Limits.Rejection.Reason == "LocalLimitExceeded"
                    && found.Details["rejectReason"] == "LocalLimitExceeded" && found.Explanation.Contains("LocalLimitExceeded"), Show(found));
                _check($"Ceiling {scripted}: every held association still answered C-ECHO", found.Limits?.AliveAtEnd == scripted && Scp.EchoCount == scripted, Scp.EchoCount.ToString());
                _check($"Ceiling {scripted}: all released afterwards", found.Details["released"] == scripted.ToString() && Scp.Associations.Count(a => a.Released) == scripted && await WaitFor(() => Scp.OpenAssociations == 0 && Scp.OpenConnections == 0),
                    Scp.OpenAssociations + " open, " + Scp.OpenConnections + " connections");
                _check($"Ceiling {scripted}: they were open at the same time", Scp.Associations.Count == scripted + 1, Scp.Associations.Count.ToString());
                _check($"Ceiling {scripted}: the timeline shows the rejection and the releases",
                    found.Timeline.Count(e => e.Type == "A-ASSOCIATE-RJ") == 1 && found.Timeline.Count(e => e.Type == "A-RELEASE-RP") == scripted && found.Timeline.Select(e => e.Association).Distinct().Count() == scripted + 1, Types(found));
            }

            Scp.Reset();
            var reached = await One("limit-max-associations", Options(ceiling: 6));
            _check("No limit: stops at the configured ceiling with PASS", reached.Result == TestResultClass.PASS && reached.Limits.AssociationsOpened == 6 && reached.Limits.AssociationLimit == null && reached.Limits.Ceiling == 6, Show(reached));
            _check("No limit: all released", Scp.Associations.Count == 6 && Scp.Associations.All(a => a.Released) && await WaitFor(() => Scp.OpenConnections == 0), Scp.OpenConnections.ToString());

            Scp.Reset();
            Scp.MaxAssociations = 3;
            Scp.CeilingAbortsInstead = true;
            var aborted = await One("limit-max-associations");
            _check("A-ABORT in place of a rejection: FAIL with a finding, and the limit is still found",
                aborted.Result == TestResultClass.FAIL && aborted.Limits.AssociationLimit == 3 && aborted.Limits.RefusedWith == "A-ABORT" && aborted.Details["finding"] == "association-limit-without-reject", Show(aborted));

            Scp.Reset();
            Scp.RejectAssociation = true;
            var none = await One("limit-max-associations");
            _check("Not one association accepted: INDETERMINATE, with the rejection", none.Result == TestResultClass.INDETERMINATE && none.Details.ContainsKey("rejectReason") && none.Limits.AssociationLimit == null, Show(none));

            Scp.Reset();
            var closed = await One("limit-max-associations", profile: Profile(ClosedPort));
            _check("Closed port: FAIL or TIMEOUT, not a limit", (closed.Result == TestResultClass.FAIL || closed.Result == TestResultClass.TIMEOUT) && closed.Limits.AssociationLimit == null, Show(closed));
        }

        // ---------------------------------------------------------------- idle timeout and ARTIM

        private static async Task TestTimeouts()
        {
            Console.WriteLine("\nLimits: idle timeout and ARTIM");

            Scp.Reset();
            Scp.IdleTimeoutMs = 700;
            Scp.IdleEndsWithAbort = true;
            var abort = await One("limit-idle-timeout");
            _check("Idle, ended with A-ABORT: PASS, and how it ended", abort.Result == TestResultClass.PASS && abort.Limits.EndedWith == "A-ABORT", Show(abort));
            _check("Idle: the time is measured from the A-ASSOCIATE-AC", abort.Limits.EndedAfterMs >= 600 && abort.Limits.EndedAfterMs < 2500, abort.Limits.EndedAfterMs.ToString());
            _check("Idle: the explanation holds no measured time", !abort.Explanation.Any(char.IsDigit) || abort.Explanation.Contains("3 s"), abort.Explanation);
            _check("Idle: timeline ends with the abort received", abort.Timeline.Last().Type == "A-ABORT" && abort.Timeline.Last().Direction == ProtocolEvent.Received, Types(abort));

            Scp.Reset();
            Scp.IdleTimeoutMs = 700;
            var closed = await One("limit-idle-timeout");
            _check("Idle, connection just closed: recorded as such", closed.Result == TestResultClass.PASS && closed.Limits.EndedWith == "connection-closed" && closed.Details.ContainsKey("endedWithoutPdu") && closed.Limits.EndedAfterMs >= 600, Show(closed) + " " + closed.Limits.EndedWith);

            Scp.Reset();
            var open = await One("limit-idle-timeout", Options(wait: 1));
            _check("Idle, never ended: PASS, still open after the wait", open.Result == TestResultClass.PASS && open.Limits.EndedWith == "still-open" && open.Limits.EndedAfterMs == null && open.Limits.WaitSeconds == 1, Show(open));
            _check("Idle, never ended: released by DCST4", Scp.Associations.Single().Released && await WaitFor(() => Scp.OpenConnections == 0), Types(open));

            Scp.Reset();
            Scp.ArtimTimeoutMs = 700;
            var artim = await One("limit-artim");
            _check("ARTIM: closed connection is PASS with the time", artim.Result == TestResultClass.PASS && artim.Limits.EndedWith == "connection-closed" && artim.Limits.EndedAfterMs >= 600 && artim.Limits.EndedAfterMs < 2500, Show(artim) + " " + artim.Limits.EndedAfterMs);
            _check("ARTIM: no A-ASSOCIATE-RQ was ever sent", Scp.AssociationCount == 0 && !artim.Timeline.Any(e => e.Type == "A-ASSOCIATE-RQ"), Types(artim));

            Scp.Reset();
            var noArtim = await One("limit-artim", Options(wait: 1));
            _check("No ARTIM timer: FAIL as a finding", noArtim.Result == TestResultClass.FAIL && noArtim.Details["finding"] == "no-artim-timeout" && noArtim.Limits.EndedWith == "still-open", Show(noArtim));
            _check("No ARTIM timer: DCST4 closes the connection itself", await WaitFor(() => Scp.OpenConnections == 0), Scp.OpenConnections.ToString());
        }

        // ---------------------------------------------------------------- PDU length

        private static async Task TestPdu()
        {
            Console.WriteLine("\nLimits: maximum PDU length");

            Scp.Reset();
            Scp.KeepStored = true;
            Scp.MaxPduLength = 16384;
            var pdu = await One("limit-max-pdu");
            var attempts = pdu.Limits?.PduAttempts ?? new List<PduAttempt>();
            _check("Max PDU: PASS, one object per announced size", pdu.Result == TestResultClass.PASS && attempts.Count == 4 && Scp.StoreCount == 4, Show(pdu));
            _check("Max PDU: our own sizes differ", string.Join(",", attempts.Select(a => a.OurMaxPdu)) == "4096,16384,65536,131072", null);
            _check("Max PDU: the mock's announced size is recorded each time", attempts.All(a => a.PeerMaxPdu == 16384) && pdu.Details["peerMaxPdu"] == "16384", pdu.Details.ContainsKey("peerMaxPdu") ? pdu.Details["peerMaxPdu"] : null);
            _check("Max PDU: data PDUs on the wire are exactly that long, none longer", Scp.DataPdus.Count(l => l == 16384) >= 4 * 7 && Scp.DataPdus.All(l => l <= 16384), Scp.DataPdus.Max().ToString());
            _check("Max PDU: what DCST4 counted agrees with the wire", attempts.All(a => a.LargestPduSent == 16384 && a.PduLengthSent == 16384 && a.Status == "0x0000" && a.Outcome == "stored"), null);
            _check("Max PDU: nothing received was longer than announced", attempts.All(a => a.LargestPduReceived > 0 && a.LargestPduReceived <= a.OurMaxPdu), null);
            _check("Max PDU: the raw sender's objects arrive whole and readable",
                Scp.Received.Count == 4 && Scp.Received.All(r => r.Frame != null && r.Frame.Length == 256 * 256 * 2 && r.Dataset.GetSingleValue<string>(DicomTag.Manufacturer) == "DCST4" && r.Syntax == DicomTransferSyntax.ExplicitVRLittleEndian), null);
            _check("Max PDU: four different instances, UIDs under the profile's root",
                Scp.Received.Select(r => r.Dataset.GetSingleValue<string>(DicomTag.SOPInstanceUID)).Distinct().Count() == 4 && attempts.All(a => a.SopInstanceUid.StartsWith("1.2.826.0.1.3680043.10.999.")), null);
            _check("Max PDU: every association released", Scp.Associations.Count == 4 && Scp.Associations.All(a => a.Released), null);
            _check("Max PDU: Patient ID from the test list", Scp.Received.All(r => DCST4.Core.TestData.TestPersonnummerRegistry.Default.Contains(r.Dataset.GetSingleValue<string>(DicomTag.PatientID))), null);

            Scp.Reset();
            Scp.StatusFor = _ => DicomStatus.StorageStorageOutOfResources;
            var refused = await One("limit-max-pdu");
            _check("Max PDU: a refused object is FAIL with the raw status", refused.Result == TestResultClass.FAIL && refused.StatusCodes.Any(s => s.Code == "0xA700"), Show(refused));

            Scp.Reset();
            Scp.AcceptedSyntaxes = new[] { DicomTransferSyntax.JPEGProcess14SV1 };
            var unsupported = await One("limit-max-pdu");
            _check("Max PDU: no accepted context is NOT_SUPPORTED, nothing sent", unsupported.Result == TestResultClass.NOT_SUPPORTED && Scp.StoreCount == 0, Show(unsupported));

            Scp.Reset();
            Scp.AcceptedSyntaxes = new[] { DicomTransferSyntax.ImplicitVRLittleEndian };
            var implicitOnly = await One("limit-max-pdu");
            _check("Max PDU: falls back to Implicit VR when that is all that is accepted", implicitOnly.Result == TestResultClass.PASS && Scp.Received.All(r => r.Syntax == DicomTransferSyntax.ImplicitVRLittleEndian) && Scp.StoreCount == 4, Show(implicitOnly));

            Scp.Reset();
            Scp.KeepStored = true;
            Scp.MaxPduLength = 16384;
            Scp.EnforceMaxPdu = true;
            var enforced = await One("limit-oversized-pdu");
            _check("Oversized PDU refused: EXPECTED_FAILURE", enforced.Result == TestResultClass.EXPECTED_FAILURE && Scp.StoreCount == 0, Show(enforced));
            _check("Oversized PDU: 1024 bytes over the announced limit, on the wire", Scp.DataPdus.Any(l => l == 16384 + 1024) && enforced.Limits.PduAttempts.Single().PduLengthSent == 17408 && enforced.Limits.PduAttempts.Single().PeerMaxPdu == 16384, null);
            _check("Oversized PDU: how the association ended is recorded", enforced.Details.ContainsKey("endedWith") && enforced.Limits.PduAttempts.Single().Outcome == "ended", enforced.Details.ContainsKey("endedWith") ? enforced.Details["endedWith"] : null);

            Scp.Reset();
            Scp.KeepStored = true;
            Scp.MaxPduLength = 16384;
            var tolerated = await One("limit-oversized-pdu");
            _check("Oversized PDU stored: FAIL with a finding", tolerated.Result == TestResultClass.FAIL && tolerated.Details["finding"] == "accepted-oversized-pdu" && Scp.StoreCount == 1, Show(tolerated));

            Scp.Reset();
            Scp.MaxPduLength = 16384;
            Scp.StatusFor = _ => DicomStatus.ProcessingFailure;
            var status = await One("limit-oversized-pdu");
            _check("Oversized PDU refused with a status: EXPECTED_FAILURE with the raw code", status.Result == TestResultClass.EXPECTED_FAILURE && status.StatusCodes.Any(s => s.Code == "0x0110"), Show(status));
        }

        // ---------------------------------------------------------------- faults

        private static async Task TestFaults()
        {
            Console.WriteLine("\nFault injection");

            Scp.Reset();
            Scp.KeepStored = true;
            Scp.MaxPduLength = 16384;
            var abort = await One("fault-abort-mid-dataset");
            var fault = abort.Limits?.Fault ?? new FaultRecord();
            var partial = Scp.PartialStores.FirstOrDefault();
            _check("Abort mid-dataset: PASS when nothing is kept and the retry is stored", abort.Result == TestResultClass.PASS && fault.StateAfterFault == InstanceState.None && fault.Retry == RetryOutcome.Stored && fault.RetryStatus == "0x0000" && fault.StateAfterRetry == InstanceState.Complete, Show(abort));
            _check("Abort mid-dataset: the mock saw a partial store ended by A-ABORT", Scp.PartialStores.Count == 1 && partial.Ending == "abort" && partial.SopInstanceUid == abort.Uids["sopInstanceUid"], partial?.Ending);
            _check("Abort mid-dataset: about half the dataset, on a PDU boundary",
                partial != null && partial.DatasetBytes == fault.DatasetBytesSent && partial.BytesOfUnfinishedPdu == 0 && fault.DatasetBytesSent >= fault.DatasetBytes / 2 && fault.DatasetBytesSent < fault.DatasetBytes / 2 + 16384 && fault.DatasetBytes > 2000000,
                fault.DatasetBytesSent + " of " + fault.DatasetBytes);
            _check("Abort mid-dataset: only the retry reached the store handler", Scp.StoreCount == 1, Scp.StoreCount.ToString());
            _check("Abort mid-dataset: timeline has the abort sent, then the query and the retry",
                abort.Timeline.Any(e => e.Type == "A-ABORT" && e.Direction == ProtocolEvent.Sent) && abort.Timeline.Any(e => e.Type == "C-FIND-RQ") && abort.Timeline.Count(e => e.Type == "C-STORE-RQ") == 2, Types(abort));
            _check("Abort mid-dataset: raw statuses of the query and the retry", abort.StatusCodes.Any(s => s.Operation == "C-FIND") && abort.StatusCodes.Any(s => s.Operation == "C-STORE" && s.Code == "0x0000"), null);
            var complete = Scp.Received.Last().Dataset;

            var again = await One("fault-abort-mid-dataset");
            _check("Same seed again: INDETERMINATE, the instance is already there and nothing is sent", again.Result == TestResultClass.INDETERMINATE && again.Explanation.Contains("seed") && Scp.StoreCount == 1 && Scp.PartialStores.Count == 1, Show(again));

            Scp.Reset();
            Scp.KeepStored = true;
            Scp.MaxPduLength = 16384;
            Scp.OnPartialStore = p =>
            {
                // A system that keeps what it got: the first half of the pixels, the rest empty.
                var kept = complete.Clone();
                var pixels = DicomPixelData.Create(kept).GetFrame(0).Data.ToArray();
                Array.Clear(pixels, pixels.Length / 2, pixels.Length - pixels.Length / 2);
                kept.AddOrUpdate(new DicomOtherWord(DicomTag.PixelData, new MemoryByteBuffer(pixels)));
                lock (Scp.Archive) Scp.Archive.Add(new ArchivedObject { Dataset = kept });
            };
            var keptPartial = await One("fault-abort-mid-dataset");
            _check("Partial object kept: FAIL with a finding", keptPartial.Result == TestResultClass.FAIL && keptPartial.Details["finding"] == "partial-object-kept" && keptPartial.Limits.Fault.StateAfterFault == InstanceState.Partial, Show(keptPartial));
            _check("Partial object kept: the retry is still made and recorded", keptPartial.Limits.Fault.Retry == RetryOutcome.Stored && keptPartial.Limits.Fault.StateAfterRetry == InstanceState.Complete, keptPartial.Limits.Fault.StateAfterRetry.ToString());

            Scp.Reset();
            Scp.KeepStored = true;
            Scp.MaxPduLength = 16384;
            var drop = await One("fault-drop-mid-dataset");
            partial = Scp.PartialStores.FirstOrDefault();
            _check("Drop mid-dataset: PASS, nothing kept, retry stored", drop.Result == TestResultClass.PASS && drop.Limits.Fault.StateAfterFault == InstanceState.None && drop.Limits.Fault.Retry == RetryOutcome.Stored, Show(drop));
            _check("Drop mid-dataset: the mock saw the connection go without an A-ABORT, in the middle of a PDU",
                await WaitFor(() => Scp.PartialStores.Count == 1) && partial != null && partial.Ending != "abort" && partial.BytesOfUnfinishedPdu > 0 && !Scp.Associations.First().AbortedByPeer, partial?.Ending + " " + partial?.BytesOfUnfinishedPdu);
            _check("Drop mid-dataset: no A-ABORT in the timeline", !drop.Timeline.Any(e => e.Type == "A-ABORT") && drop.Timeline.Any(e => e.Type == "CONNECTION-RESET" && e.Direction == ProtocolEvent.Sent), Types(drop));

            Scp.Reset();
            Scp.KeepStored = true;
            Scp.MaxPduLength = 16384;
            var late = await One("fault-drop-before-response");
            _check("Drop before the response: the whole dataset went out and was stored", late.Limits.Fault.DatasetBytesSent == late.Limits.Fault.DatasetBytes && late.Limits.Fault.StateAfterFault == InstanceState.Complete && Scp.PartialStores.IsEmpty, Show(late));
            _check("Drop before the response: PASS when the retry is taken", late.Result == TestResultClass.PASS && late.Limits.Fault.Retry == RetryOutcome.Stored && Scp.StoreCount == 2, Show(late));
            _check("Drop before the response: whether a response arrived unread is recorded, and no status of it", late.Limits.Fault.ResponseArrivedUnread.HasValue && late.Timeline.Count(e => e.Type == "C-STORE-RSP") == 1, Types(late));

            Scp.Reset();
            Scp.KeepStored = true;
            Scp.Duplicates = DuplicatePolicy.Reject;
            var duplicate = await One("fault-drop-before-response");
            _check("Retry refused as a duplicate of a complete instance: EXPECTED_FAILURE", duplicate.Result == TestResultClass.EXPECTED_FAILURE && duplicate.Limits.Fault.Retry == RetryOutcome.RefusedAsDuplicate && duplicate.Limits.Fault.RetryStatus == "0x0111", Show(duplicate));

            Scp.Reset();
            Scp.KeepStored = true;
            Scp.StatusFor = _ => DicomStatus.ProcessingFailure;
            var failed = await One("fault-abort-mid-dataset");
            _check("Retry refused with an error: FAIL with the raw status", failed.Result == TestResultClass.FAIL && failed.Limits.Fault.Retry == RetryOutcome.Refused && failed.Limits.Fault.RetryStatus == "0x0110", Show(failed));

            Scp.Reset();
            Scp.KeepStored = true;
            Scp.StatusFor = _ => DicomStatus.DuplicateSOPInstance;
            var lost = await One("fault-abort-mid-dataset");
            _check("Duplicate refusal of an instance that is not there: FAIL with a finding", lost.Result == TestResultClass.FAIL && lost.Details["finding"] == "duplicate-refusal-of-missing-instance", Show(lost));

            Scp.Reset();
            Scp.KeepStored = true;
            Scp.QueryStatus = DicomStatus.ProcessingFailure;
            var blind = await One("fault-abort-mid-dataset");
            _check("No working C-FIND: the state is unknown, the retry still decides", blind.Result == TestResultClass.PASS && blind.Limits.Fault.StateAfterFault == InstanceState.Unknown && blind.Details.ContainsKey("queryProblem"), Show(blind));

            Scp.Reset();
            Scp.RejectAssociation = true;
            var rejected = await One("fault-abort-mid-dataset");
            _check("Association rejected: INDETERMINATE, nothing to break off", rejected.Result == TestResultClass.INDETERMINATE, Show(rejected));
        }

        // ---------------------------------------------------------------- cancel

        private static async Task TestCancel()
        {
            Console.WriteLine("\nLimits: cancel cleans up");

            Scp.Reset();
            Scp.MaxAssociations = 1000;
            using (var cts = new CancellationTokenSource())
            {
                var run = One("limit-max-associations", Options(ceiling: 500), token: cts.Token);
                await WaitFor(() => Scp.AssociationCount >= 5);
                cts.Cancel();
                var cancelled = await run;
                _check("Cancel while holding associations: INDETERMINATE", cancelled.Result == TestResultClass.INDETERMINATE && cancelled.Limits.AssociationsOpened >= 4, Show(cancelled));
                _check("Cancel while holding associations: none left open on the SCP", await WaitFor(() => Scp.OpenAssociations == 0 && Scp.OpenConnections == 0), Scp.OpenAssociations + " associations, " + Scp.OpenConnections + " connections");
                _check("Cancel while holding associations: they were aborted, not left to time out", Scp.Associations.Count(a => a.AbortedByPeer) >= 4, Scp.Associations.Count(a => a.AbortedByPeer) + " of " + Scp.Associations.Count);
            }

            foreach (var test in new[] { "limit-idle-timeout", "limit-artim" })
            {
                Scp.Reset();
                Scp.MaxAssociations = 1000;
                using (var cts = new CancellationTokenSource(300))
                {
                    var clock = Stopwatch.StartNew();
                    var cancelled = await One(test, Options(wait: 30), token: cts.Token);
                    _check($"Cancel during {test}: INDETERMINATE, at once", cancelled.Result == TestResultClass.INDETERMINATE && clock.ElapsedMilliseconds < 5000, Show(cancelled) + " " + clock.ElapsedMilliseconds + " ms");
                    _check($"Cancel during {test}: nothing left open on the SCP", await WaitFor(() => Scp.OpenAssociations == 0 && Scp.OpenConnections == 0), Scp.OpenConnections.ToString());
                    if (test == "limit-idle-timeout") _check("Cancel during limit-idle-timeout: the association was aborted", Scp.Associations.Count == 1 && Scp.Associations.Single().AbortedByPeer, null);
                }
            }

            foreach (var test in new[] { "limit-max-pdu", "fault-drop-before-response" })
            {
                Scp.Reset();
                Scp.KeepStored = true;
                Scp.MaxAssociations = 1000;
                Scp.ResponseDelayMs = 1500;
                using (var cts = new CancellationTokenSource())
                {
                    var run = One(test, token: cts.Token);
                    // Until something of the test is on the wire, and then in the middle of it.
                    await WaitFor(() => Scp.AssociationCount >= 2 || Scp.StoreCount >= 1 || Scp.DataPdus.Count > 3, 1500);
                    cts.Cancel();
                    var cancelled = await run;
                    _check($"Cancel during {test}: nothing left open on the SCP", await WaitFor(() => Scp.OpenAssociations == 0 && Scp.OpenConnections == 0, 5000), cancelled.Result + ", " + Scp.OpenAssociations + " associations, " + Scp.OpenConnections + " connections");
                }
            }
        }

        // ---------------------------------------------------------------- catalog, files, front-ends

        private static async Task TestFrontEnds()
        {
            Console.WriteLine("\nLimits: catalog, result files and front-ends");

            var ids = TestCatalog.All.Where(t => t.Category == TestCategory.Limits).Select(t => t.Id).ToList();
            var expected = new[]
            {
                "tls-association", "tls-server-certificate", "tls-client-cert-wrong-ca", "tls-client-cert-expired", "limit-max-pdu", "limit-oversized-pdu",
                "fault-abort-mid-dataset", "fault-drop-mid-dataset", "fault-drop-before-response", "limit-max-associations", "limit-idle-timeout", "limit-artim",
            };
            _check("Catalog: the Limits tests, last and in this order", ids.SequenceEqual(expected) && TestCatalog.All.Skip(TestCatalog.All.Count - expected.Length).All(t => t.Category == TestCategory.Limits), string.Join(",", ids));
            _check("Tests tab: Limits tests are not ticked by default, the others are", TestCatalog.All.All(t => new TestChoice(t).IsSelected == (t.Category != TestCategory.Limits)), null);

            _check("Options: defaults", new TestRunOptions().MaxAssociations == 10 && new TestRunOptions().IdleWaitSeconds == 120 && new TestRunOptions().ArtimWaitSeconds == 60 && new TestRunOptions().FaultSettleSeconds == 5 && new TestRunOptions().Validate() == null, null);
            _check("Options: a ceiling of 0 or above 500 is refused", new TestRunOptions { MaxAssociations = 0 }.Validate() != null && new TestRunOptions { MaxAssociations = 501 }.Validate() != null, null);
            _check("Options: waits must be positive", new TestRunOptions { IdleWaitSeconds = 0 }.Validate() != null && new TestRunOptions { ArtimWaitSeconds = 0 }.Validate() != null && new TestRunOptions { FaultSettleSeconds = -1 }.Validate() != null, null);

            Scp.Reset();
            Scp.MaxAssociations = 3;
            Scp.ArtimTimeoutMs = 500;
            var selected = TestCatalog.Select("limit-max-associations,limit-artim", out _);
            var first = await new TestRunner().RunAsync(Profile(), selected, CancellationToken.None, DCST4.Core.TestData.TestPatientNaming.RegistrySwedishName, Options());
            var json = ResultFileWriter.ToJson(first);
            _check("JSON: schema version 5 with a limits section", json.Contains("\"schemaVersion\": 5") && json.Contains("\"limits\"") && json.Contains("\"associationLimit\": 3") && json.Contains("\"reason\": \"LocalLimitExceeded\"") && json.Contains("\"endedAfterMs\""), null);
            _check("JSON: a test fills only its own part", !json.Contains("\"fault\"") && !json.Contains("\"pduAttempts\"") && !json.Contains("\"tls\""), null);

            Scp.Reset();
            Scp.MaxAssociations = 3;
            Scp.ArtimTimeoutMs = 500;
            var second = await new TestRunner().RunAsync(Profile(), selected, CancellationToken.None, DCST4.Core.TestData.TestPatientNaming.RegistrySwedishName, Options());
            var a = TestSuiteTests.NormalizeJson(json);
            var b = TestSuiteTests.NormalizeJson(ResultFileWriter.ToJson(second));
            _check("Two identical runs differ only in the times", a == b, TestSuiteTests.FirstDifference(a, b));

            var profilePath = Path.Combine(_temp, "limits-profile.json");
            var profile = Profile();
            profile.Endpoint.TlsExpiredClientCertThumbprint = ExpiredThumbprint;
            profile.Save(profilePath);
            var loaded = TargetProfile.Load(profilePath);
            _check("Profile: the expired certificate thumbprint survives saving", loaded.Endpoint.TlsExpiredClientCertThumbprint == ExpiredThumbprint && loaded.Endpoint.Trimmed().TlsExpiredClientCertThumbprint == ExpiredThumbprint, null);
            _check("Profile: it is not written to result files", !ResultFileWriter.ToJson(await new TestRunner().RunAsync(profile, TestCatalog.Select("tls-association", out _), CancellationToken.None)).Contains(ExpiredThumbprint), null);

            Scp.Reset();
            Scp.MaxAssociations = 2;
            var output = Path.Combine(_temp, "limits-cli");
            var cli = await TestSuiteTests.RunCli("run", "--profile", profilePath, "--tests", "limit-max-associations,tls-association", "--out", output, "--log-dir", Path.Combine(_temp, "limits-cli-log"),
                "--max-associations", "5", "--idle-wait", "2", "--artim-wait", "2", "--fault-settle", "0");
            _check("Command line: runs a Limits test with its options", cli.ExitCode == 0 && cli.Output.Contains("limit-max-associations: EXPECTED_FAILURE") && cli.Output.Contains("holds 2 associations"), cli.Output);
            _check("Command line: a TLS test without TLS in the profile is INDETERMINATE", cli.Output.Contains("tls-association: INDETERMINATE"), cli.Output);

            var bad = await TestSuiteTests.RunCli("run", "--profile", profilePath, "--tests", "limit-max-associations", "--out", output, "--max-associations", "0");
            _check("Command line: a ceiling of 0 is exit code 2, nothing sent", bad.ExitCode == 2 && bad.Output.Contains("between 1 and 500"), bad.Output);
            var notNumber = await TestSuiteTests.RunCli("run", "--profile", profilePath, "--tests", "limit-artim", "--out", output, "--artim-wait", "soon");
            _check("Command line: a wait that is no number is exit code 2", notNumber.ExitCode == 2 && notNumber.Output.Contains("--artim-wait"), notNumber.Output);

            var byCategory = TestCatalog.Select("Limits", out _);
            _check("Select: the Limits category picks exactly these tests", byCategory.Select(t => t.Id).SequenceEqual(expected), null);
        }

        // ---------------------------------------------------------------- TLS

        private sealed class Certificates : IDisposable
        {
            public X509Certificate2 Server;
            public X509Certificate2 Authority;
            public X509Certificate2 Expired;

            public void Dispose()
            {
                Server?.Dispose();
                Authority?.Dispose();
                Expired?.Dispose();
            }
        }

        private static X509Certificate2 Usable(X509Certificate2 certificate)
        {
            // SChannel cannot use the ephemeral key of a freshly created certificate; re-import it from a PFX.
            using (certificate) return new X509Certificate2(certificate.Export(X509ContentType.Pfx, "x"), "x", X509KeyStorageFlags.Exportable);
        }

        private static Certificates CreateCertificates()
        {
            var now = DateTimeOffset.Now;
            var made = new Certificates();
            using (var serverKey = RSA.Create(2048))
            using (var authorityKey = RSA.Create(2048))
            using (var clientKey = RSA.Create(2048))
            {
                made.Server = Usable(new CertificateRequest("CN=localhost", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(now.AddDays(-1), now.AddDays(1)));

                var authorityRequest = new CertificateRequest("CN=DCST4 harness authority", authorityKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                authorityRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                made.Authority = authorityRequest.CreateSelfSigned(now.AddDays(-30), now.AddDays(30));

                var clientRequest = new CertificateRequest("CN=DCST4 harness client expired", clientKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using (var signed = clientRequest.Create(made.Authority, now.AddDays(-10), now.AddDays(-1), new byte[] { 1, 2, 3, 4 }))
                {
                    made.Expired = Usable(signed.CopyWithPrivateKey(clientKey));
                }
            }
            return made;
        }

        private static int StoreCount(StoreName name)
        {
            var store = new X509Store(name, StoreLocation.CurrentUser);
            try
            {
                store.Open(OpenFlags.ReadOnly);
                return store.Certificates.Count;
            }
            finally
            {
                store.Close();
            }
        }

        private static TargetProfile TlsProfile()
        {
            var profile = Profile(TlsPort);
            profile.Endpoint.UseTls = true;
            profile.Endpoint.TlsIgnoreCertErrors = true;
            return profile;
        }

        private static async Task TestTls()
        {
            Console.WriteLine("\nTLS tests");

            Scp.Reset();
            var plain = await One("tls-association,tls-client-cert-wrong-ca".Split(',')[0]);
            _check("Profile without TLS: INDETERMINATE, nothing sent", plain.Result == TestResultClass.INDETERMINATE && plain.Explanation.Contains("does not use TLS") && Scp.AssociationCount == 0, Show(plain));

            var personalBefore = StoreCount(StoreName.My);
            var rootBefore = StoreCount(StoreName.Root);

            Certificates certificates;
            try { certificates = CreateCertificates(); }
            catch (Exception ex)
            {
                // Not counted as a pass: this must stay visible in the output.
                Console.WriteLine("  SKIP  could not create the test certificates: " + ex.Message);
                return;
            }

            using (certificates)
            {
                var authority = certificates.Authority.Subject;
                var withExpired = Options();
                withExpired.ExpiredClientCertificate = certificates.Expired;

                // A server that asks for a client certificate, lets a client without one in, and checks the one it gets.
                // fo-dicom's own acceptor turns away a client without a certificate when it asks for one, hence this one.
                var strict = new HarnessTlsAcceptor
                {
                    Certificate = certificates.Server,
                    Accepts = certificate =>
                    {
                        if (certificate == null) return true;
                        var presented = new X509Certificate2(certificate);
                        return presented.Issuer == authority && presented.NotAfter > DateTime.Now && presented.NotBefore <= DateTime.Now;
                    },
                };

                using (DicomServerFactory.Create<MockStorageScp>(TlsPort, strict))
                {
                    Scp.Reset();
                    var association = await One("tls-association", profile: TlsProfile());
                    var tls = association.Limits?.Tls;
                    _check("TLS association: PASS", association.Result == TestResultClass.PASS && Scp.EchoCount == 1, Show(association));
                    _check("TLS association: the version and cipher are read from the SslStream", tls != null && tls.HandshakeCompleted && tls.Protocol.StartsWith("Tls1") && tls.CipherStrength > 0 && association.Explanation.Contains(tls.Protocol) && association.Details["tlsProtocol"] == tls.Protocol, tls?.Protocol);
                    _check("TLS association: the server certificate is described", tls?.ServerSubject == "CN=localhost" && tls.PolicyErrors != null && tls.PolicyErrors != "None" && tls.ServerNotAfterUtc.HasValue, tls?.PolicyErrors);

                    Scp.Reset();
                    var server = await One("tls-server-certificate", profile: TlsProfile());
                    _check("Server certificate: a self-signed one for another name is FAIL, although the profile accepts it",
                        server.Result == TestResultClass.FAIL && server.Details["finding"] == "server-certificate-not-valid" && server.Explanation.Contains("hostname mismatch") && server.Explanation.Contains("not trusted"), Show(server));
                    _check("Server certificate: what was wrong is recorded, and no association was opened", server.Limits.Tls.PolicyErrors.Contains("RemoteCertificateNameMismatch") && server.Limits.Tls.PolicyErrors.Contains("RemoteCertificateChainErrors") && Scp.AssociationCount == 0, server.Limits.Tls.PolicyErrors);

                    Scp.Reset();
                    var wrong = await One("tls-client-cert-wrong-ca", profile: TlsProfile());
                    _check("Unknown authority refused: EXPECTED_FAILURE", wrong.Result == TestResultClass.EXPECTED_FAILURE && wrong.Details.ContainsKey("refusedAt"), Show(wrong));
                    _check("Unknown authority: only the baseline reached the SCP", Scp.AssociationCount == 1 && Scp.EchoCount == 1, Scp.AssociationCount.ToString());
                    _check("Unknown authority: the certificate is described in words", wrong.Limits.ClientCertificate != null && wrong.Limits.ClientCertificate.Contains("made up") && !ResultFileWriter.ToJson(new TestRunReport { Results = { wrong }, Profile = ResultProfile.From(TlsProfile()) }).Contains("thumbprint"), wrong.Limits.ClientCertificate);

                    Scp.Reset();
                    var expired = await One("tls-client-cert-expired", withExpired, TlsProfile());
                    _check("Expired certificate refused: EXPECTED_FAILURE", expired.Result == TestResultClass.EXPECTED_FAILURE && Scp.AssociationCount == 1, Show(expired));

                    Scp.Reset();
                    var notSupplied = await One("tls-client-cert-expired", profile: TlsProfile());
                    _check("No expired certificate in the profile: NOT_SUPPORTED with an explanation", notSupplied.Result == TestResultClass.NOT_SUPPORTED && notSupplied.Explanation.Contains("tlsExpiredClientCertThumbprint"), Show(notSupplied));

                    var missing = TlsProfile();
                    missing.Endpoint.TlsExpiredClientCertThumbprint = ExpiredThumbprint;
                    var notFound = await One("tls-client-cert-expired", profile: missing);
                    _check("Expired certificate not in the store: TOOL_ERROR, with the thumbprint removed", notFound.Result == TestResultClass.TOOL_ERROR && !notFound.Explanation.Contains(ExpiredThumbprint) && notFound.Explanation.Contains("[removed]"), Show(notFound));

                    using (var stillValid = TestCertificatesForHarness.Valid())
                    {
                        var valid = Options();
                        valid.ExpiredClientCertificate = stillValid;
                        var notExpired = await One("tls-client-cert-expired", valid, TlsProfile());
                        _check("A certificate that has not expired: TOOL_ERROR, the test would prove nothing", notExpired.Result == TestResultClass.TOOL_ERROR && notExpired.Explanation.Contains("still valid"), Show(notExpired));
                    }

                    Scp.Reset();
                    Scp.RejectAssociation = true;
                    var noBaseline = await One("tls-client-cert-wrong-ca", profile: TlsProfile());
                    _check("Baseline refused: INDETERMINATE, a refusal would prove nothing", noBaseline.Result == TestResultClass.INDETERMINATE && noBaseline.Explanation.Contains("prove nothing"), Show(noBaseline));
                }

                // A server that asks for a client certificate and takes whatever comes.
                var careless = new HarnessTlsAcceptor { Certificate = certificates.Server, Accepts = certificate => true };
                using (DicomServerFactory.Create<MockStorageScp>(TlsPort, careless))
                {
                    Scp.Reset();
                    var wrong = await One("tls-client-cert-wrong-ca", profile: TlsProfile());
                    _check("Unknown authority accepted: FAIL with a finding", wrong.Result == TestResultClass.FAIL && wrong.Details["finding"] == "accepted-client-certificate-from-unknown-ca", Show(wrong));
                    _check("Unknown authority accepted: the association is still released", await WaitFor(() => Scp.Associations.Count == 2 && Scp.Associations.All(x => x.Released)), null);

                    Scp.Reset();
                    var expired = await One("tls-client-cert-expired", withExpired, TlsProfile());
                    _check("Expired certificate accepted: FAIL with a finding", expired.Result == TestResultClass.FAIL && expired.Details["finding"] == "accepted-expired-client-certificate", Show(expired));
                }

                // A server that never asks for a client certificate.
                using (DicomServerFactory.Create<MockStorageScp>(TlsPort, new DefaultTlsAcceptor(certificates.Server)))
                {
                    Scp.Reset();
                    var wrong = await One("tls-client-cert-wrong-ca", profile: TlsProfile());
                    _check("Server asks for no client certificate: NOT_SUPPORTED, not a finding", wrong.Result == TestResultClass.NOT_SUPPORTED && !wrong.Details.ContainsKey("finding"), Show(wrong));

                    Scp.Reset();
                    Scp.ArtimTimeoutMs = 0;
                    var artim = await One("limit-max-associations", Options(ceiling: 3), TlsProfile());
                    _check("The raw association works over TLS too", artim.Result == TestResultClass.PASS && artim.Limits.AliveAtEnd == 3 && Scp.EchoCount == 3, Show(artim));
                }
            }

            _check("Nothing was installed in the user's certificate stores", StoreCount(StoreName.My) == personalBefore && StoreCount(StoreName.Root) == rootBefore, null);
        }

        /// <summary>A TLS server side that asks for a client certificate without demanding one, and decides itself about the one it gets.</summary>
        private sealed class HarnessTlsAcceptor : ITlsAcceptor
        {
            public X509Certificate2 Certificate;
            /// <summary>Given the client certificate, or null if the client presented none.</summary>
            public Func<X509Certificate, bool> Accepts;

            public Stream AcceptTls(Stream encryptedStream, string remoteAddress, int localPort)
            {
                var ssl = new System.Net.Security.SslStream(encryptedStream, false, (sender, certificate, chain, errors) => Accepts(certificate));
                ssl.AuthenticateAsServer(Certificate, true, System.Security.Authentication.SslProtocols.Tls12, false);
                return ssl;
            }
        }

        /// <summary>A client certificate that is still valid, for the check that an expired one is asked for.</summary>
        private static class TestCertificatesForHarness
        {
            public static X509Certificate2 Valid()
            {
                using (var key = RSA.Create(2048))
                {
                    return Usable(new CertificateRequest("CN=DCST4 harness client valid", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1)));
                }
            }
        }
    }
}
