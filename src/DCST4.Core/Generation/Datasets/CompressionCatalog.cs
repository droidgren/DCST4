using DCST4.Core.Generation.Modalities;
using DCST4.Core.Pipeline;
using DCST4.Core.TestData;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace DCST4.Core.Generation.Datasets
{
    /// <summary>A transfer syntax a dataset can be generated in, with the name used on the command line.</summary>
    public sealed class SyntaxChoice
    {
        internal SyntaxChoice(string id, DicomTransferSyntax syntax, int maxBits = 16)
        {
            Id = id;
            Syntax = syntax;
            MaxBits = maxBits;
        }

        public string Id { get; }
        public DicomTransferSyntax Syntax { get; }
        /// <summary>The most bits per sample the syntax can hold at all.</summary>
        public int MaxBits { get; }
    }

    /// <summary>One combination that was tried.</summary>
    public sealed class CodecProbeResult
    {
        public string SyntaxId { get; set; }
        public string TransferSyntaxUid { get; set; }
        public int BitsStored { get; set; }
        public int SamplesPerPixel { get; set; }
        public bool Available => Reason == null;
        /// <summary>Why it cannot be produced. Null if it can.</summary>
        public string Reason { get; set; }
    }

    /// <summary>
    /// The transfer syntaxes of the test-suite datasets. PureCodecs is pre-1.0, so nothing is assumed about its encoders:
    /// each combination of syntax and bit depth is tried on a small image, and what fails is reported as unavailable.
    /// </summary>
    public static class CompressionCatalog
    {
        private const int ProbeMatrix = 64;

        public static readonly IReadOnlyList<SyntaxChoice> Uncompressed = new[]
        {
            new SyntaxChoice("explicit", DicomTransferSyntax.ExplicitVRLittleEndian),
            new SyntaxChoice("implicit", DicomTransferSyntax.ImplicitVRLittleEndian),
        };

        public static readonly IReadOnlyList<SyntaxChoice> Compressed = new[]
        {
            new SyntaxChoice("jpeg-baseline", DicomTransferSyntax.JPEGProcess1, 8),
            new SyntaxChoice("jpeg-extended", DicomTransferSyntax.JPEGProcess2_4, 12),
            new SyntaxChoice("jpeg-lossless", DicomTransferSyntax.JPEGProcess14SV1),
            new SyntaxChoice("jpeg-ls", DicomTransferSyntax.JPEGLSLossless),
            new SyntaxChoice("jpeg-ls-near", DicomTransferSyntax.JPEGLSNearLossless),
            new SyntaxChoice("jpeg2000", DicomTransferSyntax.JPEG2000Lossless),
            new SyntaxChoice("jpeg2000-lossy", DicomTransferSyntax.JPEG2000Lossy),
            new SyntaxChoice("htj2k", DicomTransferSyntax.HTJ2KLossless),
            new SyntaxChoice("htj2k-rpcl", DicomTransferSyntax.HTJ2KLosslessRPCL),
            new SyntaxChoice("htj2k-lossy", DicomTransferSyntax.HTJ2K),
            new SyntaxChoice("rle", DicomTransferSyntax.RLELossless),
        };

        public static IEnumerable<SyntaxChoice> All => Uncompressed.Concat(Compressed);

        /// <summary>By the name above or by Transfer Syntax UID. Null if it is neither.</summary>
        public static SyntaxChoice Find(string text) =>
            All.FirstOrDefault(c => string.Equals(c.Id, text?.Trim(), StringComparison.OrdinalIgnoreCase) || c.Syntax.UID.UID == text?.Trim());

        private static readonly ConcurrentDictionary<string, CodecProbeResult> Probed = new ConcurrentDictionary<string, CodecProbeResult>();

        /// <summary>Every syntax against 8, 12 and 16 bit grayscale and 8-bit RGB.</summary>
        public static IReadOnlyList<CodecProbeResult> ProbeAll()
        {
            var results = new List<CodecProbeResult>();
            foreach (var choice in Compressed)
            {
                foreach (var bits in new[] { 8, 12, 16 }) results.Add(Probe(choice, bits, 1));
                results.Add(Probe(choice, 8, 3));
            }
            return results;
        }

        /// <summary>Tries the encoder once for this combination, and remembers the answer.</summary>
        public static CodecProbeResult Probe(SyntaxChoice choice, int bitsStored, int samplesPerPixel) =>
            Probed.GetOrAdd(choice.Id + "/" + bitsStored + "/" + samplesPerPixel, key => TryEncode(choice, bitsStored, samplesPerPixel));

        /// <summary>The choice for a syntax that may have come from outside the catalog. Null if it is not in it.</summary>
        public static SyntaxChoice For(DicomTransferSyntax syntax) => All.FirstOrDefault(c => c.Syntax == syntax);

        private static CodecProbeResult TryEncode(SyntaxChoice choice, int bitsStored, int samplesPerPixel)
        {
            var result = new CodecProbeResult
            {
                SyntaxId = choice.Id,
                TransferSyntaxUid = choice.Syntax.UID.UID,
                BitsStored = bitsStored,
                SamplesPerPixel = samplesPerPixel,
            };

            if (bitsStored > choice.MaxBits)
            {
                result.Reason = $"{choice.Syntax.UID.Name} holds at most {choice.MaxBits} bits.";
                return result;
            }

            try
            {
                var file = new DicomFile(ProbeImage(bitsStored, samplesPerPixel));
                var encoded = TransferSyntaxConverter.Convert(file, choice.Syntax);
                var pixelData = DicomPixelData.Create(encoded.Dataset);
                if (pixelData.NumberOfFrames != 1 || pixelData.GetFrame(0).Size == 0) result.Reason = "The encoder produced no frame.";
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                // Whatever the pre-1.0 encoder does not support, so a reason to report rather than a failure.
                result.Reason = ex.GetType().Name + ": " + ex.Message;
            }
            return result;
        }

        private static DicomDataset ProbeImage(int bitsStored, int samplesPerPixel)
        {
            var modality = samplesPerPixel > 1 ? (ModalityProfile)new SecondaryCaptureProfile()
                : bitsStored <= 8 ? ModalityCatalog.Find("CR") : ModalityCatalog.Find("MG");
            var uids = new DeterministicUidSource(null, "codec-probe");
            var request = new ExamRequest
            {
                Modality = modality,
                Preset = modality.Presets[0],
                Identity = new ExamIdentity { StudyInstanceUid = uids.Uid("study"), StudyDateTime = TestDatasetRequest.DefaultReferenceDate },
                Rows = ProbeMatrix,
                Columns = ProbeMatrix,
                BitsStored = bitsStored,
                Uids = uids,
            };
            var series = new SeriesContext
            {
                SeriesInstanceUid = uids.Uid("series"),
                FrameOfReferenceUid = uids.Uid("frame-of-reference"),
                StudyTime = TestDatasetRequest.DefaultReferenceDate,
                SeriesTime = TestDatasetRequest.DefaultReferenceDate,
            };
            return DicomImageBuilder.Build(request, series, 0, TestDatasetRequest.DefaultReferenceDate);
        }
    }
}
