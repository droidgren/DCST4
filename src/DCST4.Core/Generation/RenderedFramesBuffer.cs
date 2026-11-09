using FellowOakDicom.IO.Buffer;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DCST4.Core.Generation
{
    /// <summary>
    /// Native Pixel Data whose frames are drawn when they are read, for objects too large to keep in memory.
    /// Only the most recently read frame is held. A frame is drawn again if it is read again, and the renderer draws it the same way each time.
    /// </summary>
    internal sealed class RenderedFramesBuffer : IByteBuffer
    {
        private readonly int _frames;
        private readonly int _frameSize;
        private readonly Func<int, byte[]> _render;
        private readonly object _gate = new object();

        private int _cachedFrame = -1;
        private byte[] _cached;

        public RenderedFramesBuffer(int frames, int frameSize, Func<int, byte[]> render)
        {
            _frames = frames;
            _frameSize = frameSize;
            _render = render;
        }

        public bool IsMemory => false;

        public long Size => (long)_frames * _frameSize;

        public byte[] Data
        {
            get
            {
                var data = new byte[checked((int)Size)];
                GetByteRange(0, data.Length, data);
                return data;
            }
        }

        public void GetByteRange(long offset, int count, byte[] output)
        {
            if (offset < 0 || offset + count > Size) throw new ArgumentOutOfRangeException(nameof(offset));

            var written = 0;
            while (written < count)
            {
                var position = offset + written;
                var frame = (int)(position / _frameSize);
                var inFrame = (int)(position % _frameSize);
                var length = Math.Min(count - written, _frameSize - inFrame);

                // Copied under the lock, so that another reader cannot replace the frame halfway through.
                lock (_gate)
                {
                    Buffer.BlockCopy(Frame(frame), inFrame, output, written, length);
                }
                written += length;
            }
        }

        public void CopyToStream(Stream stream)
        {
            for (var frame = 0; frame < _frames; frame++)
            {
                lock (_gate)
                {
                    stream.Write(Frame(frame), 0, _frameSize);
                }
            }
        }

        public async Task CopyToStreamAsync(Stream stream, CancellationToken cancellationToken)
        {
            for (var frame = 0; frame < _frames; frame++)
            {
                byte[] data;
                lock (_gate)
                {
                    data = Frame(frame);
                }
                await stream.WriteAsync(data, 0, _frameSize, cancellationToken).ConfigureAwait(false);
            }
        }

        private byte[] Frame(int index)
        {
            if (index != _cachedFrame)
            {
                _cached = _render(index);
                _cachedFrame = index;
            }
            return _cached;
        }
    }
}
