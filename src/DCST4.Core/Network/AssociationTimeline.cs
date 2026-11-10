using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;

namespace DCST4.Core.Network
{
    /// <summary>One protocol step of an association.</summary>
    public sealed class ProtocolEvent
    {
        public const string Sent = "Sent";
        public const string Received = "Received";

        /// <summary>Position in the timeline, from 0.</summary>
        public int Index { get; set; }
        /// <summary>Milliseconds on the monotonic clock since the timeline was attached.</summary>
        public long OffsetMs { get; set; }
        public string Direction { get; set; }
        /// <summary>A-ASSOCIATE-RQ, C-ECHO-RSP and so on.</summary>
        public string Type { get; set; }
        public string Detail { get; set; }
        /// <summary>Raw (0000,0900) as 0xNNNN on DIMSE responses, otherwise null.</summary>
        public string Status { get; set; }
        /// <summary>
        /// Pairs a DIMSE request with its response: 1 for the first request of this association. fo-dicom's Message ID is
        /// not used because it comes from a counter shared by the whole process, so it differs from run to run.
        /// </summary>
        public int? DimseIndex { get; set; }
        /// <summary>Which association of a test that opens several, from 1. Null for a test with one association.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Association { get; set; }
    }

    /// <summary>An A-ASSOCIATE-RJ as the SCP sent it.</summary>
    public sealed class AssociationRejection
    {
        public string Result { get; set; }
        public string Source { get; set; }
        public string Reason { get; set; }

        public override string ToString() => $"result {Result}, source {Source}, reason {Reason}";
    }

    /// <summary>
    /// Records what happened on one association.
    ///
    /// IDicomClient is used rather than the Advanced API: fo-dicom's event collector with OnReceiveAbortAsync is internal, and
    /// the public Advanced API reports an abort with the same DicomAssociationAbortedException that IDicomClient.SendAsync
    /// rethrows. Because AssociationReleased is also raised after an abort, before SendAsync throws, the ending can only be
    /// told once SendAsync is over, which is why <see cref="Complete"/> exists.
    ///
    /// Known limits: an abort's offset is when fo-dicom finished handling it, an A-ABORT arriving during the release looks
    /// like a release, and a release that timed out cannot be told from a completed one.
    /// A-ASSOCIATE-RQ has no event either. It is added once something proves it was sent, with the offset of the attach, so a
    /// connection that was refused shows no RQ. Its offset therefore includes the TCP connect.
    /// </summary>
    public sealed class AssociationTimeline
    {
        private readonly object _lock = new object();
        private readonly Stopwatch _clock;
        private readonly List<ProtocolEvent> _events = new List<ProtocolEvent>();
        private List<NegotiatedContext> _negotiation = new List<NegotiatedContext>();
        private AssociationRejection _rejection;
        private string _associationDetail;
        private long _attachedAtMs;
        private long? _endedAtMs;
        private bool _requestRecorded;
        private bool _accepted;
        private bool _completed;
        private int _dimseCount;

        /// <param name="clock">A running clock shared with other timelines, so that several associations of one test share a time axis.</param>
        public AssociationTimeline(Stopwatch clock = null)
        {
            _clock = clock ?? Stopwatch.StartNew();
        }

        /// <summary>The A-ASSOCIATE-RJ, or null if there was none.</summary>
        public AssociationRejection Rejection
        {
            get { lock (_lock) return _rejection; }
        }

        /// <summary>Whether an A-ASSOCIATE-AC arrived.</summary>
        public bool Accepted
        {
            get { lock (_lock) return _accepted; }
        }

        /// <summary>A snapshot of the events so far, in order.</summary>
        public List<ProtocolEvent> Events
        {
            get
            {
                lock (_lock)
                {
                    for (var i = 0; i < _events.Count; i++) _events[i].Index = i;
                    return _events.ToList();
                }
            }
        }

        /// <summary>The presentation contexts of the accepted association, or empty if none was accepted.</summary>
        public List<NegotiatedContext> Negotiation
        {
            get { lock (_lock) return _negotiation.ToList(); }
        }

        public void Attach(IDicomClient client)
        {
            lock (_lock)
            {
                _attachedAtMs = _clock.ElapsedMilliseconds;
                _associationDetail = $"Calling AE {client.CallingAe}, called AE {client.CalledAe}";
            }

            client.AssociationAccepted += (s, e) => OnAccepted(e.Association);
            client.AssociationRejected += (s, e) => OnRejected(e.Result, e.Source, e.Reason);
            client.AssociationRequestTimedOut += (s, e) => EnsureRequestRecorded();
            client.AssociationReleased += (s, e) => { lock (_lock) _endedAtMs = _clock.ElapsedMilliseconds; };
        }

        /// <summary>Records a DIMSE request and returns the number that <see cref="RecordResponse"/> needs.</summary>
        public int RecordRequest(string command, string detail)
        {
            lock (_lock)
            {
                var number = ++_dimseCount;
                Add(ProtocolEvent.Sent, command + "-RQ", detail).DimseIndex = number;
                return number;
            }
        }

