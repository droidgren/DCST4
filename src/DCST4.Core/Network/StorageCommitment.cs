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

namespace DCST4.Core.Network
{
    /// <summary>Which association an N-EVENT-REPORT came on.</summary>
    public enum CommitmentVia
    {
        /// <summary>The association of our N-ACTION, before we released it.</summary>
        SameAssociation,
        /// <summary>An association the system under test opened to our local SCP.</summary>
        SeparateAssociation,
    }

    /// <summary>One instance in a Referenced SOP Sequence or Failed SOP Sequence.</summary>
    public class ReferencedInstance
    {
        public string SopClassUid { get; set; }
        public string SopInstanceUid { get; set; }
    }

    /// <summary>One item of a Failed SOP Sequence.</summary>
    public sealed class FailedInstance : ReferencedInstance
    {
        /// <summary>Raw (0008,1197), or null if the item has none, which the standard does not allow.</summary>
        public ushort? FailureReason { get; set; }
    }

    /// <summary>What one Request Storage Commitment asks for.</summary>
    public sealed class CommitmentRequest
    {
        public const ushort RequestStorageCommitmentAction = 1;

        public string TransactionUid { get; set; }
        public List<ReferencedInstance> Instances { get; set; } = new List<ReferencedInstance>();

        /// <summary>N-ACTION, Action Type 1, to the well-known Storage Commitment Push Model SOP Instance.</summary>
        public DicomNActionRequest ToRequest()
        {
            var references = Instances.Select(i => new DicomDataset
            {
                { DicomTag.ReferencedSOPClassUID, i.SopClassUid },
                { DicomTag.ReferencedSOPInstanceUID, i.SopInstanceUid },
            }).ToArray();

            return new DicomNActionRequest(DicomUID.StorageCommitmentPushModel, DicomUID.StorageCommitmentPushModelInstance, RequestStorageCommitmentAction)
            {
                Dataset = new DicomDataset
                {
                    { DicomTag.TransactionUID, TransactionUid },
                    new DicomSequence(DicomTag.ReferencedSOPSequence, references),
                },
            };
        }
    }

    /// <summary>One N-EVENT-REPORT as it was received and answered.</summary>
    public sealed class CommitmentReport
    {
        public const ushort AllSucceeded = 1;
        public const ushort FailuresExist = 2;

        public CommitmentVia Via { get; set; }
        /// <summary>The AE title of whoever sent the report.</summary>
        public string CallingAe { get; set; }
        public ushort EventTypeId { get; set; }
        /// <summary>Empty if the report had none.</summary>
        public string TransactionUid { get; set; }
        public List<ReferencedInstance> Committed { get; set; } = new List<ReferencedInstance>();
        public List<FailedInstance> Failed { get; set; } = new List<FailedInstance>();
        /// <summary>Whether the report had a Failed SOP Sequence at all, which tells an empty one from none.</summary>
        public bool HasFailedSequence { get; set; }
        /// <summary>Milliseconds on the tracker's monotonic clock.</summary>
        public long ReceivedAtMs { get; set; }
        /// <summary>What we answered in the N-EVENT-REPORT-RSP.</summary>
        public ushort AnsweredStatus { get; set; }
        /// <summary>Why it was refused, or null if it was accepted.</summary>
        public string Problem { get; set; }
        /// <summary>It came for a transaction of ours after the wait for it had ended.</summary>
        public bool Late { get; set; }

