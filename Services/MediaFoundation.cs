using System;
using System.Runtime.InteropServices;
using System.Text;

namespace AdvancedSnip.Services
{
    /// <summary>
    /// Just enough Media Foundation to write an H.264 MP4.
    ///
    /// Why this rather than a library: the same reasoning that produced the hand-written
    /// ICO container and the hand-written PDF writer. Media Foundation ships with Windows,
    /// its H.264 encoder is hardware-accelerated on any machine with Intel Quick Sync,
    /// NVENC or AMF, and the SinkWriter will insert the colour converter and the MP4
    /// muxer by itself. That is the whole recording pipeline for the price of some interop
    /// and no NuGet package at all.
    ///
    /// COM interfaces are laid out by vtable position, not by name, so every method of
    /// every interface has to be declared in the exact order it appears in the IDL —
    /// including the ones that are never called. That is why IMFMediaType and IMFSample
    /// repeat all thirty IMFAttributes methods rather than inheriting them: flat
    /// declarations leave no room for the runtime and me to disagree about layout.
    /// Everything is [PreserveSig] so failures come back as HRESULTs to be checked rather
    /// than as exceptions thrown from inside a capture loop.
    /// </summary>
    internal static class MF
    {
        // ── attribute keys ────────────────────────────────────────────────────

        internal static Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        internal static Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        internal static Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        internal static Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
        internal static Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        internal static Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        internal static Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        internal static Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        internal static Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");

        internal static Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
        internal static Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
        internal static Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00aa00389b71");

        // ── audio ─────────────────────────────────────────────────────────────
        //
        // AAC in the same MP4, written through the same sink writer as a second stream.
        // The encoder ships with Windows and is fussy in exactly two ways, both handled in
        // VideoRecorder: it accepts 16-bit PCM only, at 44100 or 48000 Hz, in one or two
        // channels; and its bitrate must be one of four exact byte-per-second values.

        internal static Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00aa00389b71");
        internal static Guid MFAudioFormat_AAC = new("00001610-0000-0010-8000-00aa00389b71");
        internal static Guid MFAudioFormat_PCM = new("00000001-0000-0010-8000-00aa00389b71");
        internal static Guid MFAudioFormat_Float = new("00000003-0000-0010-8000-00aa00389b71");

        internal static Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        internal static Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        internal static Guid MF_MT_AUDIO_BLOCK_ALIGNMENT = new("322de230-9eeb-43bd-ab7a-ff412251541d");
        internal static Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
        internal static Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        internal static Guid MF_MT_AAC_PAYLOAD_TYPE = new("bfbabe79-7434-4d1c-94f0-72a3b9e17188");
        internal static Guid MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION =
            new("7632f0e6-9538-4d61-acda-ea29c8c14456");

        /// <summary>AAC-LC, Level 2 — stereo, up to 48 kHz. What every player handles.</summary>
        internal const uint AAC_PROFILE_LC_L2 = 0x29;

        internal static Guid MF_SINK_WRITER_DISABLE_THROTTLING =
            new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");
        internal static Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS =
            new("a634a91c-822b-41b9-a494-4de4643612b0");

        internal const uint MFVideoInterlace_Progressive = 2;

        /// <summary>H.264 High profile. Better compression than Main at the same quality.</summary>
        internal const uint eAVEncH264VProfile_High = 100;

        /// <summary>(MF_SDK_VERSION &lt;&lt; 16) | MF_API_VERSION.</summary>
        internal const int MF_VERSION = 0x00020070;

        internal const int MFSTARTUP_NOSOCKET = 1;

        // ── functions ─────────────────────────────────────────────────────────

        [DllImport("mfplat.dll", ExactSpelling = true)]
        internal static extern int MFStartup(int version, int flags);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        internal static extern int MFShutdown();

        [DllImport("mfplat.dll", ExactSpelling = true)]
        internal static extern int MFCreateAttributes(out IMFAttributes ppMFAttributes, int cInitialSize);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        internal static extern int MFCreateMediaType(out IMFMediaType ppMFType);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        internal static extern int MFCreateMemoryBuffer(int cbMaxLength, out IMFMediaBuffer ppBuffer);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        internal static extern int MFCreateSample(out IMFSample ppIMFSample);

