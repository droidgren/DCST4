using FellowOakDicom.IO.Buffer;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Core.Generation
{
    /// <summary>
    /// Bytes that are worked out when they are read, for a value too large to keep in memory. They look random, so that
    /// nothing on the way can compress them away, and they follow from the seed and the position alone, so that any part
    /// can be read again and comes out the same.
    /// </summary>
    internal sealed class GeneratedBytesBuffer : IByteBuffer
    {
        private const int WordBytes = 8;
        private const int ChunkBytes = 64 * 1024;

        private readonly ulong _seed;

        public GeneratedBytesBuffer(long size, ulong seed)
        {
            if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
            Size = size;
            _seed = seed;
        }

        public bool IsMemory => false;

        public long Size { get; }

        public byte[] Data
        {
            get
            {
                var data = new byte[checked((int)Size)];
                Fill(0, data, data.Length);
                return data;
            }
        }

        public void GetByteRange(long offset, int count, byte[] output)
        {
            if (offset < 0 || offset + count > Size) throw new ArgumentOutOfRangeException(nameof(offset));
            Fill(offset, output, count);
        }

        public void CopyToStream(Stream stream)
        {
            var chunk = new byte[ChunkBytes];
            for (long position = 0; position < Size; position += ChunkBytes)
            {
                var count = (int)Math.Min(ChunkBytes, Size - position);
                Fill(position, chunk, count);
                stream.Write(chunk, 0, count);
            }
        }

        public async Task CopyToStreamAsync(Stream stream, CancellationToken cancellationToken)
        {
            var chunk = new byte[ChunkBytes];
            for (long position = 0; position < Size; position += ChunkBytes)
            {
                var count = (int)Math.Min(ChunkBytes, Size - position);
                Fill(position, chunk, count);
                await stream.WriteAsync(chunk, 0, count, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>Byte n is byte n modulo 8 of the word numbered n / 8.</summary>
        private void Fill(long offset, byte[] output, int count)
        {
            var written = 0;
            while (written < count)
            {
                var position = offset + written;
                var word = Word((ulong)(position / WordBytes));
                for (var inWord = (int)(position % WordBytes); inWord < WordBytes && written < count; inWord++)
                {
                    output[written++] = (byte)(word >> (inWord * 8));
                }
            }
        }

        /// <summary>SplitMix64 of the word's number, which gives each word a value of its own without any state.</summary>
        private ulong Word(ulong index)
        {
            unchecked
            {
                var z = _seed + (index + 1) * 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }
    }
}
