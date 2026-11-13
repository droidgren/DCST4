using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DCST4.Core.Codecs.Video
{
    // The parts of Media Foundation (mfplat, mfreadwrite) that the H.264 encoder uses. A COM interface declared here lists
    // every method of the interfaces it derives from, in vtable order, since .NET does not inherit COM vtables.

    internal static class MediaFoundation
    {
        public const int Version = 0x00020070;
        public const int StartupLite = 1;
        public const int SourceReaderFirstVideoStream = unchecked((int)0xFFFFFFFC);
        public const int SourceReaderEndOfStream = 0x2;
        public const int InterlaceProgressive = 2;
        public const int NominalRange16To235 = 2;
        public const int TransferMatrixBt601 = 2;
        public const int H264ProfileHigh = 100;
        public const int H264Level41 = 41;

        public static readonly Guid MajorType = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        public static readonly Guid Subtype = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        public static readonly Guid MediaTypeVideo = new Guid("73646976-0000-0010-8000-00AA00389B71");
        public static readonly Guid VideoFormatH264 = new Guid("34363248-0000-0010-8000-00AA00389B71");
        public static readonly Guid VideoFormatNv12 = new Guid("3231564E-0000-0010-8000-00AA00389B71");
        public static readonly Guid AverageBitrate = new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        public static readonly Guid InterlaceMode = new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        public static readonly Guid FrameSize = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        public static readonly Guid FrameRate = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        public static readonly Guid PixelAspectRatio = new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        public static readonly Guid Mpeg2Profile = new Guid("ad76a80b-2d5c-4e0b-b375-64e520137036");
        public static readonly Guid Mpeg2Level = new Guid("96f66574-11c5-4015-8666-bff516436da7");
        public static readonly Guid DefaultStride = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        public static readonly Guid NominalRange = new Guid("c21b8ee5-b956-4071-8daf-325edf5cab11");
        public static readonly Guid YuvMatrix = new Guid("3e23d450-2c75-4d25-a00e-b91670d12327");
        public static readonly Guid TranscodeContainerType = new Guid("150FF23F-4ABC-478B-AC4F-E1916FBA1CCA");
        public static readonly Guid ContainerTypeMpeg4 = new Guid("DC6CD05D-B9D0-40EF-BD35-FA622C1AB28A");
        public static readonly Guid SinkWriterDisableThrottling = new Guid("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");
        public static readonly Guid EnableHardwareTransforms = new Guid("a634a91c-822b-41b9-a494-4de4643612b0");

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFStartup(int version, int flags);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFShutdown();

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateMediaType(out IMFMediaType mediaType);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateAttributes(out IMFAttributes attributes, int initialSize);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateSample(out IMFSample sample);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);

        [DllImport("mfreadwrite.dll", ExactSpelling = true)]
        public static extern int MFCreateSinkWriterFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, IntPtr byteStream, IMFAttributes attributes, out IMFSinkWriter sinkWriter);

        [DllImport("mfreadwrite.dll", ExactSpelling = true)]
        public static extern int MFCreateSourceReaderFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, IMFAttributes attributes, out IMFSourceReader sourceReader);

        public static void Check(int hresult, string what)
        {
            if (hresult < 0) throw new COMException($"Media Foundation could not {what} (0x{hresult:X8}).", hresult);
        }

        public static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

        public static void Release(object comObject)
        {
            if (comObject != null && Marshal.IsComObject(comObject)) Marshal.ReleaseComObject(comObject);
        }
    }

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFAttributes
    {
        void GetItem([In] ref Guid key, IntPtr value);
        void GetItemType([In] ref Guid key, out int type);
        void CompareItem([In] ref Guid key, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool result);
        void Compare(IMFAttributes theirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool result);
        void GetUINT32([In] ref Guid key, out int value);
        void GetUINT64([In] ref Guid key, out long value);
        void GetDouble([In] ref Guid key, out double value);
        void GetGUID([In] ref Guid key, out Guid value);
        void GetStringLength([In] ref Guid key, out int length);
        void GetString([In] ref Guid key, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int size, out int length);
        void GetAllocatedString([In] ref Guid key, out IntPtr value, out int length);
        void GetBlobSize([In] ref Guid key, out int size);
        void GetBlob([In] ref Guid key, [Out] byte[] buffer, int size, out int written);
        void GetAllocatedBlob([In] ref Guid key, out IntPtr buffer, out int size);
        void GetUnknown([In] ref Guid key, [In] ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object value);
        void SetItem([In] ref Guid key, IntPtr value);
        void DeleteItem([In] ref Guid key);
        void DeleteAllItems();
        void SetUINT32([In] ref Guid key, int value);
        void SetUINT64([In] ref Guid key, long value);
        void SetDouble([In] ref Guid key, double value);
        void SetGUID([In] ref Guid key, [In] ref Guid value);
        void SetString([In] ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        void SetBlob([In] ref Guid key, [In] byte[] buffer, int size);
        void SetUnknown([In] ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        void LockStore();
        void UnlockStore();
        void GetCount(out int count);
        void GetItemByIndex(int index, out Guid key, IntPtr value);
        void CopyAllItems(IMFAttributes destination);
    }

    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaType
    {
        void GetItem([In] ref Guid key, IntPtr value);
        void GetItemType([In] ref Guid key, out int type);
        void CompareItem([In] ref Guid key, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool result);
        void Compare(IMFAttributes theirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool result);
        void GetUINT32([In] ref Guid key, out int value);
        void GetUINT64([In] ref Guid key, out long value);
        void GetDouble([In] ref Guid key, out double value);
        void GetGUID([In] ref Guid key, out Guid value);
        void GetStringLength([In] ref Guid key, out int length);
        void GetString([In] ref Guid key, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int size, out int length);
        void GetAllocatedString([In] ref Guid key, out IntPtr value, out int length);
        void GetBlobSize([In] ref Guid key, out int size);
        void GetBlob([In] ref Guid key, [Out] byte[] buffer, int size, out int written);
        void GetAllocatedBlob([In] ref Guid key, out IntPtr buffer, out int size);
        void GetUnknown([In] ref Guid key, [In] ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object value);
        void SetItem([In] ref Guid key, IntPtr value);
        void DeleteItem([In] ref Guid key);
        void DeleteAllItems();
        void SetUINT32([In] ref Guid key, int value);
        void SetUINT64([In] ref Guid key, long value);
        void SetDouble([In] ref Guid key, double value);
        void SetGUID([In] ref Guid key, [In] ref Guid value);
        void SetString([In] ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        void SetBlob([In] ref Guid key, [In] byte[] buffer, int size);
        void SetUnknown([In] ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        void LockStore();
        void UnlockStore();
        void GetCount(out int count);
        void GetItemByIndex(int index, out Guid key, IntPtr value);
        void CopyAllItems(IMFAttributes destination);

        void GetMajorType(out Guid majorType);
        void IsCompressedFormat([MarshalAs(UnmanagedType.Bool)] out bool compressed);
        [PreserveSig] int IsEqual(IMFMediaType other, out int flags);
        void GetRepresentation(Guid representation, out IntPtr value);
        void FreeRepresentation(Guid representation, IntPtr value);
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSample
    {
        void GetItem([In] ref Guid key, IntPtr value);
        void GetItemType([In] ref Guid key, out int type);
        void CompareItem([In] ref Guid key, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool result);
        void Compare(IMFAttributes theirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool result);
        void GetUINT32([In] ref Guid key, out int value);
        void GetUINT64([In] ref Guid key, out long value);
        void GetDouble([In] ref Guid key, out double value);
        void GetGUID([In] ref Guid key, out Guid value);
        void GetStringLength([In] ref Guid key, out int length);
        void GetString([In] ref Guid key, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int size, out int length);
        void GetAllocatedString([In] ref Guid key, out IntPtr value, out int length);
        void GetBlobSize([In] ref Guid key, out int size);
        void GetBlob([In] ref Guid key, [Out] byte[] buffer, int size, out int written);
        void GetAllocatedBlob([In] ref Guid key, out IntPtr buffer, out int size);
        void GetUnknown([In] ref Guid key, [In] ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object value);
        void SetItem([In] ref Guid key, IntPtr value);
        void DeleteItem([In] ref Guid key);
        void DeleteAllItems();
        void SetUINT32([In] ref Guid key, int value);
        void SetUINT64([In] ref Guid key, long value);
        void SetDouble([In] ref Guid key, double value);
        void SetGUID([In] ref Guid key, [In] ref Guid value);
        void SetString([In] ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        void SetBlob([In] ref Guid key, [In] byte[] buffer, int size);
        void SetUnknown([In] ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        void LockStore();
        void UnlockStore();
        void GetCount(out int count);
        void GetItemByIndex(int index, out Guid key, IntPtr value);
        void CopyAllItems(IMFAttributes destination);

        void GetSampleFlags(out int flags);
        void SetSampleFlags(int flags);
        void GetSampleTime(out long time);
        void SetSampleTime(long time);
        void GetSampleDuration(out long duration);
        void SetSampleDuration(long duration);
        void GetBufferCount(out int count);
        void GetBufferByIndex(int index, out IMFMediaBuffer buffer);
        void ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        void AddBuffer(IMFMediaBuffer buffer);
        void RemoveBufferByIndex(int index);
        void RemoveAllBuffers();
        void GetTotalLength(out int length);
        void CopyToBuffer(IMFMediaBuffer buffer);
    }

    [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaBuffer
    {
        void Lock(out IntPtr buffer, out int maxLength, out int currentLength);
        void Unlock();
        void GetCurrentLength(out int length);
        void SetCurrentLength(int length);
        void GetMaxLength(out int length);
    }

    [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSinkWriter
    {
        void AddStream(IMFMediaType targetMediaType, out int streamIndex);
        void SetInputMediaType(int streamIndex, IMFMediaType inputMediaType, IMFAttributes encodingParameters);
        void BeginWriting();
        void WriteSample(int streamIndex, IMFSample sample);
        void SendStreamTick(int streamIndex, long timestamp);
        void PlaceMarker(int streamIndex, IntPtr context);
        void NotifyEndOfSegment(int streamIndex);
        void Flush(int streamIndex);
        /// <summary>IMFSinkWriter::Finalize, which C# keeps for destructors.</summary>
        void FinalizeWriting();
        void GetServiceForStream(int streamIndex, [In] ref Guid service, [In] ref Guid iid, out IntPtr value);
        void GetStatistics(int streamIndex, IntPtr statistics);
    }

    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSourceReader
    {
        void GetStreamSelection(int streamIndex, [MarshalAs(UnmanagedType.Bool)] out bool selected);
        void SetStreamSelection(int streamIndex, [MarshalAs(UnmanagedType.Bool)] bool selected);
        void GetNativeMediaType(int streamIndex, int mediaTypeIndex, out IMFMediaType mediaType);
        void GetCurrentMediaType(int streamIndex, out IMFMediaType mediaType);
        void SetCurrentMediaType(int streamIndex, IntPtr reserved, IMFMediaType mediaType);
        void SetCurrentPosition([In] ref Guid timeFormat, IntPtr position);
        void ReadSample(int streamIndex, int controlFlags, out int actualStreamIndex, out int streamFlags, out long timestamp, out IMFSample sample);
        void Flush(int streamIndex);
        void GetServiceForStream(int streamIndex, [In] ref Guid service, [In] ref Guid iid, out IntPtr value);
        void GetPresentationAttribute(int streamIndex, [In] ref Guid attribute, IntPtr value);
    }
}
