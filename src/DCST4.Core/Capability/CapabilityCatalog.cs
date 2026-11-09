using DCST4.Core.Generation.Datasets;
using FellowOakDicom;
using System.Collections.Generic;
using System.Linq;

namespace DCST4.Core.Capability
{
    /// <summary>A transfer syntax of the matrix, with a label short enough for a column.</summary>
    public sealed class ProbeSyntax
    {
        public ProbeSyntax(DicomTransferSyntax syntax, string shortName)
        {
            Syntax = syntax;
            ShortName = shortName;
        }

        public DicomTransferSyntax Syntax { get; }
        public string ShortName { get; }
    }

    /// <summary>What the negotiation probe proposes unless told otherwise.</summary>
    public static class CapabilityCatalog
    {
        public static IReadOnlyList<ProbeSyntax> TransferSyntaxes { get; } = new[]
        {
            new ProbeSyntax(DicomTransferSyntax.ImplicitVRLittleEndian, "Implicit"),
            new ProbeSyntax(DicomTransferSyntax.ExplicitVRLittleEndian, "Explicit LE"),
            new ProbeSyntax(DicomTransferSyntax.ExplicitVRBigEndian, "Explicit BE"),
            new ProbeSyntax(DicomTransferSyntax.DeflatedExplicitVRLittleEndian, "Deflated"),
            new ProbeSyntax(DicomTransferSyntax.JPEGProcess1, "JPEG Base"),
            new ProbeSyntax(DicomTransferSyntax.JPEGProcess14SV1, "JPEG LL"),
            new ProbeSyntax(DicomTransferSyntax.JPEGLSLossless, "JLS LL"),
            new ProbeSyntax(DicomTransferSyntax.JPEGLSNearLossless, "JLS NL"),
            new ProbeSyntax(DicomTransferSyntax.JPEG2000Lossless, "J2K LL"),
            new ProbeSyntax(DicomTransferSyntax.JPEG2000Lossy, "J2K"),
            new ProbeSyntax(DicomTransferSyntax.RLELossless, "RLE"),
            new ProbeSyntax(DicomTransferSyntax.HTJ2KLossless, "HTJ2K LL"),
            new ProbeSyntax(DicomTransferSyntax.HTJ2KLosslessRPCL, "HTJ2K RPCL"),
            new ProbeSyntax(DicomTransferSyntax.HTJ2K, "HTJ2K"),
            new ProbeSyntax(DicomTransferSyntax.MPEG4AVCH264HighProfileLevel41, "H.264 4.1"),
            new ProbeSyntax(DicomTransferSyntax.MPEG4AVCH264BDCompatibleHighProfileLevel41, "H.264 BD"),
            new ProbeSyntax(DicomTransferSyntax.MPEG4AVCH264HighProfileLevel42For2DVideo, "H.264 2D"),
            new ProbeSyntax(DicomTransferSyntax.MPEG4AVCH264HighProfileLevel42For3DVideo, "H.264 3D"),
            new ProbeSyntax(DicomTransferSyntax.MPEG4AVCH264StereoHighProfileLevel42, "H.264 St"),
        };

        /// <summary>The Query/Retrieve SOP classes, which also get extended negotiation.</summary>
        public static IReadOnlyList<DicomUID> QueryRetrieveFind { get; } = new[]
        {
            DicomUID.StudyRootQueryRetrieveInformationModelFind,
            DicomUID.PatientRootQueryRetrieveInformationModelFind,
        };

        public static IReadOnlyList<DicomUID> QueryRetrieveMoveAndGet { get; } = new[]
        {
            DicomUID.StudyRootQueryRetrieveInformationModelMove,
            DicomUID.StudyRootQueryRetrieveInformationModelGet,
            DicomUID.PatientRootQueryRetrieveInformationModelMove,
            DicomUID.PatientRootQueryRetrieveInformationModelGet,
        };

        /// <summary>Every storage SOP class DCST4 can generate. Nothing is sent, so a profile that must be asked for by name counts too.</summary>
        public static IReadOnlyList<DicomUID> StorageSopClasses { get; } = DatasetProfileCatalog.All
            .Select(p => p.IsImage ? p.Image.SopClass : p.Object.SopClass)
            .Distinct()
            .ToList();

        public static IReadOnlyList<DicomUID> ServiceSopClasses { get; } = new[] { DicomUID.Verification, DicomUID.StorageCommitmentPushModel }
            .Concat(QueryRetrieveFind)
            .Concat(QueryRetrieveMoveAndGet)
            .ToList();

        public static IReadOnlyList<DicomUID> SopClasses { get; } = StorageSopClasses.Concat(ServiceSopClasses).ToList();

        /// <summary>The column label of a syntax. A syntax outside the catalog shows its UID.</summary>
        public static string ShortName(DicomTransferSyntax syntax) =>
            TransferSyntaxes.FirstOrDefault(s => s.Syntax == syntax)?.ShortName ?? syntax.UID.UID;
    }
}
