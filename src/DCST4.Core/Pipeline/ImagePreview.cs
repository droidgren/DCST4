using DCST4.Core.Codecs;
using DCST4.Core.Generation;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace DCST4.Core.Pipeline
{
    /// <summary>The first frame of a preview, as 32-bit BGRA pixels, and what it shows.</summary>
    public sealed class PreviewImage
    {
        public int Width { get; set; }
        public int Height { get; set; }
        /// <summary>Blue, green, red and alpha for each pixel, row by row. Width * 4 bytes per row.</summary>
        public byte[] Bgra { get; set; }
        /// <summary>The object, matrix, bits, transfer syntax, file size and window, on one line.</summary>
        public string Description { get; set; }
        /// <summary>Why the picture is not quite as sent, or null.</summary>
        public string Note { get; set; }
        /// <summary>The size of the sample file in the selected syntax, or null if it was not compressed.</summary>
        public long? FileBytes { get; set; }
    }

    /// <summary>
    /// Generates the first image of an exam, puts it in the transfer syntax it would be sent in, decodes it again and renders
    /// its first frame, so that what lossy compression does can be seen before anything is sent.
    /// </summary>
    public static class ImagePreview
    {
        public static PreviewImage Render(ExamRequest request, DicomTransferSyntax syntax, CancellationToken cancellationToken)
        {
            if (request?.Modality == null) throw new ArgumentNullException(nameof(request));
            if (syntax == null) throw new ArgumentNullException(nameof(syntax));
            if (!request.Modality.HasPixelData) throw new InvalidOperationException(request.Modality.DisplayName + " has no pixels to preview.");

            // One frame is all the preview shows, and a long cine loop would take minutes to encode.
            var image = new ExamGenerator(ExamSizeEstimator.SampleRequest(request, 1)).GetImages(cancellationToken).First();
            cancellationToken.ThrowIfCancellationRequested();

            var file = image.File;
            string note = null;
            long? fileBytes = null;
            if (OwnCodecs.CanDecode(syntax))
            {
                file = TransferSyntaxConverter.Convert(file, syntax, cancellationToken, lossy: request.Lossy);
                cancellationToken.ThrowIfCancellationRequested();
                fileBytes = SavedBytes(file);
                if (file.Dataset.InternalTransferSyntax.IsEncapsulated) file = file.Clone(DicomTransferSyntax.ExplicitVRLittleEndian);
            }
            else
            {
                note = "DCST4 cannot decode " + syntax.UID.Name + ", so the image is shown before compression.";
            }
            cancellationToken.ThrowIfCancellationRequested();

            var dicomImage = new DicomImage(file.Dataset);
            var rendered = dicomImage.RenderImage(0);
            var bgra = rendered.AsBytes();

            return new PreviewImage
            {
                Width = dicomImage.Width,
                Height = dicomImage.Height,
                Bgra = bgra,
                Description = Describe(request, syntax, dicomImage, fileBytes),
                Note = note,
                FileBytes = fileBytes,
            };
        }

        private static long SavedBytes(DicomFile file)
        {
            using (var stream = new MemoryStream())
            {
                file.Save(stream);
                return stream.Length;
            }
        }

        private static string Describe(ExamRequest request, DicomTransferSyntax syntax, DicomImage image, long? fileBytes)
        {
            var text = string.Format(CultureInfo.InvariantCulture, "{0}, {1} x {2}, {3} bits stored, {4}",
                request.Modality.DisplayName, image.Width, image.Height, request.BitsStored, syntax.UID.Name);
            if (fileBytes != null) text += ", file " + ExamSizeEstimator.Format(fileBytes.Value) + (request.Modality.IsMultiFrame ? " with one frame" : string.Empty);
            if (request.Modality.SamplesPerPixel == 1)
            {
                text += string.Format(CultureInfo.InvariantCulture, ". Window {0:0.##} / {1:0.##}", image.WindowCenter, image.WindowWidth);
            }
            return text + ".";
        }
    }
}
