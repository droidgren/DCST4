using System;
using System.Collections.Generic;
using System.Linq;

namespace DCST4.Tests
{
    /// <summary>
    /// Decodes the JPEG XL codestreams DCST4 writes, and nothing else: modular, lossless, no transforms, a global MA tree of
    /// one Gradient leaf and prefix codes. Each field is checked to have the value the encoder writes, so a change to the
    /// encoder shows here. That the format itself is right was checked against libjxl, which the harness cannot depend on.
    /// </summary>
    internal static class JpegXlSubsetDecoder
    {
        private static readonly int[] CodeLengthCodeOrder = { 1, 2, 3, 4, 0, 5, 17, 6, 16, 7, 8, 9, 10, 11, 12, 13, 14, 15 };
        private static readonly int[] PrefixLength = { 2, 2, 2, 3, 2, 2, 2, 4, 2, 2, 2, 3, 2, 2, 2, 4 };
        private static readonly int[] PrefixValue = { 0, 4, 3, 2, 0, 4, 3, 1, 0, 4, 3, 2, 0, 4, 3, 5 };

        public sealed class Image
        {
            public int Width, Height, Bits;
            public bool Grey;
            public int[][] Channels;
        }

        public static Image Decode(byte[] codestream)
        {
            var r = new Reader(codestream, 0, codestream.Length);
            Expect(r.Bits(16) == 0x0AFF, "signature");
            Expect(!r.Bool(), "SizeHeader.small");
            var image = new Image();
            image.Height = (int)Dimension(r);
            Expect(r.Bits(3) == 0, "ratio");
            image.Width = (int)Dimension(r);

            Expect(!r.Bool(), "ImageMetadata.all_default");
            Expect(!r.Bool(), "extra_fields");
            Expect(!r.Bool(), "float_sample");
            image.Bits = (int)r.U32(new[] { 8u, 10u, 12u, 1u }, new[] { 0, 0, 0, 6 });
            Expect(r.Bool() == (image.Bits <= 12), "modular_16_bit_buffer_sufficient");
            Expect(r.U32(new[] { 0u, 1u, 2u, 1u }, new[] { 0, 0, 4, 12 }) == 0, "num_extra_channels");
            Expect(!r.Bool(), "xyb_encoded");
            if (r.Bool())
            {
                image.Grey = false;
            }
            else
            {
                Expect(!r.Bool(), "want_icc");
                Expect(Enum(r) == 1, "colour_space grey");
                Expect(Enum(r) == 1, "white point D65");
                Expect(!r.Bool(), "have_gamma");
                Expect(Enum(r) == 13, "transfer function sRGB");
                Expect(Enum(r) == 1, "rendering intent relative");
                image.Grey = true;
            }
            Expect(r.Bits(2) == 0, "ImageMetadata.extensions");
            Expect(r.Bool(), "CustomTransformData.all_default");
            r.ZeroPadToByte();

            Expect(!r.Bool(), "FrameHeader.all_default");
            Expect(r.Bits(2) == 0, "frame_type");
            Expect(r.Bits(1) == 1, "encoding modular");
            Expect(r.Bits(2) == 0, "flags");
            Expect(!r.Bool(), "do_YCbCr");
            Expect(r.Bits(2) == 0, "upsampling");
            var groupDim = 128 << (int)r.Bits(2);
            Expect(r.Bits(2) == 0, "num_passes");
            Expect(!r.Bool(), "have_crop");
            Expect(r.Bits(2) == 0, "blend mode");
            Expect(r.Bool(), "is_last");
            Expect(r.Bits(2) == 0, "name");
            Expect(!r.Bool(), "restoration_filter.all_default");
            Expect(!r.Bool(), "gab");
            Expect(r.Bits(2) == 0, "epf_iters");
            Expect(r.Bits(2) == 0, "restoration_filter.extensions");
            Expect(r.Bits(2) == 0, "FrameHeader.extensions");

            var groupsX = (image.Width + groupDim - 1) / groupDim;
            var groupsY = (image.Height + groupDim - 1) / groupDim;
            var lfDim = groupDim * 8;
            var lfGroups = ((image.Width + lfDim - 1) / lfDim) * ((image.Height + lfDim - 1) / lfDim);
            var single = groupsX * groupsY == 1;
            var entries = single ? 1 : 2 + lfGroups + groupsX * groupsY;

            Expect(!r.Bool(), "TOC permuted");
            r.ZeroPadToByte();
            var sizes = new int[entries];
            for (var i = 0; i < entries; i++) sizes[i] = (int)r.U32(new[] { 0u, 1024u, 17408u, 4211712u }, new[] { 10, 14, 22, 30 });
            r.ZeroPadToByte();

            var starts = new int[entries];
            var position = r.BytePosition;
            for (var i = 0; i < entries; i++) { starts[i] = position; position += sizes[i]; }
            Expect(position == codestream.Length || position == codestream.Length - 1, "the sections end the codestream");

            var channelCount = image.Grey ? 1 : 3;
            image.Channels = Enumerable.Range(0, channelCount).Select(_ => new int[image.Width * image.Height]).ToArray();

            var g = new Reader(codestream, starts[0], sizes[0]);
            Expect(g.Bool(), "LfChannelDequantization.all_default");
            Expect(g.Bool(), "global tree present");
            var treeCode = ReadEntropyCode(g, 6);
            Expect(treeCode.ReadSymbol(g) == 0, "a leaf");
            Expect(treeCode.ReadSymbol(g) == 5, "Gradient");
            Expect(treeCode.ReadSymbol(g) == 0, "offset 0");
            Expect(treeCode.ReadSymbol(g) == 0, "mul_log 0");
            Expect(treeCode.ReadSymbol(g) == 0, "mul_bits 0");
            var code = ReadEntropyCode(g, 1);
            GroupHeader(g);
            if (single)
            {
                foreach (var plane in image.Channels) DecodeRect(g, code, plane, image.Width, 0, 0, image.Width, image.Height);
                return image;
            }

            for (var i = 1; i < 2 + lfGroups; i++) Expect(sizes[i] == 0, "an empty LF group or HfGlobal");
            for (var gy = 0; gy < groupsY; gy++)
            {
                for (var gx = 0; gx < groupsX; gx++)
                {
                    var index = 2 + lfGroups + gy * groupsX + gx;
                    var p = new Reader(codestream, starts[index], sizes[index]);
                    GroupHeader(p);
                    var x0 = gx * groupDim;
                    var y0 = gy * groupDim;
                    foreach (var plane in image.Channels)
                    {
                        DecodeRect(p, code, plane, image.Width, x0, y0, Math.Min(groupDim, image.Width - x0), Math.Min(groupDim, image.Height - y0));
                    }
                }
            }
            return image;
        }

