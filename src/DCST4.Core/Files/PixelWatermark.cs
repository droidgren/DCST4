using DCST4.Core.Generation;
using FellowOakDicom;
using FellowOakDicom.IO;
using FellowOakDicom.IO.Buffer;
using System;

namespace DCST4.Core.Files
{
    /// <summary>What happened to the TEST IMAGE watermark of an existing file.</summary>
    public enum WatermarkOutcome
    {
        /// <summary>Burned into every frame.</summary>
        Drawn,
        /// <summary>The pixels are compressed. They are sent with their original stream, which is what is being tested, and without the mark.</summary>
        Compressed,
        /// <summary>A pixel layout DCST4 does not draw on, such as PALETTE COLOR, YBR, big endian, or more than 16 bits per sample.</summary>
        Unsupported,
        /// <summary>No Pixel Data: a report, a document or a presentation state.</summary>
        NoPixels,
        /// <summary>Pixel Data larger than <see cref="PixelWatermark.MaxBytes"/>, which would have to sit in memory twice.</summary>
        TooLarge,
    }

    /// <summary>
    /// Burns the TEST IMAGE watermark of a generated image into the native pixels of an existing one. Each frame is blended
    /// towards its own brightest value (darkest for MONOCHROME1), so that the mark shows whatever the window. Bits above
    /// Bits Stored, where an old overlay may live, are left as they are.
    /// </summary>
    public static class PixelWatermark
    {
        public const long MaxBytes = 1024L * 1024 * 1024;

        public static WatermarkOutcome Apply(DicomDataset dataset)
        {
            var pixels = dataset.GetDicomItem<DicomItem>(DicomTag.PixelData);
            if (pixels == null) return WatermarkOutcome.NoPixels;
            if (pixels is DicomFragmentSequence || dataset.InternalTransferSyntax.IsEncapsulated) return WatermarkOutcome.Compressed;
            if (!(pixels is DicomElement element)) return WatermarkOutcome.Unsupported;
            if (dataset.InternalTransferSyntax.Endian != Endian.Little) return WatermarkOutcome.Unsupported;

            var rows = dataset.GetSingleValueOrDefault<ushort>(DicomTag.Rows, 0);
            var columns = dataset.GetSingleValueOrDefault<ushort>(DicomTag.Columns, 0);
            var bitsAllocated = dataset.GetSingleValueOrDefault<ushort>(DicomTag.BitsAllocated, 0);
            var bitsStored = dataset.GetSingleValueOrDefault<ushort>(DicomTag.BitsStored, bitsAllocated);
            var samples = dataset.GetSingleValueOrDefault<ushort>(DicomTag.SamplesPerPixel, 1);
            var planar = dataset.GetSingleValueOrDefault<ushort>(DicomTag.PlanarConfiguration, 0);
            var signed = dataset.GetSingleValueOrDefault<ushort>(DicomTag.PixelRepresentation, 0) == 1;
            var photometric = FolderScanner.Text(dataset, DicomTag.PhotometricInterpretation).ToUpperInvariant();
            var frames = Math.Max(1, FolderScannerInt(dataset, DicomTag.NumberOfFrames));

            var monochrome = photometric == "MONOCHROME1" || photometric == "MONOCHROME2";
            if (rows == 0 || columns == 0) return WatermarkOutcome.Unsupported;
            if (!(monochrome && samples == 1) && !(photometric == "RGB" && samples == 3)) return WatermarkOutcome.Unsupported;
            if (bitsAllocated != 8 && bitsAllocated != 16) return WatermarkOutcome.Unsupported;
            if (bitsStored < 1 || bitsStored > bitsAllocated) return WatermarkOutcome.Unsupported;

            var bytesPerSample = bitsAllocated / 8;
            var frameBytes = (long)rows * columns * samples * bytesPerSample;
            var total = frameBytes * frames;
            if (total > MaxBytes) return WatermarkOutcome.TooLarge;
            if (element.Buffer.Size < total) return WatermarkOutcome.Unsupported;

            var data = new byte[element.Buffer.Size];
            Array.Copy(element.Buffer.Data, data, data.Length);

            var mask = PixelRenderer.WatermarkMask(rows, columns);
            var layout = new Layout
            {
                Pixels = rows * columns,
                Samples = samples,
                Planar = planar == 1,
                BytesPerSample = bytesPerSample,
                BitsStored = bitsStored,
                Signed = signed,
                TowardsMinimum = photometric == "MONOCHROME1",
                Colour = photometric == "RGB",
            };
            for (var frame = 0; frame < frames; frame++) Blend(data, frame * frameBytes, mask, layout);

            var buffer = new MemoryByteBuffer(data);
            if (element is DicomOtherWord) dataset.AddOrUpdate(new DicomOtherWord(DicomTag.PixelData, buffer));
            else dataset.AddOrUpdate(new DicomOtherByte(DicomTag.PixelData, buffer));
            return WatermarkOutcome.Drawn;
        }