        /// <summary>
        /// Stride-aware block copy. Used instead of Buffer.MemoryCopy so this file needs
        /// no unsafe context, and because it is what Media Foundation itself uses to move
        /// image rows — it understands that source and destination strides differ.
        /// </summary>
        [DllImport("mfplat.dll", ExactSpelling = true)]
        internal static extern int MFCopyImage(IntPtr pDest, int lDestStride,
                                               IntPtr pSrc, int lSrcStride,
                                               int dwWidthInBytes, int dwLines);

        [DllImport("mfreadwrite.dll", ExactSpelling = true)]
        internal static extern int MFCreateSinkWriterFromURL(
            [MarshalAs(UnmanagedType.LPWStr)] string pwszOutputURL,
            IntPtr pByteStream,
            IMFAttributes? pAttributes,
            out IMFSinkWriter ppSinkWriter);

        [DllImport("mfreadwrite.dll", ExactSpelling = true)]
        internal static extern int MFCreateSourceReaderFromURL(
            [MarshalAs(UnmanagedType.LPWStr)] string pwszURL,
            IMFAttributes? pAttributes,
            out IMFSourceReader ppSourceReader);

        // ── source reader constants ───────────────────────────────────────────

        /// <summary>MF_SOURCE_READER_FIRST_VIDEO_STREAM.</summary>
        internal const int MF_SOURCE_READER_FIRST_VIDEO_STREAM = unchecked((int)0xFFFFFFFC);

        /// <summary>MF_SOURCE_READER_FIRST_AUDIO_STREAM — used when re-encoding a trim.</summary>
        internal const int MF_SOURCE_READER_FIRST_AUDIO_STREAM = unchecked((int)0xFFFFFFFD);

        /// <summary>MF_SOURCE_READER_ALL_STREAMS, for enabling/disabling in bulk.</summary>
        internal const int MF_SOURCE_READER_ALL_STREAMS = unchecked((int)0xFFFFFFFE);

        /// <summary>
        /// Lets the reader insert a colour converter so it can hand back RGB32 whatever the
        /// file actually holds. Without it, asking a H.264 file for RGB32 fails.
        /// </summary>
        internal static Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING =
            new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");

        internal static Guid MF_PD_DURATION = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");

        internal const int MF_SOURCE_READERF_ENDOFSTREAM = 0x00000002;

        internal const int MF_SOURCE_READER_MEDIASOURCE = unchecked((int)0xFFFFFFFF);

        /// <summary>
        /// Packs two 32-bit values into the single UINT64 that frame size, frame rate and
        /// pixel aspect ratio all use — high word first.
        /// </summary>
        internal static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;

        internal static bool Ok(int hr) => hr >= 0;

