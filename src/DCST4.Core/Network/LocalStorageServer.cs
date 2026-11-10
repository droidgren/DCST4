using FellowOakDicom;
using FellowOakDicom.Network;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace DCST4.Core.Network
{
    /// <summary>What a retrieve brought back: one file, and what was negotiated for it.</summary>
    public sealed class RetrievedFile
    {
        public string Path { get; set; }
        public string SopClassUid { get; set; }
        public string SopInstanceUid { get; set; }
        /// <summary>The syntax it arrived in.</summary>
        public string TransferSyntaxUid { get; set; }
        /// <summary>What the sender offered for this SOP class on C-MOVE. Empty on C-GET, where we are the one who proposes.</summary>
        public List<string> ProposedTransferSyntaxUids { get; set; } = new List<string>();
    }

    /// <summary>One association that reached our listener.</summary>
    public sealed class IncomingAssociation
    {
        public string CallingAe { get; set; }
        public string CalledAe { get; set; }
    }

    /// <summary>The protocol steps of one association that reached the listener, on the clock its owner supplied.</summary>
    internal sealed class IncomingTimeline
    {
        private readonly object _lock = new object();
        private readonly Stopwatch _clock;
        private readonly List<ProtocolEvent> _events = new List<ProtocolEvent>();
        private readonly TaskCompletionSource<bool> _endedTask = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _dimseCount;
        private bool _ended;

        public IncomingTimeline(Stopwatch clock)
        {
            _clock = clock;
        }

        /// <summary>Completes when the association was released, aborted or closed.</summary>
        public Task Ended => _endedTask.Task;

        public void Add(string direction, string type, string detail, string status = null, int? dimseIndex = null)
        {
            lock (_lock)
            {
                _events.Add(new ProtocolEvent { OffsetMs = _clock.ElapsedMilliseconds, Direction = direction, Type = type, Detail = detail, Status = status, DimseIndex = dimseIndex });
            }
        }

        public int NextDimse()
        {
            lock (_lock) return ++_dimseCount;
        }

        /// <summary>Only the first ending counts: a closed connection after a release is not news.</summary>
        public void End(string direction, string type, string detail)
        {
            lock (_lock)
            {
                if (_ended) return;
                _ended = true;
            }
            Add(direction, type, detail);
            _endedTask.TrySetResult(true);
        }

        public List<ProtocolEvent> Snapshot()
        {
            lock (_lock)
            {
                return _events.Select(e => new ProtocolEvent { OffsetMs = e.OffsetMs, Direction = e.Direction, Type = e.Type, Detail = e.Detail, Status = e.Status, DimseIndex = e.DimseIndex }).ToList();
            }
        }
    }

    /// <summary>What <see cref="LocalStorageServer"/> shares with its service instances, which fo-dicom creates itself.</summary>
    internal sealed class LocalStorageState
    {
        private int _fileCount;

        public string Folder;
        public IReadOnlyDictionary<string, string> PreferredSyntaxBySopClass;
        public Action Activity;
        /// <summary>Where N-EVENT-REPORTs go. Null refuses them.</summary>
        public CommitmentTracker Commitments;
        /// <summary>Null records no protocol steps.</summary>
        public Stopwatch Clock;
        public readonly ConcurrentQueue<IncomingAssociation> Associations = new ConcurrentQueue<IncomingAssociation>();
        public readonly ConcurrentQueue<RetrievedFile> Files = new ConcurrentQueue<RetrievedFile>();
        public readonly ConcurrentQueue<IncomingTimeline> Timelines = new ConcurrentQueue<IncomingTimeline>();

        /// <summary>A counter and not the sender's UID, so that what the sender writes can never become a path.</summary>
        public string NextPath() => System.IO.Path.Combine(Folder, Interlocked.Increment(ref _fileCount).ToString("D6", CultureInfo.InvariantCulture) + ".dcm");
    }

    /// <summary>
    /// The SCP that C-MOVE sends to and that Storage Commitment reports come to, for as long as it is open. Every run gets its
    /// own, and no static state.
    /// </summary>
    public sealed class LocalStorageServer : IDisposable
    {
        private readonly IDicomServer _server;
        private readonly LocalStorageState _state;

        private LocalStorageServer(IDicomServer server, LocalStorageState state)
        {
            _server = server;
            _state = state;
        }

        /// <summary>Whatever reached the listener, whether or not anything was stored.</summary>
        public IReadOnlyList<IncomingAssociation> Associations => _state.Associations.ToList();

        public IReadOnlyList<RetrievedFile> Files => _state.Files.ToList();

        /// <summary>The protocol steps of each association that reached the listener, if it was started with a clock.</summary>
        public IReadOnlyList<List<ProtocolEvent>> IncomingTimelines => _state.Timelines.Select(t => t.Snapshot()).ToList();

        /// <summary>
        /// Waits, at most <paramref name="limit"/>, for the incoming associations to end, so that a timeline taken after a
        /// report holds its response and the release. A report completes its waiter before the response goes out.
        /// </summary>
        public Task WaitForIncomingToEndAsync(TimeSpan limit)
        {
            var open = _state.Timelines.Select(t => t.Ended).Where(t => !t.IsCompleted).ToList();
            return open.Count == 0 ? Task.CompletedTask : Task.WhenAny(Task.WhenAll(open), Task.Delay(limit));
        }

        /// <summary>Raised for each association, each stored object and each report, so that the wait for a response can be extended.</summary>
        public event Action Activity
        {
            add => _state.Activity += value;
            remove => _state.Activity -= value;
        }

        /// <param name="preferredSyntaxBySopClass">Accept this syntax if the sender offers it. Otherwise the first it offers.</param>
        /// <param name="commitments">Answers the N-EVENT-REPORTs that arrive. Without it they are refused.</param>
        /// <param name="clock">Records the protocol steps of each incoming association on this clock. Null records none.</param>
        /// <exception cref="DicomNetworkException">The port cannot be listened on, for example because it is in use.</exception>
        public static LocalStorageServer Start(int port, string folder, IReadOnlyDictionary<string, string> preferredSyntaxBySopClass, CommitmentTracker commitments = null, Stopwatch clock = null)
        {
            Directory.CreateDirectory(folder);
            var state = new LocalStorageState
            {
                Folder = folder,
                PreferredSyntaxBySopClass = preferredSyntaxBySopClass ?? new Dictionary<string, string>(),
                Commitments = commitments,
                Clock = clock,
            };
            var server = DicomServerFactory.Create<LocalStorageService>(port, userState: state);
            return new LocalStorageServer(server, state);
        }

        public void Dispose() => _server.Dispose();
    }

    /// <summary>One association of <see cref="LocalStorageServer"/>. Public because fo-dicom creates it.</summary>
    public sealed class LocalStorageService : DicomService, IDicomServiceProvider, IDicomCStoreProvider, IDicomCEchoProvider, IDicomNServiceProvider
    {
        private readonly ConcurrentDictionary<string, List<string>> _proposed = new ConcurrentDictionary<string, List<string>>(StringComparer.Ordinal);
        private IncomingTimeline _timeline;
        private string _callingAe;

        public LocalStorageService(INetworkStream stream, Encoding fallbackEncoding, ILogger log, DicomServiceDependencies dependencies)
            : base(stream, fallbackEncoding, log, dependencies) { }

        private LocalStorageState State => (LocalStorageState)UserState;

        public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
        {
            State.Associations.Enqueue(new IncomingAssociation { CallingAe = association.CallingAE, CalledAe = association.CalledAE });
            _callingAe = association.CallingAE;
            if (State.Clock != null)
            {
                _timeline = new IncomingTimeline(State.Clock);
                _timeline.Add(ProtocolEvent.Received, "A-ASSOCIATE-RQ", $"Calling AE {association.CallingAE}, called AE {association.CalledAE}");
                State.Timelines.Enqueue(_timeline);
            }
            State.Activity?.Invoke();

            foreach (var context in association.PresentationContexts)
            {
                var offered = context.GetTransferSyntaxes().ToArray();
                _proposed[context.AbstractSyntax.UID] = offered.Select(t => t.UID.UID).ToList();

                State.PreferredSyntaxBySopClass.TryGetValue(context.AbstractSyntax.UID, out var preferred);
                var accepted = offered.FirstOrDefault(t => t.UID.UID == preferred) ?? offered.FirstOrDefault();
                if (accepted != null) context.SetResult(DicomPresentationContextResult.Accept, accepted);
                else context.SetResult(DicomPresentationContextResult.RejectTransferSyntaxesNotSupported);
            }

            // A role proposed in the request is answered with the same values, so the SCP role a commitment report needs is accepted.
            _timeline?.Add(ProtocolEvent.Sent, "A-ASSOCIATE-AC", "Accepted: " + string.Join(", ", association.PresentationContexts
                .Where(c => c.Result == DicomPresentationContextResult.Accept)
                .Select(c => c.AbstractSyntax.Name + (c.ProviderRole == true ? " (SCP role for the caller)" : string.Empty))));
            return SendAssociationAcceptAsync(association);
        }

        public Task OnReceiveAssociationReleaseRequestAsync()
        {
            if (_timeline != null)
            {
                _timeline.Add(ProtocolEvent.Received, "A-RELEASE-RQ", null);
                _timeline.End(ProtocolEvent.Sent, "A-RELEASE-RP", null);
            }
            return SendAssociationReleaseResponseAsync();
        }

        public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason) =>
            _timeline?.End(ProtocolEvent.Received, "A-ABORT", $"Source {source}, reason {reason}");

        public void OnConnectionClosed(Exception exception) =>
            _timeline?.End(ProtocolEvent.Received, "CONNECTION-CLOSED", exception == null ? null : NetworkErrors.GetUserMessage(exception));

        public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request) => Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));

        public async Task<DicomCStoreResponse> OnCStoreRequestAsync(DicomCStoreRequest request)
        {
            var path = State.NextPath();
            try
            {
                // Now, while the dataset still exists. fo-dicom may back it with a temporary file.
                await request.File.SaveAsync(path).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is DicomException)
            {
                Serilog.Log.ForContext<LocalStorageServer>().Error(ex, "Could not save a received object");
                return new DicomCStoreResponse(request, DicomStatus.ProcessingFailure);
            }

            _proposed.TryGetValue(request.SOPClassUID.UID, out var offered);
            State.Files.Enqueue(new RetrievedFile
            {
                Path = path,
                SopClassUid = request.SOPClassUID.UID,
                SopInstanceUid = request.SOPInstanceUID.UID,
                TransferSyntaxUid = request.Dataset.InternalTransferSyntax.UID.UID,
                ProposedTransferSyntaxUids = offered ?? new List<string>(),
            });
            State.Activity?.Invoke();
            return new DicomCStoreResponse(request, DicomStatus.Success);
        }

        public Task OnCStoreRequestExceptionAsync(string tempFileName, Exception e)
        {
            Serilog.Log.ForContext<LocalStorageServer>().Error(e, "A received object could not be read");
            return Task.CompletedTask;
        }

        // ---------------------------------------------------------------- Storage Commitment

        /// <summary>A report for a transaction we did not issue is refused, and so is every report when none is outstanding.</summary>
        public Task<DicomNEventReportResponse> OnNEventReportRequestAsync(DicomNEventReportRequest request)
        {
            var index = _timeline?.NextDimse();
            _timeline?.Add(ProtocolEvent.Received, "N-EVENT-REPORT-RQ", StorageCommitmentService.DescribeReport(request), null, index);

            DicomNEventReportResponse response;
            if (State.Commitments == null)
            {
                response = new DicomNEventReportResponse(request, DicomStatus.ProcessingFailure);
                response.Command.AddOrUpdate(DicomTag.ErrorComment, CommitmentTracker.UnknownTransactionComment);
                Serilog.Log.ForContext<LocalStorageServer>().Warning("Refused an N-EVENT-REPORT from {ae}: no Storage Commitment is outstanding", _callingAe);
            }
            else
            {
                response = State.Commitments.Handle(request, CommitmentVia.SeparateAssociation, _callingAe);
            }

            var status = response.Command.GetSingleValueOrDefault(DicomTag.Status, response.Status.Code);
            _timeline?.Add(ProtocolEvent.Sent, "N-EVENT-REPORT-RSP", DimseStatusText.Describe(status), AssociationTimeline.FormatStatus(status), index);
            State.Activity?.Invoke();
            return Task.FromResult(response);
        }

        public Task<DicomNActionResponse> OnNActionRequestAsync(DicomNActionRequest request) => Task.FromResult(new DicomNActionResponse(request, DicomStatus.UnrecognizedOperation));

        public Task<DicomNCreateResponse> OnNCreateRequestAsync(DicomNCreateRequest request) => Task.FromResult(new DicomNCreateResponse(request, DicomStatus.UnrecognizedOperation));

        public Task<DicomNDeleteResponse> OnNDeleteRequestAsync(DicomNDeleteRequest request) => Task.FromResult(new DicomNDeleteResponse(request, DicomStatus.UnrecognizedOperation));

        public Task<DicomNGetResponse> OnNGetRequestAsync(DicomNGetRequest request) => Task.FromResult(new DicomNGetResponse(request, DicomStatus.UnrecognizedOperation));

        public Task<DicomNSetResponse> OnNSetRequestAsync(DicomNSetRequest request) => Task.FromResult(new DicomNSetResponse(request, DicomStatus.UnrecognizedOperation));
    }
}