        internal static CommitmentReport Read(DicomNEventReportRequest request, CommitmentVia via, string callingAe, long nowMs)
        {
            var report = new CommitmentReport
            {
                Via = via,
                CallingAe = callingAe,
                EventTypeId = request.Command.GetSingleValueOrDefault<ushort>(DicomTag.EventTypeID, 0),
                ReceivedAtMs = nowMs,
                TransactionUid = string.Empty,
            };

            var data = request.Dataset;
            if (data == null) return report;

            report.TransactionUid = data.GetSingleValueOrDefault(DicomTag.TransactionUID, string.Empty);
            if (data.TryGetSequence(DicomTag.ReferencedSOPSequence, out var committed))
            {
                report.Committed = committed.Items.Select(item => new ReferencedInstance
                {
                    SopClassUid = item.GetSingleValueOrDefault(DicomTag.ReferencedSOPClassUID, string.Empty),
                    SopInstanceUid = item.GetSingleValueOrDefault(DicomTag.ReferencedSOPInstanceUID, string.Empty),
                }).ToList();
            }
            if (data.TryGetSequence(DicomTag.FailedSOPSequence, out var failed))
            {
                report.HasFailedSequence = true;
                report.Failed = failed.Items.Select(item => new FailedInstance
                {
                    SopClassUid = item.GetSingleValueOrDefault(DicomTag.ReferencedSOPClassUID, string.Empty),
                    SopInstanceUid = item.GetSingleValueOrDefault(DicomTag.ReferencedSOPInstanceUID, string.Empty),
                    FailureReason = item.Contains(DicomTag.FailureReason) ? item.GetSingleValueOrDefault<ushort>(DicomTag.FailureReason, 0) : (ushort?)null,
                }).ToList();
            }
            return report;
        }
    }

    /// <summary>
    /// The transactions we are waiting for, shared by both ways a report can arrive: on the association of the N-ACTION and
    /// on one the system under test opens to our local SCP. A report for a Transaction UID that we did not issue is refused.
    /// </summary>
    public sealed class CommitmentTracker
    {
        /// <summary>At most 64 characters, the limit of LO.</summary>
        public const string UnknownTransactionComment = "Unknown Transaction UID";

        private static readonly ILogger Logger = Log.ForContext<CommitmentTracker>();

        private readonly object _lock = new object();
        private readonly Stopwatch _clock;
        private readonly Dictionary<string, TaskCompletionSource<CommitmentReport>> _waiting = new Dictionary<string, TaskCompletionSource<CommitmentReport>>(StringComparer.Ordinal);
        private readonly HashSet<string> _expired = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _answered = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<CommitmentReport> _rejected = new List<CommitmentReport>();
        private readonly List<CommitmentReport> _late = new List<CommitmentReport>();
        private readonly List<CommitmentReport> _repeated = new List<CommitmentReport>();

        /// <param name="clock">A running clock, shared with the timelines of the test so that all times are on one axis.</param>
        public CommitmentTracker(Stopwatch clock = null)
        {
            _clock = clock ?? Stopwatch.StartNew();
        }

        public long NowMs => _clock.ElapsedMilliseconds;

        /// <summary>Reports refused because of their Transaction UID or Event Type.</summary>
        public IReadOnlyList<CommitmentReport> Rejected { get { lock (_lock) return _rejected.ToList(); } }

        /// <summary>Reports for one of our transactions that came after the wait for it had ended.</summary>
        public IReadOnlyList<CommitmentReport> Late { get { lock (_lock) return _late.ToList(); } }

        /// <summary>Reports for a transaction that had already been answered once.</summary>
        public IReadOnlyList<CommitmentReport> Repeated { get { lock (_lock) return _repeated.ToList(); } }

        /// <summary>Raised for every report, after it was answered.</summary>
        public event Action<CommitmentReport> ReportReceived;

        /// <summary>Register before the N-ACTION is sent: the report can come before its response.</summary>
        public Task<CommitmentReport> Register(string transactionUid)
        {
            lock (_lock)
            {
                var waiter = new TaskCompletionSource<CommitmentReport>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiting[transactionUid] = waiter;
                _expired.Remove(transactionUid);
                return waiter.Task;
            }
        }

        /// <summary>The wait is over. A report that still comes is accepted, since we issued the UID, and counted as late.</summary>
        public void Expire(string transactionUid)
        {
            lock (_lock)
            {
                if (_waiting.TryGetValue(transactionUid, out var waiter) && !waiter.Task.IsCompleted) _expired.Add(transactionUid);
                _waiting.Remove(transactionUid);
            }
        }