        internal static void Release(object? com)
        {
            try { if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com); }
            catch { }
        }
    }

    /// <summary>
    /// IMFAttributes. The IID here is easy to get wrong: IID_IMFMediaEvent begins
    /// "2cd0bd52" and IID_IMFAttributes begins "2cd2d921", and IMFMediaEvent derives
    /// from IMFAttributes, so the two sit next to each other in the documentation.
    /// The wrong one makes MFCreateAttributes fail its QueryInterface at the very
    /// first call, which surfaces as an InvalidCastException, not an HRESULT.
    /// </summary>
    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFAttributes
    {
        [PreserveSig] int GetItem([In] ref Guid key, [In, Out] IntPtr pValue);
        [PreserveSig] int GetItemType([In] ref Guid key, out int pType);
        [PreserveSig] int CompareItem([In] ref Guid key, [In] IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
        [PreserveSig] int Compare([In] IMFAttributes pTheirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
        [PreserveSig] int GetUINT32([In] ref Guid key, out uint punValue);
        [PreserveSig] int GetUINT64([In] ref Guid key, out ulong punValue);
        [PreserveSig] int GetDouble([In] ref Guid key, out double pfValue);
        [PreserveSig] int GetGUID([In] ref Guid key, out Guid pguidValue);
        [PreserveSig] int GetStringLength([In] ref Guid key, out uint pcchLength);
        [PreserveSig] int GetString([In] ref Guid key, [Out] StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
        [PreserveSig] int GetAllocatedString([In] ref Guid key, out IntPtr ppwszValue, out uint pcchLength);
        [PreserveSig] int GetBlobSize([In] ref Guid key, out uint pcbBlobSize);
        [PreserveSig] int GetBlob([In] ref Guid key, [Out] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
        [PreserveSig] int GetAllocatedBlob([In] ref Guid key, out IntPtr ip, out uint pcbSize);
        [PreserveSig] int GetUnknown([In] ref Guid key, [In] ref Guid riid, out IntPtr ppv);
        [PreserveSig] int SetItem([In] ref Guid key, [In] IntPtr value);
        [PreserveSig] int DeleteItem([In] ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32([In] ref Guid key, uint unValue);
        [PreserveSig] int SetUINT64([In] ref Guid key, ulong unValue);
        [PreserveSig] int SetDouble([In] ref Guid key, double fValue);
        [PreserveSig] int SetGUID([In] ref Guid key, [In] ref Guid guidValue);
        [PreserveSig] int SetString([In] ref Guid key, [In, MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        [PreserveSig] int SetBlob([In] ref Guid key, [In] byte[] pBuf, uint cbBufSize);
        [PreserveSig] int SetUnknown([In] ref Guid key, [In, MarshalAs(UnmanagedType.IUnknown)] object? pUnknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint pcItems);
        [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, [In, Out] IntPtr pValue);
        [PreserveSig] int CopyAllItems([In] IMFAttributes pDest);
    }

    /// <summary>
    /// IMFMediaType. The first thirty entries are IMFAttributes and must stay in this
    /// order — the vtable does not know about C# inheritance.
    /// </summary>
    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaType
    {
        [PreserveSig] int GetItem([In] ref Guid key, [In, Out] IntPtr pValue);
        [PreserveSig] int GetItemType([In] ref Guid key, out int pType);
        [PreserveSig] int CompareItem([In] ref Guid key, [In] IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
        [PreserveSig] int Compare([In] IMFAttributes pTheirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
        [PreserveSig] int GetUINT32([In] ref Guid key, out uint punValue);
        [PreserveSig] int GetUINT64([In] ref Guid key, out ulong punValue);
        [PreserveSig] int GetDouble([In] ref Guid key, out double pfValue);
        [PreserveSig] int GetGUID([In] ref Guid key, out Guid pguidValue);
        [PreserveSig] int GetStringLength([In] ref Guid key, out uint pcchLength);
        [PreserveSig] int GetString([In] ref Guid key, [Out] StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
        [PreserveSig] int GetAllocatedString([In] ref Guid key, out IntPtr ppwszValue, out uint pcchLength);
        [PreserveSig] int GetBlobSize([In] ref Guid key, out uint pcbBlobSize);
        [PreserveSig] int GetBlob([In] ref Guid key, [Out] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
        [PreserveSig] int GetAllocatedBlob([In] ref Guid key, out IntPtr ip, out uint pcbSize);
        [PreserveSig] int GetUnknown([In] ref Guid key, [In] ref Guid riid, out IntPtr ppv);
        [PreserveSig] int SetItem([In] ref Guid key, [In] IntPtr value);
        [PreserveSig] int DeleteItem([In] ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32([In] ref Guid key, uint unValue);
        [PreserveSig] int SetUINT64([In] ref Guid key, ulong unValue);
        [PreserveSig] int SetDouble([In] ref Guid key, double fValue);
        [PreserveSig] int SetGUID([In] ref Guid key, [In] ref Guid guidValue);
        [PreserveSig] int SetString([In] ref Guid key, [In, MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        [PreserveSig] int SetBlob([In] ref Guid key, [In] byte[] pBuf, uint cbBufSize);
        [PreserveSig] int SetUnknown([In] ref Guid key, [In, MarshalAs(UnmanagedType.IUnknown)] object? pUnknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint pcItems);
        [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, [In, Out] IntPtr pValue);
        [PreserveSig] int CopyAllItems([In] IMFAttributes pDest);

        [PreserveSig] int GetMajorType(out Guid pguidMajorType);
        [PreserveSig] int IsCompressedFormat([MarshalAs(UnmanagedType.Bool)] out bool pfCompressed);
        [PreserveSig] int IsEqual([In] IMFMediaType pIMediaType, out uint pdwFlags);
        [PreserveSig] int GetRepresentation([In] Guid guidRepresentation, out IntPtr ppvRepresentation);
        [PreserveSig] int FreeRepresentation([In] Guid guidRepresentation, [In] IntPtr pvRepresentation);
    }

    [ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr ppbBuffer, out int pcbMaxLength, out int pcbCurrentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out int pcbCurrentLength);
        [PreserveSig] int SetCurrentLength(int cbCurrentLength);
        [PreserveSig] int GetMaxLength(out int pcbMaxLength);
    }

    /// <summary>IMFSample. Again the first thirty slots are IMFAttributes.</summary>
    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSample
    {
        [PreserveSig] int GetItem([In] ref Guid key, [In, Out] IntPtr pValue);
        [PreserveSig] int GetItemType([In] ref Guid key, out int pType);
        [PreserveSig] int CompareItem([In] ref Guid key, [In] IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
        [PreserveSig] int Compare([In] IMFAttributes pTheirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
        [PreserveSig] int GetUINT32([In] ref Guid key, out uint punValue);
        [PreserveSig] int GetUINT64([In] ref Guid key, out ulong punValue);
        [PreserveSig] int GetDouble([In] ref Guid key, out double pfValue);
        [PreserveSig] int GetGUID([In] ref Guid key, out Guid pguidValue);
        [PreserveSig] int GetStringLength([In] ref Guid key, out uint pcchLength);
        [PreserveSig] int GetString([In] ref Guid key, [Out] StringBuilder pwszValue, uint cchBufSize, out uint pcchLength);
        [PreserveSig] int GetAllocatedString([In] ref Guid key, out IntPtr ppwszValue, out uint pcchLength);
        [PreserveSig] int GetBlobSize([In] ref Guid key, out uint pcbBlobSize);
        [PreserveSig] int GetBlob([In] ref Guid key, [Out] byte[] pBuf, uint cbBufSize, out uint pcbBlobSize);
        [PreserveSig] int GetAllocatedBlob([In] ref Guid key, out IntPtr ip, out uint pcbSize);
        [PreserveSig] int GetUnknown([In] ref Guid key, [In] ref Guid riid, out IntPtr ppv);
        [PreserveSig] int SetItem([In] ref Guid key, [In] IntPtr value);
        [PreserveSig] int DeleteItem([In] ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32([In] ref Guid key, uint unValue);
        [PreserveSig] int SetUINT64([In] ref Guid key, ulong unValue);
        [PreserveSig] int SetDouble([In] ref Guid key, double fValue);
        [PreserveSig] int SetGUID([In] ref Guid key, [In] ref Guid guidValue);
        [PreserveSig] int SetString([In] ref Guid key, [In, MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        [PreserveSig] int SetBlob([In] ref Guid key, [In] byte[] pBuf, uint cbBufSize);
        [PreserveSig] int SetUnknown([In] ref Guid key, [In, MarshalAs(UnmanagedType.IUnknown)] object? pUnknown);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint pcItems);
        [PreserveSig] int GetItemByIndex(uint unIndex, out Guid pguidKey, [In, Out] IntPtr pValue);
        [PreserveSig] int CopyAllItems([In] IMFAttributes pDest);

        [PreserveSig] int GetSampleFlags(out uint pdwSampleFlags);
        [PreserveSig] int SetSampleFlags(uint dwSampleFlags);
        [PreserveSig] int GetSampleTime(out long phnsSampleTime);
        [PreserveSig] int SetSampleTime(long hnsSampleTime);
        [PreserveSig] int GetSampleDuration(out long phnsSampleDuration);
        [PreserveSig] int SetSampleDuration(long hnsSampleDuration);
        [PreserveSig] int GetBufferCount(out uint pdwBufferCount);
        [PreserveSig] int GetBufferByIndex(uint dwIndex, out IMFMediaBuffer ppBuffer);
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
        [PreserveSig] int AddBuffer([In] IMFMediaBuffer pBuffer);
        [PreserveSig] int RemoveBufferByIndex(uint dwIndex);
        [PreserveSig] int RemoveAllBuffers();
        [PreserveSig] int GetTotalLength(out uint pcbTotalLength);
        [PreserveSig] int CopyToBuffer([In] IMFMediaBuffer pBuffer);
    }

    /// <summary>
    /// IMFSourceReader. Used only for trimming: it decodes an existing recording back to
    /// RGB32 frames so the range the user kept can be handed to the same encoder that
    /// wrote the file in the first place.
    ///
    /// Eleven methods, and as everywhere else in this file the order is the contract.
    /// </summary>
    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSourceReader
    {
        [PreserveSig] int GetStreamSelection(int dwStreamIndex,
                                             [MarshalAs(UnmanagedType.Bool)] out bool pfSelected);
        [PreserveSig] int SetStreamSelection(int dwStreamIndex,
                                             [MarshalAs(UnmanagedType.Bool)] bool fSelected);
        [PreserveSig] int GetNativeMediaType(int dwStreamIndex, int dwMediaTypeIndex,
                                             out IMFMediaType ppMediaType);
        [PreserveSig] int GetCurrentMediaType(int dwStreamIndex, out IMFMediaType ppMediaType);
        [PreserveSig] int SetCurrentMediaType(int dwStreamIndex, IntPtr pdwReserved,
                                              [In] IMFMediaType pMediaType);
        [PreserveSig] int SetCurrentPosition([In] ref Guid guidTimeFormat, [In] ref PropVariant varPosition);
        [PreserveSig] int ReadSample(int dwStreamIndex, int dwControlFlags,
                                     out int pdwActualStreamIndex, out int pdwStreamFlags,
                                     out long pllTimestamp, out IMFSample? ppSample);
        [PreserveSig] int Flush(int dwStreamIndex);
        [PreserveSig] int GetServiceForStream(int dwStreamIndex, [In] ref Guid guidService,
                                              [In] ref Guid riid, out IntPtr ppvObject);
        [PreserveSig] int GetPresentationAttribute(int dwStreamIndex, [In] ref Guid guidAttribute,
                                                   out PropVariant pvarAttribute);
    }

    /// <summary>
    /// The subset of PROPVARIANT this code needs: a 64-bit integer for a seek position and
    /// for the file duration. Laid out to match the native structure's first 16 bytes,
    /// which is all that is read or written here.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariant
    {
        internal ushort vt;
        internal ushort wReserved1, wReserved2, wReserved3;
        internal long value;

        /// <summary>VT_I8 — what MF wants for a time in hundred-nanosecond units.</summary>
        internal static PropVariant FromLong(long hns) => new() { vt = 20, value = hns };
    }

    [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSinkWriter
    {
        [PreserveSig] int AddStream([In] IMFMediaType pTargetMediaType, out int pdwStreamIndex);
        [PreserveSig] int SetInputMediaType(int dwStreamIndex, [In] IMFMediaType pInputMediaType,
                                            [In] IMFAttributes? pEncodingParameters);
        [PreserveSig] int BeginWriting();
        [PreserveSig] int WriteSample(int dwStreamIndex, [In] IMFSample pSample);
        [PreserveSig] int SendStreamTick(int dwStreamIndex, long llTimestamp);
        [PreserveSig] int PlaceMarker(int dwStreamIndex, IntPtr pvContext);
        [PreserveSig] int NotifyEndOfSegment(int dwStreamIndex);
        [PreserveSig] int Flush(int dwStreamIndex);

        /// <summary>
        /// IMFSinkWriter::Finalize. Renamed because C# reserves Finalize on Object;
        /// only the vtable position matters, and this is slot nine.
        /// </summary>
        [PreserveSig] int DoFinalize();

        [PreserveSig] int GetServiceForStream(int dwStreamIndex, [In] ref Guid guidService,
                                              [In] ref Guid riid, out IntPtr ppvObject);
        [PreserveSig] int GetStatistics(int dwStreamIndex, IntPtr pStats);
    }
}
