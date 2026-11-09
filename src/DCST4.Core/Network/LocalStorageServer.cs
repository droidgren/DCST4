using FellowOakDicom;
using FellowOakDicom.Network;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

    /// <summary>What <see cref="LocalStorageServer"/> shares with its service instances, which fo-dicom creates itself.</summary>
    internal sealed class LocalStorageState
    {
        private int _fileCount;

        public string Folder;
        public IReadOnlyDictionary<string, string> PreferredSyntaxBySopClass;
        public Action Activity;
        public readonly ConcurrentQueue<IncomingAssociation> Associations = new ConcurrentQueue<IncomingAssociation>();
        public readonly ConcurrentQueue<RetrievedFile> Files = new ConcurrentQueue<RetrievedFile>();

        /// <summary>A counter and not the sender's UID, so that what the sender writes can never become a path.</summary>
        public string NextPath() => System.IO.Path.Combine(Folder, Interlocked.Increment(ref _fileCount).ToString("D6", CultureInfo.InvariantCulture) + ".dcm");
    }

    /// <summary>The Storage SCP that C-MOVE sends to, for as long as it is open. Every run gets its own, and no static state.</summary>
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

        /// <summary>Raised for each association and each stored object, so that the wait for a response can be extended.</summary>
        public event Action Activity
        {
            add => _state.Activity += value;
            remove => _state.Activity -= value;
        }

        /// <param name="preferredSyntaxBySopClass">Accept this syntax if the sender offers it. Otherwise the first it offers.</param>
        /// <exception cref="DicomNetworkException">The port cannot be listened on, for example because it is in use.</exception>
        public static LocalStorageServer Start(int port, string folder, IReadOnlyDictionary<string, string> preferredSyntaxBySopClass)
        {
            Directory.CreateDirectory(folder);
            var state = new LocalStorageState { Folder = folder, PreferredSyntaxBySopClass = preferredSyntaxBySopClass ?? new Dictionary<string, string>() };
            var server = DicomServerFactory.Create<LocalStorageService>(port, userState: state);
            return new LocalStorageServer(server, state);
        }

        public void Dispose() => _server.Dispose();
    }

    /// <summary>One association of <see cref="LocalStorageServer"/>. Public because fo-dicom creates it.</summary>
    public sealed class LocalStorageService : DicomService, IDicomServiceProvider, IDicomCStoreProvider, IDicomCEchoProvider
    {
        private readonly ConcurrentDictionary<string, List<string>> _proposed = new ConcurrentDictionary<string, List<string>>(StringComparer.Ordinal);

        public LocalStorageService(INetworkStream stream, Encoding fallbackEncoding, ILogger log, DicomServiceDependencies dependencies)
            : base(stream, fallbackEncoding, log, dependencies) { }

        private LocalStorageState State => (LocalStorageState)UserState;

        public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
        {
            State.Associations.Enqueue(new IncomingAssociation { CallingAe = association.CallingAE, CalledAe = association.CalledAE });
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

            return SendAssociationAcceptAsync(association);
        }

        public Task OnReceiveAssociationReleaseRequestAsync() => SendAssociationReleaseResponseAsync();

        public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason) { }

        public void OnConnectionClosed(Exception exception) { }

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
    }
}
