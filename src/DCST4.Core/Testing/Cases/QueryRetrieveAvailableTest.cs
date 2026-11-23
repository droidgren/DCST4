using DCST4.Core.Network;
using DCST4.Core.TestData;
using FellowOakDicom;
using FellowOakDicom.Network;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Core.Testing.Cases
{
    /// <summary>
    /// Is Query/Retrieve there at all? C-FIND under both roots, then C-MOVE and C-GET, all for a study that cannot exist, so
    /// it needs nothing stored and nothing comes back. The result goes by C-FIND. C-MOVE and C-GET are recorded beside it.
    /// </summary>
    public sealed class QueryRetrieveAvailableTest : ITestCase
    {
        private const string Purpose = "qr-basic";
        private const ushort MoveDestinationUnknown = 0xA801;

        public string Id => "qr-basic";
        public string Name => "Q/R: which services are accepted";
        public string Description => "Sends C-FIND under Study Root and Patient Root, then C-MOVE and C-GET, for a study that cannot exist, and records which of them the Q/R server accepts. Needs nothing stored earlier and stores nothing.";
        public string Group => TestGroups.Query;
        public bool StoresObjects => false;
        public StorageEstimate Storage => null;
        public TestCategory Category => TestCategory.Capability;

        /// <summary>How one of the four operations went.</summary>
        private sealed class Probe
        {
            public string Key;
            public DimseExchange Exchange;
            public ushort? FinalStatus;
            public string Text;

            public bool Answered => Exchange.Failure == ConnectivityFailure.None && !Exchange.Cancelled;
            public bool Succeeded => Answered && FinalStatus == 0x0000;
            public bool NotOffered => Exchange.Failure == ConnectivityFailure.ContextNotAccepted;
        }

        public async Task<TestResult> ExecuteAsync(TestContext context, CancellationToken token)
        {
            var endpoint = context.Profile.QueryEndpoint;
            var server = $"{endpoint.CalledAe}@{endpoint.Host}:{Fixtures.Int(endpoint.Port)}";
            var seed = context.Session.SeedFor(Purpose);
            // Under the profile's root and from the run's seed, so it cannot be the UID of a real study.
            var studyUid = new DeterministicUidSource(context.Profile.UidRoot, Purpose + "/" + Fixtures.Int(seed)).Uid("study");
            var patientId = TestPatientIds.Random(new Random(seed));

            var result = new TestResult();
            var timelines = new TimelineCollector();
            result.Uids["studyInstanceUid"] = studyUid;
            result.Details["queryRetrieveServer"] = server;

            var studyRoot = await FindAsync(context, endpoint, QueryRoot.Study, new DicomDataset
            {
                { DicomTag.StudyInstanceUID, studyUid },
                { DicomTag.PatientID, string.Empty },
            }, "find.studyRoot", result, timelines, token).ConfigureAwait(false);

            // Refused, unreachable or timed out: the other operations would only say the same.
            if (!studyRoot.Answered && !studyRoot.NotOffered)
            {
                result.Timeline = timelines.Events;
                Fixtures.Set(result, Fixtures.ClassOf(studyRoot.Exchange), $"Q/R at {server}: {studyRoot.Exchange.Message ?? "the query was cancelled."}");
                return result;
            }

            token.ThrowIfCancellationRequested();
            var patientRoot = await FindAsync(context, endpoint, QueryRoot.Patient, new DicomDataset
            {
                { DicomTag.PatientID, patientId },
                { DicomTag.StudyInstanceUID, studyUid },
            }, "find.patientRoot", result, timelines, token).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();
            var move = await MoveAsync(context, endpoint, studyUid, result, timelines, token).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();
            var get = await GetAsync(context, endpoint, studyUid, result, timelines, token).ConfigureAwait(false);

            result.Timeline = timelines.Events;
            Conclude(result, context, server, studyRoot, patientRoot, move, get);
            return result;
        }

        private static async Task<Probe> FindAsync(TestContext context, DicomEndpoint endpoint, QueryRoot root, DicomDataset identifier, string key,
            TestResult result, TimelineCollector timelines, CancellationToken token)
        {
            context.Report($"C-FIND under {root} Root");
            var timeline = timelines.Next();
            var outcome = await QueryService.FindAsync(endpoint, context.Profile.Network, root, DicomQueryRetrieveLevel.Study, identifier, token, timeline).ConfigureAwait(false);
            timelines.Collect(timeline);

            Fixtures.AddContexts(result, outcome.Exchange.Contexts);
            if (outcome.FinalStatus.HasValue) Fixtures.AddStatus(result, "C-FIND", outcome.FinalStatus.Value);

            var probe = new Probe { Key = key, Exchange = outcome.Exchange, FinalStatus = outcome.FinalStatus };
            probe.Text = Describe(probe, probe.Succeeded ? $", {Fixtures.Int(outcome.Matches.Count)} matches" : null);
            result.Details[key] = probe.Text;
            return probe;
        }

        private static async Task<Probe> MoveAsync(TestContext context, DicomEndpoint endpoint, string studyUid, TestResult result, TimelineCollector timelines, CancellationToken token)
        {
            var listener = RetrieveSteps.TryListen(context.Profile, context.Session.NewFolder("retrieved-" + Purpose), new Dictionary<string, string>(), out var problem);
            if (listener == null)
            {
                // Our own problem, and only beside the point here: the result goes by C-FIND.
                result.Details["move"] = "not tried: " + problem;
                return null;
            }

            using (listener)
            {
                context.Report("C-MOVE under Study Root");
                var timeline = timelines.Next();
                var outcome = await RetrieveService.MoveAsync(endpoint, context.Profile.Network, RetrieveSteps.Target(studyUid, string.Empty, QueryRoot.Study),
                    context.Profile.LocalScpAe, listener, token, timeline).ConfigureAwait(false);
                timelines.Collect(timeline);
                RetrieveSteps.Record(result, outcome, "C-MOVE");
                return Retrieved(outcome, "move", result);
            }
        }

        private static async Task<Probe> GetAsync(TestContext context, DicomEndpoint endpoint, string studyUid, TestResult result, TimelineCollector timelines, CancellationToken token)
        {
            context.Report("C-GET under Study Root");
            var timeline = timelines.Next();
            var contexts = RetrieveSteps.Contexts(new[] { DicomUID.CTImageStorage.UID }, DicomTransferSyntax.ExplicitVRLittleEndian.UID.UID);
            var outcome = await RetrieveService.GetAsync(endpoint, context.Profile.Network, RetrieveSteps.Target(studyUid, string.Empty, QueryRoot.Study),
                context.Session.NewFolder("retrieved-" + Purpose + "-get"), contexts, token, timeline).ConfigureAwait(false);
            timelines.Collect(timeline);
            RetrieveSteps.Record(result, outcome, "C-GET");
            return Retrieved(outcome, "get", result);
        }

        private static Probe Retrieved(RetrieveOutcome outcome, string key, TestResult result)
        {
            var probe = new Probe { Key = key, Exchange = outcome.Exchange, FinalStatus = outcome.FinalStatus };
            var counts = probe.Answered && outcome.Completed.HasValue ? $", {Fixtures.Int(outcome.Completed.Value)} sub-operations completed" : null;
            probe.Text = Describe(probe, counts);
            result.Details[key] = probe.Text;
            return probe;
        }

        /// <summary>The same words for the same outcome, run after run: no times.</summary>
        private static string Describe(Probe probe, string extra)
        {
            if (probe.Exchange.Cancelled) return "cancelled";
            if (probe.NotOffered) return "not offered";
            if (!probe.Answered) return "failed: " + (probe.Exchange.Message ?? "no answer");
            if (!probe.FinalStatus.HasValue) return "accepted, but no final response";
            var status = probe.FinalStatus.Value;
            return $"accepted, {AssociationTimeline.FormatStatus(status)} {DimseStatusText.Describe(status)}{extra}";
        }

        private static void Conclude(TestResult result, TestContext context, string server, Probe studyRoot, Probe patientRoot, Probe move, Probe get)
        {
            var finds = new[] { studyRoot, patientRoot };
            var text = $"Q/R at {server}. C-FIND Study Root: {studyRoot.Text}. Patient Root: {patientRoot.Text}. " +
                       $"C-MOVE: {move?.Text ?? result.Details["move"]}. C-GET: {get.Text}.";

            if (move != null && move.FinalStatus == MoveDestinationUnknown)
            {
                text += $" The C-MOVE answer means the system does not know the Listen AET {context.Profile.LocalScpAe}: add it there with this computer's address and port {Fixtures.Int(context.Profile.LocalScpPort)}.";
            }

            TestResultClass outcome;
            if (finds.Any(f => f.Succeeded)) outcome = TestResultClass.PASS;
            else if (finds.Any(f => f.Answered)) outcome = TestResultClass.FAIL;
            else if (finds.All(f => f.NotOffered)) outcome = TestResultClass.NOT_SUPPORTED;
            else outcome = ResultSeverity.Worst(finds.Where(f => !f.NotOffered).Select(f => Fixtures.ClassOf(f.Exchange)));

            if (outcome == TestResultClass.NOT_SUPPORTED) text = $"Neither Study Root nor Patient Root C-FIND is accepted. {text}";
            Fixtures.Set(result, outcome, text);
        }
    }
}