        private static void GroupHeader(Reader r)
        {
            Expect(r.Bool(), "use_global_tree");
            Expect(r.Bool(), "wp_header.all_default");
            Expect(r.Bits(2) == 0, "nb_transforms");
        }

        private static void DecodeRect(Reader r, EntropyCode code, int[] plane, int stride, int x0, int y0, int width, int height)
        {
            for (var y = 0; y < height; y++)
            {
                var row = (y0 + y) * stride + x0;
                var above = row - stride;
                for (var x = 0; x < width; x++)
                {
                    int west = x > 0 ? plane[row + x - 1] : (y > 0 ? plane[above + x] : 0);
                    int north = y > 0 ? plane[above + x] : west;
                    int northWest = x > 0 && y > 0 ? plane[above + x - 1] : west;
                    var low = Math.Min(north, west);
                    var high = Math.Max(north, west);
                    var gradient = north + west - northWest;
                    var prediction = gradient < low ? low : gradient > high ? high : gradient;

                    var packed = code.ReadValue(r);
                    var residual = (packed & 1) != 0 ? -(int)((packed + 1) / 2) : (int)(packed / 2);
                    plane[row + x] = prediction + residual;
                }
            }
        }

        private static uint Dimension(Reader r) => r.U32(new[] { 1u, 1u, 1u, 1u }, new[] { 9, 13, 18, 30 });

        private static uint Enum(Reader r) => r.U32(new[] { 0u, 1u, 2u, 18u }, new[] { 0, 0, 4, 6 });

        private static EntropyCode ReadEntropyCode(Reader r, int contexts)
        {
            Expect(!r.Bool(), "lz77");
            if (contexts > 1)
            {
                Expect(r.Bool(), "simple context map");
                Expect(r.Bits(2) == 0, "every context in cluster 0");
            }
            Expect(r.Bool(), "use_prefix_code");
            var split = (int)r.Bits(4);
            var msb = (int)r.Bits(3);
            var lsb = (int)r.Bits(3);
            var count = 1;
            if (r.Bool())
            {
                var n = (int)r.Bits(4);
                count = 1 + (1 << n) + (int)r.Bits(n);
            }
            return new EntropyCode(ReadPrefixCode(r, count), split, msb, lsb);
        }

