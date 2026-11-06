using System;
using System.Collections.Generic;

namespace DCST4.Tests
{
    /// <summary>
    /// A minimal JPEG Lossless (Process 14, predictor 1) decoder written from ITU T.81, used only to check generated streams.
    /// The test needs a decoder that shares no code with the encoder, and fo-dicom's built-in one is not usable as the
    /// reference: it was seen returning 0 for the last pixel of a frame that this decoder and PureCodecs both decode correctly.
    /// </summary>
    public sealed class LosslessJpegDecoder
    {
        private readonly byte[] _data;
        private int _position;
        private int _bitBuffer;
        private int _bitCount;

        public int Precision { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Components { get; private set; }
        public int Predictor { get; private set; }
        public int PointTransform { get; private set; }

        public LosslessJpegDecoder(byte[] data)
        {
            _data = data;
        }

        public int[] Decode()
        {
            var tables = new Dictionary<int, Dictionary<long, int>>();
            if (_data[0] != 0xFF || _data[1] != 0xD8) throw new InvalidOperationException("No SOI marker.");
            _position = 2;

            var huffmanId = 0;
            while (true)
            {
                if (_data[_position] != 0xFF) throw new InvalidOperationException($"Expected a marker at {_position}.");
                var marker = _data[_position + 1];
                var length = (_data[_position + 2] << 8) | _data[_position + 3];
                var segment = _position + 4;

                if (marker == 0xC3)
                {
                    Precision = _data[segment];
                    Height = (_data[segment + 1] << 8) | _data[segment + 2];
                    Width = (_data[segment + 3] << 8) | _data[segment + 4];
                    Components = _data[segment + 5];
                }
                else if (marker == 0xC4)
                {
                    var p = segment;
                    while (p < _position + 2 + length)
                    {
                        var id = _data[p] & 0x0F;
                        var table = new Dictionary<long, int>();
                        var code = 0;
                        var valueIndex = p + 17;
                        for (var bits = 1; bits <= 16; bits++)
                        {
                            for (var i = 0; i < _data[p + bits]; i++)
                            {
                                table[Key(bits, code)] = _data[valueIndex++];
                                code++;
                            }
                            code <<= 1;
                        }
                        tables[id] = table;
                        p = valueIndex;
                    }
                }
                else if (marker == 0xDA)
                {
                    var componentsInScan = _data[segment];
                    huffmanId = _data[segment + 2] >> 4;
                    Predictor = _data[segment + 1 + 2 * componentsInScan];
                    PointTransform = _data[segment + 1 + 2 * componentsInScan + 2] & 0x0F;
                    _position += 2 + length;
                    break;
                }

                _position += 2 + length;
            }

            if (Components != 1 || Predictor != 1 || PointTransform != 0)
            {
                throw new InvalidOperationException($"Unexpected stream: {Components} components, predictor {Predictor}, point transform {PointTransform}.");
            }

            var huffman = tables[huffmanId];
            var modulus = 1L << Precision;
            var output = new int[Width * Height];
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    int predicted;
                    if (x == 0 && y == 0) predicted = 1 << (Precision - 1);
                    else if (x == 0) predicted = output[(y - 1) * Width];
                    else predicted = output[y * Width + x - 1];

                    var difference = ReadDifference(huffman);
                    output[y * Width + x] = (int)(((predicted + difference) % modulus + modulus) % modulus);
                }
            }
            return output;
        }

        private static long Key(int bits, int code) => ((long)bits << 32) | (uint)code;

        private int ReadDifference(Dictionary<long, int> huffman)
        {
            var code = 0;
            var category = -1;
            for (var bits = 1; bits <= 16 && category < 0; bits++)
            {
                code = (code << 1) | ReadBit();
                if (huffman.TryGetValue(Key(bits, code), out var value)) category = value;
            }
            if (category < 0) throw new InvalidOperationException("Invalid Huffman code.");

            if (category == 0) return 0;
            if (category == 16) return 32768; // T.81 H.1.2.2: category 16 has no extra bits.

            var extra = 0;
            for (var i = 0; i < category; i++) extra = (extra << 1) | ReadBit();
            return extra >= (1 << (category - 1)) ? extra : extra - (1 << category) + 1;
        }

        private int ReadBit()
        {
            if (_bitCount == 0)
            {
                var value = _data[_position++];
                if (value == 0xFF)
                {
                    if (_data[_position] != 0) throw new InvalidOperationException("Marker inside the entropy-coded data.");
                    _position++;
                }
                _bitBuffer = value;
                _bitCount = 8;
            }
            _bitCount--;
            return (_bitBuffer >> _bitCount) & 1;
        }
    }
}
