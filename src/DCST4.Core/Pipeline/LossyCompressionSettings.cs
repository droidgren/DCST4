using DCST4.Core.Codecs;
using FellowOakDicom;
using FellowOakDicom.Imaging.Codec;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace DCST4.Core.Pipeline
{
    /// <summary>
    /// How hard each lossy encoder compresses, in the encoder's own unit. Null leaves the encoder at its default, which is
    /// what DCST4 did before these settings existed. HTJ2K lossy has no setting: the PureCodecs encoder does not reach a
    /// ratio that is asked of it. JPEG XL has none either, since DCST4 writes it lossless.
    /// </summary>
    public sealed class LossyCompressionSettings
    {
        public const int MinJpegQuality = 1;
        public const int MaxJpegQuality = 100;
        /// <summary>What fo-dicom uses when it is given no quality.</summary>
        public const int DefaultJpegQuality = 90;
        public const int MinJpeg2000Ratio = 2;
        public const int MaxJpeg2000Ratio = 200;
        /// <summary>What fo-dicom and PureCodecs use when they are given no rate.</summary>
        public const int DefaultJpeg2000Ratio = 20;
        public const int MinJpegLsNear = 1;
        public const int MaxJpegLsNear = 255;
        /// <summary>What PureCodecs uses when it is given no allowed error.</summary>
        public const int DefaultJpegLsNear = 3;
        public const double MinH264Mbps = 0.1;
        public const double MaxH264Mbps = 50;

        /// <summary>JPEG Baseline and JPEG Extended: 1 to 100.</summary>
        public int? JpegQuality { get; set; }
        /// <summary>JPEG 2000 (lossy): the compression ratio, N for N:1.</summary>
        public int? Jpeg2000Ratio { get; set; }
        /// <summary>JPEG-LS Near-Lossless: the largest difference allowed in a sample (NEAR).</summary>
        public int? JpegLsNear { get; set; }
        /// <summary>H.264: the average bitrate in Mbit/s.</summary>
        public double? H264Mbps { get; set; }

        public bool IsDefault => JpegQuality == null && Jpeg2000Ratio == null && JpegLsNear == null && H264Mbps == null;

        public LossyCompressionSettings Clone() => (LossyCompressionSettings)MemberwiseClone();

        /// <summary>Every problem with the values. Empty if they can be used.</summary>
        public List<string> Validate()
        {
            var problems = new List<string>();
            if (JpegQuality < MinJpegQuality || JpegQuality > MaxJpegQuality) problems.Add($"JPEG quality must be between {MinJpegQuality} and {MaxJpegQuality}.");
            if (Jpeg2000Ratio < MinJpeg2000Ratio || Jpeg2000Ratio > MaxJpeg2000Ratio) problems.Add($"The JPEG 2000 ratio must be between {MinJpeg2000Ratio} and {MaxJpeg2000Ratio}.");
            if (JpegLsNear < MinJpegLsNear || JpegLsNear > MaxJpegLsNear) problems.Add($"JPEG-LS NEAR must be between {MinJpegLsNear} and {MaxJpegLsNear}.");
            if (H264Mbps != null && (double.IsNaN(H264Mbps.Value) || H264Mbps < MinH264Mbps || H264Mbps > MaxH264Mbps))
            {
                problems.Add(string.Format(CultureInfo.InvariantCulture, "The H.264 bitrate must be between {0} and {1} Mbit/s.", MinH264Mbps, MaxH264Mbps));
            }
            return problems;
        }

        public static bool IsJpegLossy(DicomTransferSyntax syntax) => syntax == DicomTransferSyntax.JPEGProcess1 || syntax == DicomTransferSyntax.JPEGProcess2_4;

        /// <summary>The value set for this syntax, as text such as "JPEG quality 75". Null if the encoder runs at its default.</summary>
        public string Describe(DicomTransferSyntax syntax)
        {
            if (syntax == null) return null;
            if (IsJpegLossy(syntax) && JpegQuality != null) return "JPEG quality " + Text(JpegQuality.Value);
            if (syntax == DicomTransferSyntax.JPEG2000Lossy && Jpeg2000Ratio != null) return "JPEG 2000 ratio " + Text(Jpeg2000Ratio.Value) + ":1";
            if (syntax == DicomTransferSyntax.JPEGLSNearLossless && JpegLsNear != null) return "JPEG-LS NEAR " + Text(JpegLsNear.Value);
            if (OwnCodecs.IsH264(syntax) && H264Mbps != null) return "H.264 " + Text(H264Mbps.Value) + " Mbit/s";
            return null;
        }

        /// <summary>Every value that is set, such as "JPEG quality 75, JPEG 2000 ratio 10:1". Empty if none is.</summary>
        public string Describe()
        {
            var parts = new List<string>();
            if (JpegQuality != null) parts.Add("JPEG quality " + Text(JpegQuality.Value));
            if (Jpeg2000Ratio != null) parts.Add("JPEG 2000 ratio " + Text(Jpeg2000Ratio.Value) + ":1");
            if (JpegLsNear != null) parts.Add("JPEG-LS NEAR " + Text(JpegLsNear.Value));
            if (H264Mbps != null) parts.Add("H.264 " + Text(H264Mbps.Value) + " Mbit/s");
            return string.Join(", ", parts);
        }

        /// <summary>A short text for keys and contexts. Empty when everything is at the default, so that older keys stay as they were.</summary>
        public string Key
        {
            get
            {
                var parts = new List<string>();
                if (JpegQuality != null) parts.Add("q" + Text(JpegQuality.Value));
                if (Jpeg2000Ratio != null) parts.Add("j2k" + Text(Jpeg2000Ratio.Value));
                if (JpegLsNear != null) parts.Add("near" + Text(JpegLsNear.Value));
                if (H264Mbps != null) parts.Add("h264-" + Text(H264Mbps.Value));
                return string.Join("/", parts);
            }
        }

        /// <summary>The parameters for the encoder of this syntax. Null for the encoder's default, which keeps the output as it was.</summary>
        internal DicomCodecParams CodecParams(DicomTransferSyntax syntax, int bitsStored)
        {
            if (IsJpegLossy(syntax) && JpegQuality != null) return new DicomJpegParams { Quality = JpegQuality.Value };
            if (syntax == DicomTransferSyntax.JPEG2000Lossy && Jpeg2000Ratio != null)
            {
                // The rate of fo-dicom's own parameters is what PureCodecs reaches: 10 gives 10:1. Its TargetRatio lands further off.
                return new DicomJpeg2000Params { Irreversible = true, Rate = Jpeg2000Ratio.Value };
            }
            if (syntax == DicomTransferSyntax.JPEGLSNearLossless && JpegLsNear != null)
            {
                // NEAR is at most half the largest sample value (ITU-T T.87 C.2.4.1.1).
                var most = Math.Max(1, ((1 << Math.Max(2, bitsStored)) - 1) / 2);
                return new FellowOakDicom.PureCodecs.JpegLs.DicomJpegLsParams { AllowedError = Math.Min(JpegLsNear.Value, most) };
            }
            return null;
        }

        /// <summary>The H.264 bitrate in bit/s. Null for the encoder's own choice.</summary>
        internal int? H264Bitrate => H264Mbps == null ? (int?)null : (int)Math.Round(H264Mbps.Value * 1000000);

        private static string Text(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        public override string ToString() => IsDefault ? "codec defaults" : Describe();
    }
}
