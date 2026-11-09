using DCST4.Core.Network;
using FellowOakDicom;
using FellowOakDicom.Network;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Core.Testing.Cases
{
    /// <summary>Retrieving one stored object type, shared by the retrieve tests and the duplicate tests.</summary>
    internal static class RetrieveSteps
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(RetrieveSteps));

        public const string NothingStored = "This test needs an object stored earlier in the same run, and no store test stored one.";

        /// <summary>What to ask for in the context of each expected SOP class: the syntax it was sent in, then both uncompressed ones.</summary>
        public static Dictionary<string, DicomTransferSyntax[]> Contexts(IEnumerable<string> sopClasses, string sentSyntaxUid)
        {
            var sent = DicomTransferSyntax.Parse(sentSyntaxUid);
            var syntaxes = new[] { sent, DicomTransferSyntax.ExplicitVRLittleEndian, DicomTransferSyntax.ImplicitVRLittleEndian }.Distinct().ToArray();
            return sopClasses.Distinct().ToDictionary(uid => uid, uid => syntaxes, StringComparer.Ordinal);
        }

        public static Dictionary<string, string> Preferred(IEnumerable<string> sopClasses, string sentSyntaxUid) =>
            sopClasses.Distinct().ToDictionary(uid => uid, uid => sentSyntaxUid, StringComparer.Ordinal);

        /// <summary>Opens the listener, or returns why it could not be, which is a local problem and never the PACS's.</summary>
        public static LocalStorageServer TryListen(TargetProfile profile, string folder, IReadOnlyDictionary<string, string> preferred, out string problem)
        {
            problem = null;
            try
            {
                return LocalStorageServer.Start(profile.LocalScpPort, folder, preferred);
            }
            catch (Exception ex) when (ex is DicomNetworkException || ex is SocketException || ex is InvalidOperationException || ex is DicomException)
            {
                Logger.Error(ex, "Could not listen on port {port}", profile.LocalScpPort);
                problem = $"DCST4 could not listen on port {profile.LocalScpPort.ToString(System.Globalization.CultureInfo.InvariantCulture)} for the C-MOVE, which is a local problem and not a fault of the system under test. Is another program using that port? {NetworkErrors.GetUserMessage(ex)}";
                return null;
            }
        }

        public static RetrieveTarget Target(string studyUid, string patientId, QueryRoot root) =>
            new RetrieveTarget { StudyInstanceUid = studyUid, PatientId = patientId, Root = root };

        public static string PatientIdOf(StoredObjectRecord record)
        {
            var path = record.Stored.Select(i => i.SavedPath).FirstOrDefault(p => p != null);
            return path == null ? string.Empty : DicomFile.Open(path).Dataset.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty);
        }

        public static ChainStep ClassifyGet(RetrieveOutcome outcome, int expected)
        {
            var failure = Fixtures.ClassOf(outcome.Exchange);
            if (failure == TestResultClass.NOT_SUPPORTED) return ChainStep.Of(failure, "The C-GET SOP class is not accepted.");
            if (failure != TestResultClass.PASS) return ChainStep.Of(failure, outcome.Exchange.Message ?? "The C-GET was cancelled.");
            return ChainStep.Of(CountsAgree(outcome, expected) ? TestResultClass.PASS : TestResultClass.FAIL, Describe(outcome, expected));
        }

        private static bool CountsAgree(RetrieveOutcome outcome, int expected) =>
            outcome.ResponseSaysSuccess && outcome.Files.Count == expected;

        public static string Describe(RetrieveOutcome outcome, int expected)
        {
            var status = outcome.FinalStatus.HasValue
                ? $"{AssociationTimeline.FormatStatus(outcome.FinalStatus.Value)} ({DimseStatusText.Describe(outcome.FinalStatus.Value)})"
                : "no final status";
            return $"{Fixtures.Int(outcome.Files.Count)} of {Fixtures.Int(expected)} instances came back, final status {status}.";
        }

        public static void Record(TestResult result, RetrieveOutcome outcome, string operation)
        {
            Fixtures.AddContexts(result, outcome.Exchange.Contexts);
            if (outcome.FinalStatus.HasValue) Fixtures.AddStatus(result, operation, outcome.FinalStatus.Value);
        }

        public static void Conclude(TestResult result, IReadOnlyCollection<StoredObjectRecord> records, Func<StoredObjectRecord, ChainStep> step)
        {
            var worst = ResultSeverity.Worst(records.Select(r => step(r).Result));
            var passed = records.Count(r => step(r).Result == TestResultClass.PASS);
            var firstBad = records.FirstOrDefault(r => step(r).Result == worst && worst != TestResultClass.PASS);

            result.Details["objectTypes"] = Fixtures.Int(records.Count);
            result.Details["retrieved"] = Fixtures.Int(passed);
            Fixtures.Set(result, worst, firstBad != null
                ? $"{Fixtures.Int(passed)} of {Fixtures.Int(records.Count)} object types came back whole. {firstBad.DisplayName}: {step(firstBad).Explanation}"
                : $"All {Fixtures.Int(records.Count)} object types came back whole.");
        }
    }

    /// <summary>C-GET for each stored object type, with the objects coming back on the same association.</summary>
    public sealed class RetrieveGetTest : ITestCase
    {
        public string Id => "retrieve-cget";
        public string Name => "C-GET: every stored object type";
        public TestCategory Category => TestCategory.Integrity;

        public async Task<TestResult> ExecuteAsync(TestContext context, CancellationToken token)
        {
            var records = context.Session.Objects.Where(o => o.Stored.Any()).ToList();
            if (records.Count == 0) return new TestResult { Result = TestResultClass.INDETERMINATE, Explanation = RetrieveSteps.NothingStored };

            var result = new TestResult();
            var timelines = new TimelineCollector();

            foreach (var record in records)
            {
                token.ThrowIfCancellationRequested();
                var expected = record.Stored.Count();
                var contexts = RetrieveSteps.Contexts(record.Stored.Select(i => i.Manifest.SopClassUid), record.TransferSyntaxUid);

                var outcome = await GetAsync(context, record, QueryRoot.Study, contexts, timelines, token).ConfigureAwait(false);
                if (outcome.Exchange.Failure == ConnectivityFailure.ContextNotAccepted)
                {
                    outcome = await GetAsync(context, record, QueryRoot.Patient, contexts, timelines, token).ConfigureAwait(false);
                }

                RetrieveSteps.Record(result, outcome, "C-GET");
                record.CGet = new RetrieveRecord { Method = RetrieveMethod.CGet, Outcome = outcome, Step = RetrieveSteps.ClassifyGet(outcome, expected) };
            }

            result.Timeline = timelines.Events;
            RetrieveSteps.Conclude(result, records, r => r.CGet.Step);
            return result;
        }

        private static async Task<RetrieveOutcome> GetAsync(TestContext context, StoredObjectRecord record, QueryRoot root, IReadOnlyDictionary<string, DicomTransferSyntax[]> contexts, TimelineCollector timelines, CancellationToken token)
        {
            var timeline = timelines.Next();
            var outcome = await RetrieveService.GetAsync(context.Profile.Endpoint, context.Profile.Network,
                RetrieveSteps.Target(record.StudyInstanceUid, RetrieveSteps.PatientIdOf(record), root),
                context.Session.NewFolder("retrieved-cget-" + record.ObjectType), contexts, token, timeline).ConfigureAwait(false);
            timelines.Collect(timeline);
            return outcome;
        }
    }

    /// <summary>C-MOVE of each stored object type to the local Storage SCP.</summary>
    public sealed class RetrieveMoveTest : ITestCase
    {
        public string Id => "retrieve-cmove";
        public string Name => "C-MOVE: every stored object type";
        public TestCategory Category => TestCategory.Integrity;

        public async Task<TestResult> ExecuteAsync(TestContext context, CancellationToken token)
        {
            var records = context.Session.Objects.Where(o => o.Stored.Any()).ToList();
            if (records.Count == 0) return new TestResult { Result = TestResultClass.INDETERMINATE, Explanation = RetrieveSteps.NothingStored };

            var result = new TestResult();
            result.Details["localScpAe"] = context.Profile.LocalScpAe;
            result.Details["localScpPort"] = Fixtures.Int(context.Profile.LocalScpPort);
            var timelines = new TimelineCollector();

            foreach (var record in records)
            {
                token.ThrowIfCancellationRequested();
                var step = await MoveOneAsync(context, record, result, timelines, token).ConfigureAwait(false);
                if (step == null) return result;
            }

            result.Timeline = timelines.Events;
            RetrieveSteps.Conclude(result, records, r => r.CMove.Step);
            return result;
        }

        /// <summary>Null if the listener could not be opened, which ends the test.</summary>
        private static async Task<ChainStep> MoveOneAsync(TestContext context, StoredObjectRecord record, TestResult result, TimelineCollector timelines, CancellationToken token)
        {
            var expected = record.Stored.Count();
            var folder = context.Session.NewFolder("retrieved-cmove-" + record.ObjectType);
            var preferred = RetrieveSteps.Preferred(record.Stored.Select(i => i.Manifest.SopClassUid), record.TransferSyntaxUid);

            var listener = RetrieveSteps.TryListen(context.Profile, folder, preferred, out var problem);
            if (listener == null)
            {
                Fixtures.Set(result, TestResultClass.TOOL_ERROR, problem);
                return null;
            }

            using (listener)
            {
                var outcome = await MoveAsync(context, record, QueryRoot.Study, listener, timelines, token).ConfigureAwait(false);
                if (outcome.Exchange.Failure == ConnectivityFailure.ContextNotAccepted)
                {
                    outcome = await MoveAsync(context, record, QueryRoot.Patient, listener, timelines, token).ConfigureAwait(false);
                }

                RetrieveSteps.Record(result, outcome, "C-MOVE");
                var diagnosis = MoveDiagnosis.Diagnose(outcome, listener.Associations, expected, context.Profile.LocalScpAe, context.Profile.LocalScpPort, context.Profile.Endpoint.UseTls);
                var step = Classify(outcome, diagnosis, expected);
                record.CMove = new RetrieveRecord { Method = RetrieveMethod.CMove, Outcome = outcome, Step = step };
                return step;
            }
        }

        private static async Task<RetrieveOutcome> MoveAsync(TestContext context, StoredObjectRecord record, QueryRoot root, LocalStorageServer listener, TimelineCollector timelines, CancellationToken token)
        {
            var timeline = timelines.Next();
            var outcome = await RetrieveService.MoveAsync(context.Profile.Endpoint, context.Profile.Network,
                RetrieveSteps.Target(record.StudyInstanceUid, RetrieveSteps.PatientIdOf(record), root),
                context.Profile.LocalScpAe, listener, token, timeline).ConfigureAwait(false);
            timelines.Collect(timeline);
            return outcome;
        }

        private static ChainStep Classify(RetrieveOutcome outcome, MoveDiagnosis diagnosis, int expected)
        {
            switch (diagnosis.Kind)
            {
                case MoveDiagnosisKind.Cancelled:
                    return ChainStep.Of(TestResultClass.INDETERMINATE, diagnosis.Explanation);
                case MoveDiagnosisKind.NotSupported:
                    return ChainStep.Of(TestResultClass.NOT_SUPPORTED, diagnosis.Explanation);
                case MoveDiagnosisKind.AssociationFailed:
                    return ChainStep.Of(Fixtures.ClassOf(outcome.Exchange), diagnosis.Explanation);
                case MoveDiagnosisKind.Delivered:
                    return outcome.Files.Count == expected
                        ? ChainStep.Of(TestResultClass.PASS, diagnosis.Explanation)
                        : ChainStep.Of(TestResultClass.FAIL, RetrieveSteps.Describe(outcome, expected));
                default:
                    // Without this listener reached we cannot say whether the PACS can deliver, and that is what this test asks.
                    return ChainStep.Of(diagnosis.IsEnvironmental ? TestResultClass.INDETERMINATE : TestResultClass.FAIL, diagnosis.Explanation);
            }
        }
    }

    /// <summary>A dedicated answer to why a C-MOVE does not arrive, which is usually a matter of how the two systems are set up.</summary>
    public sealed class RetrieveMoveDiagnosticTest : ITestCase
    {
        public string Id => "retrieve-cmove-diagnostic";
        public string Name => "C-MOVE: can the system reach this computer?";
        public TestCategory Category => TestCategory.Integrity;

        public async Task<TestResult> ExecuteAsync(TestContext context, CancellationToken token)
        {
            var record = context.Session.Objects.Where(o => o.Stored.Any()).OrderBy(o => o.Stored.Count()).FirstOrDefault();
            if (record == null) return new TestResult { Result = TestResultClass.INDETERMINATE, Explanation = RetrieveSteps.NothingStored };

            var result = new TestResult();
            result.Details["localScpAe"] = context.Profile.LocalScpAe;
            result.Details["localScpPort"] = Fixtures.Int(context.Profile.LocalScpPort);
            result.Uids["studyInstanceUid"] = record.StudyInstanceUid;

            var expected = record.Stored.Count();
            var listener = RetrieveSteps.TryListen(context.Profile, context.Session.NewFolder("retrieved-diagnostic"), RetrieveSteps.Preferred(record.Stored.Select(i => i.Manifest.SopClassUid), record.TransferSyntaxUid), out var problem);
            if (listener == null)
            {
                Fixtures.Set(result, TestResultClass.TOOL_ERROR, problem);
                return result;
            }

            using (listener)
            {
                var timeline = new AssociationTimeline();
                var outcome = await RetrieveService.MoveAsync(context.Profile.Endpoint, context.Profile.Network,
                    RetrieveSteps.Target(record.StudyInstanceUid, RetrieveSteps.PatientIdOf(record), QueryRoot.Study),
                    context.Profile.LocalScpAe, listener, token, timeline).ConfigureAwait(false);

                result.Timeline = timeline.Events;
                RetrieveSteps.Record(result, outcome, "C-MOVE");

                var diagnosis = MoveDiagnosis.Diagnose(outcome, listener.Associations, expected, context.Profile.LocalScpAe, context.Profile.LocalScpPort, context.Profile.Endpoint.UseTls);
                result.Details["diagnosis"] = diagnosis.Kind.ToString();
                result.Details["incomingAssociations"] = Fixtures.Int(listener.Associations.Count);
                Fixtures.Set(result, ResultFor(outcome, diagnosis), diagnosis.Explanation);
            }
            return result;
        }

        private static TestResultClass ResultFor(RetrieveOutcome outcome, MoveDiagnosis diagnosis)
        {
            switch (diagnosis.Kind)
            {
                case MoveDiagnosisKind.Delivered: return TestResultClass.PASS;
                case MoveDiagnosisKind.Cancelled: return TestResultClass.INDETERMINATE;
                case MoveDiagnosisKind.NotSupported: return TestResultClass.NOT_SUPPORTED;
                case MoveDiagnosisKind.AssociationFailed: return Fixtures.ClassOf(outcome.Exchange);
                default: return TestResultClass.FAIL;
            }
        }
    }
}
