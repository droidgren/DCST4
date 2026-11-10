using DCST4.Core.Generation.Datasets;
using DCST4.Core.Network;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using FellowOakDicom.Network;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Core.Testing.Cases
{
    /// <summary>
    /// A C-STORE of one larger object that DCST4 breaks off on purpose. Afterwards the test asks whether the instance is
    /// there, none, partial or complete, and then sends the whole object again, as a modality would after a lost connection.
    /// </summary>
    public abstract class FaultTest : ITestCase
    {
        /// <summary>1024 x 1024 at 16 bits: about 2 MB, which is many PDUs at any usual PDU length.</summary>
        private const int Matrix = 1024;
        /// <summary>A longer PDU would leave too few of them to stop in the middle of.</summary>
        private const uint LongestPdu = 65536;

        public abstract string Id { get; }
        public abstract string Name { get; }
        public TestCategory Category => TestCategory.Limits;

        /// <summary>abort-mid-dataset and so on, as written to the result file.</summary>
        protected abstract string Kind { get; }
        /// <summary>What was done, as it reads after "After ".</summary>
        protected abstract string Description { get; }
        /// <summary>Whether the whole dataset reaches the system under test, so that a complete instance afterwards is no surprise.</summary>
        protected abstract bool SendsEverything { get; }

        /// <summary>Breaks the transfer off. Returns how many bytes of the dataset went out.</summary>
        private protected abstract Task<int> BreakAsync(RawAssociation raw, byte contextId, byte[] dataset, uint pduLength, FaultRecord record);

        public async Task<TestResult> ExecuteAsync(TestContext context, CancellationToken token)
        {
            var target = RawSupport.Target(context);
            var record = new FaultRecord { Kind = Kind };
            var result = new TestResult { Limits = new LimitsResult { Fault = record } };
            var timelines = new TimelineCollector();
            RawAssociation raw = null;
            var collected = false;

            try
            {
                context.Report("Sending a 2 MB image and breaking the transfer off");
                var request = Fixtures.Request(context, "ct", Id, DicomTransferSyntax.ExplicitVRLittleEndian);
                request.Rows = Matrix;
                request.Columns = Matrix;
                var file = new TestDatasetGenerator(request).GetImages(token).Single().File;
                var sopInstanceUid = file.Dataset.GetSingleValue<string>(DicomTag.SOPInstanceUID);
                result.Uids["sopInstanceUid"] = sopInstanceUid;
                result.Uids["studyInstanceUid"] = file.Dataset.GetSingleValue<string>(DicomTag.StudyInstanceUID);

                // With a fixed seed the instance may be there from an earlier run, and then nothing below would mean anything.
                var before = await ProbeAsync(context, file, 0, false, result, timelines, token).ConfigureAwait(false);
                if (before == InstanceState.PresentUnverified)
                {
                    Fixtures.Set(result, TestResultClass.INDETERMINATE,
                        $"{target} already holds the instance this test sends, probably from an earlier run with the same seed. Run it with another seed.");
                    return result;
                }

                var open = await RawSupport.OpenAsync(context, timelines.Next(), timelines.Clock, token).ConfigureAwait(false);
                raw = open.Raw;
                if (raw == null)
                {
                    RawSupport.SetNotOpen(result, open, target);
                    return result;
                }

                using (raw.CloseOn(token))
                {
                    var outcome = await raw.AssociateAsync(RawSupport.StoreContexts(file.Dataset.GetSingleValue<string>(DicomTag.SOPClassUID)), RawSupport.OurMaxPdu, RawSupport.AssociationTimeout(context), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (!outcome.Accepted)
                    {
                        RawSupport.SetNotAssociated(result, outcome, target, "the transfer could not be started");
                        if (outcome.Rejection != null) RawSupport.RecordRejection(result, outcome.Rejection);
                        return result;
                    }

                    Fixtures.AddContexts(result, raw.Negotiation());
                    var picked = RawSupport.PickContext(raw, file);
                    if (picked == null)
                    {
                        await raw.ReleaseAsync(RawSupport.AssociationTimeout(context), token).ConfigureAwait(false);
                        Fixtures.Set(result, TestResultClass.NOT_SUPPORTED, $"{target} did not accept CT Image Storage in Explicit or Implicit VR Little Endian, so nothing was sent.");
                        return result;
                    }

                    var dataset = RawAssociation.DatasetBytes(picked.Item2);
                    var pduLength = Math.Min(raw.SendPduLength, LongestPdu);
                    record.DatasetBytes = dataset.Length;
                    record.PduLength = pduLength;

                    try
                    {
                        await raw.SendStoreCommandAsync(picked.Item1, file.Dataset.GetSingleValue<string>(DicomTag.SOPClassUID), sopInstanceUid).ConfigureAwait(false);
                        record.DatasetBytesSent = await BreakAsync(raw, picked.Item1, dataset, pduLength, record).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (RawSupport.IsNetworkFailure(ex))
                    {
                        token.ThrowIfCancellationRequested();
                        var broken = await RawSupport.BrokenWhileSendingAsync(raw, ex, token).ConfigureAwait(false);
                        Fixtures.Set(result, TestResultClass.INDETERMINATE,
                            $"{target} {broken.Ending.Describe()} while DCST4 was still sending, before the transfer could be broken off as the test intends.");
                        return result;
                    }
                }
                // Before the associations that follow, so that the timeline keeps the order in which things happened.
                timelines.Collect(raw.Timeline);
                collected = true;
                token.ThrowIfCancellationRequested();

                context.Report($"Transfer broken off. Asking with C-FIND whether the image is there, for up to {Fixtures.Int(context.Session.Options.FaultSettleSeconds)} s");
                record.StateAfterFault = await ProbeAsync(context, file, context.Session.Options.FaultSettleSeconds, true, result, timelines, token).ConfigureAwait(false);

                context.Report("Sending the whole image again, then asking whether it is there");
                var second = await DuplicateSupport.SendAsync(context, new FileListSource(file), file.Dataset.InternalTransferSyntax, "resent-" + Id, result, timelines, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var again = second.Results.SingleOrDefault();
                record.Retry = RetryOf(again);
                if (again != null && again.StatusCode.HasValue && again.AcceptedTransferSyntax != null) record.RetryStatus = again.StatusCodeText;

                var stored = record.Retry == RetryOutcome.Stored || record.Retry == RetryOutcome.StoredWithWarning;
                record.StateAfterRetry = await ProbeAsync(context, file, stored ? context.Session.Options.IndexWaitSeconds : 0, true, result, timelines, token).ConfigureAwait(false);

                Conclude(result, record, second, target);
                return result;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Fixtures.Set(result, TestResultClass.INDETERMINATE, "The test was cancelled before it finished. Its association was aborted.");
                return result;
            }
            finally
            {
                if (raw != null)
                {
                    await RawSupport.CleanUpAsync(new[] { raw }, "Cancelled").ConfigureAwait(false);
                    if (!collected) timelines.Collect(raw.Timeline);
                }
                result.Timeline = timelines.Events;
            }
        }

        private static RetryOutcome RetryOf(StoreResult again)
        {
            if (again == null || !again.StatusCode.HasValue || again.AcceptedTransferSyntax == null) return RetryOutcome.NoResponse;
            if (again.StatusCode == DuplicateSupport.DuplicateSopInstance) return RetryOutcome.RefusedAsDuplicate;
            if (again.State == StoreState.Success) return RetryOutcome.Stored;
            return again.State == StoreState.Warning ? RetryOutcome.StoredWithWarning : RetryOutcome.Refused;
        }

        /// <summary>
        /// Is the instance there? Asks with C-FIND until it is found or the wait is over, and with
        /// <paramref name="compare"/> gets it back to see whether it is what was sent.
        /// </summary>
        private static async Task<InstanceState> ProbeAsync(TestContext context, DicomFile sent, int waitSeconds, bool compare, TestResult result, TimelineCollector timelines, CancellationToken token)
        {
            var dataset = sent.Dataset;
            var sopInstanceUid = dataset.GetSingleValue<string>(DicomTag.SOPInstanceUID);
            var clock = Stopwatch.StartNew();
            var first = true;

            while (true)
            {
                var identifier = new DicomDataset
                {
                    { DicomTag.StudyInstanceUID, dataset.GetSingleValue<string>(DicomTag.StudyInstanceUID) },
                    { DicomTag.SeriesInstanceUID, dataset.GetSingleValue<string>(DicomTag.SeriesInstanceUID) },
                    { DicomTag.SOPInstanceUID, sopInstanceUid },
                };

                var timeline = first ? timelines.Next() : null;
                var outcome = await QueryService.FindAsync(context.Profile.Endpoint, context.Profile.Network, QueryRoot.Study, DicomQueryRetrieveLevel.Image, identifier, token, timeline).ConfigureAwait(false);
                if (timeline != null) timelines.Collect(timeline);
                first = false;
                token.ThrowIfCancellationRequested();

                Fixtures.AddContexts(result, outcome.Exchange.Contexts);
                if (outcome.FinalStatus.HasValue) Fixtures.AddStatus(result, "C-FIND", outcome.FinalStatus.Value);
                if (outcome.Exchange.Failure != ConnectivityFailure.None || !outcome.Succeeded)
                {
                    result.Details["queryProblem"] = outcome.Exchange.Message ?? (outcome.FinalStatus.HasValue ? DimseStatusText.Describe(outcome.FinalStatus.Value) : "no final status");
                    return InstanceState.Unknown;
                }

                if (outcome.Matches.Any(m => m.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, string.Empty) == sopInstanceUid))
                {
                    return compare ? await CompareAsync(context, sent, result, timelines, token).ConfigureAwait(false) : InstanceState.PresentUnverified;
                }

                if (clock.Elapsed >= TimeSpan.FromSeconds(waitSeconds)) return InstanceState.None;
                await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            }
        }

        private static async Task<InstanceState> CompareAsync(TestContext context, DicomFile sent, TestResult result, TimelineCollector timelines, CancellationToken token)
        {
            var retrieved = await DuplicateSupport.RetrieveAsync(context, sent.Dataset, "fault", result, timelines, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var sopInstanceUid = sent.Dataset.GetSingleValue<string>(DicomTag.SOPInstanceUID);

            foreach (var item in retrieved.Files)
            {
                try
                {
                    var back = DicomFile.Open(item.Path);
                    if (back.Dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, string.Empty) != sopInstanceUid) continue;

                    var expected = DicomPixelData.Create(sent.Dataset).GetFrame(0).Data;
                    var actual = DicomPixelData.Create(back.Clone(DicomTransferSyntax.ExplicitVRLittleEndian).Dataset).GetFrame(0).Data;
                    return actual.SequenceEqual(expected) ? InstanceState.Complete : InstanceState.Partial;
                }
                catch (Exception ex) when (ex is DicomException || ex is System.IO.IOException || ex is ArgumentException || ex is InvalidOperationException || ex is IndexOutOfRangeException)
                {
                    // What came back cannot even be read as the image that was sent.
                    result.Details["retrievedUnreadable"] = ex.Message;
                    return InstanceState.Partial;
                }
            }
            return InstanceState.PresentUnverified;
        }

        private void Conclude(TestResult result, FaultRecord record, Pipeline.RunSummary retry, string target)
        {
            result.Details["stateAfterFault"] = record.StateAfterFault.ToString();
            result.Details["retry"] = record.Retry.ToString();
            result.Details["stateAfterRetry"] = record.StateAfterRetry.ToString();

            var after = $"After {Description}, {StateText(record.StateAfterFault)}";
            var retried = RetryText(record);

            if (record.StateAfterFault == InstanceState.Partial)
            {
                result.Details["finding"] = "partial-object-kept";
                Fixtures.Set(result, TestResultClass.FAIL, $"Finding: {after} {retried}");
                return;
            }
            if (record.StateAfterFault == InstanceState.Complete && !SendsEverything)
            {
                result.Details["finding"] = "complete-after-broken-transfer";
                Fixtures.Set(result, TestResultClass.FAIL, $"Finding: {after} That cannot come from this transfer, of which only {record.DatasetBytesSent} of {record.DatasetBytes} bytes were sent. {retried}");
                return;
            }

            switch (record.Retry)
            {
                case RetryOutcome.Stored:
                case RetryOutcome.StoredWithWarning:
                    if (record.StateAfterRetry == InstanceState.None || record.StateAfterRetry == InstanceState.Partial)
                    {
                        result.Details["finding"] = record.StateAfterRetry == InstanceState.None ? "retry-accepted-but-not-found" : "retry-accepted-but-partial";
                        Fixtures.Set(result, TestResultClass.FAIL, $"Finding: {after} {retried} But afterwards {StateText(record.StateAfterRetry)}");
                    }
                    else
                    {
                        Fixtures.Set(result, TestResultClass.PASS, $"{after} {retried}");
                    }
                    return;

                case RetryOutcome.RefusedAsDuplicate:
                    if (record.StateAfterFault == InstanceState.None)
                    {
                        result.Details["finding"] = "duplicate-refusal-of-missing-instance";
                        Fixtures.Set(result, TestResultClass.FAIL, $"Finding: {after} {retried} So the object can neither be found nor be sent again.");
                    }
                    else
                    {
                        Fixtures.Set(result, TestResultClass.EXPECTED_FAILURE, $"{after} {retried}");
                    }
                    return;

                case RetryOutcome.Refused:
                    Fixtures.Set(result, TestResultClass.FAIL, $"{after} {retried}");
                    return;

                default:
                    var sent = StoreMapping.Classify(retry, "the retry");
                    Fixtures.Set(result, sent.Result == TestResultClass.PASS ? TestResultClass.FAIL : sent.Result, $"{after} The retry got no answer: {sent.Explanation}");
                    return;
            }
        }

        private static string StateText(InstanceState state)
        {
            switch (state)
            {
                case InstanceState.None: return "a query does not find the instance.";
                case InstanceState.Partial: return "the instance is found, but what comes back is not what was sent.";
                case InstanceState.Complete: return "the instance is found and comes back complete.";
                case InstanceState.PresentUnverified: return "the instance is found, but could not be retrieved to see whether it is complete.";
                default: return "a query could not say whether the instance is there.";
            }
        }

        private static string RetryText(FaultRecord record)
        {
            switch (record.Retry)
            {
                case RetryOutcome.Stored: return $"The retry was stored with {record.RetryStatus}.";
                case RetryOutcome.StoredWithWarning: return $"The retry was stored with the warning {record.RetryStatus}.";
                case RetryOutcome.RefusedAsDuplicate: return "The retry was refused with 0x0111, Duplicate SOP Instance.";
                case RetryOutcome.Refused: return $"The retry was refused with {record.RetryStatus}.";
                default: return "The retry got no answer.";
            }
        }
    }

    public sealed class AbortMidDatasetTest : FaultTest
    {
        public override string Id => "fault-abort-mid-dataset";
        public override string Name => "Fault: A-ABORT in the middle of a C-STORE";
        protected override string Kind => "abort-mid-dataset";
        protected override string Description => "an A-ABORT halfway through the dataset";
        protected override bool SendsEverything => false;

        private protected override async Task<int> BreakAsync(RawAssociation raw, byte contextId, byte[] dataset, uint pduLength, FaultRecord record)
        {
            var sent = await raw.SendDatasetAsync(contextId, dataset, pduLength, dataset.Length / 2).ConfigureAwait(false);
            await raw.AbortAsync("In the middle of the dataset, on purpose").ConfigureAwait(false);
            return sent;
        }
    }

    public sealed class DropMidDatasetTest : FaultTest
    {
        public override string Id => "fault-drop-mid-dataset";
        public override string Name => "Fault: connection lost in the middle of a C-STORE";
        protected override string Kind => "drop-mid-dataset";
        protected override string Description => "the TCP connection was reset halfway through the dataset, in the middle of a PDU and without an A-ABORT";
        protected override bool SendsEverything => false;

        private protected override async Task<int> BreakAsync(RawAssociation raw, byte contextId, byte[] dataset, uint pduLength, FaultRecord record)
        {
            var sent = await raw.SendDatasetAsync(contextId, dataset, pduLength, dataset.Length / 2).ConfigureAwait(false);
            sent += await raw.SendHalfPduAsync(contextId, dataset, sent, pduLength).ConfigureAwait(false);
            raw.Drop("In the middle of a PDU, on purpose");
            return sent;
        }
    }

    public sealed class DropBeforeResponseTest : FaultTest
    {
        /// <summary>How long the other side gets to notice that the connection is closing before it is closed for good.</summary>
        private static readonly TimeSpan Drain = TimeSpan.FromSeconds(3);

        public override string Id => "fault-drop-before-response";
        public override string Name => "Fault: connection lost before the C-STORE response";
        protected override string Kind => "drop-before-response";
        protected override string Description => "the connection was closed when the whole dataset had been delivered, without reading the C-STORE response";
        protected override bool SendsEverything => true;

        private protected override async Task<int> BreakAsync(RawAssociation raw, byte contextId, byte[] dataset, uint pduLength, FaultRecord record)
        {
            var sent = await raw.SendDatasetAsync(contextId, dataset, pduLength).ConfigureAwait(false);
            raw.Timeline?.RecordStep(ProtocolEvent.Sent, "CONNECTION-CLOSED", "After the last byte of the dataset, on purpose. The response is not read");
            await raw.CloseAfterDeliveryAsync(Drain).ConfigureAwait(false);
            record.ResponseArrivedUnread = raw.HasUnread(RawAssociation.PduData);
            return sent;
        }
    }
}
