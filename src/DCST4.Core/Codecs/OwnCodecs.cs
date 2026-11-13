using FellowOakDicom;
using System.Collections.Generic;

namespace DCST4.Core.Codecs
{
    /// <summary>
    /// The transfer syntaxes that DCST4 encodes itself instead of through fo-dicom and PureCodecs: JPEG XL with its own
    /// encoder, and H.264 with the encoder of Windows. None of them can be decoded here.
    /// </summary>
    public static class OwnCodecs
    {
        public const string JpegXlLosslessUid = "1.2.840.10008.1.2.4.110";
        public const string JpegXlUid = "1.2.840.10008.1.2.4.112";
        public const string H264HighProfileLevel41Uid = "1.2.840.10008.1.2.4.102";
        /// <summary>The defined term for MPEG-4 AVC/H.264 in Lossy Image Compression Method (PS3.3 C.7.6.1.1.5).</summary>
        public const string H264CompressionMethod = "ISO_14496_10";

        /// <summary>The MPEG-2, H.264 and HEVC syntaxes, with their fragmentable forms (PS3.5 8.2.5 to 8.2.11).</summary>
        private static readonly HashSet<string> VideoUids = new HashSet<string>
        {
            "1.2.840.10008.1.2.4.100", "1.2.840.10008.1.2.4.100.1", "1.2.840.10008.1.2.4.101", "1.2.840.10008.1.2.4.101.1",
            "1.2.840.10008.1.2.4.102", "1.2.840.10008.1.2.4.102.1", "1.2.840.10008.1.2.4.103", "1.2.840.10008.1.2.4.103.1",
            "1.2.840.10008.1.2.4.104", "1.2.840.10008.1.2.4.104.1", "1.2.840.10008.1.2.4.105", "1.2.840.10008.1.2.4.105.1",
            "1.2.840.10008.1.2.4.106", "1.2.840.10008.1.2.4.106.1", "1.2.840.10008.1.2.4.107", "1.2.840.10008.1.2.4.108",
        };

        /// <summary>The two JPEG XL syntaxes DCST4 writes. JPEG Recompression (.111) is not one of them.</summary>
        public static bool IsJpegXl(DicomTransferSyntax syntax) =>
            syntax != null && (syntax.UID.UID == JpegXlLosslessUid || syntax.UID.UID == JpegXlUid);

        public static bool IsH264(DicomTransferSyntax syntax) => syntax != null && syntax.UID.UID == H264HighProfileLevel41Uid;

        /// <summary>A video syntax: one stream for all frames, which fo-dicom cannot split into frames.</summary>
        public static bool IsVideo(DicomTransferSyntax syntax) => syntax != null && VideoUids.Contains(syntax.UID.UID);

        private static readonly FellowOakDicom.PureCodecs.PureTranscoderManager PureCodecs = new FellowOakDicom.PureCodecs.PureTranscoderManager();

        /// <summary>Whether an image in this syntax can be decoded, which sending it in another syntax needs.</summary>
        public static bool CanDecode(DicomTransferSyntax syntax) => syntax != null && (!syntax.IsEncapsulated || PureCodecs.HasCodec(syntax));
    }
}