        private sealed class Layout
        {
            public int Pixels;
            public int Samples;
            public bool Planar;
            public int BytesPerSample;
            public int BitsStored;
            public bool Signed;
            public bool TowardsMinimum;
            public bool Colour;
        }

        private static void Blend(byte[] data, long frameStart, byte[] mask, Layout layout)
        {
            var storedMask = (1 << layout.BitsStored) - 1;
            int target;
            if (layout.Colour)
            {
                target = Signed(storedMask >> (layout.Signed ? 1 : 0), layout);
            }
            else
            {
                // The frame's own range, so that a CT with values far below the largest storable one still shows the mark.
                var min = int.MaxValue;
                var max = int.MinValue;
                for (var p = 0; p < layout.Pixels; p++)
                {
                    var value = Read(data, Offset(frameStart, p, 0, layout), layout);
                    if (value < min) min = value;
                    if (value > max) max = value;
                }
                target = layout.TowardsMinimum ? min : max;
            }

            for (var p = 0; p < layout.Pixels; p++)
            {
                var alpha = mask[p];
                if (alpha == 0) continue;
                for (var s = 0; s < layout.Samples; s++)
                {
                    var offset = Offset(frameStart, p, s, layout);
                    var value = Read(data, offset, layout);
                    var blended = value + (int)Math.Round((target - value) * (alpha / 255.0));
                    Write(data, offset, blended, layout);
                }
            }
        }

        private static long Offset(long frameStart, int pixel, int sample, Layout layout) =>
            frameStart + (layout.Planar
                ? ((long)sample * layout.Pixels + pixel) * layout.BytesPerSample
                : ((long)pixel * layout.Samples + sample) * layout.BytesPerSample);

        private static int Read(byte[] data, long offset, Layout layout)
        {
            var raw = layout.BytesPerSample == 1 ? data[offset] : data[offset] | (data[offset + 1] << 8);
            return Signed(raw & ((1 << layout.BitsStored) - 1), layout);
        }

        /// <summary>The stored bits as a number, with the sign of the top stored bit when the pixels are signed.</summary>
        private static int Signed(int stored, Layout layout)
        {
            if (!layout.Signed) return stored;
            var signBit = 1 << (layout.BitsStored - 1);
            return (stored & signBit) != 0 ? stored - (1 << layout.BitsStored) : stored;
        }

        private static void Write(byte[] data, long offset, int value, Layout layout)
        {
            var storedMask = (1 << layout.BitsStored) - 1;
            var low = layout.Signed ? -(1 << (layout.BitsStored - 1)) : 0;
            var high = layout.Signed ? (1 << (layout.BitsStored - 1)) - 1 : storedMask;
            var clamped = Math.Max(low, Math.Min(high, value)) & storedMask;
            if (layout.BytesPerSample == 1)
            {
                data[offset] = (byte)((data[offset] & ~storedMask) | clamped);
            }
            else
            {
                var word = data[offset] | (data[offset + 1] << 8);
                word = (word & ~storedMask) | clamped;
                data[offset] = (byte)(word & 0xFF);
                data[offset + 1] = (byte)((word >> 8) & 0xFF);
            }
        }

        private static int FolderScannerInt(DicomDataset dataset, DicomTag tag)
        {
            var text = FolderScanner.Text(dataset, tag).Split('\\')[0].Trim();
            return int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 1;
        }
    }
}
