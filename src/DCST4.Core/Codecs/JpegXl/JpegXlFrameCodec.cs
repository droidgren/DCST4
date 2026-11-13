using FellowOakDicom;
using FellowOakDicom.IO.Buffer;
using System;
using System.Linq;

namespace DCST4.Core.Codecs.JpegXl
{
    /// <summary>One native frame of a DICOM image as a JPEG XL codestream, in the shape the frame-by-frame encoder expects.</summary>
    public static class JpegXlFrameCodec
    {
        /// <summary>
        /// <paramref name="single"/> describes the pixels (group 0028) and holds one native frame. The result has the same
        /// description and the codestream as its only fragment. Samples are stored as their Bits Stored bits, so a signed
        /// value keeps its two's complement pattern, as the JPEG-LS and JPEG Lossless codecs keep it.
        /// </summary>
        public static DicomDataset Encode(DicomDataset single, DicomTransferSyntax target)
        {
            var rows = single.GetSingleValue<ushort>(DicomTag.Rows);
            var columns = single.GetSingleValue<ushort>(DicomTag.Columns);
            var samples = single.GetSingleValueOrDefault<ushort>(DicomTag.SamplesPerPixel, 1);
            var bitsAllocated = single.GetSingleValue<ushort>(DicomTag.BitsAllocated);
            var bitsStored = single.GetSingleValueOrDefault(DicomTag.BitsStored, bitsAllocated);
            var planar = single.GetSingleValueOrDefault<ushort>(DicomTag.PlanarConfiguration, 0);

            if (bitsAllocated != 8 && bitsAllocated != 16) throw new NotSupportedException($"JPEG XL is written here for 8 or 16 bits allocated, not {bitsAllocated}.");
            if (samples != 1 && samples != 3) throw new NotSupportedException($"JPEG XL is written here for 1 or 3 samples per pixel, not {samples}.");
            if (bitsStored < 1 || bitsStored > Math.Min(bitsAllocated, (ushort)JpegXlEncoder.MaxBitsPerSample)) throw new NotSupportedException($"{bitsStored} bits stored cannot be JPEG XL here.");

            var data = single.GetDicomItem<DicomElement>(DicomTag.PixelData).Buffer.Data;
            var pixels = rows * columns;
            var bytesPerSample = bitsAllocated / 8;
            if (data.Length < (long)pixels * samples * bytesPerSample) throw new ArgumentException("The frame is shorter than its rows, columns and samples.");

            var mask = (1 << bitsStored) - 1;
            var planes = new int[samples][];
            for (var channel = 0; channel < samples; channel++)
            {
                var plane = new int[pixels];
                for (var pixel = 0; pixel < pixels; pixel++)
                {
                    var index = planar == 0 ? pixel * samples + channel : channel * pixels + pixel;
                    var value = bytesPerSample == 1 ? data[index] : data[2 * index] | (data[2 * index + 1] << 8);
                    plane[pixel] = value & mask;
                }
                planes[channel] = plane;
            }

            var codestream = JpegXlEncoder.EncodeLossless(columns, rows, bitsStored, planes);
            // Every fragment has even length (PS3.5 A.4). A decoder stops at the end of the last frame, so the pad is not read.
            if (codestream.Length % 2 != 0) Array.Resize(ref codestream, codestream.Length + 1);

            var result = new DicomDataset(target);
            foreach (var item in single.Where(item => item.Tag.Group == DicomTag.Rows.Group && item.Tag != DicomTag.PixelData && !(item is DicomSequence))) result.Add(item);
            // The decoder gives the samples interleaved.
            if (samples == 3) result.AddOrUpdate(DicomTag.PlanarConfiguration, (ushort)0);

            var fragments = new DicomOtherByteFragment(DicomTag.PixelData);
            fragments.Fragments.Add(new MemoryByteBuffer(codestream));
            result.Add(fragments);
            return result;
        }
    }
}
