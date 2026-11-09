using System.Collections.Generic;
using System.Globalization;

namespace DCST4.Core.Network
{
    public enum MoveDiagnosisKind
    {
        Delivered,
        /// <summary>Delivered, but not everything.</summary>
        PartlyDelivered,
        /// <summary>The system answered successfully and had nothing to send.</summary>
        NothingToMove,
        /// <summary>0xA801: it does not know our AE title.</summary>
        DestinationUnknown,
        /// <summary>It tried, and nothing ever reached our listener.</summary>
        DestinationUnreachable,
        NotSupported,
        /// <summary>It refused for another reason, and the status says which.</summary>
        Refused,
        /// <summary>The association for the C-MOVE itself went wrong.</summary>
        AssociationFailed,
        Cancelled,
    }

    /// <summary>Why a C-MOVE went the way it did, in words meant for whoever has to fix it.</summary>
    public sealed class MoveDiagnosis
    {
        private const ushort MoveDestinationUnknown = 0xA801;

        public MoveDiagnosisKind Kind { get; private set; }
        public string Explanation { get; private set; }

        /// <summary>A problem with how the two systems are set up, not with what the PACS does with data.</summary>
        public bool IsEnvironmental => Kind == MoveDiagnosisKind.DestinationUnknown || Kind == MoveDiagnosisKind.DestinationUnreachable;

        /// <param name="incoming">Every association that reached our listener during the C-MOVE.</param>
        /// <param name="localAe">The AE title we told the system to send to.</param>
        /// <param name="localPort">The port we were listening on.</param>
        /// <param name="tls">Whether the C-MOVE itself went over TLS.</param>
        public static MoveDiagnosis Diagnose(RetrieveOutcome outcome, IReadOnlyList<IncomingAssociation> incoming, int expected, string localAe, int localPort, bool tls)
        {
            var exchange = outcome.Exchange;
            if (exchange.Cancelled) return Make(MoveDiagnosisKind.Cancelled, "The C-MOVE was cancelled.");

            if (exchange.Failure == ConnectivityFailure.ContextNotAccepted)
            {
                return Make(MoveDiagnosisKind.NotSupported, "The system does not accept the C-MOVE SOP class.");
            }
            if (exchange.Failure != ConnectivityFailure.None)
            {
                return Make(MoveDiagnosisKind.AssociationFailed, exchange.Message ?? "The association for the C-MOVE failed.");
            }

            var delivered = outcome.Files.Count;
            if (outcome.FinalStatus == MoveDestinationUnknown)
            {
                return Make(MoveDiagnosisKind.DestinationUnknown,
                    $"The system answered 0xA801, Move Destination unknown: it does not know the AE title {localAe}. " +
                    $"Add {localAe} on the system as a DICOM node, with this computer's address and port {Int(localPort)}. " +
                    "It is usually listed there as a modality or a remote AE.");
            }

            if (outcome.FinalStatus.HasValue && outcome.ResponseSaysSuccess && delivered == 0 && outcome.Failed.GetValueOrDefault() == 0 && outcome.Completed.GetValueOrDefault() == 0)
            {
                return Make(MoveDiagnosisKind.NothingToMove, "The system answered Success but sent nothing, so it found nothing to move.");
            }

            if (delivered >= expected && expected > 0 && outcome.ResponseSaysSuccess)
            {
                return Make(MoveDiagnosisKind.Delivered, $"All {Int(delivered)} instances reached the local Storage SCP on port {Int(localPort)}.");
            }

            if (delivered == 0 && incoming.Count == 0)
            {
                var status = outcome.FinalStatus.HasValue ? "0x" + outcome.FinalStatus.Value.ToString("X4", CultureInfo.InvariantCulture) : "no final status";
                var note = tls ? " The C-MOVE went over TLS, while the local Storage SCP listens without it." : string.Empty;
                return Make(MoveDiagnosisKind.DestinationUnreachable,
                    $"The system took the C-MOVE ({status}) but nothing reached the local Storage SCP on port {Int(localPort)}, so it knows the AE title {localAe} and cannot reach us. " +
                    $"Check the address and port it has for {localAe}. On Windows, Windows Defender Firewall is a common cause: allow incoming TCP on port {Int(localPort)} for DCST4." + note);
            }

            if (delivered > 0)
            {
                var ended = outcome.FinalStatus.HasValue
                    ? "0x" + outcome.FinalStatus.Value.ToString("X4", CultureInfo.InvariantCulture) + " (" + DimseStatusText.Describe(outcome.FinalStatus.Value) + ")"
                    : "no final status";
                return Make(MoveDiagnosisKind.PartlyDelivered, $"{Int(delivered)} of {Int(expected)} instances reached the local Storage SCP, and the C-MOVE ended with {ended}.");
            }

            var failure = outcome.FinalStatus.HasValue
                ? "0x" + outcome.FinalStatus.Value.ToString("X4", CultureInfo.InvariantCulture) + " (" + DimseStatusText.Describe(outcome.FinalStatus.Value) + ")"
                : "no final status";
            return Make(MoveDiagnosisKind.Refused, $"The C-MOVE failed with {failure}, although an association reached the local Storage SCP.");
        }

        private static MoveDiagnosis Make(MoveDiagnosisKind kind, string explanation) => new MoveDiagnosis { Kind = kind, Explanation = explanation };

        private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