        /// <summary>Answers one N-EVENT-REPORT-RQ, from whichever association it came on.</summary>
        public DicomNEventReportResponse Handle(DicomNEventReportRequest request, CommitmentVia via, string callingAe)
        {
            CommitmentReport report;
            DicomStatus status = DicomStatus.Success;
            string comment = null;
            TaskCompletionSource<CommitmentReport> waiter = null;

            try
            {
                report = CommitmentReport.Read(request, via, callingAe, NowMs);
            }
            catch (Exception ex) when (ex is DicomDataException || ex is FormatException || ex is InvalidCastException)
            {
                Logger.Warning(ex, "An N-EVENT-REPORT could not be read");
                report = new CommitmentReport { Via = via, CallingAe = callingAe, ReceivedAtMs = NowMs, TransactionUid = string.Empty, Problem = "The report could not be read: " + ex.Message };
                status = DicomStatus.ProcessingFailure;
                comment = "Unreadable report";
            }

            lock (_lock)
            {
                if (report.Problem != null)
                {
                    // Already refused above.
                }
                else if (report.EventTypeId != CommitmentReport.AllSucceeded && report.EventTypeId != CommitmentReport.FailuresExist)
                {
                    status = DicomStatus.NoSuchEventType;
                    report.Problem = $"Event Type ID {report.EventTypeId.ToString(CultureInfo.InvariantCulture)} is not 1 or 2.";
                }
                else if (_waiting.TryGetValue(report.TransactionUid, out waiter))
                {
                    _waiting.Remove(report.TransactionUid);
                    _answered.Add(report.TransactionUid);
                }
                else if (_expired.Remove(report.TransactionUid))
                {
                    report.Late = true;
                    _answered.Add(report.TransactionUid);
                    _late.Add(report);
                }
                else if (_answered.Contains(report.TransactionUid))
                {
                    _repeated.Add(report);
                }
                else
                {
                    status = DicomStatus.ProcessingFailure;
                    comment = UnknownTransactionComment;
                    report.Problem = report.TransactionUid.Length == 0 ? "The report has no Transaction UID." : $"Transaction UID {report.TransactionUid} was not issued by DCST4.";
                }

                report.AnsweredStatus = status.Code;
                if (report.Problem != null) _rejected.Add(report);
            }

            if (report.Problem != null)
            {
                Logger.Warning("Refused an N-EVENT-REPORT via {via} from {ae}: {problem}", via, callingAe, report.Problem);
            }
            else
            {
                Logger.Information("N-EVENT-REPORT via {via} from {ae}: event type {type}, transaction {uid}, {committed} committed, {failed} failed{late}",
                    via, callingAe, report.EventTypeId, report.TransactionUid, report.Committed.Count, report.Failed.Count, report.Late ? ", late" : string.Empty);
            }

            waiter?.TrySetResult(report);
            Raise(report);

            var response = new DicomNEventReportResponse(request, status);
            if (comment != null) response.Command.AddOrUpdate(DicomTag.ErrorComment, comment);
            return response;
        }

