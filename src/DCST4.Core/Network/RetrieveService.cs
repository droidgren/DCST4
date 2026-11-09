using FellowOakDicom;
using FellowOakDicom.Network;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Core.Network
{
    public enum RetrieveMethod { CGet, CMove }

    public static class RetrieveNames
    {
        public static string Of(RetrieveMethod method) => method == RetrieveMethod.CGet ? "C-GET" : "C-MOVE";
    }

    /// <summary>What to retrieve: one study, by the information model chosen.</summary>
    public sealed class RetrieveTarget
    {
        public QueryRoot Root { get; set; } = QueryRoot.Study;
        public string StudyInstanceUid { get; set; }
        /// <summary>Needed by Patient Root.</summary>
        public string PatientId { get; set; }
    }

    /// <summary>How a C-GET or C-MOVE ended, and what came back.</summary>
    public sealed class RetrieveOutcome
    {
        public DimseExchange Exchange { get; internal set; }
        /// <summary>Raw (0000,0900) of the final response, or null if none came.</summary>
        public ushort? FinalStatus { get; internal set; }
        public string ErrorComment { get; internal set; }
        /// <summary>Null if the SCP left the number out.</summary>
        public int? Completed { get; internal set; }
        public int? Failed { get; internal set; }
        public int? Warning { get; internal set; }
        public List<RetrievedFile> Files { get; internal set; } = new List<RetrievedFile>();

        public bool ResponseSaysSuccess => FinalStatus == 0x0000;
    }

    /// <summary>C-GET, with the objects coming back on the same association, and C-MOVE, with them going to a listener of ours.</summary>
    public static class RetrieveService
    {
        private const ushort StatusPending = 0xFF00;

        private static readonly ILogger Logger = Log.ForContext(typeof(RetrieveService));

        public static DicomUID GetSopClass(QueryRoot root) => root == QueryRoot.Patient
            ? DicomUID.PatientRootQueryRetrieveInformationModelGet
            : DicomUID.StudyRootQueryRetrieveInformationModelGet;

        public static DicomUID MoveSopClass(QueryRoot root) => root == QueryRoot.Patient
            ? DicomUID.PatientRootQueryRetrieveInformationModelMove
            : DicomUID.StudyRootQueryRetrieveInformationModelMove;

        /// <param name="folder">Where the objects are written.</param>
        /// <param name="sopClassSyntaxes">
        /// The storage SOP classes to expect and the syntaxes to take them in, best first. An association holds at most 128
        /// contexts and there are more storage classes than that, so only those actually expected are proposed.
        /// </param>
        public static async Task<RetrieveOutcome> GetAsync(
            DicomEndpoint endpoint,
            NetworkOptions options,
            RetrieveTarget target,
            string folder,
            IReadOnlyDictionary<string, DicomTransferSyntax[]> sopClassSyntaxes,
            CancellationToken token,
            AssociationTimeline timeline = null)
        {
            Directory.CreateDirectory(folder);

            var sopClass = GetSopClass(target.Root);
            var request = new DicomCGetRequest(target.StudyInstanceUid);
            Aim(request, sopClass, target);

            var outcome = new RetrieveOutcome();
            var files = new List<RetrievedFile>();
            var sync = new object();
            var dimseIndex = 0;
            var fileCount = 0;

            outcome.Exchange = await DimseRunner.RunAsync(endpoint, options, request, sopClass, timeline, (client, watch, exchange) =>
            {
                foreach (var entry in sopClassSyntaxes)
                {
                    client.AdditionalPresentationContexts.Add(DicomPresentationContext.GetScpRolePresentationContext(DicomUID.Parse(entry.Key), entry.Value));
                }

                client.OnCStoreRequest = async store =>
                {
                    watch.Restart();
                    var path = Path.Combine(folder, Interlocked.Increment(ref fileCount).ToString("D6", CultureInfo.InvariantCulture) + ".dcm");
                    try
                    {
                        await store.File.SaveAsync(path).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is DicomException)
                    {
                        Logger.Error(ex, "Could not save a retrieved object");
                        return new DicomCStoreResponse(store, DicomStatus.ProcessingFailure);
                    }

                    lock (sync)
                    {
                        files.Add(new RetrievedFile
                        {
                            Path = path,
                            SopClassUid = store.SOPClassUID.UID,
                            SopInstanceUid = store.SOPInstanceUID.UID,
                            TransferSyntaxUid = store.Dataset.InternalTransferSyntax.UID.UID,
                        });
                    }
                    return new DicomCStoreResponse(store, DicomStatus.Success);
                };

                client.AssociationAccepted += (s, e) =>
                {
                    if (exchange.ContextAccepted && timeline != null) dimseIndex = timeline.RecordRequest("C-GET", $"{target.Root} Root, level STUDY");
                };

                request.OnResponseReceived = (req, response) => OnProgress(response, exchange, watch, outcome, "C-GET", dimseIndex, timeline);
            }, token).ConfigureAwait(false);

            lock (sync) outcome.Files = files;
            return outcome;
        }

        /// <param name="listener">Already open. What arrives there counts as this retrieve's.</param>
        public static async Task<RetrieveOutcome> MoveAsync(
            DicomEndpoint endpoint,
            NetworkOptions options,
            RetrieveTarget target,
            string destinationAe,
            LocalStorageServer listener,
            CancellationToken token,
            AssociationTimeline timeline = null)
        {
            var sopClass = MoveSopClass(target.Root);
            var request = new DicomCMoveRequest(destinationAe, target.StudyInstanceUid);
            Aim(request, sopClass, target);

            var outcome = new RetrieveOutcome();
            var dimseIndex = 0;
            Action activity = null;

            try
            {
                outcome.Exchange = await DimseRunner.RunAsync(endpoint, options, request, sopClass, timeline, (client, watch, exchange) =>
                {
                    // Sub-operations arrive on another association, so the only sign of progress may be there.
                    activity = watch.Restart;
                    listener.Activity += activity;

                    client.AssociationAccepted += (s, e) =>
                    {
                        if (exchange.ContextAccepted && timeline != null) dimseIndex = timeline.RecordRequest("C-MOVE", $"{target.Root} Root, level STUDY, destination {destinationAe}");
                    };

                    request.OnResponseReceived = (req, response) => OnProgress(response, exchange, watch, outcome, "C-MOVE", dimseIndex, timeline);
                }, token).ConfigureAwait(false);
            }
            finally
            {
                if (activity != null) listener.Activity -= activity;
            }

            outcome.Files = listener.Files.ToList();
            return outcome;
        }

        private static void Aim(DicomRequest request, DicomUID sopClass, RetrieveTarget target)
        {
            // These request types are fixed to Study Root by fo-dicom.
            request.Command.AddOrUpdate(DicomTag.AffectedSOPClassUID, sopClass);
            if (target.Root == QueryRoot.Patient && !string.IsNullOrEmpty(target.PatientId))
            {
                request.Dataset.AddOrUpdate(DicomTag.PatientID, target.PatientId);
            }
        }

        private static void OnProgress(DicomResponse response, DimseExchange exchange, TimeoutWatch watch, RetrieveOutcome outcome, string name, int dimseIndex, AssociationTimeline timeline)
        {
            if (!exchange.ContextAccepted) return;

            watch.Restart();
            var status = response.Command.GetSingleValueOrDefault(DicomTag.Status, response.Status.Code);
            if (status == StatusPending) return;

            outcome.FinalStatus = status;
            outcome.ErrorComment = response.Command.GetSingleValueOrDefault(DicomTag.ErrorComment, string.Empty);
            outcome.Completed = Count(response, DicomTag.NumberOfCompletedSuboperations);
            outcome.Failed = Count(response, DicomTag.NumberOfFailedSuboperations);
            outcome.Warning = Count(response, DicomTag.NumberOfWarningSuboperations);
            timeline?.RecordResponse(dimseIndex, name, status, DimseStatusText.Describe(status));
            watch.Stop();
        }

        private static int? Count(DicomResponse response, DicomTag tag) =>
            response.Command.Contains(tag) ? response.Command.GetSingleValueOrDefault<ushort>(tag, 0) : (int?)null;
    }
}
