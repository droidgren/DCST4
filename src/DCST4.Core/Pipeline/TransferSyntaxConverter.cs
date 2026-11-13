using DCST4.Core.Codecs;
using DCST4.Core.Codecs.JpegXl;
using DCST4.Core.Codecs.Video;
using DCST4.Core.Generation;
using DCST4.Core.Generation.Modalities;
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

            // DCST4's own encoders read native samples, so an object that is already compressed is decoded first.
            var ownEncoder = OwnCodecs.IsH264(syntax) || OwnCodecs.IsJpegXl(syntax);
            if (ownEncoder && file.Dataset.InternalTransferSyntax.IsEncapsulated) file = file.Clone(DicomTransferSyntax.ExplicitVRLittleEndian);
            if (OwnCodecs.IsH264(syntax)) return EncodeVideo(file, syntax, token, frameEncoded);

            var uncompressedSize = syntax.IsLossy ? PixelBytes(file.Dataset) : 0;
            var rendered = syntax.IsEncapsulated ? (file.Dataset.GetDicomItem<DicomElement>(DicomTag.PixelData)?.Buffer as RenderedFramesBuffer) : null;
            DicomFile converted;
            if (OwnCodecs.IsJpegXl(syntax))
            {
                var frames = FramesOf(file.Dataset, rendered);
                converted = EncodeFrameByFrame(file, frames.Item1, frames.Item2, syntax, token, frameEncoded, JpegXlFrameCodec.Encode);
            }
            else if (rendered != null)
            {
                converted = EncodeFrameByFrame(file, rendered.Frames, rendered.ReadFrame, syntax, token, frameEncoded,
                    (single, target) => new DicomTranscoder(single.InternalTransferSyntax, target).Transcode(single));
            }
            else
            {
                converted = file.Clone(syntax);
            }
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
        private static DicomFile EncodeFrameByFrame(DicomFile file, int frameCount, Func<int, byte[]> readFrame, DicomTransferSyntax syntax,
            CancellationToken token, Action<int, int> frameEncoded, Func<DicomDataset, DicomTransferSyntax, DicomDataset> encodeOne)
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
            for (var start = 0; start < frameCount; start += batch)
            {
                token.ThrowIfCancellationRequested();

                var count = Math.Min(batch, frameCount - start);
                var drawn = new byte[count][];
                long drawnBytes = 0;
                for (var i = 0; i < count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    drawn[i] = readFrame(start + i);
                    drawnBytes += drawn[i].Length;
                }

                var ones = new DicomDataset[count];
                Parallel.For(0, count, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = batch }, i =>
                {
                    var single = description.Clone();
                    single.AddOrUpdate(DicomTag.NumberOfFrames, 1);
                    var bytes = new MemoryByteBuffer(drawn[i]);
                    if (inWords) single.AddOrUpdate(new DicomOtherWord(DicomTag.PixelData, bytes));
                    else single.AddOrUpdate(new DicomOtherByte(DicomTag.PixelData, bytes));
                    ones[i] = encodeOne(single, syntax);
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
                    frameEncoded?.Invoke(start + i + 1, frameCount);
                }

                // The frames as drawn and what the encoders used are garbage now, many times the size of what is kept.
                // Left to itself, the collector lets a long loop of large frames pile up over a gigabyte of it.
                drawn = null;
                ones = null;
                if (drawnBytes >= CollectAfterBatchBytes) GC.Collect();
            }

            return WithFileMeta(file, result);
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
            dataset.AddOrUpdate(DicomTag.LossyImageCompressionMethod, OwnCodecs.IsH264(syntax) ? OwnCodecs.H264CompressionMethod : syntax.LossyCompressionMethod);
            dataset.AddOrUpdate(DicomTag.LossyImageCompressionRatio, ratio.ToString(RatioFormat, CultureInfo.InvariantCulture));

            if (dataset.TryGetValues<string>(DicomTag.ImageType, out var imageType) && imageType.Length > 0 && imageType[0] != ImageTypeDerived)
            {
                dataset.AddOrUpdate(DicomTag.ImageType, new[] { ImageTypeDerived }.Concat(imageType.Skip(1)).ToArray());
            }
        }

        /// <summary>
        /// The bytes of Pixel Data: the frames of native data, or the fragments of encapsulated data. A video stream is one
        /// fragment for all frames, which fo-dicom cannot split into frames.
        /// </summary>
        public static long PixelBytes(DicomDataset dataset)
        {
            if (!dataset.Contains(DicomTag.PixelData)) return 0;
            if (dataset.GetDicomItem<DicomItem>(DicomTag.PixelData) is DicomFragmentSequence fragments) return fragments.Fragments.Sum(f => f.Size);

            var pixelData = DicomPixelData.Create(dataset);
            long size = 0;
            for (var frame = 0; frame < pixelData.NumberOfFrames; frame++) size += pixelData.GetFrame(frame).Size;
            return size;
        }

        /// <summary>The number of frames and how to read each, as native bytes.</summary>
        private static Tuple<int, Func<int, byte[]>> FramesOf(DicomDataset dataset, RenderedFramesBuffer rendered)
        {
            if (rendered != null) return Tuple.Create<int, Func<int, byte[]>>(rendered.Frames, rendered.ReadFrame);
            var pixelData = DicomPixelData.Create(dataset);
            return Tuple.Create<int, Func<int, byte[]>>(pixelData.NumberOfFrames, frame => pixelData.GetFrame(frame).Data);
        }

        /// <summary>
        /// The whole loop as one H.264 stream in an MP4 container, in one fragment (PS3.5 8.2.7, Non-Fragmentable). The
        /// Basic Offset Table is empty, since the frames are not separate in the stream.
        /// </summary>
        private static DicomFile EncodeVideo(DicomFile file, DicomTransferSyntax syntax, CancellationToken token, Action<int, int> frameEncoded)
        {
            var source = file.Dataset;
            var rows = source.GetSingleValue<ushort>(DicomTag.Rows);
            var columns = source.GetSingleValue<ushort>(DicomTag.Columns);
            var samples = source.GetSingleValueOrDefault<ushort>(DicomTag.SamplesPerPixel, 1);
            var bitsAllocated = source.GetSingleValueOrDefault<ushort>(DicomTag.BitsAllocated, 0);
            var photometric = source.GetSingleValueOrDefault(DicomTag.PhotometricInterpretation, string.Empty);
            var planar = source.GetSingleValueOrDefault<ushort>(DicomTag.PlanarConfiguration, 0);
            if (samples != 3 || bitsAllocated != 8 || photometric != "RGB")
            {
                throw new NotSupportedException($"{syntax.UID.Name} is made from 8-bit RGB frames. This object is {photometric} with {samples} samples of {bitsAllocated} bits.");
            }

            var fps = FramesPerSecond(source);
            var rendered = source.GetDicomItem<DicomElement>(DicomTag.PixelData)?.Buffer as RenderedFramesBuffer;
            var frames = FramesOf(source, rendered);
            Func<int, byte[]> rgb = frame => planar == 0 ? frames.Item2(frame) : Interleave(frames.Item2(frame), rows * columns);
            var uncompressed = (long)rows * columns * 3 * frames.Item1;

            var mp4 = H264Encoder.EncodeMp4(columns, rows, frames.Item1, fps, rgb, token, frameEncoded);
            if (mp4.Length % 2 != 0) Array.Resize(ref mp4, mp4.Length + 1);

            var result = new DicomDataset(syntax);
            foreach (var item in source.Where(item => item.Tag != DicomTag.PixelData)) result.Add(item);
            result.AddOrUpdate(DicomTag.PhotometricInterpretation, "YBR_PARTIAL_420");
            result.AddOrUpdate(DicomTag.SamplesPerPixel, (ushort)3);
            result.AddOrUpdate(DicomTag.PlanarConfiguration, (ushort)0);
            result.AddOrUpdate(DicomTag.BitsAllocated, (ushort)8);
            result.AddOrUpdate(DicomTag.BitsStored, (ushort)8);
            result.AddOrUpdate(DicomTag.HighBit, (ushort)7);
            result.AddOrUpdate(DicomTag.PixelRepresentation, (ushort)0);
            result.AddOrUpdate(DicomTag.NumberOfFrames, frames.Item1);
            // The stream is the timing. Pixel Aspect Ratio is absent for square pixels (PS3.5 8.2.7).
            result.AddOrUpdate(DicomTag.FrameTime, ModalityProfile.Ds(1000.0 / fps));
            result.Remove(DicomTag.PixelAspectRatio);

            var fragments = new DicomOtherByteFragment(DicomTag.PixelData);
            fragments.Fragments.Add(new MemoryByteBuffer(mp4));
            result.AddOrUpdate(fragments);
            MarkLossy(result, syntax, uncompressed);
            return WithFileMeta(file, result);
        }

        /// <summary>Cine Rate, or else what Frame Time works out to.</summary>
        private static double FramesPerSecond(DicomDataset dataset)
        {
            var cineRate = dataset.GetSingleValueOrDefault(DicomTag.CineRate, 0);
            if (cineRate > 0) return cineRate;
            var frameTime = dataset.GetSingleValueOrDefault(DicomTag.FrameTime, 0.0);
            return frameTime > 0 ? 1000.0 / frameTime : 0;
        }

        private static byte[] Interleave(byte[] planar, int pixels)
        {
            var interleaved = new byte[pixels * 3];
            for (var i = 0; i < pixels; i++)
            {
                interleaved[i * 3] = planar[i];
                interleaved[i * 3 + 1] = planar[pixels + i];
                interleaved[i * 3 + 2] = planar[2 * pixels + i];
            }
            return interleaved;
        }

        private static DicomFile WithFileMeta(DicomFile file, DicomDataset dataset)
        {
            var converted = new DicomFile(dataset);
            foreach (var tag in new[] { DicomTag.SourceApplicationEntityTitle, DicomTag.ImplementationClassUID, DicomTag.ImplementationVersionName })
            {
                if (file.FileMetaInfo.Contains(tag)) converted.FileMetaInfo.AddOrUpdate(file.FileMetaInfo.GetDicomItem<DicomItem>(tag));
            }
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
