using DCST4.Core.Network;
using DCST4.Core.Testing;
using FellowOakDicom;
using FellowOakDicom.Network;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Core.Capability
{
    /// <summary>Everything the probe learned. Rejections, aborts and timeouts end up here and are never thrown.</summary>
    public sealed class NegotiationProbeOutcome
    {
        public CapabilityMatrix Matrix { get; set; }
        public List<NegotiatedContext> Contexts { get; } = new List<NegotiatedContext>();
        public List<ProtocolEvent> Timeline { get; } = new List<ProtocolEvent>();
        public List<DimseStatusRecord> StatusCodes { get; } = new List<DimseStatusRecord>();
        public bool Cancelled { get; set; }
    }

    /// <summary>
    /// Finds out what an SCP supports from association negotiation alone. Each presentation context holds one transfer syntax,
    /// so the answer is per syntax and not just the first one accepted. No object is ever stored.
    ///
    /// fo-dicom needs a request to open an association. A plain C-ECHO makes it add one context of its own, Verification with
    /// Implicit VR, as ID 1, and the rest are added after it unchanged. After the A-ASSOCIATE-AC a context no longer holds what
    /// was proposed, so results are paired with the plan by their position among the contexts, which are sorted by ID.
    /// </summary>
    public static class NegotiationProbe
    {
        private const string ReleaseResponse = "A-RELEASE-RP";

        private static readonly ILogger Logger = Log.ForContext(typeof(NegotiationProbe));

        private sealed class AnsweredContext
        {
            public int Id;
            public string AbstractSyntaxUid;
            public DicomPresentationContextResult Result;
            public string AcceptedSyntaxUid;
            public bool? ScuRole;
            public bool? ScpRole;
        }

        private sealed class ExtendedAnswer
        {
            public string SopClassUid;
            public string Requested;
            public string Accepted;
        }

        /// <summary>A copy of the accepted association, taken while fo-dicom raises the event.</summary>
        private sealed class AcceptedSnapshot
        {
            public List<AnsweredContext> Contexts;
            public List<ExtendedAnswer> Extended;
            public string ImplementationClassUid;
            public string ImplementationVersionName;
            public long MaxPduLength;
        }

        public static async Task<NegotiationProbeOutcome> RunAsync(TargetProfile profile, ProbeOptions options, CancellationToken token)
        {
            var plan = ProbePlan.Create(options);
            var outcome = new NegotiationProbeOutcome { Matrix = NewMatrix(plan, profile) };
            var clock = Stopwatch.StartNew();
            Logger.Information("Negotiation probe: {cells} combinations in {associations} associations", plan.SopClasses.Count * plan.TransferSyntaxes.Count, plan.Associations.Count);

            var stopped = false;
            foreach (var association in plan.Associations)
            {
                if (stopped || token.IsCancellationRequested)
                {
                    outcome.Matrix.Associations.Add(new ProbeAssociationRecord { Index = association.Index, IsFeatureProbe = association.IsFeatureProbe, ProposedContexts = association.Contexts.Count + 1, Ending = AssociationEnding.NotStarted });
                    continue;
                }

                stopped = await RunAssociationAsync(profile, plan, association, outcome, clock, token).ConfigureAwait(false);
            }

            outcome.Cancelled = token.IsCancellationRequested;
            var identified = outcome.Matrix.Associations.FirstOrDefault(a => a.RemoteImplementationClassUid != null);
            if (identified != null)
            {
                outcome.Matrix.RemoteImplementationClassUid = identified.RemoteImplementationClassUid;
                outcome.Matrix.RemoteImplementationVersionName = identified.RemoteImplementationVersionName;
                outcome.Matrix.RemoteMaxPduLength = identified.RemoteMaxPduLength;
            }
            return outcome;
        }

        /// <summary>Returns true if there is no point in opening another association.</summary>
        private static async Task<bool> RunAssociationAsync(TargetProfile profile, ProbePlan plan, ProbeAssociationPlan association, NegotiationProbeOutcome outcome, Stopwatch clock, CancellationToken token)
        {
            var timeline = new AssociationTimeline(clock);
            AcceptedSnapshot accepted = null;

            var echo = await EchoService.EchoAsync(profile.Endpoint, profile.Network, token, timeline, (client, request) =>
            {
                client.AdditionalPresentationContexts.AddRange(association.Contexts.Select(ToPresentationContext));
                client.AdditionalExtendedNegotiations.AddRange(association.ExtendedNegotiations);
                client.AssociationAccepted += (s, e) => accepted = Snapshot(e.Association);
            }).ConfigureAwait(false);

            var events = timeline.Events;
            foreach (var entry in events)
            {
                entry.Association = association.Index;
                entry.Index = outcome.Timeline.Count;
                outcome.Timeline.Add(entry);
            }

            if (echo.StatusCode.HasValue)
            {
                outcome.StatusCodes.Add(new DimseStatusRecord
                {
                    Operation = "C-ECHO",
                    Code = AssociationTimeline.FormatStatus(echo.StatusCode.Value),
                    Description = StoreStatus.Describe(echo.StatusCode.Value),
                });
            }

            var record = new ProbeAssociationRecord
            {
                Index = association.Index,
                IsFeatureProbe = association.IsFeatureProbe,
                ProposedContexts = association.Contexts.Count + 1,
                Ending = EndingOf(echo, timeline, events),
            };
            if (timeline.Rejection != null)
            {
                record.Rejection = new AssociationRejectionRecord { Result = timeline.Rejection.Result, Source = timeline.Rejection.Source, Reason = timeline.Rejection.Reason };
            }
            if (record.Ending != AssociationEnding.Released) record.Detail = echo.Message;
            outcome.Matrix.Associations.Add(record);

            if (accepted != null)
            {
                record.RemoteImplementationClassUid = accepted.ImplementationClassUid;
                record.RemoteImplementationVersionName = accepted.ImplementationVersionName;
                record.RemoteMaxPduLength = accepted.MaxPduLength;
                ApplyAnswers(plan, association, accepted, outcome);
            }
            else if (!association.IsFeatureProbe)
            {
                ApplyFailure(plan, association, record.Ending, outcome.Matrix);
            }

            if (record.Ending != AssociationEnding.Released)
            {
                Logger.Warning("Negotiation probe: association {index} ended as {ending:l}: {detail:l}", association.Index, record.Ending.ToString(), record.Detail);
            }

            return record.Ending == AssociationEnding.TimedOut || record.Ending == AssociationEnding.ConnectionFailed
                || record.Ending == AssociationEnding.LocalProblem || record.Ending == AssociationEnding.Cancelled;
        }

        private static DicomPresentationContext ToPresentationContext(ProbeContext context)
        {
            // Role null is written as 0 on the wire. fo-dicom overwrites it when a role selection reply arrives,
            // and leaves it null when none does, which is the only way to tell "no reply" from "agreed".
            var presentation = new DicomPresentationContext(0, context.SopClass, null, context.ProposeScpRole ? true : (bool?)null);
            presentation.AddTransferSyntax(context.Syntax);
            return presentation;
        }

        private static AcceptedSnapshot Snapshot(DicomAssociation association) => new AcceptedSnapshot
        {
            Contexts = association.PresentationContexts.Select(c => new AnsweredContext
            {
                Id = c.ID,
                AbstractSyntaxUid = c.AbstractSyntax.UID,
                Result = c.Result,
                AcceptedSyntaxUid = c.Result == DicomPresentationContextResult.Accept ? c.AcceptedTransferSyntax?.UID.UID : null,
                ScuRole = c.UserRole,
                ScpRole = c.ProviderRole,
            }).ToList(),
            Extended = association.ExtendedNegotiations.Select(e => new ExtendedAnswer
            {
                SopClassUid = e.SopClassUid.UID,
                Requested = Hex(e.RequestedApplicationInfo),
                Accepted = Hex(e.AcceptedApplicationInfo),
            }).ToList(),
            ImplementationClassUid = association.RemoteImplementationClassUID?.UID,
            ImplementationVersionName = association.RemoteImplementationVersion,
            MaxPduLength = association.MaximumPDULength,
        };

        private static string Hex(DicomServiceApplicationInfo information) =>
            information == null ? null : string.Join(" ", information.GetValues().Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

        private static AssociationEnding EndingOf(ConnectivityResult echo, AssociationTimeline timeline, List<ProtocolEvent> events)
        {
            if (echo.Outcome == ConnectivityOutcome.Cancelled) return AssociationEnding.Cancelled;
            if (timeline.Rejection != null) return AssociationEnding.Rejected;
            if (echo.Failure == ConnectivityFailure.LocalConfiguration) return AssociationEnding.LocalProblem;
            if (events.LastOrDefault()?.Type == ReleaseResponse) return AssociationEnding.Released;
            if (echo.Failure == ConnectivityFailure.Aborted) return AssociationEnding.Aborted;
            if (echo.Failure == ConnectivityFailure.Timeout) return AssociationEnding.TimedOut;
            return AssociationEnding.ConnectionFailed;
        }

        private static void ApplyAnswers(ProbePlan plan, ProbeAssociationPlan association, AcceptedSnapshot accepted, NegotiationProbeOutcome outcome)
        {
            // One entry per proposed context, whatever the SCP did, because fo-dicom keeps them all. A mismatch is our own bug.
            if (accepted.Contexts.Count != association.Contexts.Count + 1)
            {
                throw new InvalidOperationException($"Association {association.Index} proposed {association.Contexts.Count + 1} contexts, and fo-dicom reports {accepted.Contexts.Count}.");
            }

            var echoContext = accepted.Contexts[0];
            CheckPairing(echoContext, ProbePlan.EchoSopClass, association);
            AddNegotiated(outcome, ProbePlan.EchoSopClass, ProbePlan.EchoSyntax, echoContext);

            for (var i = 0; i < association.Contexts.Count; i++)
            {
                var planned = association.Contexts[i];
                var answer = accepted.Contexts[i + 1];
                CheckPairing(answer, planned.SopClass, association);
                AddNegotiated(outcome, planned.SopClass, planned.Syntax, answer);
                if (!association.IsFeatureProbe) Fill(outcome.Matrix, planned.SopClass, planned.Syntax, answer, association.Index);
                if (planned.ProposeScpRole) outcome.Matrix.RoleSelectionReplies.Add(RoleReply(planned.SopClass, answer));
            }

            // Any association can answer the C-ECHO's own cell, and the first one that does counts.
            if (plan.CoversEchoContext && !CapabilityMatrix.WasAnswered(outcome.Matrix.Find(ProbePlan.EchoSopClass.UID, ProbePlan.EchoSyntax.UID.UID).Result))
            {
                Fill(outcome.Matrix, ProbePlan.EchoSopClass, ProbePlan.EchoSyntax, echoContext, association.Index);
            }

            if (association.IsFeatureProbe)
            {
                outcome.Matrix.ExtendedNegotiationReplies.AddRange(accepted.Extended.Select(e => new ExtendedNegotiationReply
                {
                    SopClassUid = e.SopClassUid,
                    SopClassName = CapabilityMatrix.DisplayName(e.SopClassUid),
                    Requested = e.Requested,
                    Replied = e.Accepted != null,
                    Accepted = e.Accepted,
                }));
            }
        }

        private static void CheckPairing(AnsweredContext answer, DicomUID expected, ProbeAssociationPlan association)
        {
            if (answer.AbstractSyntaxUid != expected.UID)
            {
                throw new InvalidOperationException($"Association {association.Index}: context {answer.Id} holds {answer.AbstractSyntaxUid}, where {expected.UID} was planned.");
            }
        }

        private static RoleSelectionReply RoleReply(DicomUID sopClass, AnsweredContext answer) => new RoleSelectionReply
        {
            SopClassUid = sopClass.UID,
            SopClassName = sopClass.Name,
            Replied = answer.ScuRole.HasValue,
            ScuRole = answer.ScuRole.HasValue ? answer.ScuRole : null,
            ScpRole = answer.ScuRole.HasValue ? answer.ScpRole : null,
        };

        private static void AddNegotiated(NegotiationProbeOutcome outcome, DicomUID sopClass, DicomTransferSyntax syntax, AnsweredContext answer)
        {
            outcome.Contexts.Add(new NegotiatedContext
            {
                Id = answer.Id,
                AbstractSyntaxUid = sopClass.UID,
                AbstractSyntaxName = sopClass.Name,
                ProposedTransferSyntaxUids = new List<string> { syntax.UID.UID },
                Result = answer.Result.ToString(),
                AcceptedTransferSyntaxUid = answer.AcceptedSyntaxUid,
            });
        }

        private static void Fill(CapabilityMatrix matrix, DicomUID sopClass, DicomTransferSyntax syntax, AnsweredContext answer, int association)
        {
            var cell = matrix.Find(sopClass.UID, syntax.UID.UID);
            cell.Result = ResultOf(answer, syntax);
            cell.AcceptedTransferSyntaxUid = answer.AcceptedSyntaxUid;
            cell.Association = association;
            cell.PresentationContextId = answer.Id;
        }

        private static CapabilityResult ResultOf(AnsweredContext answer, DicomTransferSyntax proposed)
        {
            switch (answer.Result)
            {
                case DicomPresentationContextResult.Accept:
                    return answer.AcceptedSyntaxUid == proposed.UID.UID ? CapabilityResult.Accepted : CapabilityResult.AcceptedOtherSyntax;
                case DicomPresentationContextResult.Proposed: return CapabilityResult.NotAnswered;
                case DicomPresentationContextResult.RejectUser: return CapabilityResult.UserRejection;
                case DicomPresentationContextResult.RejectAbstractSyntaxNotSupported: return CapabilityResult.AbstractSyntaxNotSupported;
                case DicomPresentationContextResult.RejectTransferSyntaxesNotSupported: return CapabilityResult.TransferSyntaxNotSupported;
                default: return CapabilityResult.NoReason;
            }
        }

        private static void ApplyFailure(ProbePlan plan, ProbeAssociationPlan association, AssociationEnding ending, CapabilityMatrix matrix)
        {
            CapabilityResult result;
            switch (ending)
            {
                case AssociationEnding.Rejected: result = CapabilityResult.AssociationRejected; break;
                case AssociationEnding.Cancelled:
                case AssociationEnding.LocalProblem: return;
                default: result = CapabilityResult.AssociationFailed; break;
            }

            foreach (var context in association.Contexts) Mark(matrix.Find(context.SopClass.UID, context.Syntax.UID.UID), result, association.Index);

            if (plan.CoversEchoContext)
            {
                var echoCell = matrix.Find(ProbePlan.EchoSopClass.UID, ProbePlan.EchoSyntax.UID.UID);
                if (echoCell.Result == CapabilityResult.NotProbed) Mark(echoCell, result, association.Index);
            }
        }

        private static void Mark(MatrixCell cell, CapabilityResult result, int association)
        {
            cell.Result = result;
            cell.Association = association;
        }

        private static CapabilityMatrix NewMatrix(ProbePlan plan, TargetProfile profile)
        {
            var matrix = new CapabilityMatrix
            {
                ToolVersion = ProductInfo.Version,
                Target = ResultProfile.From(profile),
                TransferSyntaxes = plan.TransferSyntaxes.Select(s => new MatrixSyntax { Uid = s.UID.UID, Name = s.UID.Name, ShortName = CapabilityCatalog.ShortName(s) }).ToList(),
            };

            foreach (var sop in plan.SopClasses)
            {
                matrix.Rows.Add(new MatrixRow
                {
                    SopClassUid = sop.UID,
                    SopClassName = sop.Name,
                    Cells = plan.TransferSyntaxes.Select(s => new MatrixCell { TransferSyntaxUid = s.UID.UID, Result = CapabilityResult.NotProbed }).ToList(),
                });
            }
            return matrix;
        }
    }
}
