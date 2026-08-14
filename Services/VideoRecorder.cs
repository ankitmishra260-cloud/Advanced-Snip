using System;
using System.Runtime.InteropServices;

namespace AdvancedSnip.Services
{
    /// <summary>Quality presets, expressed as bits per pixel per frame.</summary>
    public enum VideoQuality { Efficient, Balanced, High, Maximum }

    /// <summary>
    /// Turns a stream of 32-bit frames into an H.264 MP4.
    ///
    /// Bitrate is derived rather than fixed, because a fixed number is wrong at every
    /// resolution but one. The figure that actually transfers between resolutions is bits
    /// per pixel per frame, and screen content needs remarkably few of them: it is mostly
    /// flat colour and unchanging between frames, so H.264's inter-frame prediction has an
    /// easy time. The presets below run from about 0.03 to about 0.14 bpp, where video
    /// from a camera would want 0.10 to 0.25 for the same perceived quality.
    ///
    /// Everything here must run on one thread, and that thread must be MTA — see
    /// <see cref="ScreenRecorder"/>, which owns it.
    /// </summary>
    internal sealed class VideoRecorder : IDisposable
    {
        /// <summary>Largest frame Media Foundation's H.264 encoder will accept.</summary>
        internal const int MaxEncodeWidth = 4096;
        internal const int MaxEncodeHeight = 2304;

        private IMFSinkWriter? _writer;
        private int _stream;
        private int _audioStream = -1;
        private int _width, _height, _stride, _frameBytes;
        private bool _started, _mfStarted;

        internal string? LastError { get; private set; }

        /// <summary>True when an audio stream was negotiated and can be written to.</summary>
        internal bool HasAudio => _audioStream >= 0;

        /// <summary>
        /// Bits per pixel per frame for each preset. "Efficient" is genuinely usable for a
        /// screen recording of text and UI; "Maximum" exists for content with video or
        /// heavy animation in it, where the cheap presets start to smear.
        /// </summary>
        private static double BitsPerPixel(VideoQuality q) => q switch
        {
            VideoQuality.Efficient => 0.030,
            VideoQuality.Balanced  => 0.055,
            VideoQuality.High      => 0.090,
            _                      => 0.140,
        };

        /// <summary>The bitrate a given size, rate and preset works out to, in bits/second.</summary>
        internal static int BitrateFor(int width, int height, int fps, VideoQuality q)
        {
            double bits = (double)width * height * fps * BitsPerPixel(q);
            // Floor and ceiling: below about 800 kbps even a still desktop looks blocky,
            // and above 40 Mbps the file grows without anything visible improving.
            return (int)Math.Clamp(bits, 800_000, 40_000_000);
        }

        /// <summary>Rough finished size, so the settings page can warn before recording.</summary>
        internal static double MegabytesPerMinute(int width, int height, int fps, VideoQuality q)
            => BitrateFor(width, height, fps, q) * 60.0 / 8.0 / (1024 * 1024);

        /// <summary>
        /// What the audio stream should be, or null for a silent recording.
        ///
        /// Windows' AAC encoder is narrow about its input, and every constraint here is
        /// one it enforces: 16-bit PCM only, 44100 or 48000 Hz, one or two channels.
        /// </summary>
        internal sealed class AudioSpec
        {
            internal int SampleRate { get; init; } = 48000;
            internal int Channels { get; init; } = 2;

            /// <summary>
            /// Bytes per second. The encoder accepts exactly four values — 12000, 16000,
            /// 20000 and 24000 — and refuses anything else, which is why this is not
            /// derived from a quality preset the way the video bitrate is. 24000 B/s is
            /// 192 kbps stereo: transparent for speech and system sounds.
            /// </summary>
            internal int BytesPerSecond { get; init; } = 24000;

            internal int BlockAlign => Channels * 2;
        }