        public void RecordResponse(int dimseIndex, string command, ushort status, string detail)
        {
            lock (_lock)
            {
                var entry = Add(ProtocolEvent.Received, command + "-RSP", detail);
                entry.DimseIndex = dimseIndex;
                entry.Status = FormatStatus(status);
            }
        }

        /// <summary>A DIMSE request that the other side sent us on this association, such as N-EVENT-REPORT. Returns the number for <see cref="RecordOutgoingResponse"/>.</summary>
        public int RecordIncomingRequest(string command, string detail)
        {
            lock (_lock)
            {
                var number = ++_dimseCount;
                Add(ProtocolEvent.Received, command + "-RQ", detail).DimseIndex = number;
                return number;
            }
        }

        public void RecordOutgoingResponse(int dimseIndex, string command, ushort status, string detail)
        {
            lock (_lock)
            {
                var entry = Add(ProtocolEvent.Sent, command + "-RSP", detail);
                entry.DimseIndex = dimseIndex;
                entry.Status = FormatStatus(status);
            }
        }

        /// <summary>
        /// Settles how the association ended. Call after SendAsync, whether it returned or threw.
        /// </summary>
        /// <param name="exception">What SendAsync threw, or null.</param>
        /// <param name="abortedLocally">We stopped it ourselves, through cancellation or a timeout.</param>
        /// <param name="localReason">Why, as it should read in the timeline.</param>
        public void Complete(Exception exception, bool abortedLocally, string localReason)
        {
            lock (_lock)
            {
                if (_completed) return;
                _completed = true;

                var aborted = Find<DicomAssociationAbortedException>(exception);
                if (aborted != null)
                {
                    EnsureRequestRecorded();
                    AddAtEnd(ProtocolEvent.Received, "A-ABORT", $"Source {aborted.AbortSource}, reason {aborted.AbortReason}");
                }
                else if (Find<DicomAssociationRejectedException>(exception) != null)
                {
                    // Already recorded when the event was raised.
                }
                else if (abortedLocally)
                {
                    // fo-dicom only sends A-ABORT when an association exists.
                    if (_accepted) AddAtEnd(ProtocolEvent.Sent, "A-ABORT", localReason);
                }
                else if (Find<DicomNetworkException>(exception) != null || (exception != null && _accepted))
                {
                    EnsureRequestRecorded();
                    AddAtEnd(ProtocolEvent.Received, "CONNECTION-CLOSED", NetworkErrors.GetUserMessage(exception));
                }
                else if (exception == null && _accepted && _endedAtMs.HasValue)
                {
                    AddAtEnd(ProtocolEvent.Sent, "A-RELEASE-RQ", null);
                    AddAtEnd(ProtocolEvent.Received, "A-RELEASE-RP", null);
                }
            }
        }

        public static string FormatStatus(ushort status) => "0x" + status.ToString("X4", CultureInfo.InvariantCulture);

        private void OnAccepted(DicomAssociation association)
        {
            lock (_lock)
            {
                EnsureRequestRecorded();
                _accepted = true;
                _negotiation = AssociationNegotiation.Read(association);
                Add(ProtocolEvent.Received, "A-ASSOCIATE-AC",
                    $"Max PDU {association.MaximumPDULength}, implementation {association.RemoteImplementationClassUID?.UID} {association.RemoteImplementationVersion}".TrimEnd());
            }
        }

        private void OnRejected(DicomRejectResult result, DicomRejectSource source, DicomRejectReason reason)
        {
            lock (_lock)
            {
                EnsureRequestRecorded();
                _rejection = new AssociationRejection { Result = result.ToString(), Source = source.ToString(), Reason = reason.ToString() };
                Add(ProtocolEvent.Received, "A-ASSOCIATE-RJ", $"Result {result}, source {source}, reason {reason}");
            }
        }

        private void EnsureRequestRecorded()
        {
            lock (_lock)
            {
                if (_requestRecorded) return;
                _requestRecorded = true;
                _events.Insert(0, new ProtocolEvent { OffsetMs = _attachedAtMs, Direction = ProtocolEvent.Sent, Type = "A-ASSOCIATE-RQ", Detail = _associationDetail });
            }
        }

        private ProtocolEvent Add(string direction, string type, string detail)
        {
            var entry = new ProtocolEvent { OffsetMs = _clock.ElapsedMilliseconds, Direction = direction, Type = type, Detail = detail };
            _events.Add(entry);
            return entry;
        }

        private void AddAtEnd(string direction, string type, string detail)
        {
            Add(direction, type, detail).OffsetMs = _endedAtMs ?? _clock.ElapsedMilliseconds;
        }

        private static T Find<T>(Exception exception) where T : Exception
        {
            if (exception == null) return null;
            if (exception is T match) return match;
            if (exception is AggregateException aggregate)
            {
                return aggregate.Flatten().InnerExceptions.Select(Find<T>).FirstOrDefault(found => found != null);
            }
            return Find<T>(exception.InnerException);
        }
    }
}
