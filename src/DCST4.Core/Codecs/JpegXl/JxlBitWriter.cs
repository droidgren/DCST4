using System;
using System.IO;

namespace DCST4.Core.Codecs.JpegXl
{
    /// <summary>
    /// Writes bits the way a JPEG XL decoder reads them: least significant bit first, each value with its lowest bit first
    /// (ISO/IEC 18181-1 B.2).
    /// </summary>
    internal sealed class JxlBitWriter
    {
        private readonly MemoryStream _bytes = new MemoryStream();
        private ulong _pending;
        private int _pendingBits;

        /// <summary>Bits written so far, including those not yet a whole byte.</summary>
        public long BitCount => _bytes.Length * 8 + _pendingBits;

        public void Write(int bits, ulong value)
        {
            if (bits < 0 || bits > 56) throw new ArgumentOutOfRangeException(nameof(bits));
            if (bits == 0) return;
            if (bits < 64 && value >> bits != 0) throw new ArgumentOutOfRangeException(nameof(value), $"{value} does not fit in {bits} bits.");

            _pending |= value << _pendingBits;
            _pendingBits += bits;
            while (_pendingBits >= 8)
            {
                _bytes.WriteByte((byte)_pending);
                _pending >>= 8;
                _pendingBits -= 8;
            }
        }

        public void Write(int bits, int value) => Write(bits, (ulong)value);

        public void WriteBool(bool value) => Write(1, value ? 1UL : 0UL);

        /// <summary>ZeroPadToByte (B.2.3).</summary>
        public void ZeroPadToByte()
        {
            if (_pendingBits > 0) Write(8 - _pendingBits, 0UL);
        }

        /// <summary>
        /// U32 (B.2.2): a 2-bit selector and then the bits of the chosen distribution. Each distribution is a constant when
        /// its bit count is 0, otherwise an offset plus that many bits.
        /// </summary>
        public void WriteU32(uint value, U32Distribution d0, U32Distribution d1, U32Distribution d2, U32Distribution d3)
        {
            var distributions = new[] { d0, d1, d2, d3 };
            for (var selector = 0; selector < 4; selector++)
            {
                var d = distributions[selector];
                if (value < d.Offset) continue;
                var rest = value - d.Offset;
                if (d.Bits == 0 ? rest != 0 : (d.Bits < 32 && rest >> d.Bits != 0)) continue;
                Write(2, selector);
                Write(d.Bits, rest);
                return;
            }
            throw new ArgumentOutOfRangeException(nameof(value), $"{value} has no U32 encoding with these distributions.");
        }

        /// <summary>U64 (B.2.4), here only ever zero.</summary>
        public void WriteU64Zero() => Write(2, 0);

        /// <summary>Enum (B.2.6), which is U32(Val(0), Val(1), BitsOffset(4, 2), BitsOffset(6, 18)).</summary>
        public void WriteEnum(uint value) => WriteU32(value, U32Distribution.Val(0), U32Distribution.Val(1), U32Distribution.BitsOffset(4, 2), U32Distribution.BitsOffset(6, 18));

        /// <summary>Appends whole bytes. The writer must be at a byte boundary.</summary>
        public void WriteBytes(byte[] data)
        {
            if (_pendingBits != 0) throw new InvalidOperationException("Not at a byte boundary.");
            _bytes.Write(data, 0, data.Length);
        }

        public byte[] ToArray()
        {
            if (_pendingBits != 0) throw new InvalidOperationException("Not at a byte boundary.");
            return _bytes.ToArray();
        }
    }

    internal struct U32Distribution
    {
        public uint Offset;
        public int Bits;

        public static U32Distribution Val(uint value) => new U32Distribution { Offset = value, Bits = 0 };
        public static U32Distribution BitsOffset(int bits, uint offset) => new U32Distribution { Offset = offset, Bits = bits };
        public static U32Distribution BitsOnly(int bits) => new U32Distribution { Offset = 0, Bits = bits };
    }
}
