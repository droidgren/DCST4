using DCST4.Core.Generation;
using FellowOakDicom;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace DCST4.Core.Pipeline
{
    /// <summary>Works out how much data an exam will be, before anything is generated. The DICOMDIR of a saved File-set is not counted.</summary>
    public static class ExamSizeEstimator
    {
        /// <summary>The Part 10 preamble, file meta information and tags of one generated image. Real files of the ten modalities have 1400 to 1900.</summary>
        public const long HeaderBytes = 1650;

        private const int BitsPerByte = 8;
        private const long BytesPerUnit = 1024;
        private static readonly string[] Units = { "bytes", "KB", "MB", "GB", "TB" };

        /// <summary>The size of one image without compression, or null if the values could not be run as they stand.</summary>
        public static long? UncompressedImageBytes(ExamRequest request)
        {
            if (!CanEstimate(request)) return null;

            var pixels = (long)request.Rows * request.Columns * request.FramesPerImage;
            var pixelBytes = request.Modality.IsBinaryMask
                ? (pixels + BitsPerByte - 1) / BitsPerByte
                : pixels * request.Modality.SamplesPerPixel * request.BitsAllocated / BitsPerByte;

            // Pixel Data always has even length.
            return HeaderBytes + pixelBytes + pixelBytes % 2;
        }

        /// <summary>
        /// Generates one image and compresses it as a run would, and returns the size of the file. The test images compress
        /// far better than real ones, and how much depends on the picture, so a typical ratio would say very little.
        /// </summary>
        public static long MeasureImageBytes(ExamRequest request, DicomTransferSyntax syntax, CancellationToken cancellationToken)
        {
            var sample = new ExamRequest
            {
                Modality = request.Modality,
                Preset = request.Preset,
                Identity = request.Identity,
                SeriesCount = 1,
                ImagesPerSeries = 1,
                Rows = request.Rows,
                Columns = request.Columns,
                BitsStored = request.BitsStored,
                CharacterSet = request.CharacterSet,
                BurnedInTags = request.BurnedInTags,
                Uids = request.Uids,
                FramesPerImage = request.FramesPerImage,
                SourceImage = request.SourceImage,
            };

            var image = new ExamGenerator(sample).GetImages(cancellationToken).First();
            cancellationToken.ThrowIfCancellationRequested();
            var file = TransferSyntaxConverter.Convert(image.File, syntax);

            using (var stream = new MemoryStream())
            {
                file.Save(stream);
                return stream.Length;
            }
        }

        /// <summary>As Windows Explorer counts, so that the figure matches what the folder shows: 850 KB, 52.4 MB, 1.3 GB.</summary>
        public static string Format(long bytes)
        {
            double value = bytes;
            var unit = 0;
            while (unit < Units.Length - 1 && System.Math.Round(value, Decimals(unit)) >= BytesPerUnit)
            {
                value /= BytesPerUnit;
                unit++;
            }
            return value.ToString(Decimals(unit) == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + Units[unit];
        }

        private static int Decimals(int unit) => unit < 2 ? 0 : 1;

        private static bool CanEstimate(ExamRequest request)
        {
            if (request.Modality == null || !request.Modality.BitDepths.Contains(request.BitsStored)) return false;
            if (request.Rows < ExamRequest.MinMatrix || request.Rows > ExamRequest.MaxMatrix) return false;
            if (request.Columns < ExamRequest.MinMatrix || request.Columns > ExamRequest.MaxMatrix) return false;
            if (request.SeriesCount < 1 || request.ImagesPerSeries < 1 || request.FramesPerImage < 1) return false;
            return (long)request.SeriesCount * request.ImagesPerSeries <= ExamValidator.MaxTotalImages;
        }
    }
}
