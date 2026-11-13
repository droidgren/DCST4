using System;
using System.Collections.Generic;

namespace DCST4.Core.Codecs.JpegXl
{
    /// <summary>
    /// A lossless JPEG XL encoder (ISO/IEC 18181-1) for the samples of one DICOM frame. It writes a bare codestream, one
    /// modular frame without colour transform, and the smallest structure that is valid: a global MA tree with a single
    /// leaf that predicts each sample from its neighbours with the clamped gradient, and one prefix code for the
    /// residuals of the whole image. That compresses less than libjxl, but every decoder reads it and gives back the
    /// samples exactly.
    /// </summary>
    public static class JpegXlEncoder
    {
        /// <summary>group_size_shift 1: groups of 256 x 256.</summary>
        private const int GroupSizeShift = 1;
        private const int GroupDim = 128 << GroupSizeShift;
        /// <summary>An LF group is 8 x 8 groups.</summary>
        private const int LfGroupDim = GroupDim * 8;

        /// <summary>The hybrid uint configuration of every token: split_exponent 4, no bits of the value in the token.</summary>
        private const int SplitExponent = 4;
        private const int Split = 1 << SplitExponent;
        /// <summary>A prefix-coded stream has log_alpha_size 15 (C.2.1).</summary>
        private const int PrefixLogAlphaSize = 15;

        private const int PredictorGradient = 5;
        private const int TreeContexts = 6;
        private const int PropertyContext = 1;
        private const int PredictorContext = 2;
        private const int OffsetContext = 3;
        private const int MultiplierLogContext = 4;
        private const int MultiplierBitsContext = 5;

        private const uint ColourSpaceGrey = 1;
        private const uint WhitePointD65 = 1;
        private const uint TransferFunctionSrgb = 13;
        private const uint RenderingIntentRelative = 1;

        public const int MaxBitsPerSample = 16;

        /// <summary>
        /// Encodes one image. <paramref name="channels"/> holds 1 (grey) or 3 (R, G, B) planes of
        /// <paramref name="width"/> x <paramref name="height"/> samples, row by row, each in 0 to 2^bits - 1.
        /// </summary>
        public static byte[] EncodeLossless(int width, int height, int bitsPerSample, IReadOnlyList<int[]> channels)
        {
            if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width), "The image is empty.");
            if (width > 1 << 18 || height > 1 << 18) throw new ArgumentOutOfRangeException(nameof(width), "The image is larger than JPEG XL level 5 allows.");
            if (bitsPerSample < 1 || bitsPerSample > MaxBitsPerSample) throw new ArgumentOutOfRangeException(nameof(bitsPerSample), $"JPEG XL is written here for 1 to {MaxBitsPerSample} bits per sample.");
            if (channels == null || (channels.Count != 1 && channels.Count != 3)) throw new ArgumentException("An image has 1 or 3 channels.", nameof(channels));

            var max = (1 << bitsPerSample) - 1;
            foreach (var plane in channels)
            {
                if (plane.Length != (long)width * height) throw new ArgumentException("A channel does not have width x height samples.", nameof(channels));
                foreach (var sample in plane) if (sample < 0 || sample > max) throw new ArgumentOutOfRangeException(nameof(channels), $"A sample is outside 0 to {max}.");
            }

            var groups = Groups(width, height);
            var counts = new long[32];
            foreach (var group in groups) foreach (var plane in channels) CountTokens(plane, width, group, counts);
            var alphabet = Math.Max(2, LastUsed(counts) + 1);
            var code = new JxlPrefixCode(Trim(counts, alphabet));

