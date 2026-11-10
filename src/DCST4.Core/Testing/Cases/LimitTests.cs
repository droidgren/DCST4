using DCST4.Core.Generation.Datasets;
using DCST4.Core.Network;
using FellowOakDicom;
using FellowOakDicom.Imaging.Codec;
using FellowOakDicom.Network.Tls;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Core.Testing.Cases
{
    /// <summary>An attempt to open a raw connection: the connection, or why there is none.</summary>
    internal sealed class RawOpen
    {
        public RawAssociation Raw;
        public bool Cancelled;
        public ConnectivityFailure Failure;
        public string Message;
    }

    /// <summary>What the limit, fault and TLS tests share: opening a <see cref="RawAssociation"/> and sending on it.</summary>
    internal static class RawSupport
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(RawSupport));

        public const byte EchoContextId = 1;
        /// <summary>What DCST4 announces as its own maximum PDU length where the test is not about that.</summary>
        public const uint OurMaxPdu = 16384;

        public static string Target(TestContext context) => $"{context.Profile.Endpoint.Host}:{Fixtures.Int(context.Profile.Endpoint.Port)}";

        public static TimeSpan AssociationTimeout(TestContext context) =>
            TimeSpan.FromSeconds(context.Profile.Network.AssociationTimeoutSeconds > 0 ? context.Profile.Network.AssociationTimeoutSeconds : 30);

        /// <summary>The profile's response timeout. Where it says no limit, these tests still stop after a minute.</summary>
        public static TimeSpan ResponseTimeout(TestContext context) =>
            TimeSpan.FromSeconds(context.Profile.Network.ResponseTimeoutSeconds > 0 ? context.Profile.Network.ResponseTimeoutSeconds : 60);

        public static List<RawContext> EchoContexts() => new List<RawContext>
        {
            new RawContext { Id = EchoContextId, AbstractSyntaxUid = DicomUID.Verification.UID, TransferSyntaxUids = { DicomTransferSyntax.ImplicitVRLittleEndian.UID.UID } },
        };

        /// <summary>One context per syntax, so that the answer says which of them is accepted.</summary>
        public static List<RawContext> StoreContexts(string sopClassUid) => new List<RawContext>
        {
            new RawContext { Id = 1, AbstractSyntaxUid = sopClassUid, TransferSyntaxUids = { DicomTransferSyntax.ExplicitVRLittleEndian.UID.UID } },
            new RawContext { Id = 3, AbstractSyntaxUid = sopClassUid, TransferSyntaxUids = { DicomTransferSyntax.ImplicitVRLittleEndian.UID.UID } },
        };

        /// <summary>Opens the connection the way the profile says, with its TLS settings unless <paramref name="tls"/> replaces them.</summary>
        public static async Task<RawOpen> OpenAsync(TestContext context, AssociationTimeline timeline, Stopwatch clock, CancellationToken token, ITlsInitiator tls = null)
        {
            var endpoint = context.Profile.Endpoint;
            var open = new RawOpen();

            if (tls == null && endpoint.UseTls)
            {
                try
                {
                    tls = DicomClientBuilder.CreateTlsInitiator(endpoint);
                }
                catch (InvalidOperationException ex)
                {
                    open.Failure = ConnectivityFailure.LocalConfiguration;
                    open.Message = ex.Message;
                    return open;
                }
            }

            try
            {
                open.Raw = await RawAssociation.ConnectAsync(endpoint, context.Profile.Network, tls, token, clock, timeline).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                open.Cancelled = true;
            }
            catch (TimeoutException ex)
            {
                open.Failure = ConnectivityFailure.Timeout;
                open.Message = ex.Message + " (timed out)";
            }
            catch (Exception ex) when (IsNetworkFailure(ex))
            {
                Logger.Warning(ex, "Could not connect to {host}:{port}", endpoint.Host, endpoint.Port);
                open.Failure = EchoService.Classify(ex) == ConnectivityFailure.Timeout ? ConnectivityFailure.Timeout : ConnectivityFailure.Connection;
                open.Message = NetworkErrors.GetUserMessage(ex);
            }
            return open;
        }

        public static bool IsNetworkFailure(Exception ex) =>
            ex is IOException || ex is SocketException || ex is ObjectDisposedException || ex is InvalidOperationException
            || ex is System.Security.Authentication.AuthenticationException || ex is System.ComponentModel.Win32Exception || ex is NotSupportedException;

        /// <summary>Sets the result for a connection that could not be opened.</summary>
        public static void SetNotOpen(TestResult result, RawOpen open, string target)
        {
            if (open.Cancelled) Fixtures.Set(result, TestResultClass.INDETERMINATE, "The test was cancelled before it finished.");
            else if (open.Failure == ConnectivityFailure.LocalConfiguration) Fixtures.Set(result, TestResultClass.TOOL_ERROR, $"DCST4 could not set up the connection, which is a local problem and not a fault of {target}: {open.Message}");
            else if (open.Failure == ConnectivityFailure.Timeout) Fixtures.Set(result, TestResultClass.TIMEOUT, $"The connection to {target} timed out: {open.Message}");
            else Fixtures.Set(result, TestResultClass.FAIL, $"DCST4 could not connect to {target}: {open.Message}");
        }

        /// <summary>Sets the result for an association that was not accepted, where the test needs one to say anything.</summary>
        public static void SetNotAssociated(TestResult result, RawAssociateOutcome outcome, string target, string purpose)
        {
            if (outcome.TimedOut) Fixtures.Set(result, TestResultClass.TIMEOUT, $"{target} did not answer the A-ASSOCIATE-RQ, so {purpose}.");
            else if (outcome.Rejection != null) Fixtures.Set(result, TestResultClass.INDETERMINATE, $"{target} rejected the association ({outcome.Rejection}), so {purpose}.");
            else Fixtures.Set(result, TestResultClass.INDETERMINATE, $"{target} {outcome.Ending.Describe()} instead of answering the A-ASSOCIATE-RQ, so {purpose}.");
        }

        public static void RecordRejection(TestResult result, AssociationRejection rejection)
        {
            result.Details["rejectResult"] = rejection.Result;
            result.Details["rejectSource"] = rejection.Source;
            result.Details["rejectReason"] = rejection.Reason;
        }

        /// <summary>The accepted context to send a file on, and the file in that context's syntax. Null if none was accepted.</summary>
        public static Tuple<byte, DicomFile> PickContext(RawAssociation raw, DicomFile file)
        {
            var own = file.Dataset.InternalTransferSyntax.UID.UID;
            var same = raw.Contexts.FirstOrDefault(c => c.Accepted && c.AcceptedTransferSyntaxUid == own);
            if (same != null) return Tuple.Create(same.Id, file);

            var other = raw.Contexts.FirstOrDefault(c => c.Accepted && !DicomTransferSyntax.Parse(c.AcceptedTransferSyntaxUid).IsEncapsulated);
            return other == null ? null : Tuple.Create(other.Id, file.Clone(DicomTransferSyntax.Parse(other.AcceptedTransferSyntaxUid)));
        }

        /// <summary>
        /// A whole C-STORE with data PDUs of <paramref name="pduLength"/>. A connection that breaks while sending comes back
        /// as the ending of the reply, since that is an answer from the system under test too.
        /// </summary>
        public static async Task<RawDimseReply> StoreAsync(RawAssociation raw, byte contextId, DicomFile file, uint pduLength, TimeSpan timeout, CancellationToken token)
        {
            var dimse = 0;
            try
            {
                dimse = await raw.SendStoreCommandAsync(contextId, file.Dataset.GetSingleValue<string>(DicomTag.SOPClassUID), file.Dataset.GetSingleValue<string>(DicomTag.SOPInstanceUID)).ConfigureAwait(false);
                await raw.SendDatasetAsync(contextId, RawAssociation.DatasetBytes(file), pduLength).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsNetworkFailure(ex))
            {
                token.ThrowIfCancellationRequested();
                return await BrokenWhileSendingAsync(raw, ex, token).ConfigureAwait(false);
            }
            return await raw.ReadResponseAsync(timeout, token, dimse, "C-STORE").ConfigureAwait(false);
        }

        /// <summary>What the other side did that made a write fail: its abort or close if one arrived, otherwise the error.</summary>
        public static async Task<RawDimseReply> BrokenWhileSendingAsync(RawAssociation raw, Exception ex, CancellationToken token)
        {
            RawEvent ending = null;
            for (var i = 0; i < 20 && ending == null; i++)
            {
                var next = await raw.NextAsync(TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
                if (next != null && (next.IsEnding || next.PduType == RawAssociation.PduAbort)) ending = next;
            }
            ending = ending ?? new RawEvent { Kind = RawEventKind.Reset, Error = NetworkErrors.GetUserMessage(ex), AtMs = raw.NowMs };
            raw.RecordEnding(ending);
            return new RawDimseReply { Ending = ending };
        }

        /// <summary>Ends what a test still holds open: with A-ABORT where an association exists, and always by closing the socket.</summary>
        public static async Task CleanUpAsync(IEnumerable<RawAssociation> associations, string reason)
        {
            var open = associations.Where(a => a != null && !a.IsClosed).ToList();
            await Task.WhenAll(open.Select(a => a.Contexts.Count > 0 && !a.EndedByPeer ? a.AbortAsync(reason) : Task.Run(() => a.Dispose()))).ConfigureAwait(false);
            foreach (var association in open) association.Dispose();
        }

        public static string EndingName(RawEvent ending)
        {
            if (ending == null) return "still-open";
            if (ending.Kind == RawEventKind.ClosedByPeer) return "connection-closed";
            if (ending.Kind == RawEventKind.Reset) return "connection-reset";
            if (ending.Kind == RawEventKind.ClosedLocally) return "closed-by-dcst4";
            if (ending.PduType == RawAssociation.PduAbort) return "A-ABORT";
            if (ending.PduType == RawAssociation.PduReleaseRq) return "A-RELEASE-RQ";
            return "PDU-" + Fixtures.Int(ending.PduType);
        }

        public static string Status(ushort status) => AssociationTimeline.FormatStatus(status);
    }

    /// <summary>
    /// How many associations the system under test holds at once. Associations are opened one at a time and held, until one
    /// is refused or the ceiling is reached. All are released afterwards, and aborted if the run is cancelled.
    /// </summary>
    public sealed class MaxAssociationsTest : ITestCase
    {
        public string Id => "limit-max-associations";
        public string Name => "Limit: simultaneous associations";
        public TestCategory Category => TestCategory.Limits;

        public async Task<TestResult> ExecuteAsync(TestContext context, CancellationToken token)
        {
            var ceiling = context.Session.Options.MaxAssociations;
            var target = RawSupport.Target(context);
            var result = new TestResult { Limits = new LimitsResult { Ceiling = ceiling } };
            var limits = result.Limits;
            var timelines = new TimelineCollector();
            var held = new List<RawAssociation>();
            var all = new List<RawAssociation>();
            RawOpen failedOpen = null;
            RawAssociateOutcome refusal = null;
            var cancelled = false;

            try
            {
                for (var number = 1; number <= ceiling; number++)
                {
                    token.ThrowIfCancellationRequested();
                    var open = await RawSupport.OpenAsync(context, timelines.Next(), timelines.Clock, token).ConfigureAwait(false);
                    if (open.Raw == null)
                    {
                        if (open.Cancelled) token.ThrowIfCancellationRequested();
                        failedOpen = open;
                        break;
                    }

                    all.Add(open.Raw);
                    using (open.Raw.CloseOn(token))
                    {
                        var outcome = await open.Raw.AssociateAsync(RawSupport.EchoContexts(), RawSupport.OurMaxPdu, RawSupport.AssociationTimeout(context), token).ConfigureAwait(false);
                        if (!outcome.Accepted)
                        {
                            token.ThrowIfCancellationRequested();
                            refusal = outcome;
                            open.Raw.Dispose();
                            break;
                        }
                    }

                    held.Add(open.Raw);
                    if (number == 1) result.NegotiatedContexts = open.Raw.Negotiation();
                }

                limits.AssociationsOpened = held.Count;

                var alive = 0;
                foreach (var raw in held)
                {
                    using (raw.CloseOn(token))
                    {
                        if (await AnswersEchoAsync(raw, context, result, token).ConfigureAwait(false)) alive++;
                    }
                }
                limits.AliveAtEnd = alive;

                var released = 0;
                foreach (var raw in held)
                {
                    if (!raw.IsClosed && await raw.ReleaseAsync(RawSupport.AssociationTimeout(context), token).ConfigureAwait(false)) released++;
                }
                result.Details["released"] = Fixtures.Int(released);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                cancelled = true;
            }
            catch (Exception ex) when (RawSupport.IsNetworkFailure(ex) && token.IsCancellationRequested)
            {
                cancelled = true;
            }
            finally
            {
                await RawSupport.CleanUpAsync(all, cancelled ? "Cancelled" : "The test is over").ConfigureAwait(false);
                foreach (var raw in all) timelines.Collect(raw.Timeline);
                result.Timeline = timelines.Events;
            }

            limits.AssociationsOpened = held.Count;
            if (cancelled)
            {
                Fixtures.Set(result, TestResultClass.INDETERMINATE, $"The test was cancelled with {Count(held.Count)} open. What was open was aborted.");
                return result;
            }

            Conclude(result, target, ceiling, held.Count, failedOpen, refusal);
            return result;
        }

        private static async Task<bool> AnswersEchoAsync(RawAssociation raw, TestContext context, TestResult result, CancellationToken token)
        {
            if (raw.IsClosed || !raw.Contexts.Any(c => c.Id == RawSupport.EchoContextId && c.Accepted)) return false;
            try
            {
                var dimse = await raw.SendEchoAsync(RawSupport.EchoContextId).ConfigureAwait(false);
                var reply = await raw.ReadResponseAsync(RawSupport.ResponseTimeout(context), token, dimse, "C-ECHO").ConfigureAwait(false);
                if (reply.Status.HasValue) Fixtures.AddStatus(result, "C-ECHO", reply.Status.Value);
                return reply.Status == 0x0000;
            }
            catch (Exception ex) when (RawSupport.IsNetworkFailure(ex))
            {
                token.ThrowIfCancellationRequested();
                return false;
            }
        }

        private static string Count(int associations) => associations == 1 ? "1 association" : Fixtures.Int(associations) + " associations";

        private static void Conclude(TestResult result, string target, int ceiling, int opened, RawOpen failedOpen, RawAssociateOutcome refusal)
        {
            var limits = result.Limits;
            var next = Fixtures.Int(opened + 1);

            if (opened == 0)
            {
                if (failedOpen != null) RawSupport.SetNotOpen(result, failedOpen, target);
                else RawSupport.SetNotAssociated(result, refusal, target, "its limit could not be looked for");
                if (refusal?.Rejection != null) RawSupport.RecordRejection(result, refusal.Rejection);
                return;
            }

            var dropped = opened - (limits.AliveAtEnd ?? opened);
            var held = dropped == 0
                ? (opened == 1 ? "The one that was open still answered a C-ECHO at the end." : $"All {Fixtures.Int(opened)} that were open still answered a C-ECHO at the end.")
                : $"{Fixtures.Int(dropped)} of the {Fixtures.Int(opened)} that were open no longer answered a C-ECHO at the end.";
            if (dropped > 0) result.Details["droppedWhileHeld"] = Fixtures.Int(dropped);

            if (failedOpen == null && refusal == null)
            {
                Fixtures.Set(result, TestResultClass.PASS,
                    $"{target} held {Count(ceiling)} at once, which is where DCST4 was told to stop. Its limit is higher, or it has none. {held}");
                return;
            }

            limits.AssociationLimit = opened;
            if (refusal?.Rejection != null)
            {
                limits.RefusedWith = "A-ASSOCIATE-RJ";
                limits.Rejection = refusal.Rejection;
                RawSupport.RecordRejection(result, refusal.Rejection);
                Fixtures.Set(result, TestResultClass.EXPECTED_FAILURE,
                    $"{target} holds {Count(opened)} at once and rejected number {next}: {refusal.Rejection}. {held}");
                return;
            }

            if (refusal != null && refusal.TimedOut)
            {
                limits.RefusedWith = "no-answer";
                Fixtures.Set(result, TestResultClass.TIMEOUT,
                    $"{target} holds {Count(opened)} at once and did not answer the A-ASSOCIATE-RQ of number {next}. {held}");
                return;
            }

            if (failedOpen != null && failedOpen.Failure == ConnectivityFailure.LocalConfiguration)
            {
                RawSupport.SetNotOpen(result, failedOpen, target);
                return;
            }

            string how;
            if (refusal != null)
            {
                limits.RefusedWith = RawSupport.EndingName(refusal.Ending);
                how = refusal.Ending.Describe();
            }
            else
            {
                limits.RefusedWith = failedOpen.Failure == ConnectivityFailure.Timeout ? "no-answer" : "connection-refused";
                how = "the connection could not be opened: " + failedOpen.Message;
            }

            result.Details["finding"] = "association-limit-without-reject";
            Fixtures.Set(result, TestResultClass.FAIL,
                $"Finding: {target} holds {Count(opened)} at once and refused number {next} without an A-ASSOCIATE-RJ: {how}. {held}");
        }
    }

    /// <summary>How long the system under test keeps an association open on which nothing is sent.</summary>
    public sealed class IdleTimeoutTest : ITestCase
    {
        public string Id => "limit-idle-timeout";
        public string Name => "Limit: idle association timeout";
        public TestCategory Category => TestCategory.Limits;

        public async Task<TestResult> ExecuteAsync(TestContext context, CancellationToken token)
        {
            var wait = context.Session.Options.IdleWaitSeconds;
            var target = RawSupport.Target(context);
            var result = new TestResult { Limits = new LimitsResult { WaitSeconds = wait } };
            var timeline = new AssociationTimeline();
            RawAssociation raw = null;
            var cancelled = false;

            try
            {
                var open = await RawSupport.OpenAsync(context, timeline, null, token).ConfigureAwait(false);
                raw = open.Raw;
                if (raw == null)
                {
                    RawSupport.SetNotOpen(result, open, target);
                    return result;
                }

                using (raw.CloseOn(token))
                {
                    var outcome = await raw.AssociateAsync(RawSupport.EchoContexts(), RawSupport.OurMaxPdu, RawSupport.AssociationTimeout(context), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (!outcome.Accepted)
                    {
                        RawSupport.SetNotAssociated(result, outcome, target, "the idle timeout could not be measured");
                        if (outcome.Rejection != null) RawSupport.RecordRejection(result, outcome.Rejection);
                        return result;
                    }

                    result.NegotiatedContexts = raw.Negotiation();
                    var acceptedAtMs = raw.NowMs;
                    var deadline = acceptedAtMs + wait * 1000L;
                    RawEvent ending = null;
                    while (ending == null && raw.NowMs < deadline)
                    {
                        var next = await raw.NextAsync(TimeSpan.FromMilliseconds(Math.Max(1, deadline - raw.NowMs)), token).ConfigureAwait(false);
                        // Anything but an ending is unexpected on an idle association, and does not end it.
                        if (next != null && (next.IsEnding || next.PduType == RawAssociation.PduAbort || next.PduType == RawAssociation.PduReleaseRq)) ending = next;
                    }
                    token.ThrowIfCancellationRequested();

                    result.Limits.EndedWith = RawSupport.EndingName(ending);
                    if (ending == null)
                    {
                        await raw.ReleaseAsync(RawSupport.AssociationTimeout(context), token).ConfigureAwait(false);
                        Fixtures.Set(result, TestResultClass.PASS,
                            $"{target} kept an idle association open for the {Fixtures.Int(wait)} s that DCST4 waited. Its idle timeout is longer, or it has none.");
                        return result;
                    }

                    raw.RecordEnding(ending);
                    result.Limits.EndedAfterMs = Math.Max(0, ending.AtMs - acceptedAtMs);
                    var orderly = ending.Kind == RawEventKind.Pdu;
                    if (!orderly) result.Details["endedWithoutPdu"] = "true";
                    Fixtures.Set(result, TestResultClass.PASS,
                        $"{target} ended the idle association within the {Fixtures.Int(wait)} s that DCST4 waited: it {ending.Describe()}"
                        + (orderly ? "." : ", without an A-ABORT or A-RELEASE-RQ.")
                        + " The time is on the timeline and in limits.endedAfterMs.");
                    return result;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                cancelled = true;
            }
            catch (Exception ex) when (RawSupport.IsNetworkFailure(ex))
            {
                if (token.IsCancellationRequested) cancelled = true;
                else Fixtures.Set(result, TestResultClass.FAIL, $"The connection to {target} failed: {NetworkErrors.GetUserMessage(ex)}");
            }
            finally
            {
                await RawSupport.CleanUpAsync(new[] { raw }, cancelled ? "Cancelled" : "The test is over").ConfigureAwait(false);
                result.Timeline = timeline.Events;
            }

            if (cancelled) Fixtures.Set(result, TestResultClass.INDETERMINATE, "The test was cancelled before it finished. The association was aborted.");
            return result;
        }
    }

    /// <summary>
    /// The ARTIM timer of PS3.8: a connection on which no A-ASSOCIATE-RQ arrives must not be kept for ever, or idle
    /// connections can use up what the system has.
    /// </summary>
    public sealed class ArtimTest : ITestCase
    {
        public string Id => "limit-artim";
        public string Name => "Limit: connection without an association request (ARTIM)";
        public TestCategory Category => TestCategory.Limits;

        public async Task<TestResult> ExecuteAsync(TestContext context, CancellationToken token)
        {
            var wait = context.Session.Options.ArtimWaitSeconds;
            var target = RawSupport.Target(context);
            var result = new TestResult { Limits = new LimitsResult { WaitSeconds = wait } };
            var timeline = new AssociationTimeline();
            RawAssociation raw = null;
            var cancelled = false;

            try
            {
                var open = await RawSupport.OpenAsync(context, timeline, null, token).ConfigureAwait(false);
                raw = open.Raw;
                if (raw == null)
                {
                    RawSupport.SetNotOpen(result, open, target);
                    return result;
                }

                timeline.RecordStep(ProtocolEvent.Sent, "CONNECTION-OPENED", context.Profile.Endpoint.UseTls ? "TLS session, no A-ASSOCIATE-RQ" : "TCP connection, no A-ASSOCIATE-RQ", raw.ConnectedAtMs);
                using (raw.CloseOn(token))
                {
                    var deadline = raw.ConnectedAtMs + wait * 1000L;
                    RawEvent ending = null;
                    RawEvent abort = null;
                    while (ending == null && raw.NowMs < deadline)
                    {
                        var next = await raw.NextAsync(TimeSpan.FromMilliseconds(Math.Max(1, deadline - raw.NowMs)), token).ConfigureAwait(false);
                        if (next == null) continue;
                        if (next.IsEnding) ending = next;
                        else if (next.PduType == RawAssociation.PduAbort) abort = next;
                    }
                    token.ThrowIfCancellationRequested();

                    // An A-ABORT counts as the end even if the connection is still to be closed.
                    var seen = abort ?? ending;
                    result.Limits.EndedWith = RawSupport.EndingName(seen);
                    if (seen == null)
                    {
                        result.Details["finding"] = "no-artim-timeout";
                        Fixtures.Set(result, TestResultClass.FAIL,
                            $"Finding: {target} kept a connection without an A-ASSOCIATE-RQ open for the {Fixtures.Int(wait)} s that DCST4 waited. Its ARTIM timer is longer than that, or missing.");
                        return result;
                    }

                    raw.RecordEnding(seen);
                    result.Limits.EndedAfterMs = Math.Max(0, seen.AtMs - raw.ConnectedAtMs);
                    Fixtures.Set(result, TestResultClass.PASS,
                        $"{target} did not keep a connection without an A-ASSOCIATE-RQ: it {seen.Describe()} within the {Fixtures.Int(wait)} s that DCST4 waited. The time is on the timeline and in limits.endedAfterMs.");
                    return result;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                cancelled = true;
            }
            finally
            {
                raw?.Dispose();
                result.Timeline = timeline.Events;
            }

            if (cancelled) Fixtures.Set(result, TestResultClass.INDETERMINATE, "The test was cancelled before it finished. The connection was closed.");
            return result;
        }
    }

    /// <summary>What the PDU tests share: the object they send, and one association with one C-STORE.</summary>
    internal static class PduSupport
    {
        /// <summary>256 x 256 at 16 bits is about 130 KB, so PDU lengths up to 128 K are filled to the limit.</summary>
        public const int Matrix = 256;

        public static DicomFile Generate(TestContext context, string purpose, int matrix, CancellationToken token)
        {
            var request = Fixtures.Request(context, "ct", purpose, DicomTransferSyntax.ExplicitVRLittleEndian);
            request.Rows = matrix;
            request.Columns = matrix;
            return new TestDatasetGenerator(request).GetImages(token).Single().File;
        }

        public static string OutcomeOf(RawDimseReply reply)
        {
            if (reply.TimedOut) return "timeout";
            if (reply.Ending != null) return "ended";
            if (!reply.Status.HasValue) return "ended";
            var state = StoreStatus.Classify(reply.Status.Value);
            return state == StoreState.Success || state == StoreState.Warning ? "stored" : "refused";
        }
    }

    /// <summary>
    /// Announces several maximum PDU lengths of our own, records what the system under test announces each time, and stores
    /// one object per association with data PDUs of exactly the length it said it takes.
    /// </summary>
    public sealed class MaxPduTest : ITestCase
    {
        private static readonly uint[] OurSizes = { 4096, 16384, 65536, 131072 };

        public string Id => "limit-max-pdu";
        public string Name => "Limit: maximum PDU length";
        public TestCategory Category => TestCategory.Limits;

        public async Task<TestResult> ExecuteAsync(TestContext context, CancellationToken token)
        {
            var target = RawSupport.Target(context);
            var result = new TestResult { Limits = new LimitsResult { PduAttempts = new List<PduAttempt>() } };
            var timelines = new TimelineCollector();
            var all = new List<RawAssociation>();
            var cancelled = false;
            RawOpen failedOpen = null;
            RawAssociateOutcome refusal = null;

            try
            {
                foreach (var size in OurSizes)
                {
                    token.ThrowIfCancellationRequested();
                    var file = PduSupport.Generate(context, Id + "/" + Fixtures.Int((int)size), PduSupport.Matrix, token);
                    var attempt = new PduAttempt { OurMaxPdu = size, SopInstanceUid = file.Dataset.GetSingleValue<string>(DicomTag.SOPInstanceUID) };
                    result.Limits.PduAttempts.Add(attempt);
                    result.Uids["sopInstanceUid." + Fixtures.Int((int)size)] = attempt.SopInstanceUid;

                    var open = await RawSupport.OpenAsync(context, timelines.Next(), timelines.Clock, token).ConfigureAwait(false);
                    if (open.Raw == null)
                    {
                        if (open.Cancelled) token.ThrowIfCancellationRequested();
                        failedOpen = open;
                        attempt.Outcome = "not-connected";
                        break;
                    }

                    var raw = open.Raw;
                    all.Add(raw);
                    using (raw.CloseOn(token))
                    {
                        var outcome = await raw.AssociateAsync(RawSupport.StoreContexts(file.Dataset.GetSingleValue<string>(DicomTag.SOPClassUID)), size, RawSupport.AssociationTimeout(context), token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        if (!outcome.Accepted)
                        {
                            refusal = outcome;
                            attempt.Outcome = outcome.Rejection != null ? "association-rejected" : outcome.TimedOut ? "timeout" : "ended";
                            break;
                        }

                        Fixtures.AddContexts(result, raw.Negotiation());
                        attempt.PeerMaxPdu = raw.PeerMaxPdu;
                        var picked = RawSupport.PickContext(raw, file);
                        if (picked == null)
                        {
                            attempt.Outcome = "not-accepted";
                            await raw.ReleaseAsync(RawSupport.AssociationTimeout(context), token).ConfigureAwait(false);
                            continue;
                        }

                        // Without a limit from the other side, our own size is as good as any, and it varies between the associations.
                        var pduLength = raw.PeerMaxPdu == 0 ? size : raw.PeerMaxPdu;
                        attempt.PduLengthSent = pduLength;
                        var reply = await RawSupport.StoreAsync(raw, picked.Item1, picked.Item2, pduLength, RawSupport.ResponseTimeout(context), token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();

                        attempt.LargestPduSent = raw.LargestPduSent;
                        attempt.LargestPduReceived = raw.LargestPduReceived;
                        attempt.Outcome = PduSupport.OutcomeOf(reply);
                        if (reply.Status.HasValue)
                        {
                            attempt.Status = RawSupport.Status(reply.Status.Value);
                            Fixtures.AddStatus(result, "C-STORE", reply.Status.Value);
                        }
                        if (reply.Ending != null) result.Details["ended." + Fixtures.Int((int)size)] = reply.Ending.Describe();

                        if (!raw.IsClosed && reply.Ending == null) await raw.ReleaseAsync(RawSupport.AssociationTimeout(context), token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                cancelled = true;
            }
            catch (Exception ex) when (RawSupport.IsNetworkFailure(ex) && token.IsCancellationRequested)
            {
                cancelled = true;
            }
            finally
            {
                await RawSupport.CleanUpAsync(all, cancelled ? "Cancelled" : "The test is over").ConfigureAwait(false);
                foreach (var raw in all) timelines.Collect(raw.Timeline);
                result.Timeline = timelines.Events;
            }

            if (cancelled)
            {
                Fixtures.Set(result, TestResultClass.INDETERMINATE, "The test was cancelled before it finished. Its association was aborted.");
                return result;
            }

            Conclude(result, target, failedOpen, refusal);
            return result;
        }

        private static void Conclude(TestResult result, string target, RawOpen failedOpen, RawAssociateOutcome refusal)
        {
            var attempts = result.Limits.PduAttempts;
            if (failedOpen != null)
            {
                RawSupport.SetNotOpen(result, failedOpen, target);
                return;
            }
            if (refusal != null)
            {
                if (refusal.Rejection != null) RawSupport.RecordRejection(result, refusal.Rejection);
                var announced = Fixtures.Int((int)attempts.Last().OurMaxPdu);
                if (refusal.TimedOut) Fixtures.Set(result, TestResultClass.TIMEOUT, $"{target} did not answer an A-ASSOCIATE-RQ that announced a maximum PDU length of {announced}.");
                else if (refusal.Rejection != null) Fixtures.Set(result, TestResultClass.FAIL, $"{target} rejected an association that announced a maximum PDU length of {announced}: {refusal.Rejection}.");
                else Fixtures.Set(result, TestResultClass.FAIL, $"{target} {refusal.Ending.Describe()} when an A-ASSOCIATE-RQ announced a maximum PDU length of {announced}.");
                return;
            }

            if (attempts.All(a => a.Outcome == "not-accepted"))
            {
                Fixtures.Set(result, TestResultClass.NOT_SUPPORTED, $"{target} did not accept CT Image Storage in Explicit or Implicit VR Little Endian, so nothing was sent.");
                return;
            }

            var announcedByPeer = string.Join(", ", attempts.Where(a => a.PeerMaxPdu.HasValue).Select(a => a.PeerMaxPdu.Value).Distinct().Select(v => v == 0 ? "0 (no limit)" : v.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            result.Details["peerMaxPdu"] = announcedByPeer;

            var tooLong = attempts.FirstOrDefault(a => a.OurMaxPdu > 0 && a.LargestPduReceived > a.OurMaxPdu);
            if (tooLong != null)
            {
                result.Details["finding"] = "pdu-longer-than-announced";
                Fixtures.Set(result, TestResultClass.FAIL,
                    $"Finding: {target} sent a PDU of {tooLong.LargestPduReceived} bytes on an association where DCST4 announced a maximum of {tooLong.OurMaxPdu}.");
                return;
            }

            var bad = attempts.FirstOrDefault(a => a.Outcome != "stored" && a.Outcome != "not-accepted");
            if (bad != null)
            {
                var what = bad.Outcome == "timeout" ? "did not answer" : bad.Outcome == "refused" ? $"refused the object with {bad.Status}" : "ended the association";
                Fixtures.Set(result, bad.Outcome == "timeout" ? TestResultClass.TIMEOUT : TestResultClass.FAIL,
                    $"{target} announced a maximum PDU length of {bad.PeerMaxPdu} and {what} when DCST4 sent data PDUs of {bad.PduLengthSent} bytes, having announced {bad.OurMaxPdu} itself.");
                return;
            }

            var reached = attempts.Any(a => a.LargestPduSent.HasValue && a.LargestPduSent == a.PduLengthSent);
            Fixtures.Set(result, TestResultClass.PASS,
                $"{target} announced a maximum PDU length of {announcedByPeer} and stored an object on each of {Fixtures.Int(attempts.Count(a => a.Outcome == "stored"))} associations"
                + (reached ? " sent in data PDUs of exactly the length it takes." : ". The object was smaller than one PDU of that length, so the limit itself was not reached.")
                + " Nothing it sent was longer than DCST4 announced.");
        }
    }

    /// <summary>Sends data PDUs longer than the system under test said it takes. It should refuse them.</summary>
    public sealed class OversizedPduTest : ITestCase
    {
        /// <summary>How far above the announced limit the PDUs go.</summary>
        private const uint Excess = 1024;
        /// <summary>The largest announced limit this test will go above: the object has to be larger than the PDU.</summary>
        private const uint LargestProbedLimit = 8 * 1024 * 1024;

        public string Id => "limit-oversized-pdu";
        public string Name => "Limit: PDU longer than the announced maximum";
        public TestCategory Category => TestCategory.Limits;

        public async Task<TestResult> ExecuteAsync(TestContext context, CancellationToken token)
        {
            var target = RawSupport.Target(context);
            var attempt = new PduAttempt { OurMaxPdu = RawSupport.OurMaxPdu };
            var result = new TestResult { Limits = new LimitsResult { PduAttempts = new List<PduAttempt> { attempt } } };
            var timelines = new TimelineCollector();
            var all = new List<RawAssociation>();
            var cancelled = false;

            try
            {
                // The limit is only known from an A-ASSOCIATE-AC, and the object has to be made larger than it. So ask first.
                var first = await RawSupport.OpenAsync(context, timelines.Next(), timelines.Clock, token).ConfigureAwait(false);
                if (first.Raw == null)
                {
                    RawSupport.SetNotOpen(result, first, target);
                    return result;
                }
                all.Add(first.Raw);

                uint limit;
                using (first.Raw.CloseOn(token))
                {
                    var asked = await first.Raw.AssociateAsync(RawSupport.StoreContexts(DicomUID.CTImageStorage.UID), RawSupport.OurMaxPdu, RawSupport.AssociationTimeout(context), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (!asked.Accepted)
                    {
                        RawSupport.SetNotAssociated(result, asked, target, "its maximum PDU length is not known");
                        if (asked.Rejection != null) RawSupport.RecordRejection(result, asked.Rejection);
                        return result;
                    }
                    limit = first.Raw.PeerMaxPdu;
                    await first.Raw.ReleaseAsync(RawSupport.AssociationTimeout(context), token).ConfigureAwait(false);
                }

                attempt.PeerMaxPdu = limit;
                if (limit == 0)
                {
                    Fixtures.Set(result, TestResultClass.INDETERMINATE, $"{target} announces a maximum PDU length of 0, which means no limit, so no PDU can be too long for it.");
                    return result;
                }
                if (limit > LargestProbedLimit)
                {
                    Fixtures.Set(result, TestResultClass.INDETERMINATE,
                        $"{target} announces a maximum PDU length of {limit}. Going above it would take a larger object than DCST4 sends for this test.");
                    return result;
                }

                var pduLength = limit + Excess;
                var matrix = PduSupport.Matrix;
                while ((long)matrix * matrix * 2 < pduLength * 2L && matrix < Generation.ExamRequest.MaxMatrix) matrix *= 2;
                var file = PduSupport.Generate(context, Id, matrix, token);
                attempt.SopInstanceUid = file.Dataset.GetSingleValue<string>(DicomTag.SOPInstanceUID);
                attempt.PduLengthSent = pduLength;
                result.Uids["sopInstanceUid"] = attempt.SopInstanceUid;
                result.Details["rows"] = Fixtures.Int(matrix);

                var open = await RawSupport.OpenAsync(context, timelines.Next(), timelines.Clock, token).ConfigureAwait(false);
                if (open.Raw == null)
                {
                    RawSupport.SetNotOpen(result, open, target);
                    return result;
                }

                var raw = open.Raw;
                all.Add(raw);
                using (raw.CloseOn(token))
                {
                    var outcome = await raw.AssociateAsync(RawSupport.StoreContexts(file.Dataset.GetSingleValue<string>(DicomTag.SOPClassUID)), RawSupport.OurMaxPdu, RawSupport.AssociationTimeout(context), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (!outcome.Accepted)
                    {
                        RawSupport.SetNotAssociated(result, outcome, target, "nothing was sent");
                        return result;
                    }

                    result.NegotiatedContexts = raw.Negotiation();
                    var picked = RawSupport.PickContext(raw, file);
                    if (picked == null)
                    {
                        attempt.Outcome = "not-accepted";
                        await raw.ReleaseAsync(RawSupport.AssociationTimeout(context), token).ConfigureAwait(false);
                        Fixtures.Set(result, TestResultClass.NOT_SUPPORTED, $"{target} did not accept CT Image Storage in Explicit or Implicit VR Little Endian, so nothing was sent.");
                        return result;
                    }
                    if (raw.PeerMaxPdu != limit)
                    {
                        attempt.PeerMaxPdu = raw.PeerMaxPdu;
                        await raw.ReleaseAsync(RawSupport.AssociationTimeout(context), token).ConfigureAwait(false);
                        Fixtures.Set(result, TestResultClass.INDETERMINATE,
                            $"{target} announced a maximum PDU length of {limit} on one association and {raw.PeerMaxPdu} on the next, so nothing was sent.");
                        return result;
                    }

                    var reply = await RawSupport.StoreAsync(raw, picked.Item1, picked.Item2, pduLength, RawSupport.ResponseTimeout(context), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();

                    attempt.LargestPduSent = raw.LargestPduSent;
                    attempt.LargestPduReceived = raw.LargestPduReceived;
                    attempt.Outcome = PduSupport.OutcomeOf(reply);
                    if (reply.Status.HasValue)
                    {
                        attempt.Status = RawSupport.Status(reply.Status.Value);
                        Fixtures.AddStatus(result, "C-STORE", reply.Status.Value);
                    }
                    if (!raw.IsClosed && reply.Ending == null && !reply.TimedOut) await raw.ReleaseAsync(RawSupport.AssociationTimeout(context), token).ConfigureAwait(false);

                    var sent = $"data PDUs of {pduLength} bytes, {Excess} more than the {limit} it announced";
                    if (attempt.Outcome == "stored")
                    {
                        result.Details["finding"] = "accepted-oversized-pdu";
                        Fixtures.Set(result, TestResultClass.FAIL, $"Finding: {target} stored an object sent in {sent}, with status {attempt.Status}.");
                    }
                    else if (attempt.Outcome == "refused")
                    {
                        Fixtures.Set(result, TestResultClass.EXPECTED_FAILURE, $"{target} refused an object sent in {sent}, with status {attempt.Status} ({StoreStatus.Describe(reply.Status.Value)}).");
                    }
                    else if (attempt.Outcome == "timeout")
                    {
                        Fixtures.Set(result, TestResultClass.TIMEOUT, $"{target} did not answer an object sent in {sent}.");
                    }
                    else
                    {
                        result.Details["endedWith"] = RawSupport.EndingName(reply.Ending);
                        Fixtures.Set(result, TestResultClass.EXPECTED_FAILURE, $"{target} did not take {sent}: it {reply.Ending.Describe()}.");
                    }
                    return result;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                cancelled = true;
            }
            catch (Exception ex) when (RawSupport.IsNetworkFailure(ex) && token.IsCancellationRequested)
            {
                cancelled = true;
            }
            finally
            {
                await RawSupport.CleanUpAsync(all, cancelled ? "Cancelled" : "The test is over").ConfigureAwait(false);
                foreach (var raw in all) timelines.Collect(raw.Timeline);
                result.Timeline = timelines.Events;
            }

            Fixtures.Set(result, TestResultClass.INDETERMINATE, "The test was cancelled before it finished. Its association was aborted.");
            return result;
        }
    }
}
