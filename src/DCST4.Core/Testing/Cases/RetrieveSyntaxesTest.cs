using DCST4.Core.Capability;
using DCST4.Core.Codecs;
using DCST4.Core.Generation.Datasets;
using DCST4.Core.Network;
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
    /// The retrieve matrix: which transfer syntaxes the system under test can deliver on C-GET, per SOP class. One stored
    /// object per SOP class is retrieved once per syntax, each time with exactly that one syntax proposed for it.
    /// </summary>
    public sealed class RetrieveSyntaxesTest : ITestCase
    {
        /// <summary>A system that does not answer is not asked again and again, each time for its whole response timeout.</summary>
        internal const int TimeoutsBeforeStopping = 2;

        private static readonly DicomTransferSyntax[] Native =
        {
            DicomTransferSyntax.ImplicitVRLittleEndian, DicomTransferSyntax.ExplicitVRLittleEndian,
            DicomTransferSyntax.ExplicitVRBigEndian, DicomTransferSyntax.DeflatedExplicitVRLittleEndian,
        };

        private readonly IReadOnlyList<DicomTransferSyntax> _syntaxes;

        /// <param name="syntaxes">What to ask for, or null for every syntax in <see cref="CapabilityCatalog"/>.</param>
        public RetrieveSyntaxesTest(IReadOnlyList<DicomTransferSyntax> syntaxes = null)
        {
            _syntaxes = (syntaxes ?? CapabilityCatalog.TransferSyntaxes.Select(s => s.Syntax).ToList()).Distinct().ToList();
        }

        public string Id => "retrieve-syntaxes";
        public string Name => "C-GET: which transfer syntaxes the system can deliver";
        public TestCategory Category => TestCategory.Capability;
        public string Description => "Retrieves one stored object per SOP class with C-GET, once per transfer syntax, each time proposing only that syntax. Fills the retrieve matrix on the Capabilities tab: what the system can deliver, next to what it accepts on store. Needs a store test earlier in the same run.";
        public string Group => TestGroups.Retrieve;
        public bool StoresObjects => false;
        public StorageEstimate Storage => null;

        /// <summary>The syntaxes that make sense for an object: without pixel data only the native ones, and video only for more than one frame.</summary>
        public static IReadOnlyList<DicomTransferSyntax> Fitted(IReadOnlyList<DicomTransferSyntax> syntaxes, bool hasPixelData, bool multiFrame)
        {
            if (!hasPixelData) return syntaxes.Where(s => Native.Contains(s)).ToList();
            return multiFrame ? syntaxes : syntaxes.Where(s => !OwnCodecs.IsVideo(s)).ToList();
        }

        public async Task<TestResult> ExecuteAsync(TestContext context, CancellationToken token)
        {
            var rows = Rows(context.Session);
            if (rows.Count == 0) return new TestResult { Result = TestResultClass.INDETERMINATE, Explanation = RetrieveSteps.NothingStored };

            var result = new TestResult();
            var matrix = NewMatrix(context, rows);
            var timelines = new TimelineCollector();
            var root = QueryRoot.Study;
            var rootKnown = false;
            var association = 0;
            var timeouts = 0;
            var irregular = new List<string>();
            string stopped = null;
            var total = rows.Sum(r => r.Syntaxes.Count);
            var done = 0;

            foreach (var row in rows)
            {
                var matrixRow = matrix.Rows.First(r => r.SopClassUid == row.SopClassUid);
                foreach (var syntax in row.Syntaxes)
                {
                    if (token.IsCancellationRequested || stopped != null) break;
                    done++;
                    context.Report($"C-GET of {row.Record.DisplayName} in {CapabilityCatalog.ShortName(syntax)}, {Fixtures.Int(done)} of {Fixtures.Int(total)}");

                    var outcome = await GetAsync(context, row, syntax, root, timelines, token).ConfigureAwait(false);
                    association++;
                    if (!rootKnown && outcome.Exchange.Failure == ConnectivityFailure.ContextNotAccepted)
                    {
                        matrix.Associations.Add(AssociationRecord(association, outcome.Exchange));
                        root = QueryRoot.Patient;
                        outcome = await GetAsync(context, row, syntax, root, timelines, token).ConfigureAwait(false);
                        association++;
                        if (outcome.Exchange.Failure == ConnectivityFailure.ContextNotAccepted)
                        {
                            matrix.Associations.Add(AssociationRecord(association, outcome.Exchange));
                            Fixtures.AddContexts(result, outcome.Exchange.Contexts);
                            return Finish(result, matrix, timelines, TestResultClass.NOT_SUPPORTED, "The C-GET SOP class is not accepted, with Study Root or Patient Root, so nothing can be retrieved this way.");
                        }
                    }
                    if (outcome.Exchange.Failure != ConnectivityFailure.ContextNotAccepted && outcome.Exchange.AssociationAccepted) rootKnown = true;

                    matrix.Associations.Add(AssociationRecord(association, outcome.Exchange));
                    Fixtures.AddContexts(result, outcome.Exchange.Contexts);
                    if (outcome.FinalStatus.HasValue) Fixtures.AddStatus(result, "C-GET", outcome.FinalStatus.Value);

                    var cell = matrixRow.Cells.First(c => c.TransferSyntaxUid == syntax.UID.UID);
                    var problem = Fill(cell, outcome, row, syntax, association);
                    if (problem != null) irregular.Add($"{row.Record.DisplayName} in {CapabilityCatalog.ShortName(syntax)}: {problem}");

                    if (outcome.Exchange.Failure == ConnectivityFailure.LocalConfiguration)
                    {
                        return Finish(result, matrix, timelines, TestResultClass.TOOL_ERROR, $"DCST4 could not set up the connection, which is a local problem and not a fault of the system under test: {outcome.Exchange.Message}");
                    }
                    if (outcome.Exchange.Failure == ConnectivityFailure.Timeout && ++timeouts >= TimeoutsBeforeStopping)
                    {
                        stopped = $"Stopped after {Fixtures.Int(timeouts)} C-GETs timed out, so the rest were not tried. The last: {outcome.Exchange.Message}";
                    }
                }
            }

            result.Details["root"] = root.ToString();
            return Conclude(context, result, matrix, timelines, token.IsCancellationRequested, timeouts, stopped, irregular);
        }

        private sealed class Row
        {
            public string SopClassUid;
            public StoredObjectRecord Record;
            public StoredInstance Instance;
            public string PatientId;
            public IReadOnlyList<DicomTransferSyntax> Syntaxes;
        }

        /// <summary>One row per SOP class, from the first object type stored with it, so that the plain one comes before its variants.</summary>
        private List<Row> Rows(TestRunSession session)
        {
            var rows = new List<Row>();
            foreach (var record in session.Objects.Where(o => o.Stored.Any()))
            {
                var instance = record.Stored.FirstOrDefault(i => i.Manifest.Role == ManifestInstance.RoleTarget) ?? record.Stored.First();
                if (rows.Any(r => r.SopClassUid == instance.Manifest.SopClassUid)) continue;

                var dataset = DicomFile.Open(instance.SavedPath).Dataset;
                var hasPixelData = dataset.Contains(DicomTag.PixelData) || dataset.Contains(DicomTag.FloatPixelData) || dataset.Contains(DicomTag.DoubleFloatPixelData);
                var multiFrame = dataset.GetSingleValueOrDefault(DicomTag.NumberOfFrames, 1) > 1;
                var syntaxes = Fitted(_syntaxes, hasPixelData, multiFrame);
                if (syntaxes.Count == 0) continue;

                var patientId = dataset.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty);
                rows.Add(new Row { SopClassUid = instance.Manifest.SopClassUid, Record = record, Instance = instance, PatientId = patientId, Syntaxes = syntaxes });
            }
            return rows;
        }

        private CapabilityMatrix NewMatrix(TestContext context, List<Row> rows)
        {
            var used = _syntaxes.Where(s => rows.Any(r => r.Syntaxes.Contains(s))).ToList();
            var matrix = new CapabilityMatrix
            {
                ToolVersion = ProductInfo.Version,
                Target = ResultProfile.From(context.Profile),
                TransferSyntaxes = used.Select(s => new MatrixSyntax { Uid = s.UID.UID, Name = s.UID.Name, ShortName = CapabilityCatalog.ShortName(s) }).ToList(),
            };
            foreach (var row in rows)
            {
                matrix.Rows.Add(new MatrixRow
                {
                    SopClassUid = row.SopClassUid,
                    SopClassName = CapabilityMatrix.DisplayName(row.SopClassUid),
                    Cells = row.Syntaxes.Select(s => new MatrixCell { TransferSyntaxUid = s.UID.UID, Result = CapabilityResult.NotProbed }).ToList(),
                });
            }
            return matrix;
        }

        private static async Task<RetrieveOutcome> GetAsync(TestContext context, Row row, DicomTransferSyntax syntax, QueryRoot root, TimelineCollector timelines, CancellationToken token)
        {
            var target = RetrieveSteps.Target(row.Record.StudyInstanceUid, row.PatientId, root);
            target.SeriesInstanceUid = row.Instance.Manifest.SeriesInstanceUid;
            target.SopInstanceUid = row.Instance.Manifest.SopInstanceUid;
            var contexts = new Dictionary<string, DicomTransferSyntax[]>(StringComparer.Ordinal) { { row.SopClassUid, new[] { syntax } } };

            var timeline = timelines.Next();
            var outcome = await RetrieveService.GetAsync(context.Profile.QueryEndpoint, context.Profile.Network, target,
                context.Session.NewFolder("retrieved-syntaxes-" + row.Record.ObjectType), contexts, token, timeline).ConfigureAwait(false);
            timelines.Collect(timeline);
            return outcome;
        }

        /// <summary>Fills the cell. Returns what was irregular about the answer, or null.</summary>
        private static string Fill(MatrixCell cell, RetrieveOutcome outcome, Row row, DicomTransferSyntax syntax, int association)
        {
            var exchange = outcome.Exchange;
            cell.Association = association;
            cell.Status = outcome.FinalStatus.HasValue ? AssociationTimeline.FormatStatus(outcome.FinalStatus.Value) : null;

            if (exchange.Cancelled)
            {
                cell.Result = CapabilityResult.NotProbed;
                return null;
            }
            if (exchange.Failure == ConnectivityFailure.AssociationRejected)
            {
                cell.Result = CapabilityResult.AssociationRejected;
                return null;
            }
            if (!exchange.AssociationAccepted || (exchange.Failure != ConnectivityFailure.None && exchange.Failure != ConnectivityFailure.ContextNotAccepted && outcome.Files.Count == 0 && !outcome.FinalStatus.HasValue))
            {
                cell.Result = CapabilityResult.AssociationFailed;
                return null;
            }

            var storage = exchange.Contexts.FirstOrDefault(c => c.AbstractSyntaxUid == row.SopClassUid);
            if (storage != null)
            {
                cell.PresentationContextId = storage.Id;
                cell.AcceptedTransferSyntaxUid = storage.AcceptedTransferSyntaxUid;
            }

            var negotiated = NegotiatedResult(storage, syntax);
            var delivered = outcome.Files.Any(f => f.SopInstanceUid == row.Instance.Manifest.SopInstanceUid);
            switch (negotiated)
            {
                case CapabilityResult.Accepted:
                    if (delivered)
                    {
                        cell.Result = CapabilityResult.Accepted;
                        return null;
                    }
                    cell.Result = exchange.Failure == ConnectivityFailure.None ? CapabilityResult.NotDelivered : CapabilityResult.AssociationFailed;
                    if (exchange.Failure != ConnectivityFailure.None) return null;
                    // Saying that all went well, and sending nothing, is the one answer that is not allowed.
                    return outcome.ResponseSaysSuccess && (outcome.Failed ?? 0) == 0
                        ? $"the context was accepted and the C-GET ended with Success, but the object did not come ({RetrieveSteps.Describe(outcome, 1)})"
                        : null;
                case CapabilityResult.AcceptedOtherSyntax:
                    cell.Result = CapabilityResult.AcceptedOtherSyntax;
                    return $"the context was accepted with {CapabilityMatrix.DisplayName(storage.AcceptedTransferSyntaxUid)}, which was not proposed";
                case CapabilityResult.NotAnswered:
                    cell.Result = CapabilityResult.NotAnswered;
                    return "the A-ASSOCIATE-AC had no answer for the storage context, which PS3.8 requires";
                default:
                    cell.Result = negotiated;
                    return delivered ? "the object came, although its context was not accepted" : null;
            }
        }

        private static CapabilityResult NegotiatedResult(NegotiatedContext context, DicomTransferSyntax proposed)
        {
            if (context == null || !Enum.TryParse(context.Result, out DicomPresentationContextResult result)) return CapabilityResult.NotAnswered;
            switch (result)
            {
                case DicomPresentationContextResult.Accept:
                    return context.AcceptedTransferSyntaxUid == proposed.UID.UID ? CapabilityResult.Accepted : CapabilityResult.AcceptedOtherSyntax;
                case DicomPresentationContextResult.Proposed: return CapabilityResult.NotAnswered;
                case DicomPresentationContextResult.RejectUser: return CapabilityResult.UserRejection;
                case DicomPresentationContextResult.RejectAbstractSyntaxNotSupported: return CapabilityResult.AbstractSyntaxNotSupported;
                case DicomPresentationContextResult.RejectTransferSyntaxesNotSupported: return CapabilityResult.TransferSyntaxNotSupported;
                default: return CapabilityResult.NoReason;
            }
        }

        private static ProbeAssociationRecord AssociationRecord(int index, DimseExchange exchange) => new ProbeAssociationRecord
        {
            Index = index,
            ProposedContexts = 2,
            Ending = EndingOf(exchange),
            Detail = exchange.Message,
        };

        private static AssociationEnding EndingOf(DimseExchange exchange)
        {
            if (exchange.Cancelled) return AssociationEnding.Cancelled;
            switch (exchange.Failure)
            {
                case ConnectivityFailure.None:
                case ConnectivityFailure.ContextNotAccepted:
                case ConnectivityFailure.DimseStatus: return AssociationEnding.Released;
                case ConnectivityFailure.Timeout: return AssociationEnding.TimedOut;
                case ConnectivityFailure.AssociationRejected: return AssociationEnding.Rejected;
                case ConnectivityFailure.Aborted: return AssociationEnding.Aborted;
                case ConnectivityFailure.LocalConfiguration: return AssociationEnding.LocalProblem;
                default: return AssociationEnding.ConnectionFailed;
            }
        }

        private static TestResult Conclude(TestContext context, TestResult result, CapabilityMatrix matrix, TimelineCollector timelines, bool cancelled, int timeouts, string stopped, List<string> irregular)
        {
            var cells = matrix.AllCells().ToList();
            var delivered = cells.Count(c => c.Item2.Result == CapabilityResult.Accepted);
            var failed = cells.Count(c => c.Item2.Result == CapabilityResult.AssociationFailed || c.Item2.Result == CapabilityResult.AssociationRejected);
            var none = matrix.Rows.Where(r => r.Cells.All(c => CapabilityMatrix.WasAnswered(c.Result)) && r.Cells.All(c => c.Result != CapabilityResult.Accepted)).ToList();

            result.Details["combinations"] = Fixtures.Int(cells.Count);
            result.Details["delivered"] = Fixtures.Int(delivered);
            result.Details["sopClasses"] = Fixtures.Int(matrix.Rows.Count);

            var summary = $"{Fixtures.Int(delivered)} of {Fixtures.Int(cells.Count)} combinations delivered, for {Fixtures.Int(matrix.Rows.Count)} SOP classes.";
            if (context.Session.Matrix != null)
            {
                var comparison = StoreRetrieveComparison.Compare(context.Session.Matrix, matrix);
                var notDelivered = comparison.OfKind(StoreRetrieveDifferenceKind.StoredButNotDelivered).Count();
                var transcoded = comparison.OfKind(StoreRetrieveDifferenceKind.DeliveredButNotStored).Count();
                result.Details["storedButNotDelivered"] = Fixtures.Int(notDelivered);
                result.Details["deliveredButNotStored"] = Fixtures.Int(transcoded);
                summary += $" Against the store matrix: {Fixtures.Int(notDelivered)} accepted on store but not delivered, {Fixtures.Int(transcoded)} delivered but not accepted on store.";
            }

            if (cancelled) return Finish(result, matrix, timelines, TestResultClass.INDETERMINATE, "The test was cancelled before it finished. " + summary);
            if (timeouts > 0) return Finish(result, matrix, timelines, TestResultClass.TIMEOUT, (stopped ?? $"{Fixtures.Int(timeouts)} C-GET timed out.") + " " + summary);
            if (irregular.Count > 0) return Finish(result, matrix, timelines, TestResultClass.FAIL, $"{Fixtures.Int(irregular.Count)} answers were irregular. {irregular[0]}. {summary}");
            if (failed > 0) return Finish(result, matrix, timelines, TestResultClass.FAIL, $"{Fixtures.Int(failed)} C-GET associations failed or were rejected. {summary}");
            if (none.Count > 0) return Finish(result, matrix, timelines, TestResultClass.FAIL, $"{none[0].SopClassName} was delivered in none of the syntaxes asked for. {summary}");
            return Finish(result, matrix, timelines, TestResultClass.PASS, summary);
        }

        private static TestResult Finish(TestResult result, CapabilityMatrix matrix, TimelineCollector timelines, TestResultClass resultClass, string explanation)
        {
            result.RetrieveMatrix = matrix;
            result.Timeline = timelines.Events;
            Fixtures.Set(result, resultClass, explanation);
            return result;
        }
    }
}