            var writer = new JxlBitWriter();
            WriteHeaders(writer, width, height, bitsPerSample, channels.Count == 1);
            WriteFrame(writer, width, height, channels, groups, code);
            return writer.ToArray();
        }

        private struct Rect
        {
            public int X, Y, Width, Height;
        }

        private static List<Rect> Groups(int width, int height)
        {
            var groups = new List<Rect>();
            for (var y = 0; y < height; y += GroupDim)
            {
                for (var x = 0; x < width; x += GroupDim)
                {
                    groups.Add(new Rect { X = x, Y = y, Width = Math.Min(GroupDim, width - x), Height = Math.Min(GroupDim, height - y) });
                }
            }
            return groups;
        }

        private static void WriteHeaders(JxlBitWriter w, int width, int height, int bits, bool grey)
        {
            // Signature.
            w.Write(8, 0xFF);
            w.Write(8, 0x0A);

            // SizeHeader: not small, height, ratio 0, width.
            w.WriteBool(false);
            WriteDimension(w, (uint)height);
            w.Write(3, 0);
            WriteDimension(w, (uint)width);

            // ImageMetadata.
            w.WriteBool(false);                       // all_default
            w.WriteBool(false);                       // extra_fields
            w.WriteBool(false);                       // bit_depth.float_sample
            w.WriteU32((uint)bits, U32Distribution.Val(8), U32Distribution.Val(10), U32Distribution.Val(12), U32Distribution.BitsOffset(6, 1));
            w.WriteBool(bits <= 12);                  // modular_16_bit_buffer_sufficient
            w.WriteU32(0, U32Distribution.Val(0), U32Distribution.Val(1), U32Distribution.BitsOffset(4, 2), U32Distribution.BitsOffset(12, 1)); // num_extra_channels
            w.WriteBool(false);                       // xyb_encoded: the samples are stored as they are
            if (grey)
            {
                w.WriteBool(false);                   // colour_encoding.all_default
                w.WriteBool(false);                   // want_icc
                w.WriteEnum(ColourSpaceGrey);
                w.WriteEnum(WhitePointD65);
                w.WriteBool(false);                   // have_gamma
                w.WriteEnum(TransferFunctionSrgb);
                w.WriteEnum(RenderingIntentRelative);
            }
            else
            {
                w.WriteBool(true);                    // colour_encoding.all_default: sRGB
            }
            w.WriteU64Zero();                         // extensions

            // CustomTransformData.
            w.WriteBool(true);                        // all_default
            w.ZeroPadToByte();
        }

        private static void WriteDimension(JxlBitWriter w, uint value) =>
            w.WriteU32(value, U32Distribution.BitsOffset(9, 1), U32Distribution.BitsOffset(13, 1), U32Distribution.BitsOffset(18, 1), U32Distribution.BitsOffset(30, 1));

        private static void WriteFrame(JxlBitWriter w, int width, int height, IReadOnlyList<int[]> channels, List<Rect> groups, JxlPrefixCode code)
        {
            // FrameHeader.
            w.WriteBool(false);                       // all_default
            w.Write(2, 0);                            // frame_type: regular
            w.Write(1, 1);                            // encoding: modular
            w.WriteU64Zero();                         // flags
            w.WriteBool(false);                       // do_YCbCr
            w.Write(2, 0);                            // upsampling 1
            w.Write(2, GroupSizeShift);
            w.Write(2, 0);                            // num_passes 1
            w.WriteBool(false);                       // have_crop
            w.Write(2, 0);                            // blending_info.mode: replace
            w.WriteBool(true);                        // is_last
            w.Write(2, 0);                            // name: empty
            w.WriteBool(false);                       // restoration_filter.all_default
            w.WriteBool(false);                       // gab: off, since it would change the samples
            w.Write(2, 0);                            // epf_iters 0
            w.WriteU64Zero();                         // restoration_filter.extensions
            w.WriteU64Zero();                         // extensions

            var sections = new List<byte[]>();
            var single = groups.Count == 1;
            sections.Add(LfGlobal(channels, width, single ? groups[0] : (Rect?)null, code));
            if (!single)
            {
                var lfGroups = ((width + LfGroupDim - 1) / LfGroupDim) * ((height + LfGroupDim - 1) / LfGroupDim);
                // The LF groups and HfGlobal hold nothing in a modular frame whose channels are not downsampled.
                for (var i = 0; i < lfGroups + 1; i++) sections.Add(new byte[0]);
                foreach (var group in groups) sections.Add(PassGroup(channels, width, group, code));
            }

            // TOC: not permuted, then the size of each section.
            w.WriteBool(false);
            w.ZeroPadToByte();
            foreach (var section in sections)
            {
                w.WriteU32((uint)section.Length, U32Distribution.BitsOnly(10), U32Distribution.BitsOffset(14, 1024), U32Distribution.BitsOffset(22, 17408), U32Distribution.BitsOffset(30, 4211712));
            }
            w.ZeroPadToByte();
            foreach (var section in sections) w.WriteBytes(section);
        }

        /// <param name="whole">The image when it is a single group, whose channels are then coded here.</param>
        private static byte[] LfGlobal(IReadOnlyList<int[]> channels, int width, Rect? whole, JxlPrefixCode code)
        {
            var w = new JxlBitWriter();
            w.WriteBool(true);                        // LfChannelDequantization.all_default

            // GlobalModular: the global tree and its code, then the header of the full image.
            w.WriteBool(true);                        // global tree present
            WriteSingleLeafTree(w);
            WriteEntropyCodeSpec(w, 1, code);
            WriteGroupHeader(w);
            if (whole.HasValue) foreach (var plane in channels) WriteResiduals(w, plane, width, whole.Value, code);

            w.ZeroPadToByte();
            return w.ToArray();
        }

        private static byte[] PassGroup(IReadOnlyList<int[]> channels, int width, Rect group, JxlPrefixCode code)
        {
            var w = new JxlBitWriter();
            WriteGroupHeader(w);
            foreach (var plane in channels) WriteResiduals(w, plane, width, group, code);
            w.ZeroPadToByte();
            return w.ToArray();
        }

        /// <summary>GroupHeader: the global tree, default weighted predictor parameters and no transforms.</summary>
        private static void WriteGroupHeader(JxlBitWriter w)
        {
            w.WriteBool(true);                        // use_global_tree
            w.WriteBool(true);                        // wp_header.all_default
            w.Write(2, 0);                            // nb_transforms 0
        }

        /// <summary>
        /// An MA tree of one leaf: Gradient, offset 0, multiplier 1. Its symbols are coded over six contexts, all mapped to
        /// one prefix code.
        /// </summary>
        private static void WriteSingleLeafTree(JxlBitWriter w)
        {
            var symbols = new[]
            {
                Tuple.Create(PropertyContext, 0),               // property -1: a leaf
                Tuple.Create(PredictorContext, PredictorGradient),
                Tuple.Create(OffsetContext, 0),                  // PackSigned(0)
                Tuple.Create(MultiplierLogContext, 0),
                Tuple.Create(MultiplierBitsContext, 0),
            };

            var counts = new long[PredictorGradient + 1];
            foreach (var symbol in symbols) counts[symbol.Item2]++;
            var code = new JxlPrefixCode(counts);

            WriteEntropyCodeSpec(w, TreeContexts, code);
            foreach (var symbol in symbols) code.WriteSymbol(w, symbol.Item2);
        }

        /// <summary>The histograms of an entropy-coded stream (C.2.1): no LZ77, every context in cluster 0, one prefix code.</summary>
        private static void WriteEntropyCodeSpec(JxlBitWriter w, int contexts, JxlPrefixCode code)
        {
            w.WriteBool(false);                       // lz77.enabled
            if (contexts > 1)
            {
                w.WriteBool(true);                    // a simple context map
                w.Write(2, 0);                        // 0 bits per entry: all in cluster 0
            }
            w.WriteBool(true);                        // use_prefix_code

            // HybridUintConfig for log_alpha_size 15.
            w.Write(CeilLog2(PrefixLogAlphaSize + 1), SplitExponent);
            w.Write(CeilLog2(SplitExponent + 1), 0);  // msb_in_token
            w.Write(CeilLog2(SplitExponent + 1), 0);  // lsb_in_token

            // The alphabet size: 1 + 2^n + n more bits.
            var rest = code.AlphabetSize - 1;
            var n = 0;
            while ((2 << n) <= rest) n++;
            w.WriteBool(true);
            w.Write(4, n);
            w.Write(n, rest - (1 << n));

            code.WriteDescription(w);
        }

        private static int CeilLog2(int value)
        {
            var bits = 0;
            while ((1 << bits) < value) bits++;
            return bits;
        }

        private static void CountTokens(int[] plane, int stride, Rect rect, long[] counts)
        {
            ForEachResidual(plane, stride, rect, packed => counts[Token(packed, out _, out _)]++);
        }

        private static void WriteResiduals(JxlBitWriter w, int[] plane, int stride, Rect rect, JxlPrefixCode code)
        {
            ForEachResidual(plane, stride, rect, packed =>
            {
                var token = Token(packed, out var extraBits, out var extra);
                code.WriteSymbol(w, token);
                w.Write(extraBits, (ulong)extra);
            });
        }

        /// <summary>
        /// The residual of each sample of the rectangle after the clamped gradient predictor, in decoding order. The
        /// neighbours are those inside the rectangle, as the decoder sees a group (H.5).
        /// </summary>
        private static void ForEachResidual(int[] plane, int stride, Rect rect, Action<uint> residual)
        {
            for (var y = 0; y < rect.Height; y++)
            {
                var row = (rect.Y + y) * stride + rect.X;
                var above = row - stride;
                for (var x = 0; x < rect.Width; x++)
                {
                    int west = x > 0 ? plane[row + x - 1] : (y > 0 ? plane[above + x] : 0);
                    int north = y > 0 ? plane[above + x] : west;
                    int northWest = x > 0 && y > 0 ? plane[above + x - 1] : west;

                    var low = Math.Min(north, west);
                    var high = Math.Max(north, west);
                    var gradient = north + west - northWest;
                    var prediction = gradient < low ? low : gradient > high ? high : gradient;

                    var difference = plane[row + x] - prediction;
                    residual(difference >= 0 ? (uint)difference * 2 : (uint)(-difference) * 2 - 1);
                }
            }
        }

        /// <summary>The hybrid uint token of a value (C.2.4), with the bits that follow it.</summary>
        private static int Token(uint value, out int extraBits, out uint extra)
        {
            if (value < Split)
            {
                extraBits = 0;
                extra = 0;
                return (int)value;
            }
            var n = 31;
            while ((value >> n) == 0) n--;
            extraBits = n;
            extra = value - (1u << n);
            return Split + n - SplitExponent;
        }

        private static int LastUsed(long[] counts)
        {
            for (var i = counts.Length - 1; i >= 0; i--) if (counts[i] > 0) return i;
            return 0;
        }

        private static long[] Trim(long[] counts, int length)
        {
            var trimmed = new long[length];
            Array.Copy(counts, trimmed, Math.Min(length, counts.Length));
            return trimmed;
        }
    }
}
