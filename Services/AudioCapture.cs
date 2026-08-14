using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace AdvancedSnip.Services
{
    /// <summary>An audio endpoint the user can choose in settings.</summary>
    public sealed class AudioDeviceInfo
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";

        /// <summary>The synthetic "whatever Windows is using" entry, whose Id is empty.</summary>
        public bool IsDefault => Id.Length == 0;

        public override string ToString() => Name;
    }

    /// <summary>
    /// Sound capture through WASAPI, for both halves of what a screen recording needs:
    /// what the machine is playing, and what the microphone hears.
    ///
    /// **Why WASAPI and not something else.** The same reasoning as the hand-written ICO
    /// container, the PDF writer and the Media Foundation encoder: it is already on every
    /// Windows machine, it needs no package, and loopback capture — recording the audio
    /// the speakers are being sent — is a documented flag on the ordinary capture path
    /// rather than a driver or a virtual cable the user has to install.
    ///
    /// **Two clocks, one timeline.** The speakers and the microphone are separate devices
    /// with separate crystals; left alone they drift apart, and both drift against the
    /// video. So neither device drives the timeline. Each one writes into its own ring
    /// buffer, and <see cref="AudioMixer"/> pulls from both against the *recording's* own
    /// stopwatch, padding with silence when a device has nothing to give. Drift then shows
    /// up as a few dropped or repeated milliseconds rather than as audio that is three
    /// seconds ahead by the end of a long take.
    ///
    /// **Nothing here is allowed to fail a recording.** Every entry point returns a bool
    /// or an empty list. If the machine has no microphone, if an IID is wrong, if the
    /// audio service is stopped — the video still records, silently. Sound is worth having
    /// and it is not worth losing a take over.
    /// </summary>
    internal static class AudioDevices
    {
        /// <summary>Playback endpoints. Recording one of these means loopback capture.</summary>
        internal static List<AudioDeviceInfo> Playback() => Enumerate(EDataFlow.Render);

        /// <summary>Microphones and other inputs.</summary>
        internal static List<AudioDeviceInfo> Recording() => Enumerate(EDataFlow.Capture);

        private static List<AudioDeviceInfo> Enumerate(EDataFlow flow)
        {
            var list = new List<AudioDeviceInfo>
            {
                new() { Id = "", Name = flow == EDataFlow.Render
                    ? "Default playback device"
                    : "Default microphone" }
            };

            IMMDeviceEnumerator? enumerator = null;
            IMMDeviceCollection? collection = null;

            try
            {
                enumerator = CreateEnumerator();
                if (enumerator == null) return list;

                if (enumerator.EnumAudioEndpoints(flow, DEVICE_STATE_ACTIVE, out collection) < 0)
                    return list;

                if (collection.GetCount(out int count) < 0) return list;

                for (int i = 0; i < count; i++)
                {
                    IMMDevice? device = null;
                    try
                    {
                        if (collection.Item(i, out device) < 0 || device == null) continue;
                        if (device.GetId(out IntPtr idPtr) < 0) continue;

                        string id = Marshal.PtrToStringUni(idPtr) ?? "";
                        Marshal.FreeCoTaskMem(idPtr);
                        if (id.Length == 0) continue;

                        list.Add(new AudioDeviceInfo { Id = id, Name = FriendlyName(device, id) });
                    }
                    catch { }
                    finally { MF.Release(device); }
                }
            }
            catch { }
            finally
            {
                MF.Release(collection);
                MF.Release(enumerator);
            }

            return list;
        }

        private static string FriendlyName(IMMDevice device, string fallback)
        {
            IPropertyStore? store = null;
            try
            {
                if (device.OpenPropertyStore(STGM_READ, out store) < 0 || store == null)
                    return fallback;

                var key = PKEY_Device_FriendlyName;
                if (store.GetValue(ref key, out PropVariantNative pv) < 0) return fallback;

                try
                {
                    // VT_LPWSTR. Anything else is a device that will not name itself, and
                    // its id is at least unique.
                    if (pv.vt == 31 && pv.pointer != IntPtr.Zero)
                        return Marshal.PtrToStringUni(pv.pointer) ?? fallback;
                }
                finally { try { PropVariantClear(ref pv); } catch { } }

                return fallback;
            }
            catch { return fallback; }
            finally { MF.Release(store); }
        }

        internal static IMMDeviceEnumerator? CreateEnumerator()
        {
            try
            {
                var type = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator);
                if (type == null) return null;
                return Activator.CreateInstance(type) as IMMDeviceEnumerator;
            }
            catch { return null; }
        }

        /// <summary>Resolves an id to a device, falling back to the default endpoint.</summary>
        internal static IMMDevice? Open(IMMDeviceEnumerator enumerator, string? id, EDataFlow flow)
        {
            if (!string.IsNullOrEmpty(id))
            {
                try
                {
                    if (enumerator.GetDevice(id!, out var byId) >= 0 && byId != null) return byId;
                }
                catch { }
                // A device that has been unplugged since the setting was saved should fall
                // back to the default rather than silently recording nothing.
            }

            try
            {
                // eConsole: what Windows itself would use for ordinary playback or a call.
                return enumerator.GetDefaultAudioEndpoint(flow, ERole.Console, out var def) >= 0
                    ? def : null;
            }
            catch { return null; }
        }

        // ── COM plumbing ──────────────────────────────────────────────────────
        //
        // These IIDs and vtable orders come from mmdeviceapi.h and audioclient.h. As
        // everywhere else in this project, COM dispatches by slot position, so a method
        // declared out of order calls the wrong function with the right-looking arguments.
        // Getting one wrong here costs the audio track, never the video: every caller
        // treats a failure as "no sound available".

        internal static readonly Guid CLSID_MMDeviceEnumerator =
            new("BCDE0395-E52F-467C-8E3D-C4579291692E");

        internal const int DEVICE_STATE_ACTIVE = 0x00000001;
        internal const int STGM_READ = 0x00000000;
        internal const int CLSCTX_ALL = 0x17;

        /// <summary>
        /// PKEY_Device_FriendlyName. The property key is a GUID plus an index, and the
        /// index is part of the identity — fmtid alone names a whole property set.
        /// </summary>
        internal static PropertyKey PKEY_Device_FriendlyName =
            new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };

        [DllImport("ole32.dll")]
        internal static extern int PropVariantClear(ref PropVariantNative pvar);
    }

    internal enum EDataFlow { Render = 0, Capture = 1, All = 2 }
    internal enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        internal Guid fmtid;
        internal int pid;
    }

    /// <summary>
    /// The first 16 bytes of a PROPVARIANT, which is all that is needed to pull a string
    /// pointer out of one. Cleared through ole32 so the string is freed by whoever
    /// allocated it.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariantNative
    {
        internal ushort vt;
        internal ushort r1, r2, r3;
        internal IntPtr pointer;
        internal IntPtr tail;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int dwStateMask,
                                             out IMMDeviceCollection ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role,
                                                  out IMMDevice ppEndpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId,
                                    out IMMDevice ppDevice);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr pClient);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int pcDevices);
        [PreserveSig] int Item(int nDevice, out IMMDevice ppDevice);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate([In] ref Guid iid, int dwClsCtx, IntPtr pActivationParams,
                                   [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);
        [PreserveSig] int GetId(out IntPtr ppstrId);
        [PreserveSig] int GetState(out int pdwState);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int cProps);
        [PreserveSig] int GetAt(int iProp, out PropertyKey pkey);
        [PreserveSig] int GetValue([In] ref PropertyKey key, out PropVariantNative pv);
        [PreserveSig] int SetValue([In] ref PropertyKey key, [In] ref PropVariantNative pv);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long hnsBufferDuration,
                                     long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out int pNumBufferFrames);
        [PreserveSig] int GetStreamLatency(out long phnsLatency);
        [PreserveSig] int GetCurrentPadding(out int pNumPaddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr ppClosestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr ppDeviceFormat);
        [PreserveSig] int GetDevicePeriod(out long phnsDefaultDevicePeriod,
                                          out long phnsMinimumDevicePeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService([In] ref Guid riid,
                                     [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr ppData, out int pNumFramesToRead,
                                    out int pdwFlags, out long pu64DevicePosition,
                                    out long pu64QPCPosition);
        [PreserveSig] int ReleaseBuffer(int numFramesRead);
        [PreserveSig] int GetNextPacketSize(out int pNumFramesInNextPacket);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct WaveFormatEx
    {
        internal ushort wFormatTag;
        internal ushort nChannels;
        internal uint nSamplesPerSec;
        internal uint nAvgBytesPerSec;
        internal ushort nBlockAlign;
        internal ushort wBitsPerSample;
        internal ushort cbSize;
    }

    /// <summary>
    /// One device, captured onto a shared ring buffer in a fixed format.
    ///
    /// Everything device-specific is dealt with here — sample format, channel count and
    /// sample rate — so the mixer downstream only ever sees interleaved stereo floats at
    /// one rate. That is deliberate: format negotiation failures are the most common way
    /// audio capture breaks, and they are much easier to reason about when they are all
    /// in one place.
    /// </summary>
    internal sealed class WasapiCapture : IDisposable
    {
        private const int AUDCLNT_SHAREMODE_SHARED = 0;
        private const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        private const int AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
        private const int AUDCLNT_S_BUFFER_EMPTY = 0x08890001;

        private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

        private IMMDeviceEnumerator? _enumerator;
        private IMMDevice? _device;
        private IAudioClient? _client;
        private IAudioCaptureClient? _capture;
        private IntPtr _formatPtr;

        private Thread? _thread;
        private volatile bool _stop;
        private bool _started;

        private AudioRing? _ring;
        private int _targetRate;
        private readonly int _targetChannels;

        private WaveFormatEx _format;
        private bool _isFloat;
        private double _resamplePos;
        private float[] _previousFrame = Array.Empty<float>();
        private byte[] _scratch = Array.Empty<byte>();
        private float[] _converted = Array.Empty<float>();

        internal string? Error { get; private set; }

        /// <summary>The device's own sample rate, known once <see cref="Open"/> succeeds.</summary>
        internal int DeviceRate => (int)_format.nSamplesPerSec;

        /// <summary>
        /// The output format is fixed at construction; the destination and the rate are
        /// not, because the mixer cannot choose a master rate until it has asked a device
        /// what rate it runs at. See <see cref="SetTarget"/>.
        /// </summary>
        internal WasapiCapture(int targetChannels) => _targetChannels = targetChannels;

        /// <summary>
        /// Says where the converted samples go and at what rate, between
        /// <see cref="Open"/> and <see cref="Start"/>.
        ///
        /// This exists so a device is opened once rather than twice. The mixer used to
        /// open the playback device, read its rate, close it and open it again with the
        /// right ring — which works, and gives another application a window in which to
        /// take the device and turn a recording silent for no reason.
        /// </summary>
        internal void SetTarget(AudioRing ring, int targetRate)
        {
            _ring = ring;
            _targetRate = targetRate;
            _resamplePos = 0;
        }

        /// <summary>
        /// Opens a device far enough to know its format, without starting the thread.
        /// Split from <see cref="Start"/> so the mixer can pick a master sample rate that
        /// matches the system device and avoid resampling the higher-fidelity source.
        /// </summary>
        internal bool Open(string? deviceId, bool loopback)
        {
            try
            {
                _enumerator = AudioDevices.CreateEnumerator();
                if (_enumerator == null) return Fail("The Windows audio service is not available.");

                _device = AudioDevices.Open(_enumerator, deviceId,
                                            loopback ? EDataFlow.Render : EDataFlow.Capture);
                if (_device == null)
                    return Fail(loopback ? "No playback device to record from."
                                         : "No microphone is available.");

                var iid = IID_IAudioClient;
                if (_device.Activate(ref iid, AudioDevices.CLSCTX_ALL, IntPtr.Zero, out object clientObj) < 0
                    || clientObj is not IAudioClient client)
                    return Fail("The audio device would not open.");
                _client = client;

                if (_client.GetMixFormat(out _formatPtr) < 0 || _formatPtr == IntPtr.Zero)
                    return Fail("The audio device would not report its format.");

                _format = Marshal.PtrToStructure<WaveFormatEx>(_formatPtr);
                _isFloat = IsFloat(_formatPtr, _format);

                if (_format.nChannels < 1 || _format.nSamplesPerSec < 8000)
                    return Fail("The audio device reported a format that cannot be read.");

                _previousFrame = new float[_targetChannels];
                return true;
            }
            catch (Exception ex) { return Fail(ex.Message); }
        }

        /// <summary>
        /// Initialises the stream and starts pumping. Shared mode throughout: exclusive
        /// mode would take the device away from everything else on the machine, which for
        /// loopback would mean recording silence and muting the very thing being recorded.
        /// </summary>
        internal bool Start(bool loopback)
        {
            if (_client == null) return Fail("Open() was not called.");
            if (_ring == null) return Fail("SetTarget() was not called.");

            try
            {
                // 400 ms of headroom. The polling loop below wakes about every 10 ms, so
                // this is forty times more slack than it needs — which is what keeps a
                // momentarily busy machine from dropping packets.
                const long bufferHns = 4_000_000;

                int flags = loopback ? AUDCLNT_STREAMFLAGS_LOOPBACK : 0;

                int hr = _client.Initialize(AUDCLNT_SHAREMODE_SHARED, flags,
                                            bufferHns, 0, _formatPtr, IntPtr.Zero);
                if (hr < 0) return Fail($"The audio stream would not open (0x{hr:X8}).");

                var iid = IID_IAudioCaptureClient;
                if (_client.GetService(ref iid, out object captureObj) < 0
                    || captureObj is not IAudioCaptureClient capture)
                    return Fail("The audio device would not hand over a capture client.");
                _capture = capture;

                if (_client.Start() < 0) return Fail("The audio device would not start.");
                _started = true;

                _stop = false;
                _thread = new Thread(Pump)
                {
                    IsBackground = true,
                    Name = loopback ? "AdvancedSnip.Loopback" : "AdvancedSnip.Microphone",
                    // Audio glitches are far more noticeable than a dropped video frame,
                    // and the work per wake-up is tiny.
                    Priority = ThreadPriority.Highest
                };
                _thread.SetApartmentState(ApartmentState.MTA);
                _thread.Start();
                return true;
            }
            catch (Exception ex) { return Fail(ex.Message); }
        }

        private void Pump()
        {
            while (!_stop)
            {
                try
                {
                    if (_capture == null) break;

                    if (_capture.GetNextPacketSize(out int packet) < 0) { Thread.Sleep(8); continue; }

                    if (packet == 0)
                    {
                        // Nothing playing, or nobody speaking. The mixer pads the gap with
                        // silence against the recording clock, so there is nothing to do
                        // here but wait — writing silence from this end as well would push
                        // the timeline ahead of itself.
                        Thread.Sleep(6);
                        continue;
                    }

                    while (packet > 0 && !_stop)
                    {
                        int hr = _capture.GetBuffer(out IntPtr data, out int frames,
                                                    out int flags, out _, out _);
                        if (hr == AUDCLNT_S_BUFFER_EMPTY || frames == 0)
                        {
                            if (hr >= 0) _capture.ReleaseBuffer(0);
                            break;
                        }
                        if (hr < 0) break;

                        try
                        {
                            bool silent = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                            Convert(data, frames, silent);
                        }
                        finally { _capture.ReleaseBuffer(frames); }

                        if (_capture.GetNextPacketSize(out packet) < 0) break;
                    }
                }
                catch
                {
                    // A device removed mid-recording throws here. Stop feeding rather than
                    // tearing the recording down; the mixer will pad with silence.
                    break;
                }
            }
        }

        /// <summary>
        /// Device format to the mixer's format: sample type, channel count, sample rate.
        /// Runs on the capture thread, so it reuses its scratch arrays rather than
        /// allocating a few hundred times a second.
        /// </summary>
        private void Convert(IntPtr data, int frames, bool silent)
        {
            int srcChannels = _format.nChannels;
            int bytes = frames * _format.nBlockAlign;

            if (_scratch.Length < bytes) _scratch = new byte[bytes];
            if (!silent) Marshal.Copy(data, _scratch, 0, bytes);
            else Array.Clear(_scratch, 0, bytes);

            if (_targetRate <= 0) return;
            double ratio = (double)_format.nSamplesPerSec / _targetRate;

            // Worst case output length, plus a frame of slack for the fractional position.
            int maxOut = (int)(frames / ratio) + 2;
            int need = maxOut * _targetChannels;
            if (_converted.Length < need) _converted = new float[need];

            int outFrames = 0;
            var frame = new float[_targetChannels];

            if (Math.Abs(ratio - 1.0) < 1e-9)
            {
                // The common case — the device is already at the master rate, so nothing
                // is interpolated and nothing is lost.
                for (int i = 0; i < frames; i++)
                {
                    ReadFrame(i, srcChannels, frame);
                    for (int c = 0; c < _targetChannels; c++)
                        _converted[outFrames * _targetChannels + c] = frame[c];
                    outFrames++;
                }
                if (frames > 0) ReadFrame(frames - 1, srcChannels, _previousFrame);
            }
            else
            {
                // Linear interpolation. Not a windowed-sinc resampler, and it does not need
                // to be: the only time this path runs is a microphone at 44.1 kHz next to
                // speakers at 48 kHz, where the error sits far below the noise floor of the
                // microphone itself.
                double pos = _resamplePos;
                var a = new float[_targetChannels];
                var b = new float[_targetChannels];

                while (true)
                {
                    int i0 = (int)Math.Floor(pos);
                    double frac = pos - i0;
                    if (i0 + 1 >= frames) break;

                    if (i0 < 0) Array.Copy(_previousFrame, a, _targetChannels);
                    else ReadFrame(i0, srcChannels, a);
                    ReadFrame(i0 + 1, srcChannels, b);

                    for (int c = 0; c < _targetChannels; c++)
                        _converted[outFrames * _targetChannels + c] =
                            (float)(a[c] + (b[c] - a[c]) * frac);

                    outFrames++;
                    pos += ratio;
                    if (outFrames >= maxOut) break;
                }

                if (frames > 0) ReadFrame(frames - 1, srcChannels, _previousFrame);
                // Carry the leftover fraction into the next packet so the two do not
                // develop a seam at every buffer boundary.
                _resamplePos = pos - frames;
                if (_resamplePos < -1) _resamplePos = -1;
            }

            if (outFrames > 0) _ring?.Write(_converted, outFrames * _targetChannels);
        }

        /// <summary>Decodes one source frame into <paramref name="dest"/> as target channels.</summary>
        private void ReadFrame(int index, int srcChannels, float[] dest)
        {
            int bytesPerSample = _format.wBitsPerSample / 8;
            int baseOffset = index * _format.nBlockAlign;

            float left = Sample(baseOffset, bytesPerSample);
            float right = srcChannels > 1
                ? Sample(baseOffset + bytesPerSample, bytesPerSample)
                : left;

            if (_targetChannels == 1)
            {
                dest[0] = srcChannels > 1 ? (left + right) * 0.5f : left;
                return;
            }

            dest[0] = left;
            dest[1] = right;
            // Anything beyond stereo is dropped rather than downmixed. A 5.1 desktop mix
            // folded down by summing would clip; taking the front pair is what every
            // simple recorder does and it is what the user expects to hear.
            for (int c = 2; c < _targetChannels; c++) dest[c] = 0;
        }

        private float Sample(int offset, int bytesPerSample)
        {
            if (offset < 0 || offset + bytesPerSample > _scratch.Length) return 0;

            if (_isFloat && bytesPerSample == 4)
                return BitConverter.ToSingle(_scratch, offset);

            switch (bytesPerSample)
            {
                case 2:
                    return BitConverter.ToInt16(_scratch, offset) / 32768f;
                case 4:
                    return BitConverter.ToInt32(_scratch, offset) / 2147483648f;
                case 3:
                    {
                        int v = _scratch[offset] | (_scratch[offset + 1] << 8) | (_scratch[offset + 2] << 16);
                        if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);   // sign extend
                        return v / 8388608f;
                    }
                case 1:
                    return (_scratch[offset] - 128) / 128f;
                default:
                    return 0;
            }
        }

        private static bool IsFloat(IntPtr ptr, WaveFormatEx format)
        {
            const ushort WAVE_FORMAT_IEEE_FLOAT = 3;
            const ushort WAVE_FORMAT_EXTENSIBLE = 0xFFFE;

            if (format.wFormatTag == WAVE_FORMAT_IEEE_FLOAT) return true;
            if (format.wFormatTag != WAVE_FORMAT_EXTENSIBLE) return false;

            try
            {
                // WAVEFORMATEXTENSIBLE: the 18-byte WAVEFORMATEX, then a two-byte union and
                // a four-byte channel mask, then the sub-format GUID at offset 24.
                var sub = Marshal.PtrToStructure<Guid>(IntPtr.Add(ptr, 24));
                return sub == new Guid("00000003-0000-0010-8000-00aa00389b71");
            }
            catch { return false; }
        }

        internal void Stop()
        {
            _stop = true;
            try { _thread?.Join(1500); } catch { }
            _thread = null;

            try { if (_started) _client?.Stop(); } catch { }
            _started = false;
        }

        private bool Fail(string message)
        {
            Error ??= message;
            return false;
        }

        public void Dispose()
        {
            Stop();

            MF.Release(_capture);
            _capture = null;
            MF.Release(_client);
            _client = null;
            MF.Release(_device);
            _device = null;
            MF.Release(_enumerator);
            _enumerator = null;

            if (_formatPtr != IntPtr.Zero)
            {
                try { Marshal.FreeCoTaskMem(_formatPtr); } catch { }
                _formatPtr = IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// A fixed-size circular buffer of interleaved float samples, written by a capture
    /// thread and read by the encoder pump.
    ///
    /// It drops the oldest samples when it fills rather than blocking the writer. That is
    /// the right trade for live capture: the writer is a real-time audio thread that must
    /// never wait, and a backlog that is allowed to grow becomes latency that never comes
    /// back — audio that is progressively further behind the picture.
    /// </summary>
    internal sealed class AudioRing
    {
        private readonly object _gate = new();
        private readonly float[] _buffer;
        private int _read, _count;

        internal AudioRing(int capacitySamples) => _buffer = new float[Math.Max(1024, capacitySamples)];

        internal int Available { get { lock (_gate) return _count; } }

        internal void Write(float[] source, int count)
        {
            if (count <= 0) return;

            lock (_gate)
            {
                if (count >= _buffer.Length)
                {
                    // A single packet larger than the whole ring can only mean the reader
                    // has stopped; keep the newest tail.
                    Array.Copy(source, count - _buffer.Length, _buffer, 0, _buffer.Length);
                    _read = 0;
                    _count = _buffer.Length;
                    return;
                }

                int write = (_read + _count) % _buffer.Length;
                int first = Math.Min(count, _buffer.Length - write);
                Array.Copy(source, 0, _buffer, write, first);
                if (first < count) Array.Copy(source, first, _buffer, 0, count - first);

                _count += count;
                if (_count > _buffer.Length)
                {
                    int overflow = _count - _buffer.Length;
                    _read = (_read + overflow) % _buffer.Length;
                    _count = _buffer.Length;
                }
            }
        }

        /// <summary>Takes up to <paramref name="count"/> samples. Returns how many it got.</summary>
        internal int Read(float[] destination, int offset, int count)
        {
            lock (_gate)
            {
                int take = Math.Min(count, _count);
                if (take <= 0) return 0;

                int first = Math.Min(take, _buffer.Length - _read);
                Array.Copy(_buffer, _read, destination, offset, first);
                if (first < take) Array.Copy(_buffer, 0, destination, offset + first, take - first);

                _read = (_read + take) % _buffer.Length;
                _count -= take;
                return take;
            }
        }

        /// <summary>
        /// Throws away everything buffered. Used on resume: audio captured while the
        /// recording was paused belongs to a moment that is not in the video.
        /// </summary>
        internal void Clear()
        {
            lock (_gate) { _read = 0; _count = 0; }
        }

        /// <summary>
        /// Caps how far behind the buffer is allowed to get. Called by the mixer each
        /// pump, so a device that runs slightly fast cannot accumulate a delay.
        /// </summary>
        internal void TrimTo(int maxSamples)
        {
            lock (_gate)
            {
                if (_count <= maxSamples) return;
                int drop = _count - maxSamples;
                _read = (_read + drop) % _buffer.Length;
                _count = maxSamples;
            }
        }
    }
}
