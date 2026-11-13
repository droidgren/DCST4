using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace DCST4.Core.Codecs.Video
{
    /// <summary>
    /// H.264 High Profile / Level 4.1 in an MP4 container (PS3.5 8.2.7), written by the H.264 encoder that comes with
    /// Windows, through Media Foundation. The frames go in as 8-bit RGB and are stored as BT.601 limited range 4:2:0,
    /// which is YBR_PARTIAL_420.
    /// </summary>
    public static class H264Encoder
    {
        /// <summary>The largest picture the transfer syntax is meant for: 1080 rows of 1920.</summary>
        public const int MaxColumns = 1920;
        public const int MaxRows = 1080;

        /// <summary>Level 4.1 (ITU-T H.264 Table A-1): macroblocks per frame and per second.</summary>
        private const int MaxFrameMacroblocks = 8192;
        private const int MaxMacroblocksPerSecond = 245760;
        /// <summary>Level 4.1 allows 62.5 Mbit/s for High Profile. The test images need far less.</summary>
        private const int MaxBitrate = 50000000;
        private const int MinBitrate = 1000000;
        /// <summary>About what an ultrasound system's own encoder spends on a cine loop.</summary>
        private const double BitsPerPixel = 0.2;
        private const long HundredNanosecondsPerSecond = 10000000;
        private const int FrameRateDenominator = 1000;

        /// <summary>Why a picture of this size and rate cannot be H.264 High Profile / Level 4.1. Null if it can.</summary>
        public static string LimitProblem(int columns, int rows, double framesPerSecond)
        {
            if (columns % 2 != 0 || rows % 2 != 0) return $"{rows} x {columns} cannot be H.264: 4:2:0 needs an even number of rows and columns.";
            if (columns > MaxColumns || rows > MaxRows) return $"{rows} x {columns} is larger than H.264 High Profile / Level 4.1 holds, which is {MaxRows} rows of {MaxColumns}.";
            if (framesPerSecond <= 0) return "H.264 needs a frame rate, from Cine Rate or Frame Time.";

            var macroblocks = ((columns + 15) / 16) * ((rows + 15) / 16);
            if (macroblocks > MaxFrameMacroblocks) return $"{rows} x {columns} is more than H.264 Level 4.1 allows in a frame.";
            if (macroblocks * framesPerSecond > MaxMacroblocksPerSecond)
            {
                var most = Math.Floor(MaxMacroblocksPerSecond / (double)macroblocks);
                return string.Format(CultureInfo.InvariantCulture, "{0} x {1} at {2:0.##} frames per second is more than H.264 Level 4.1 allows, which is {3} frames per second at this size.", rows, columns, framesPerSecond, most);
            }
            return null;
        }

        /// <summary>
        /// Encodes the frames, which <paramref name="rgbFrame"/> gives one at a time as 8-bit RGB, interleaved, row by row.
        /// Returns the MP4 file. <paramref name="frameEncoded"/> is told the frames done and the total.
        /// </summary>
        public static byte[] EncodeMp4(int columns, int rows, int frames, double framesPerSecond, Func<int, byte[]> rgbFrame,
            CancellationToken token = default(CancellationToken), Action<int, int> frameEncoded = null)
        {
            var problem = LimitProblem(columns, rows, framesPerSecond);
            if (problem != null) throw new ArgumentException(problem);
            if (frames < 1) throw new ArgumentOutOfRangeException(nameof(frames));

            var rateNumerator = (int)Math.Round(framesPerSecond * FrameRateDenominator);
            var bitrate = (int)Math.Max(MinBitrate, Math.Min(MaxBitrate, (double)columns * rows * framesPerSecond * BitsPerPixel));
            var path = TempFile();

            MediaFoundation.Check(MediaFoundation.MFStartup(MediaFoundation.Version, MediaFoundation.StartupLite), "start");
            IMFSinkWriter writer = null;
            try
            {
                writer = CreateWriter(path, columns, rows, rateNumerator, bitrate, out var stream);
                writer.BeginWriting();

                var nv12 = new byte[columns * rows * 3 / 2];
                for (var frame = 0; frame < frames; frame++)
                {
                    token.ThrowIfCancellationRequested();
                    var rgb = rgbFrame(frame);
                    if (rgb == null || rgb.Length < columns * rows * 3) throw new ArgumentException($"Frame {frame} is not {rows} x {columns} RGB.");
                    RgbToNv12(rgb, columns, rows, nv12);
                    WriteFrame(writer, stream, nv12, Time(frame, rateNumerator), Time(frame + 1, rateNumerator) - Time(frame, rateNumerator));
                    frameEncoded?.Invoke(frame + 1, frames);
                }

                token.ThrowIfCancellationRequested();
                writer.FinalizeWriting();
                MediaFoundation.Release(writer);
                writer = null;
                return File.ReadAllBytes(path);
            }
            finally
            {
                MediaFoundation.Release(writer);
                MediaFoundation.MFShutdown();
                TryDelete(path);
            }
        }

        /// <summary>
        /// Decodes an MP4 with Windows' own H.264 decoder, and returns each frame as NV12: a plane of Y and a plane of
        /// interleaved Cb and Cr at half the size. For checking what was encoded.
        /// </summary>
        public static IReadOnlyList<byte[]> DecodeToNv12(byte[] mp4, out int columns, out int rows)
        {
            var path = TempFile();
            File.WriteAllBytes(path, mp4);
            MediaFoundation.Check(MediaFoundation.MFStartup(MediaFoundation.Version, MediaFoundation.StartupLite), "start");
            IMFSourceReader reader = null;
            IMFMediaType type = null;
            try
            {
                MediaFoundation.Check(MediaFoundation.MFCreateSourceReaderFromURL(path, null, out reader), "open the MP4");
                MediaFoundation.Check(MediaFoundation.MFCreateMediaType(out type), "create a media type");
                Set(type, MediaFoundation.MajorType, MediaFoundation.MediaTypeVideo);
                Set(type, MediaFoundation.Subtype, MediaFoundation.VideoFormatNv12);
                reader.SetCurrentMediaType(MediaFoundation.SourceReaderFirstVideoStream, IntPtr.Zero, type);
                MediaFoundation.Release(type);
                type = null;

                reader.GetCurrentMediaType(MediaFoundation.SourceReaderFirstVideoStream, out type);
                var key = MediaFoundation.FrameSize;
                type.GetUINT64(ref key, out var size);
                columns = (int)((ulong)size >> 32);
                rows = (int)((ulong)size & 0xFFFFFFFF);

                var frames = new List<byte[]>();
                while (true)
                {
                    reader.ReadSample(MediaFoundation.SourceReaderFirstVideoStream, 0, out _, out var flags, out _, out var sample);
                    if (sample != null)
                    {
                        try { frames.Add(Contents(sample)); }
                        finally { MediaFoundation.Release(sample); }
                    }
                    if ((flags & MediaFoundation.SourceReaderEndOfStream) != 0) break;
                }
                return frames;
            }
            finally
            {
                MediaFoundation.Release(type);
                MediaFoundation.Release(reader);
                MediaFoundation.MFShutdown();
                TryDelete(path);
            }
        }

        /// <summary>BT.601 limited range (PS3.3 C.7.6.3.1.2 YBR_PARTIAL_420), each Cb and Cr from the average of four pixels.</summary>
        public static void RgbToNv12(byte[] rgb, int columns, int rows, byte[] nv12)
        {
            for (var y = 0; y < rows; y++)
            {
                var source = y * columns * 3;
                var target = y * columns;
                for (var x = 0; x < columns; x++, source += 3)
                {
                    nv12[target + x] = (byte)(((66 * rgb[source] + 129 * rgb[source + 1] + 25 * rgb[source + 2] + 128) >> 8) + 16);
                }
            }

            var chroma = columns * rows;
            for (var y = 0; y < rows; y += 2)
            {
                for (var x = 0; x < columns; x += 2)
                {
                    int r = 0, g = 0, b = 0;
                    for (var dy = 0; dy < 2; dy++)
                    {
                        for (var dx = 0; dx < 2; dx++)
                        {
                            var i = ((y + dy) * columns + x + dx) * 3;
                            r += rgb[i];
                            g += rgb[i + 1];
                            b += rgb[i + 2];
                        }
                    }
                    var target = chroma + (y / 2) * columns + x;
                    nv12[target] = (byte)(((-38 * r - 74 * g + 112 * b + 512) >> 10) + 128);
                    nv12[target + 1] = (byte)(((112 * r - 94 * g - 18 * b + 512) >> 10) + 128);
                }
            }
        }

        private static IMFSinkWriter CreateWriter(string path, int columns, int rows, int rateNumerator, int bitrate, out int stream)
        {
            IMFAttributes attributes = null;
            IMFMediaType output = null;
            IMFMediaType input = null;
            IMFSinkWriter writer = null;
            try
            {
                MediaFoundation.Check(MediaFoundation.MFCreateAttributes(out attributes, 3), "create attributes");
                Set(attributes, MediaFoundation.TranscodeContainerType, MediaFoundation.ContainerTypeMpeg4);
                Set(attributes, MediaFoundation.SinkWriterDisableThrottling, 1);
                // The software encoder, so that the same frames give the same stream on every computer with this Windows.
                Set(attributes, MediaFoundation.EnableHardwareTransforms, 0);
                MediaFoundation.Check(MediaFoundation.MFCreateSinkWriterFromURL(path, IntPtr.Zero, attributes, out writer), "create the MP4 writer");

                MediaFoundation.Check(MediaFoundation.MFCreateMediaType(out output), "create a media type");
                Set(output, MediaFoundation.MajorType, MediaFoundation.MediaTypeVideo);
                Set(output, MediaFoundation.Subtype, MediaFoundation.VideoFormatH264);
                Set(output, MediaFoundation.AverageBitrate, bitrate);
                Set(output, MediaFoundation.Mpeg2Profile, MediaFoundation.H264ProfileHigh);
                Set(output, MediaFoundation.Mpeg2Level, MediaFoundation.H264Level41);
                Set(output, MediaFoundation.NominalRange, MediaFoundation.NominalRange16To235);
                Set(output, MediaFoundation.YuvMatrix, MediaFoundation.TransferMatrixBt601);
                SetPicture(output, columns, rows, rateNumerator);
                writer.AddStream(output, out stream);

                MediaFoundation.Check(MediaFoundation.MFCreateMediaType(out input), "create a media type");
                Set(input, MediaFoundation.MajorType, MediaFoundation.MediaTypeVideo);
                Set(input, MediaFoundation.Subtype, MediaFoundation.VideoFormatNv12);
                Set(input, MediaFoundation.DefaultStride, columns);
                Set(input, MediaFoundation.NominalRange, MediaFoundation.NominalRange16To235);
                Set(input, MediaFoundation.YuvMatrix, MediaFoundation.TransferMatrixBt601);
                SetPicture(input, columns, rows, rateNumerator);
                writer.SetInputMediaType(stream, input, null);

                var created = writer;
                writer = null;
                return created;
            }
            catch (COMException ex)
            {
                throw new NotSupportedException("The H.264 encoder of Windows could not be used. Windows N needs the Media Feature Pack for it. " + ex.Message, ex);
            }
            finally
            {
                MediaFoundation.Release(writer);
                MediaFoundation.Release(input);
                MediaFoundation.Release(output);
                MediaFoundation.Release(attributes);
            }
        }

        private static void SetPicture(IMFMediaType type, int columns, int rows, int rateNumerator)
        {
            Set(type, MediaFoundation.InterlaceMode, MediaFoundation.InterlaceProgressive);
            Set(type, MediaFoundation.FrameSize, MediaFoundation.Pack(columns, rows));
            Set(type, MediaFoundation.FrameRate, MediaFoundation.Pack(rateNumerator, FrameRateDenominator));
            // Square pixels (PS3.5 8.2.7).
            Set(type, MediaFoundation.PixelAspectRatio, MediaFoundation.Pack(1, 1));
        }

        private static void WriteFrame(IMFSinkWriter writer, int stream, byte[] nv12, long time, long duration)
        {
            IMFMediaBuffer buffer = null;
            IMFSample sample = null;
            try
            {
                MediaFoundation.Check(MediaFoundation.MFCreateMemoryBuffer(nv12.Length, out buffer), "create a buffer");
                buffer.Lock(out var pointer, out _, out _);
                try { Marshal.Copy(nv12, 0, pointer, nv12.Length); }
                finally { buffer.Unlock(); }
                buffer.SetCurrentLength(nv12.Length);

                MediaFoundation.Check(MediaFoundation.MFCreateSample(out sample), "create a sample");
                sample.AddBuffer(buffer);
                sample.SetSampleTime(time);
                sample.SetSampleDuration(duration);
                writer.WriteSample(stream, sample);
            }
            finally
            {
                MediaFoundation.Release(sample);
                MediaFoundation.Release(buffer);
            }
        }

        private static byte[] Contents(IMFSample sample)
        {
            sample.ConvertToContiguousBuffer(out var buffer);
            try
            {
                buffer.Lock(out var pointer, out _, out var length);
                try
                {
                    var data = new byte[length];
                    Marshal.Copy(pointer, data, 0, length);
                    return data;
                }
                finally { buffer.Unlock(); }
            }
            finally { MediaFoundation.Release(buffer); }
        }

        /// <summary>In units of 100 ns, exact for the rational frame rate.</summary>
        private static long Time(int frame, int rateNumerator) => frame * HundredNanosecondsPerSecond * FrameRateDenominator / rateNumerator;

        private static void Set(IMFAttributes attributes, Guid key, Guid value) => attributes.SetGUID(ref key, ref value);
        private static void Set(IMFAttributes attributes, Guid key, int value) => attributes.SetUINT32(ref key, value);
        private static void Set(IMFMediaType type, Guid key, Guid value) => type.SetGUID(ref key, ref value);
        private static void Set(IMFMediaType type, Guid key, int value) => type.SetUINT32(ref key, value);
        private static void Set(IMFMediaType type, Guid key, ulong value) => type.SetUINT64(ref key, (long)value);

        private static string TempFile() => Path.Combine(Path.GetTempPath(), "DCST4-" + Guid.NewGuid().ToString("N") + ".mp4");

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
