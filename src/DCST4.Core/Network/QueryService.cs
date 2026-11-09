using FellowOakDicom;
using FellowOakDicom.Network;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Core.Network
{
    public enum QueryRoot { Patient, Study }

    /// <summary>What a C-FIND returned, and how it ended.</summary>
    public sealed class QueryOutcome
    {
        public DimseExchange Exchange { get; internal set; }
        public List<DicomDataset> Matches { get; } = new List<DicomDataset>();
        /// <summary>Raw (0000,0900) of the final response, or null if none came.</summary>
        public ushort? FinalStatus { get; internal set; }
        public string ErrorComment { get; internal set; }
        /// <summary>The query stopped at <see cref="QueryService.MaxMatches"/>, so the list is not all there is.</summary>
        public bool Truncated { get; internal set; }

        public bool Succeeded => FinalStatus == 0x0000;
    }

    /// <summary>C-FIND with a chosen information model and level.</summary>
    public static class QueryService
    {
        /// <summary>A guard against a query that matches a whole archive. The test queries are all scoped to one patient or study.</summary>
        public const int MaxMatches = 2000;

        private const ushort StatusPending = 0xFF00;
        private const ushort StatusPendingWarning = 0xFF01;

        private static readonly ILogger Logger = Log.ForContext(typeof(QueryService));

        public static DicomUID SopClass(QueryRoot root) => root == QueryRoot.Patient
            ? DicomUID.PatientRootQueryRetrieveInformationModelFind
            : DicomUID.StudyRootQueryRetrieveInformationModelFind;

        /// <param name="identifier">The keys. Query/Retrieve Level comes from <paramref name="level"/>.</param>
        public static async Task<QueryOutcome> FindAsync(
            DicomEndpoint endpoint,
            NetworkOptions options,
            QueryRoot root,
            DicomQueryRetrieveLevel level,
            DicomDataset identifier,
            CancellationToken token,
            AssociationTimeline timeline = null)
        {
            var sopClass = SopClass(root);
            var request = new DicomCFindRequest(sopClass, level);
            foreach (var item in identifier) request.Dataset.AddOrUpdate(item);

            var outcome = new QueryOutcome();
            var dimseIndex = 0;
            outcome.Exchange = await DimseRunner.RunAsync(endpoint, options, request, sopClass, timeline, (client, watch, exchange) =>
            {
                client.AssociationAccepted += (s, e) =>
                {
                    if (exchange.ContextAccepted && timeline != null) dimseIndex = timeline.RecordRequest("C-FIND", $"{root} Root, level {level}");
                };

                request.OnResponseReceived = (req, response) =>
                {
                    if (!exchange.ContextAccepted) return;

                    watch.Restart();
                    var status = response.Command.GetSingleValueOrDefault(DicomTag.Status, response.Status.Code);
                    if (status == StatusPending || status == StatusPendingWarning)
                    {
                        if (response.Dataset != null && !outcome.Truncated)
                        {
                            outcome.Matches.Add(response.Dataset);
                            if (outcome.Matches.Count >= MaxMatches)
                            {
                                outcome.Truncated = true;
                                Logger.Warning("C-FIND stopped after {count} matches", MaxMatches);
                                watch.Abort($"The query matched more than {MaxMatches.ToString(CultureInfo.InvariantCulture)} records and was stopped.");
                            }
                        }
                        return;
                    }

                    outcome.FinalStatus = status;
                    outcome.ErrorComment = response.Command.GetSingleValueOrDefault(DicomTag.ErrorComment, string.Empty);
                    timeline?.RecordResponse(dimseIndex, "C-FIND", status, DimseStatusText.Describe(status) + ", " + outcome.Matches.Count.ToString(CultureInfo.InvariantCulture) + " matches");
                    watch.Stop();
                };
            }, token).ConfigureAwait(false);

            return outcome;
        }
    }

    /// <summary>Words for the status codes of C-FIND, C-GET and C-MOVE, which differ from those of C-STORE in places.</summary>
    public static class DimseStatusText
    {
        public static string Describe(ushort code)
        {
            switch (code)
            {
                case 0x0000: return "Success";
                case 0xFE00: return "Cancel";
                case 0xFF00: return "Pending";
                case 0xFF01: return "Pending: an optional key is not supported";
                case 0xA700: return "Refused: Out of Resources";
                case 0xA701: return "Refused: Out of Resources, unable to calculate the number of matches";
                case 0xA702: return "Refused: Out of Resources, unable to perform sub-operations";
                case 0xA801: return "Refused: Move Destination unknown";
                case 0xA900: return "Failed: Identifier does not match SOP Class";
                case 0xB000: return "Warning: sub-operations completed with one or more failures";
            }

            if ((code & 0xF000) == 0xC000) return "Failed: Unable to process";
            return StoreStatus.Describe(code);
        }
    }
}