        /// <summary>
        /// Opens the file and negotiates the encoder. Returns false with
        /// <see cref="LastError"/> set rather than throwing, because the caller is a
        /// capture thread that has a HUD to update either way.
        ///
        /// <paramref name="audio"/> adds a second stream to the same MP4. If the audio
        /// stream cannot be negotiated the video stream is kept and the recording goes on
        /// silently — losing the take because a microphone format was refused would be a
        /// far worse outcome than losing the sound.
        /// </summary>
        internal bool Start(string path, int width, int height, int fps, VideoQuality quality,
                            AudioSpec? audio = null)
        {
            try
            {
                // H.264 works in 16x16 macroblocks and rejects odd dimensions outright.
                // Rounding down rather than up avoids inventing a column of pixels.
                _width = Math.Max(2, width & ~1);
                _height = Math.Max(2, height & ~1);
                _stride = _width * 4;
                _frameBytes = _stride * _height;

                // Media Foundation's H.264 encoder tops out at 4096x2304. Two wide
                // monitors side by side pass that easily, and the failure without this
                // check is an opaque HRESULT from AddStream rather than anything the user
                // could act on.
                if (_width > MaxEncodeWidth || _height > MaxEncodeHeight)
                    return Fail($"That area is {_width}x{_height}, and H.264 cannot encode "
                              + $"anything larger than {MaxEncodeWidth}x{MaxEncodeHeight}. "
                              + "Record a single monitor or a region instead.");

                if (!MF.Ok(MF.MFStartup(MF.MF_VERSION, MF.MFSTARTUP_NOSOCKET)))
                    return Fail("Media Foundation would not start.");
                _mfStarted = true;

                if (!MF.Ok(MF.MFCreateAttributes(out var attrs, 4)))
                    return Fail("Could not create encoder attributes.");

                // Hardware transforms is the difference between a few percent of one core
                // and a whole core saturated at 1440p.
                attrs.SetUINT32(ref MF.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
                // Without this the writer paces itself to real time and blocks our thread,
                // which is wrong when we are already pacing the capture ourselves.
                attrs.SetUINT32(ref MF.MF_SINK_WRITER_DISABLE_THROTTLING, 1);

                int hr = MF.MFCreateSinkWriterFromURL(path, IntPtr.Zero, attrs, out var writer);
                MF.Release(attrs);
                if (!MF.Ok(hr)) return Fail($"Could not create the MP4 writer (0x{hr:X8}).");
                _writer = writer;

                // ── what comes out ──
                if (!MF.Ok(MF.MFCreateMediaType(out var outType)))
                    return Fail("Could not describe the output format.");

                outType.SetGUID(ref MF.MF_MT_MAJOR_TYPE, ref MF.MFMediaType_Video);
                outType.SetGUID(ref MF.MF_MT_SUBTYPE, ref MF.MFVideoFormat_H264);
                outType.SetUINT32(ref MF.MF_MT_AVG_BITRATE,
                                  (uint)BitrateFor(_width, _height, fps, quality));
                outType.SetUINT32(ref MF.MF_MT_INTERLACE_MODE, MF.MFVideoInterlace_Progressive);
                outType.SetUINT32(ref MF.MF_MT_MPEG2_PROFILE, MF.eAVEncH264VProfile_High);
                outType.SetUINT64(ref MF.MF_MT_FRAME_SIZE, MF.Pack((uint)_width, (uint)_height));
                outType.SetUINT64(ref MF.MF_MT_FRAME_RATE, MF.Pack((uint)fps, 1));
                outType.SetUINT64(ref MF.MF_MT_PIXEL_ASPECT_RATIO, MF.Pack(1, 1));

                hr = _writer.AddStream(outType, out _stream);
                MF.Release(outType);
                if (!MF.Ok(hr)) return Fail($"The H.264 encoder refused this format (0x{hr:X8}).");

                // ── what goes in ──
                if (!MF.Ok(MF.MFCreateMediaType(out var inType)))
                    return Fail("Could not describe the input format.");

                inType.SetGUID(ref MF.MF_MT_MAJOR_TYPE, ref MF.MFMediaType_Video);
                inType.SetGUID(ref MF.MF_MT_SUBTYPE, ref MF.MFVideoFormat_RGB32);
                inType.SetUINT32(ref MF.MF_MT_INTERLACE_MODE, MF.MFVideoInterlace_Progressive);
                inType.SetUINT64(ref MF.MF_MT_FRAME_SIZE, MF.Pack((uint)_width, (uint)_height));
                inType.SetUINT64(ref MF.MF_MT_FRAME_RATE, MF.Pack((uint)fps, 1));
                inType.SetUINT64(ref MF.MF_MT_PIXEL_ASPECT_RATIO, MF.Pack(1, 1));

                // The single most important line in this file. RGB32 in Media Foundation
                // is bottom-up by convention, while a GDI+ Bitmap hands us top-down rows.
                // Declaring a positive stride is what says "top-down"; without it the
                // whole recording comes out vertically mirrored and it looks like a
                // capture bug rather than a metadata one.
                inType.SetUINT32(ref MF.MF_MT_DEFAULT_STRIDE, (uint)_stride);

                hr = _writer.SetInputMediaType(_stream, inType, null);
                MF.Release(inType);
                if (!MF.Ok(hr)) return Fail($"The encoder would not accept RGB frames (0x{hr:X8}).");

                // ── sound ──
                // Deliberately after the video stream and deliberately non-fatal: every
                // failure here leaves _audioStream at -1 and the recording continues
                // without sound.
                if (audio != null) TryAddAudio(audio);

                if (!MF.Ok(hr = _writer.BeginWriting()))
                    return Fail($"The writer would not start (0x{hr:X8}).");

                _started = true;
                return true;
            }
            catch (DllNotFoundException)
            {
                return Fail("Media Foundation is not available on this edition of Windows. "
                          + "The 'N' editions need the Media Feature Pack.");
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        /// <summary>
        /// Encodes one frame. <paramref name="scan0"/> points at top-down BGRA rows.
        /// Times are in 100-nanosecond units, which is Media Foundation's unit throughout.
        /// </summary>
        internal bool WriteFrame(IntPtr scan0, int sourceStride, long timeHns, long durationHns)
        {
            if (!_started || _writer == null) return false;

            IMFMediaBuffer? buffer = null;
            IMFSample? sample = null;

            try
            {
                if (!MF.Ok(MF.MFCreateMemoryBuffer(_frameBytes, out buffer))) return false;
                if (!MF.Ok(buffer.Lock(out IntPtr dest, out _, out _))) return false;

                try
                {
                    // Stride-aware on purpose: GDI+ pads a bitmap's rows to a four-byte
                    // boundary and the encoder wants its own packed stride, and an
                    // off-by-one stride shears the entire picture diagonally.
                    MF.MFCopyImage(dest, _stride, scan0, sourceStride, _width * 4, _height);
                }
                finally { buffer.Unlock(); }

                buffer.SetCurrentLength(_frameBytes);

                if (!MF.Ok(MF.MFCreateSample(out sample))) return false;
                sample.AddBuffer(buffer);
                sample.SetSampleTime(timeHns);
                sample.SetSampleDuration(durationHns);

                return MF.Ok(_writer.WriteSample(_stream, sample));
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
            finally
            {
                MF.Release(sample);
                MF.Release(buffer);
            }
        }

        /// <summary>
        /// Negotiates the AAC stream. Returns quietly on any failure — see the note on
        /// <see cref="Start"/> about why this must never take the video down with it.
        /// </summary>
        private void TryAddAudio(AudioSpec audio)
        {
            IMFMediaType? outType = null;
            IMFMediaType? inType = null;

            try
            {
                if (_writer == null) return;
                if (audio.Channels is < 1 or > 2) return;
                if (audio.SampleRate is not (44100 or 48000)) return;

                if (!MF.Ok(MF.MFCreateMediaType(out outType))) return;

                outType.SetGUID(ref MF.MF_MT_MAJOR_TYPE, ref MF.MFMediaType_Audio);
                outType.SetGUID(ref MF.MF_MT_SUBTYPE, ref MF.MFAudioFormat_AAC);
                outType.SetUINT32(ref MF.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
                outType.SetUINT32(ref MF.MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)audio.SampleRate);
                outType.SetUINT32(ref MF.MF_MT_AUDIO_NUM_CHANNELS, (uint)audio.Channels);
                outType.SetUINT32(ref MF.MF_MT_AUDIO_AVG_BYTES_PER_SECOND,
                                  (uint)audio.BytesPerSecond);
                // 0 means raw AAC in an MP4 container rather than an ADTS stream, which is
                // what the muxer wants; the other values produce a file that plays in some
                // decoders and not others.
                outType.SetUINT32(ref MF.MF_MT_AAC_PAYLOAD_TYPE, 0);
                outType.SetUINT32(ref MF.MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION,
                                  MF.AAC_PROFILE_LC_L2);

                if (!MF.Ok(_writer.AddStream(outType, out int stream))) return;

                if (!MF.Ok(MF.MFCreateMediaType(out inType))) return;

                inType.SetGUID(ref MF.MF_MT_MAJOR_TYPE, ref MF.MFMediaType_Audio);
                inType.SetGUID(ref MF.MF_MT_SUBTYPE, ref MF.MFAudioFormat_PCM);
                inType.SetUINT32(ref MF.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
                inType.SetUINT32(ref MF.MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)audio.SampleRate);
                inType.SetUINT32(ref MF.MF_MT_AUDIO_NUM_CHANNELS, (uint)audio.Channels);
                inType.SetUINT32(ref MF.MF_MT_AUDIO_BLOCK_ALIGNMENT, (uint)audio.BlockAlign);
                inType.SetUINT32(ref MF.MF_MT_AUDIO_AVG_BYTES_PER_SECOND,
                                 (uint)(audio.SampleRate * audio.BlockAlign));

                if (!MF.Ok(_writer.SetInputMediaType(stream, inType, null))) return;

                _audioStream = stream;
                _audioBlockAlign = audio.BlockAlign;
                _audioRate = audio.SampleRate;
            }
            catch { /* silent recording */ }
            finally
            {
                MF.Release(outType);
                MF.Release(inType);
            }
        }

        private int _audioBlockAlign = 4;
        private int _audioRate = 48000;

        /// <summary>
        /// Encodes a block of interleaved 16-bit PCM.
        ///
        /// <paramref name="startFrame"/> is the sample index this block begins at, which is
        /// what makes the timestamp exact: deriving it from the wall clock instead would
        /// let rounding accumulate across thousands of blocks until the sound sat visibly
        /// behind the picture.
        /// </summary>
        internal bool WriteAudio(byte[] pcm, int byteCount, long startFrame)
        {
            if (!_started || _writer == null || _audioStream < 0 || byteCount <= 0) return false;

            IMFMediaBuffer? buffer = null;
            IMFSample? sample = null;

            try
            {
                if (!MF.Ok(MF.MFCreateMemoryBuffer(byteCount, out buffer))) return false;
                if (!MF.Ok(buffer.Lock(out IntPtr dest, out _, out _))) return false;

                try { Marshal.Copy(pcm, 0, dest, byteCount); }
                finally { buffer.Unlock(); }

                buffer.SetCurrentLength(byteCount);

                if (!MF.Ok(MF.MFCreateSample(out sample))) return false;
                sample.AddBuffer(buffer);

                int frames = byteCount / Math.Max(1, _audioBlockAlign);
                long timeHns = startFrame * 10_000_000L / Math.Max(1, _audioRate);
                long durationHns = frames * 10_000_000L / Math.Max(1, _audioRate);

                sample.SetSampleTime(timeHns);
                sample.SetSampleDuration(Math.Max(1, durationHns));

                return MF.Ok(_writer.WriteSample(_audioStream, sample));
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
            finally
            {
                MF.Release(sample);
                MF.Release(buffer);
            }
        }

        /// <summary>Closes the container. Skipping this leaves an unplayable file.</summary>
        internal void Stop()
        {
            try
            {
                if (_started && _writer != null) _writer.DoFinalize();
            }
            catch { }
            finally
            {
                _started = false;
                _audioStream = -1;
                MF.Release(_writer);
                _writer = null;

                if (_mfStarted)
                {
                    try { MF.MFShutdown(); } catch { }
                    _mfStarted = false;
                }
            }
        }

        private bool Fail(string message)
        {
            LastError = message;
            Stop();
            return false;
        }

        public void Dispose() => Stop();
    }
}