        private void Raise(CommitmentReport report)
        {
            if (report == null) return;
            try
            {
                ReportReceived?.Invoke(report);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "A commitment report handler failed");
            }
        }
    }

    /// <summary>How the N-ACTION went.</summary>
    public sealed class CommitmentOutcome
    {
        public DimseExchange Exchange { get; internal set; }
        /// <summary>Raw (0000,0900) of the N-ACTION-RSP, or null if none came.</summary>
        public ushort? ActionStatus { get; internal set; }
        public string ErrorComment { get; internal set; }
        /// <summary>On the tracker's clock. Null if the request never went out.</summary>
        public long? SentAtMs { get; internal set; }
        public long? ResponseAtMs { get; internal set; }
    }

    /// <summary>Storage Commitment Push Model as SCU: N-ACTION, and the N-EVENT-REPORT if it comes before we release.</summary>
    public static class StorageCommitmentService
    {
        /// <param name="lingerSeconds">
        /// How long the association stays open after the N-ACTION-RSP, so that the report can come on it. 0 releases at once.
        /// fo-dicom's association linger is used for this, and it holds the association for the whole time.
        /// </param>
        public static async Task<CommitmentOutcome> RequestAsync(
            DicomEndpoint endpoint,
            NetworkOptions options,
            CommitmentRequest commitment,
            int lingerSeconds,
            CommitmentTracker tracker,
            CancellationToken token,
            AssociationTimeline timeline = null)
        {
            var request = commitment.ToRequest();
            var outcome = new CommitmentOutcome();
            var dimseIndex = 0;

            outcome.Exchange = await DimseRunner.RunAsync(endpoint, options, request, DicomUID.StorageCommitmentPushModel, timeline, (client, watch, exchange) =>
            {
                client.ClientOptions.AssociationLingerTimeoutInMs = Math.Max(0, lingerSeconds) * 1000;

                client.OnNEventReportRequest = report =>
                {
                    var index = timeline?.RecordIncomingRequest("N-EVENT-REPORT", DescribeReport(report)) ?? 0;
                    var response = tracker.Handle(report, CommitmentVia.SameAssociation, endpoint.CalledAe);
                    var status = response.Command.GetSingleValueOrDefault(DicomTag.Status, response.Status.Code);
                    timeline?.RecordOutgoingResponse(index, "N-EVENT-REPORT", status, DimseStatusText.Describe(status));
                    return Task.FromResult(response);
                };

                client.AssociationAccepted += (s, e) =>
                {
                    if (!exchange.ContextAccepted) return;
                    outcome.SentAtMs = tracker.NowMs;
                    if (timeline != null) dimseIndex = timeline.RecordRequest("N-ACTION", $"Request Storage Commitment, {commitment.Instances.Count.ToString(CultureInfo.InvariantCulture)} instances, transaction {commitment.TransactionUid}");
                };

                request.OnResponseReceived = (req, response) =>
                {
                    if (!exchange.ContextAccepted) return;
                    var status = response.Command.GetSingleValueOrDefault(DicomTag.Status, response.Status.Code);
                    outcome.ActionStatus = status;
                    outcome.ErrorComment = response.Command.GetSingleValueOrDefault(DicomTag.ErrorComment, string.Empty);
                    outcome.ResponseAtMs = tracker.NowMs;
                    timeline?.RecordResponse(dimseIndex, "N-ACTION", status, DimseStatusText.Describe(status));
                    // The linger that follows is not a wait for a response.
                    watch.Stop();
                };
            }, token).ConfigureAwait(false);

            return outcome;
        }

        /// <summary>For the timeline. Holds nothing that differs between identical runs.</summary>
        public static string DescribeReport(DicomNEventReportRequest report)
        {
            var type = report.Command.GetSingleValueOrDefault<ushort>(DicomTag.EventTypeID, 0);
            var transaction = report.Dataset?.GetSingleValueOrDefault(DicomTag.TransactionUID, string.Empty) ?? string.Empty;
            return $"Event Type {type.ToString(CultureInfo.InvariantCulture)}, transaction {transaction}";
        }
    }

    /// <summary>Text for the Failure Reason (0008,1197) of a Failed SOP Sequence, PS3.3 C.14.1.1.</summary>
    public static class CommitmentStatus
    {
        public static string Describe(ushort? reason)
        {
            if (!reason.HasValue) return "no Failure Reason";
            switch (reason.Value)
            {
                case 0x0110: return "Processing failure";
                case 0x0112: return "No such object instance";
                case 0x0119: return "Class-instance conflict";
                case 0x0122: return "Referenced SOP Class not supported";
                case 0x0131: return "Duplicate transaction UID";
                case 0x0213: return "Resource limitation";
                default: return "vendor-specific or unknown reason";
            }
        }
    }
}
