using FellowOakDicom.Network;
using Serilog;
using System.Collections.Generic;
using System.Linq;

namespace DCST4.Core.Network
{
    /// <summary>One presentation context as it came out of the negotiation.</summary>
    public sealed class NegotiatedContext
    {
        public int Id { get; set; }
        public string AbstractSyntaxUid { get; set; }
        public string AbstractSyntaxName { get; set; }
        public List<string> ProposedTransferSyntaxUids { get; set; } = new List<string>();
        /// <summary>fo-dicom's name of the result, e.g. Accept or RejectTransferSyntaxesNotSupported.</summary>
        public string Result { get; set; }
        /// <summary>Null when the context was not accepted.</summary>
        public string AcceptedTransferSyntaxUid { get; set; }
    }

    /// <summary>Reads and logs what an accepted association negotiated. Shared by everything that opens an association.</summary>
    public static class AssociationNegotiation
    {
        /// <summary>Writes the negotiation to <paramref name="logger"/>, so the log lines keep the caller as their source.</summary>
        public static void Log(ILogger logger, DicomAssociation association)
        {
            logger.Information("Association accepted: {calling} -> {called}, max PDU {pdu}",
                association.CallingAE, association.CalledAE, association.MaximumPDULength);

            foreach (var context in association.PresentationContexts)
            {
                var proposed = string.Join(", ", context.GetTransferSyntaxes().Select(t => t.UID.Name));
                if (context.Result == DicomPresentationContextResult.Accept)
                {
                    logger.Information("Presentation context {id} {sopClass}: proposed [{proposed}], accepted {accepted}",
                        context.ID, context.AbstractSyntax.Name, proposed, context.AcceptedTransferSyntax?.UID.Name);
                }
                else
                {
                    logger.Warning("Presentation context {id} {sopClass}: proposed [{proposed}], NOT accepted: {result}",
                        context.ID, context.AbstractSyntax.Name, proposed, context.Result);
                }
            }
        }

        public static List<NegotiatedContext> Read(DicomAssociation association)
        {
            return association.PresentationContexts.Select(context => new NegotiatedContext
            {
                Id = context.ID,
                AbstractSyntaxUid = context.AbstractSyntax.UID,
                AbstractSyntaxName = context.AbstractSyntax.Name,
                ProposedTransferSyntaxUids = context.GetTransferSyntaxes().Select(t => t.UID.UID).ToList(),
                Result = context.Result.ToString(),
                AcceptedTransferSyntaxUid = context.Result == DicomPresentationContextResult.Accept ? context.AcceptedTransferSyntax?.UID.UID : null,
            }).ToList();
        }
    }
}