        /// <summary>A prefix code (RFC 7932 3.4, 3.5) as a table from code length and code to symbol.</summary>
        private static Dictionary<long, int> ReadPrefixCode(Reader r, int alphabet)
        {
            var lengths = new int[alphabet];
            var skip = (int)r.Bits(2);
            if (skip == 1)
            {
                var symbols = (int)r.Bits(2) + 1;
                var bits = 0;
                while ((1 << bits) < alphabet) bits++;
                var list = Enumerable.Range(0, symbols).Select(_ => (int)r.Bits(bits)).ToArray();
                Expect(symbols == 1, "a simple code of one symbol");
                return new Dictionary<long, int> { { 0, list[0] } };
            }

            var codeLengthLengths = new int[18];
            var space = 32;
            var codes = 0;
            for (var i = skip; i < 18; i++)
            {
                var peek = (int)r.Peek(4);
                r.Bits(PrefixLength[peek]);
                var v = PrefixValue[peek];
                codeLengthLengths[CodeLengthCodeOrder[i]] = v;
                if (v != 0)
                {
                    space -= 32 >> v;
                    codes++;
                    if (space <= 0) break;
                }
            }
            Expect(codes == 1 || space == 0, "a complete code length code");
            var lengthTable = codes == 1 ? null : Canonical(codeLengthLengths);
            var only = Array.FindIndex(codeLengthLengths, l => l != 0);

            var symbol = 0;
            var remaining = 32768;
            while (symbol < alphabet && remaining > 0)
            {
                var length = lengthTable == null ? only : ReadWith(r, lengthTable);
                Expect(length < 16, "no repeat codes");
                lengths[symbol++] = length;
                if (length != 0) remaining -= 32768 >> length;
            }
            Expect(remaining == 0, "a complete code");
            return Canonical(lengths);
        }

        private static Dictionary<long, int> Canonical(int[] lengths)
        {
            var table = new Dictionary<long, int>();
            var code = 0;
            for (var length = 1; length <= 15; length++)
            {
                for (var symbol = 0; symbol < lengths.Length; symbol++)
                {
                    if (lengths[symbol] != length) continue;
                    table[((long)length << 32) | (uint)code] = symbol;
                    code++;
                }
                code <<= 1;
            }
            return table;
        }

        private static int ReadWith(Reader r, Dictionary<long, int> table)
        {
            if (table.Count == 1 && table.ContainsKey(0)) return table[0];
            var code = 0;
            for (var length = 1; length <= 15; length++)
            {
                code = (code << 1) | (int)r.Bits(1);
                if (table.TryGetValue(((long)length << 32) | (uint)code, out var symbol)) return symbol;
            }
            throw new InvalidOperationException("Not a code of the prefix code.");
        }

        private static void Expect(bool condition, string what)
        {
            if (!condition) throw new FormatException("Unexpected JPEG XL field: " + what);
        }

        private sealed class EntropyCode
        {
            private readonly Dictionary<long, int> _table;
            private readonly int _split, _msb, _lsb;

            public EntropyCode(Dictionary<long, int> table, int split, int msb, int lsb)
            {
                _table = table;
                _split = split;
                _msb = msb;
                _lsb = lsb;
            }

            public int ReadSymbol(Reader r) => (int)ReadValue(r);

            /// <summary>A token and the bits after it (C.2.4).</summary>
            public uint ReadValue(Reader r)
            {
                var token = (uint)ReadWith(r, _table);
                var splitValue = 1u << _split;
                if (token < splitValue) return token;
                var bits = _split - (_msb + _lsb) + (int)((token - splitValue) >> (_msb + _lsb));
                var low = token & ((1u << _lsb) - 1);
                token >>= _lsb;
                token &= (1u << _msb) - 1;
                token |= 1u << _msb;
                return (((token << bits) | (uint)r.Bits(bits)) << _lsb) | low;
            }
        }

        private sealed class Reader
        {
            private readonly byte[] _data;
            private readonly int _start;
            private readonly int _end;
            private long _bit;

            public Reader(byte[] data, int start, int length)
            {
                _data = data;
                _start = start;
                _end = start + length;
                _bit = (long)start * 8;
            }

            public int BytePosition => (int)(_bit / 8);

            public ulong Bits(int count)
            {
                var value = Peek(count);
                _bit += count;
                if (_bit > (long)_end * 8) throw new FormatException("Read past the end of the section.");
                return value;
            }

            public ulong Peek(int count)
            {
                ulong value = 0;
                for (var i = 0; i < count; i++)
                {
                    var bit = _bit + i;
                    var index = (int)(bit / 8);
                    var set = index < _end && ((_data[index] >> (int)(bit % 8)) & 1) != 0;
                    if (set) value |= 1UL << i;
                }
                return value;
            }

            public bool Bool() => Bits(1) == 1;

            public uint U32(uint[] offsets, int[] bits)
            {
                var selector = (int)Bits(2);
                return offsets[selector] + (uint)Bits(bits[selector]);
            }

            public void ZeroPadToByte()
            {
                var rest = (int)((8 - _bit % 8) % 8);
                if (Bits(rest) != 0) throw new FormatException("Padding is not zero.");
            }
        }
    }
}
