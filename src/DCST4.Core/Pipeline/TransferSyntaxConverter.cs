using DCST4.Core.Generation;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using FellowOakDicom.IO.Buffer;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Core.Pipeline
{
    /// <summary>Puts an image in the selected transfer syntax.</summary>
    public static class TransferSyntaxConverter
    {
        /// <summary>DS holds at most 16 characters.</summary>
        private const string RatioFormat = "0.####";
        private const string ImageTypeDerived = "DERIVED";
        /// <summary>The tag and length in front of each fragment. The Basic Offset Table counts them.</summary>
        private const uint FragmentItemHeaderBytes = 8;
        /// <summary>A frame of 4096 x 4096 at 16 bits is 32 MB as drawn, so this many are at most half a gigabyte.</summary>
        private const int MaxFramesEncodedAtOnce = 16;
        /// <summary>Small frames are not worth a collection after every batch.</summary>
        private const long CollectAfterBatchBytes = 8 * 1024 * 1024;

        /// <summary>
        /// <paramref name="frameEncoded"/> is told the number of frames done and the total, for an image whose frames are
        /// encoded one at a time. Such an image can take minutes, so it also follows <paramref name="token"/>.
        /// </summary>
        public static DicomFile Convert(DicomFile file, DicomTransferSyntax syntax, CancellationToken token = default(CancellationToken), Action<int, int> frameEncoded = null)
        {
            // An object without Pixel Data has nothing to compress, so a JPEG run sends it uncompressed.
            if (syntax.IsEncapsulated && !file.Dataset.Contains(DicomTag.PixelData)) return file;

            if (file.Dataset.InternalTransferSyntax == syntax)
            {
                UseOtherByteForEncapsulatedPixelData(file.Dataset);
                return file;
            }

            var uncompressedSize = syntax.IsLossy ? PixelBytes(file.Dataset) : 0;
            var rendered = syntax.IsEncapsulated ? (file.Dataset.GetDicomItem<DicomElement>(DicomTag.PixelData)?.Buffer as RenderedFramesBuffer) : null;
            var converted = rendered != null ? EncodeFrameByFrame(file, rendered, syntax, token, frameEncoded) : file.Clone(syntax);
            // The transcoder adds Number of Frames, which a single-frame IOD does not have.
            if (!file.Dataset.Contains(DicomTag.NumberOfFrames)) converted.Dataset.Remove(DicomTag.NumberOfFrames);
            UseOtherByteForEncapsulatedPixelData(converted.Dataset);
            PadOddFragments(converted.Dataset);
            if (syntax.IsLossy) MarkLossy(converted.Dataset, syntax, uncompressedSize);
            return converted;
        }

        /// <summary>
        /// The transcoder reads every frame before it writes the first, which a long cine loop or a tomosynthesis object does
        /// not fit in memory for. Here each frame is drawn, encoded as an image of its own and let go, so that only the
        /// encoded frames are kept. The result is the one the transcoder gives for the whole image.
        /// </summary>
        private static DicomFile EncodeFrameByFrame(DicomFile file, RenderedFramesBuffer frames, DicomTransferSyntax syntax, CancellationToken token, Action<int, int> frameEncoded)
        {
            var source = file.Dataset;
            var inWords = source.GetDicomItem<DicomItem>(DicomTag.PixelData) is DicomOtherWord;

            // What describes the pixels of one frame, which is all an encoder reads.
            var description = new DicomDataset(source.InternalTransferSyntax);
            foreach (var item in source.Where(item => item.Tag.Group == DicomTag.Rows.Group && !(item is DicomSequence))) description.Add(item);

            var result = new DicomDataset(syntax);
            foreach (var item in source.Where(item => item.Tag != DicomTag.PixelData)) result.Add(item);
            DicomPixelData encoded = null;

            // The frames are drawn one after the other, since the drawing is not made for several threads, and then encoded
            // side by side, which is where the time goes: JPEG Baseline takes several times as long as the drawing. A batch
            // is as many frames as there are cores, so that is also the most frames in memory as drawn.
            var batch = Math.Max(1, Math.Min(Environment.ProcessorCount, MaxFramesEncodedAtOnce));
            for (var start = 0; start < frames.Frames; start += batch)
            {
                token.ThrowIfCancellationRequested();

                var count = Math.Min(batch, frames.Frames - start);
                var drawn = new byte[count][];
                for (var i = 0; i < count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    drawn[i] = frames.ReadFrame(start + i);
                }

                var ones = new DicomDataset[count];
                Parallel.For(0, count, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = batch }, i =>
                {
                    var single = description.Clone();
                    single.AddOrUpdate(DicomTag.NumberOfFrames, 1);
                    var bytes = new MemoryByteBuffer(drawn[i]);
                    if (inWords) single.AddOrUpdate(new DicomOtherWord(DicomTag.PixelData, bytes));
                    else single.AddOrUpdate(new DicomOtherByte(DicomTag.PixelData, bytes));
                    ones[i] = new DicomTranscoder(source.InternalTransferSyntax, syntax).Transcode(single);
                });

                // In the order of the frames, whatever order they were finished in.
                for (var i = 0; i < count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    if (encoded == null)
                    {
                        // The encoder decides how the samples are stored, such as the colour space of a JPEG image.
                        foreach (var item in ones[i].Where(item => item.Tag.Group == DicomTag.Rows.Group && item.Tag != DicomTag.NumberOfFrames)) result.AddOrUpdate(item);
                        encoded = DicomPixelData.Create(result, true);
                    }
                    encoded.AddFrame(DicomPixelData.Create(ones[i]).GetFrame(0));
                    frameEncoded?.Invoke(start + i + 1, frames.Frames);
                }

                // The frames as drawn and what the encoders used are garbage now, many times the size of what is kept.
                // Left to itself, the collector lets a long loop of large frames pile up over a gigabyte of it.
                drawn = null;
                ones = null;
                if ((long)count * frames.FrameSize >= CollectAfterBatchBytes) GC.Collect();
            }

            var converted = new DicomFile(result);
            foreach (var tag in new[] { DicomTag.SourceApplicationEntityTitle, DicomTag.ImplementationClassUID, DicomTag.ImplementationVersionName })
            {
                if (file.FileMetaInfo.Contains(tag)) converted.FileMetaInfo.AddOrUpdate(file.FileMetaInfo.GetDicomItem<DicomItem>(tag));
            }
            return converted;
        }

        /// <summary>
        /// Every fragment must have even length (PS3.5 A.4), but the HTJ2K encoder can leave one odd. DCMTK warns about those files,
        /// and a strict PACS could reject them. A fragment that grows moves the ones after it, so the Basic Offset Table follows.
        /// </summary>
        private static void PadOddFragments(DicomDataset dataset)
        {
            var sequence = dataset.GetDicomItem<DicomItem>(DicomTag.PixelData) as DicomFragmentSequence;
            if (sequence == null || sequence.Fragments.All(f => f.Size % 2 == 0)) return;

            var movedTo = new Dictionary<uint, uint>();
            uint oldPosition = 0;
            uint newPosition = 0;
            for (var i = 0; i < sequence.Fragments.Count; i++)
            {
                var fragment = sequence.Fragments[i];
                movedTo[oldPosition] = newPosition;
                oldPosition += (uint)fragment.Size + FragmentItemHeaderBytes;

                if (fragment.Size % 2 != 0)
                {
                    // A new array is one byte longer than the encoded stream, and the added byte is zero.
                    var padded = new byte[fragment.Size + 1];
                    fragment.GetByteRange(0, (int)fragment.Size, padded);
                    fragment = new MemoryByteBuffer(padded);
                    sequence.Fragments[i] = fragment;
                }
                newPosition += (uint)fragment.Size + FragmentItemHeaderBytes;
            }

            for (var i = 0; i < sequence.OffsetTable.Count; i++)
            {
                if (movedTo.TryGetValue(sequence.OffsetTable[i], out var moved)) sequence.OffsetTable[i] = moved;
            }
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
