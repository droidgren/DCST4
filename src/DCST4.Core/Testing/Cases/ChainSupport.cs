using DCST4.Core.Capability;
using DCST4.Core.Generation.Datasets;
using DCST4.Core.Network;
using DCST4.Core.Pipeline;
using FellowOakDicom;
using FellowOakDicom.Network;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Core.Testing.Cases
{
    /// <summary>Hands out one timeline per association of a test and keeps them on a shared time axis.</summary>
    internal sealed class TimelineCollector
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<ProtocolEvent> _events = new List<ProtocolEvent>();
        private int _associations;

        /// <summary>The shared time axis, for steps recorded outside an <see cref="AssociationTimeline"/>.</summary>
        public Stopwatch Clock => _clock;

        public AssociationTimeline Next() => new AssociationTimeline(_clock);

        public void Collect(AssociationTimeline timeline) => Collect(timeline.Events);

        /// <summary>The steps of one association, recorded on <see cref="Clock"/>.</summary>
        public void Collect(IEnumerable<ProtocolEvent> events)
        {
            _associations++;
            foreach (var entry in events)
            {
                entry.Association = _associations;
                _events.Add(entry);
            }
        }

        /// <summary>In the order they were collected, numbered from 0.</summary>
        public List<ProtocolEvent> Events
        {
            get
            {
                for (var i = 0; i < _events.Count; i++) _events[i].Index = i;
                return _events;
            }
        }
    }

    /// <summary>The objects these tests send: small, marked, and following from the run's seed.</summary>
    internal static class Fixtures
    {
        /// <summary>Assume that a PACS cannot delete what it receives.</summary>
        private const int Matrix = 128;

        public const string StorePurpose = "store";

        public static TestDatasetRequest Request(TestContext context, string profileId, string purpose, DicomTransferSyntax syntax, CharacterSetVariant characterSet = CharacterSetVariant.Default, EdgeCases edgeCases = EdgeCases.None) =>
            new TestDatasetRequest
            {
                ProfileId = profileId,
                Seed = context.Session.SeedFor(purpose),
                UidRoot = context.Profile.UidRoot,
                PatientNaming = context.PatientNaming,
                Rows = Matrix,
                Columns = Matrix,
                TransferSyntax = syntax,
                CharacterSet = characterSet,
                EdgeCases = edgeCases,
            };

        /// <summary>Sends a source and keeps what was sent in <paramref name="folder"/>.</summary>
        public static Task<RunSummary> SendAsync(TestContext context, IImageSource source, DicomTransferSyntax syntax, string folder, AssociationTimeline timeline, CancellationToken token)
        {
            var run = new StoreRun(source, new StoreRunOptions
            {
                Send = true,
                Save = true,
                SaveFolder = folder,
                TransferSyntax = syntax,
                Endpoint = context.Profile.Endpoint,
                Network = context.Profile.Network,
                Timeline = timeline,
            });
            return run.ExecuteAsync(token);
        }

        /// <summary>Waits until a system that indexes in the background has caught up. Returns the instances it found.</summary>
        public static async Task<IndexPollResult> WaitForInstancesAsync(TestContext context, string studyUid, string seriesUid, ICollection<string> sopInstanceUids, TimelineCollector timelines, CancellationToken token)
        {
            var result = new IndexPollResult();
            var clock = Stopwatch.StartNew();
            var deadline = TimeSpan.FromSeconds(context.Session.Options.IndexWaitSeconds);
            var first = true;
            context.Report($"Asking with C-FIND whether what was stored can be found, for up to {Int(context.Session.Options.IndexWaitSeconds)} s");

            while (true)
            {
                var identifier = new DicomDataset
                {
                    { DicomTag.StudyInstanceUID, studyUid },
                    { DicomTag.SeriesInstanceUID, seriesUid },
                    { DicomTag.SOPInstanceUID, string.Empty },
                };

                var timeline = first ? timelines?.Next() : null;
                var outcome = await QueryService.FindAsync(context.Profile.Endpoint, context.Profile.Network, QueryRoot.Study, DicomQueryRetrieveLevel.Image, identifier, token, timeline).ConfigureAwait(false);
                if (timeline != null) timelines.Collect(timeline);
                first = false;

                result.Last = outcome;
                if (outcome.Exchange.Failure != ConnectivityFailure.None || outcome.Exchange.Cancelled || !outcome.Succeeded) return result;

                result.Found = outcome.Matches.Select(m => m.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, string.Empty)).Where(sopInstanceUids.Contains).Distinct().ToList();
                if (result.Found.Count >= sopInstanceUids.Count || clock.Elapsed >= deadline) return result;

                await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            }
        }

        public static TestResultClass ClassOf(DimseExchange exchange)
        {
            if (exchange.Cancelled) return TestResultClass.INDETERMINATE;
            switch (exchange.Failure)
            {
                case ConnectivityFailure.None: return TestResultClass.PASS;
                case ConnectivityFailure.Timeout: return TestResultClass.TIMEOUT;
                case ConnectivityFailure.ContextNotAccepted: return TestResultClass.NOT_SUPPORTED;
                case ConnectivityFailure.LocalConfiguration: return TestResultClass.TOOL_ERROR;
                default: return TestResultClass.FAIL;
            }
        }

        public static void AddStatus(TestResult result, string operation, ushort status)
        {
            var code = AssociationTimeline.FormatStatus(status);
            if (result.StatusCodes.Any(s => s.Operation == operation && s.Code == code)) return;
            result.StatusCodes.Add(new DimseStatusRecord { Operation = operation, Code = code, Description = DimseStatusText.Describe(status) });
        }

        public static void AddContexts(TestResult result, IEnumerable<NegotiatedContext> contexts)
        {
            foreach (var context in contexts)
            {
                if (result.NegotiatedContexts.All(c => c.AbstractSyntaxUid != context.AbstractSyntaxUid || c.AcceptedTransferSyntaxUid != context.AcceptedTransferSyntaxUid))
                {
                    result.NegotiatedContexts.Add(context);
                }
            }
        }

        public static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

        public static void Set(TestResult result, TestResultClass resultClass, string explanation)
        {
            result.Result = resultClass;
            result.Explanation = explanation;
        }
    }

    internal sealed class IndexPollResult
    {
        public List<string> Found = new List<string>();
        public QueryOutcome Last;
    }

    /// <summary>The objects of an earlier send, sent again as they are or after a change.</summary>
    internal sealed class FileListSource : IImageSource
    {
        private readonly IReadOnlyList<DicomFile> _files;

        public FileListSource(params DicomFile[] files)
        {
            _files = files;
        }

        public int ExpectedCount => _files.Count;

        public IEnumerable<SourceImage> GetImages(CancellationToken cancellationToken)
        {
            for (var i = 0; i < _files.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new SourceImage(_files[i], 1, i + 1);
            }
        }
    }

    /// <summary>Turns a finished send into a result class. The order matters: how the association ended comes before what the images say.</summary>
    internal static class StoreMapping
    {
        public static ChainStep Classify(RunSummary summary, string what)
        {
            if (summary.WasCancelled) return ChainStep.Of(TestResultClass.INDETERMINATE, $"Cancelled while sending {what}.");

            if (summary.FailureKind != ConnectivityFailure.None)
            {
                var message = $"{what}: {summary.FatalError}";
                switch (summary.FailureKind)
                {
                    case ConnectivityFailure.Timeout: return ChainStep.Of(TestResultClass.TIMEOUT, message);
                    case ConnectivityFailure.ContextNotAccepted: return ChainStep.Of(TestResultClass.NOT_SUPPORTED, message);
                    case ConnectivityFailure.LocalConfiguration: return ChainStep.Of(TestResultClass.TOOL_ERROR, $"DCST4 could not send {what}, which is a local problem and not the fault of the system under test: {summary.FatalError}");
                    default: return ChainStep.Of(TestResultClass.FAIL, message);
                }
            }

            var unsaved = summary.Results.FirstOrDefault(r => r.SaveError != null);
            if (unsaved != null) return ChainStep.Of(TestResultClass.TOOL_ERROR, $"DCST4 could not keep its own copy of {what}: {unsaved.SaveError}");

            var failures = summary.Results.Where(r => r.State == StoreState.Failure).ToList();
            if (failures.Count > 0)
            {
                var first = Describe(failures[0]);
                var allUnsupported = failures.All(r => r.StatusCode == 0x0122);
                return ChainStep.Of(allUnsupported ? TestResultClass.NOT_SUPPORTED : TestResultClass.FAIL, $"{Fixtures.Int(failures.Count)} of {Fixtures.Int(summary.Results.Count)} objects of {what} were refused: {first}");
            }

            var notSent = summary.Results.Where(r => r.State == StoreState.NotSent || r.State == StoreState.Cancel).ToList();
            if (notSent.Count > 0)
            {
                var noContext = notSent.All(r => r.AcceptedTransferSyntax == null);
                return ChainStep.Of(noContext ? TestResultClass.NOT_SUPPORTED : TestResultClass.FAIL, $"{Fixtures.Int(notSent.Count)} objects of {what} got no response: {notSent[0].Description}.");
            }

            if (summary.Results.Count == 0 || summary.Results.Count < summary.Expected)
            {
                return ChainStep.Of(TestResultClass.INDETERMINATE, $"Only {Fixtures.Int(summary.Results.Count)} of {Fixtures.Int(summary.Expected)} objects of {what} could be generated and sent.");
            }

            var warnings = summary.Results.Where(r => r.State == StoreState.Warning).ToList();
            if (warnings.Count > 0)
            {
                return ChainStep.Of(TestResultClass.PASS, $"{what} was stored, with a warning: {Describe(warnings[0])}");
            }

            return ChainStep.Of(TestResultClass.PASS, $"{what} was stored.");
        }

        private static string Describe(StoreResult result)
        {
            var text = $"{result.StatusCodeText} ({result.Description})";
            if (!string.IsNullOrEmpty(result.ErrorComment)) text += ", comment: " + result.ErrorComment;
            if (!string.IsNullOrEmpty(result.OffendingElement)) text += ", offending element " + result.OffendingElement;
            return text + ".";
        }
    }

    /// <summary>Picks the transfer syntax for an object type.</summary>
    internal static class StoreSyntaxSelector
    {
        public const string ByOverride = "user-choice";
        public const string ByMatrix = "capability-matrix";
        public const string ByDefault = "default";

        /// <summary>In the order the matrix is read, so that an uncompressed syntax wins if both are accepted and the test does not depend on an encoder.</summary>
        private static readonly string[] Preference =
        {
            DicomTransferSyntax.ExplicitVRLittleEndian.UID.UID,
            DicomTransferSyntax.ImplicitVRLittleEndian.UID.UID,
            DicomTransferSyntax.JPEGProcess14SV1.UID.UID,
            DicomTransferSyntax.JPEGLSLossless.UID.UID,
            DicomTransferSyntax.JPEG2000Lossless.UID.UID,
            DicomTransferSyntax.RLELossless.UID.UID,
        };

        public static Tuple<DicomTransferSyntax, string> Select(DatasetProfile profile, TestContext context, EdgeCases edgeCases)
        {
            var session = context.Session;
            var chosen = session.Options.SyntaxOverride;
            if (chosen != null && Fits(profile, chosen, context, edgeCases)) return Tuple.Create(chosen, ByOverride);

            if (session.Matrix != null)
            {
                var sopClass = (profile.IsImage ? profile.Image.SopClass : profile.Object.SopClass).UID;
                foreach (var uid in Preference)
                {
                    var cell = session.Matrix.Find(sopClass, uid);
                    if (cell != null && cell.Result == CapabilityResult.Accepted)
                    {
                        var syntax = DicomTransferSyntax.Parse(uid);
                        if (Fits(profile, syntax, context, edgeCases)) return Tuple.Create(syntax, ByMatrix);
                    }
                }
            }

            return Tuple.Create(DicomTransferSyntax.ExplicitVRLittleEndian, ByDefault);
        }

        private static bool Fits(DatasetProfile profile, DicomTransferSyntax syntax, TestContext context, EdgeCases edgeCases) =>
            Fixtures.Request(context, profile.Id, Fixtures.StorePurpose, syntax, edgeCases: edgeCases).Validate().Count == 0;
    }
}
