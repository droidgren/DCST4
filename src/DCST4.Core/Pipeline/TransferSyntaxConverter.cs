using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using System.Globalization;
using System.Linq;

namespace DCST4.Core.Pipeline
{
    /// <summary>Puts an image in the selected transfer syntax.</summary>
    public static class TransferSyntaxConverter
    {
        /// <summary>DS holds at most 16 characters.</summary>
        private const string RatioFormat = "0.####";
        private const string ImageTypeDerived = "DERIVED";

        public static DicomFile Convert(DicomFile file, DicomTransferSyntax syntax)
        {
            // An object without Pixel Data has nothing to compress, so a JPEG run sends it uncompressed.
            if (syntax.IsEncapsulated && !file.Dataset.Contains(DicomTag.PixelData)) return file;

            if (file.Dataset.InternalTransferSyntax == syntax)
            {
                UseOtherByteForEncapsulatedPixelData(file.Dataset);
                return file;
            }

            var uncompressedSize = syntax.IsLossy ? PixelBytes(file.Dataset) : 0;
            var converted = file.Clone(syntax);
            UseOtherByteForEncapsulatedPixelData(converted.Dataset);
            if (syntax.IsLossy) MarkLossy(converted.Dataset, syntax, uncompressedSize);
            return converted;
        }

        /// <summary>
        /// fo-dicom writes Lossy Image Compression and the method, but works out the ratio from the first frame alone. Image Type
        /// value 1 is not ORIGINAL once a lossy step has been applied (PS3.3 C.7.6.1.1.2).
        /// </summary>
        private static void MarkLossy(DicomDataset dataset, DicomTransferSyntax syntax, long uncompressedSize)
        {
            var compressedSize = PixelBytes(dataset);
            var ratio = compressedSize > 0 ? (double)uncompressedSize / compressedSize : 0;

            dataset.AddOrUpdate(DicomTag.LossyImageCompression, "01");
            dataset.AddOrUpdate(DicomTag.LossyImageCompressionMethod, syntax.LossyCompressionMethod);
            dataset.AddOrUpdate(DicomTag.LossyImageCompressionRatio, ratio.ToString(RatioFormat, CultureInfo.InvariantCulture));

            if (dataset.TryGetValues<string>(DicomTag.ImageType, out var imageType) && imageType.Length > 0 && imageType[0] != ImageTypeDerived)
            {
                dataset.AddOrUpdate(DicomTag.ImageType, new[] { ImageTypeDerived }.Concat(imageType.Skip(1)).ToArray());
            }
        }

        private static long PixelBytes(DicomDataset dataset)
        {
            var pixelData = DicomPixelData.Create(dataset);
            long size = 0;
            for (var frame = 0; frame < pixelData.NumberOfFrames; frame++) size += pixelData.GetFrame(frame).Size;
            return size;
        }

        /// <summary>
        /// The standard (PS3.5 A.4) requires VR OB for encapsulated Pixel Data, but the transcoder keeps OW for images with
        /// 16 bits allocated. DCMTK warns about those files, and a strict PACS could reject them.
        /// </summary>
        private static void UseOtherByteForEncapsulatedPixelData(DicomDataset dataset)
        {
            var wordFragments = dataset.GetDicomItem<DicomItem>(DicomTag.PixelData) as DicomOtherWordFragment;
            if (wordFragments == null) return;

            var byteFragments = new DicomOtherByteFragment(DicomTag.PixelData);
            foreach (var offset in wordFragments.OffsetTable) byteFragments.OffsetTable.Add(offset);
            foreach (var fragment in wordFragments.Fragments) byteFragments.Fragments.Add(fragment);
            dataset.AddOrUpdate(byteFragments);
        }
    }
}
