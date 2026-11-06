using FellowOakDicom;
using FellowOakDicom.Imaging.Codec;

namespace DCST4.Core.Pipeline
{
    /// <summary>Puts an image in the selected transfer syntax.</summary>
    public static class TransferSyntaxConverter
    {
        public static DicomFile Convert(DicomFile file, DicomTransferSyntax syntax)
        {
            var converted = file.Dataset.InternalTransferSyntax == syntax ? file : file.Clone(syntax);
            UseOtherByteForEncapsulatedPixelData(converted.Dataset);
            return converted;
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
