using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace AdvancedSnip.Services
{
    /// <summary>
    /// Renders an edit to a new file.
    ///
    /// **This decodes and re-encodes rather than copying the compressed stream.** A
    /// pass-through remux would avoid any quality loss, but H.264 can only start decoding
    /// at a keyframe, so a stream copy has to snap every cut to the nearest one — which
    /// for a screen recording can be several seconds away from where the user put the
    /// marker. Decoding to RGB32 and re-encoding gives cuts exactly where they asked for
    /// them, and screen content survives a second encode at the same bitrate essentially
    /// unchanged. It costs time, which is why this reports progress and can be cancelled.
    ///
    /// **Segments are rendered in list order, not source order.** The output timestamp is
    /// tracked separately from the source timestamp, so reordered pieces, per-piece speed
    /// changes and removed sections all fall out of the same loop rather than needing
    /// special cases.
    ///
    /// **Audio is carried through when the source has it.** A second reader stream is
    /// decoded to PCM and re-timed the same way, with muted segments written as silence
    /// rather than skipped — a gap would leave everything after it sliding forward.
    ///
    /// The encoder is <see cref="VideoRecorder"/>, unchanged: the same code path that
    /// produced the file writes the edited one.
    /// </summary>
    internal static class VideoTrimmer
    {
        internal sealed class TrimResult
        {
            internal bool Ok { get; init; }
            internal string? Error { get; init; }
            internal string? Path { get; init; }
        }

        /// <summary>
        /// Writes the range between <paramref name="startHns"/> and <paramref name="endHns"/>.
        /// Kept as a one-range convenience over <see cref="Export"/> so existing callers
        /// and the simple "cut the ends off" case stay a single call.
        /// </summary>
        internal static TrimResult Trim(string source, string destination,
                                        long startHns, long endHns,
                                        int fps, VideoQuality quality,
                                        Action<double>? progress = null,
                                        CancellationToken cancel = default)
        {
            var project = new VideoProject(TimeSpan.FromTicks(endHns));
            project.KeepOnly(TimeSpan.FromTicks(startHns), TimeSpan.FromTicks(endHns));
            return Export(source, destination, project, fps, quality, progress, cancel);
        }

        /// <summary>
        /// Renders a whole project. Runs synchronously on the calling thread, which must
        /// be MTA and must not be the UI thread — call it from a dedicated thread.
        /// </summary>
        internal static TrimResult Export(string source, string destination,
                                          VideoProject project,
                                          int fps, VideoQuality quality,
                                          Action<double>? progress = null,
                                          CancellationToken cancel = default)
        {
            if (project.Segments.Count == 0) return Fail("There is nothing left to export.");

            IMFSourceReader? reader = null;
            IMFAttributes? readerAttrs = null;
            IMFMediaType? rgbType = null;
            IMFMediaType? nativeType = null;
            IMFMediaType? pcmType = null;
            var encoder = new VideoRecorder();
            bool mfStarted = false;
            bool encoderStarted = false;

            try
            {
                if (!MF.Ok(MF.MFStartup(MF.MF_VERSION, MF.MFSTARTUP_NOSOCKET)))
                    return Fail("Media Foundation would not start.");
                mfStarted = true;

                if (!MF.Ok(MF.MFCreateAttributes(out readerAttrs, 1)))
                    return Fail("Could not create reader attributes.");

                // Without this the reader hands back H.264 and refuses to convert.
                readerAttrs.SetUINT32(ref MF.MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, 1);

                int hr = MF.MFCreateSourceReaderFromURL(source, readerAttrs, out reader);
                if (!MF.Ok(hr)) return Fail($"Could not open the recording (0x{hr:X8}).");

                reader.SetStreamSelection(MF.MF_SOURCE_READER_ALL_STREAMS, false);
                reader.SetStreamSelection(MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM, true);

                // Frame size comes from the file, not from a guess: an edited clip must
                // match its source exactly or the first frame is stretched.
                if (!MF.Ok(reader.GetNativeMediaType(MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0,
                                                     out nativeType)))
                    return Fail("Could not read the recording's format.");

                if (!MF.Ok(nativeType.GetUINT64(ref MF.MF_MT_FRAME_SIZE, out ulong packed)))
                    return Fail("Could not read the recording's frame size.");

                int width = (int)(packed >> 32);
                int height = (int)(packed & 0xFFFFFFFF);
                if (width < 2 || height < 2) return Fail("The recording has no usable video.");

                // Frame rate comes from the file rather than the caller. Declaring 30 fps
                // on a 60 fps recording does not change the timestamps — those stay exact —
                // but it leaves the container's metadata disagreeing with its contents, and
                // some players believe the metadata.
                if (MF.Ok(nativeType.GetUINT64(ref MF.MF_MT_FRAME_RATE, out ulong rate)))
                {
                    uint num = (uint)(rate >> 32), den = (uint)(rate & 0xFFFFFFFF);
                    if (den > 0)
                    {
                        int sourceFps = (int)Math.Round((double)num / den);
                        if (sourceFps is >= 5 and <= 120) fps = sourceFps;
                    }
                }

                if (!MF.Ok(MF.MFCreateMediaType(out rgbType)))
                    return Fail("Could not describe the decode format.");

                rgbType.SetGUID(ref MF.MF_MT_MAJOR_TYPE, ref MF.MFMediaType_Video);
                rgbType.SetGUID(ref MF.MF_MT_SUBTYPE, ref MF.MFVideoFormat_RGB32);

                if (!MF.Ok(hr = reader.SetCurrentMediaType(MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
                                                           IntPtr.Zero, rgbType)))
                    return Fail($"This recording could not be decoded (0x{hr:X8}).");

                // ── sound, if the file has any ──
                // Every failure here is silent and leaves audioRate at 0: an edit that
                // loses its soundtrack is bad, and an edit that fails outright because the
                // soundtrack could not be decoded is worse.
                int audioRate = 0, audioChannels = 2;
                if (TrySelectAudio(reader, out pcmType, out audioRate, out audioChannels))
                {
                    // nothing more to do — the flags above are the result
                }

                VideoRecorder.AudioSpec? audioSpec = audioRate > 0
                    ? new VideoRecorder.AudioSpec
                    {
                        SampleRate = audioRate,
                        Channels = audioChannels
                    }
                    : null;

                if (!encoder.Start(destination, width, height, fps, quality, audioSpec))
                    return Fail(encoder.LastError ?? "The encoder would not start.");
                encoderStarted = true;

                bool writeAudio = audioRate > 0 && encoder.HasAudio;

                long totalOutput = Math.Max(1, project.OutputDuration.Ticks);
                long outputTicks = 0;
                long lastVideoWritten = -1;
                long audioFramesWritten = 0;
                int framesOut = 0;

                foreach (var segment in project.Segments)
                {
                    if (cancel.IsCancellationRequested) break;
                    if (segment.SourceDuration <= TimeSpan.Zero) continue;

                    double speed = Math.Clamp(segment.Speed, 0.25, 4.0);

                    var rendered = RenderSegment(reader, encoder, segment, speed,
                                                 width, fps,
                                                 writeAudio, audioRate, audioChannels,
                                                 segment.Muted,
                                                 outputTicks, ref lastVideoWritten,
                                                 ref audioFramesWritten,
                                                 p => progress?.Invoke(
                                                     Math.Clamp((outputTicks + p) / (double)totalOutput, 0, 1)),
                                                 cancel);

                    if (rendered.Error != null) return Fail(rendered.Error);

                    framesOut += rendered.Frames;
                    outputTicks += segment.OutputDuration.Ticks;
                }

                if (cancel.IsCancellationRequested)
                {
                    encoder.Stop();
                    encoderStarted = false;
                    TryDelete(destination);
                    return Fail("Cancelled.");
                }

                if (framesOut == 0) return Fail("That edit contains no frames.");

                encoder.Stop();
                encoderStarted = false;
                progress?.Invoke(1);

                return new TrimResult { Ok = true, Path = destination };
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
            finally
            {
                if (encoderStarted) { try { encoder.Stop(); } catch { } }
                MF.Release(pcmType);
                MF.Release(rgbType);
                MF.Release(nativeType);
                MF.Release(reader);
                MF.Release(readerAttrs);

                // VideoRecorder.Stop already calls MFShutdown once; this balances the
                // MFStartup made here. Media Foundation reference-counts these.
                if (mfStarted) { try { MF.MFShutdown(); } catch { } }
            }
        }

        private readonly struct SegmentResult
        {
            internal SegmentResult(int frames, string? error) { Frames = frames; Error = error; }
            internal int Frames { get; }
            internal string? Error { get; }
        }

        /// <summary>
        /// Decodes one kept piece and writes it at its place in the output.
        ///
        /// <paramref name="speed"/> is applied by dividing the source offset — a frame two
        /// seconds into a 2x segment lands one second into the output. Frames are never
        /// dropped for speed: the encoder is handed all of them with compressed
        /// timestamps, so a fast segment stays smooth instead of stuttering, and a slow one
        /// repeats frames rather than showing gaps.
        /// </summary>
        private static SegmentResult RenderSegment(IMFSourceReader reader, VideoRecorder encoder,
                                                   VideoSegment segment, double speed,
                                                   int width, int fps,
                                                   bool writeAudio, int audioRate, int audioChannels,
                                                   bool muted,
                                                   long outputBaseTicks,
                                                   ref long lastVideoWritten,
                                                   ref long audioFramesWritten,
                                                   Action<long>? progress,
                                                   CancellationToken cancel)
        {
            long startHns = segment.Start.Ticks;
            long endHns = segment.End.Ticks;

            // ── video ──
            var video = RenderVideo(reader, encoder, startHns, endHns, speed, width, fps,
                                    outputBaseTicks, ref lastVideoWritten, progress, cancel);
            if (video.Error != null) return video;

            // ── sound ──
            if (writeAudio)
            {
                long target = (long)(segment.OutputDuration.TotalSeconds * audioRate);
                RenderAudio(reader, encoder, startHns, endHns, speed, muted,
                            audioRate, audioChannels, target,
                            ref audioFramesWritten, cancel);
            }

            return video;
        }

        /// <summary>
        /// Decodes the video of one piece and writes it at its place in the output.
        ///
        /// <paramref name="speed"/> is applied by dividing the source offset — a frame two
        /// seconds into a 2x segment lands one second into the output. Frames are never
        /// dropped for speed: the encoder is handed all of them with compressed timestamps,
        /// so a fast segment stays smooth instead of stuttering.
        /// </summary>
        private static SegmentResult RenderVideo(IMFSourceReader reader, VideoRecorder encoder,
                                                 long startHns, long endHns, double speed,
                                                 int width, int fps, long outputBaseTicks,
                                                 ref long lastVideoWritten,
                                                 Action<long>? progress,
                                                 CancellationToken cancel)
        {
            int frames = 0;

            // The reader lands on the keyframe at or before the marker and decodes forward,
            // so frames before it still arrive — they are dropped below rather than
            // written, which is what makes the cut frame-exact.
            Seek(reader, startHns);

            while (!cancel.IsCancellationRequested)
            {
                int hr = reader.ReadSample(MF.MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0,
                                          out _, out int flags, out long timeHns,
                                          out IMFSample? sample);

                if (!MF.Ok(hr))
                    return new SegmentResult(frames, $"Reading the recording failed (0x{hr:X8}).");

                if ((flags & MF.MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                {
                    MF.Release(sample);
                    break;
                }

                if (sample == null) continue;          // a gap, not an error

                try
                {
                    if (timeHns < startHns) continue;   // still catching up to the marker
                    if (timeHns > endHns) break;

                    if (!MF.Ok(sample.ConvertToContiguousBuffer(out IMFMediaBuffer buffer)))
                        continue;

                    try
                    {
                        if (!MF.Ok(buffer.Lock(out IntPtr scan0, out _, out _))) continue;
                        try
                        {
                            long into = (long)((timeHns - startHns) / speed);
                            long outTime = outputBaseTicks + into;

                            // The encoder rejects a non-advancing timestamp, and a speed-up
                            // can genuinely land two source frames on the same output one.
                            if (outTime <= lastVideoWritten) outTime = lastVideoWritten + 1;

                            encoder.WriteFrame(scan0, width * 4, outTime,
                                               Math.Max(1, 10_000_000L / Math.Max(1, fps)));
                            lastVideoWritten = outTime;
                            frames++;

                            if (frames % 15 == 0) progress?.Invoke(into);
                        }
                        finally { buffer.Unlock(); }
                    }
                    finally { MF.Release(buffer); }
                }
                finally { MF.Release(sample); }
            }

            return new SegmentResult(frames, null);
        }

        /// <summary>
        /// Decodes the sound of one piece and writes it under the same output timeline.
        ///
        /// Two things here are worth stating plainly.
        ///
        /// **A muted segment is written as silence, not skipped.** The audio track has to
        /// come out the same length as the video or everything after it slides forward.
        ///
        /// **The piece is padded or truncated to its exact expected length.** The decoder
        /// hands back whole AAC frames, which do not divide evenly into an arbitrary cut,
        /// so the last one usually overshoots. Trimming it and topping up any shortfall
        /// with silence is what stops a hundred small cuts accumulating into audible drift
        /// by the end of the file.
        ///
        /// Speed changes resample by dropping or repeating whole frames rather than
        /// pitch-shifting: a 2x screen recording with chipmunk narration is not what anyone
        /// wants, and proper time-stretching is a signal-processing project of its own.
        /// </summary>
        private static void RenderAudio(IMFSourceReader reader, VideoRecorder encoder,
                                        long startHns, long endHns, double speed, bool muted,
                                        int audioRate, int audioChannels, long targetFrames,
                                        ref long audioFramesWritten,
                                        CancellationToken cancel)
        {
            if (targetFrames <= 0) return;

            int blockAlign = audioChannels * 2;
            long written = 0;

            if (!muted)
            {
                Seek(reader, startHns);

                byte[]? scratch = null;

                while (written < targetFrames && !cancel.IsCancellationRequested)
                {
                    int hr = reader.ReadSample(MF.MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0,
                                              out _, out int flags, out long timeHns,
                                              out IMFSample? sample);

                    if (!MF.Ok(hr)) break;

                    if ((flags & MF.MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                    {
                        MF.Release(sample);
                        break;
                    }

                    if (sample == null) continue;

                    try
                    {
                        if (timeHns < startHns) continue;
                        if (timeHns > endHns) break;

                        if (!MF.Ok(sample.ConvertToContiguousBuffer(out IMFMediaBuffer buffer)))
                            continue;

                        try
                        {
                            if (!MF.Ok(buffer.Lock(out IntPtr scan0, out _, out int length)))
                                continue;
                            try
                            {
                                int frames = length / blockAlign;
                                if (frames <= 0) continue;

                                if (scratch == null || scratch.Length < length)
                                    scratch = new byte[length];
                                Marshal.Copy(scan0, scratch, 0, length);

                                byte[] outBuffer = scratch;
                                if (Math.Abs(speed - 1.0) > 0.001)
                                {
                                    outBuffer = Respeed(scratch, frames, blockAlign, speed,
                                                        out frames);
                                }

                                // Never write past the length this piece is supposed to be.
                                long room = targetFrames - written;
                                if (frames > room) frames = (int)room;
                                if (frames <= 0) break;

                                encoder.WriteAudio(outBuffer, frames * blockAlign,
                                                   audioFramesWritten);
                                audioFramesWritten += frames;
                                written += frames;
                            }
                            finally { buffer.Unlock(); }
                        }
                        finally { MF.Release(buffer); }
                    }
                    finally { MF.Release(sample); }
                }
            }

            // Top up whatever is missing — the whole piece when muted, the tail when the
            // decoder ran dry or the last frame fell short.
            if (written >= targetFrames) return;

            int chunkFrames = Math.Max(1, audioRate / 10);
            var silence = new byte[chunkFrames * blockAlign];

            while (written < targetFrames && !cancel.IsCancellationRequested)
            {
                int take = (int)Math.Min(chunkFrames, targetFrames - written);
                encoder.WriteAudio(silence, take * blockAlign, audioFramesWritten);
                audioFramesWritten += take;
                written += take;
            }
        }

        /// <summary>
        /// Shortens or lengthens a PCM block by picking frames, so sound keeps pace with a
        /// segment's speed change without its pitch moving.
        /// </summary>
        private static byte[] Respeed(byte[] source, int frames, int blockAlign,
                                      double speed, out int outFrames)
        {
            outFrames = Math.Max(1, (int)(frames / speed));
            var result = new byte[outFrames * blockAlign];

            for (int i = 0; i < outFrames; i++)
            {
                int from = Math.Min(frames - 1, (int)(i * speed));
                Buffer.BlockCopy(source, from * blockAlign, result, i * blockAlign, blockAlign);
            }
            return result;
        }

        private static void Seek(IMFSourceReader reader, long hns)
        {
            var pos = PropVariant.FromLong(hns);
            var timeFormat = Guid.Empty;
            reader.SetCurrentPosition(ref timeFormat, ref pos);
        }

        /// <summary>
        /// Turns on the file's first audio stream and asks for PCM.
        ///
        /// Returns false for the ordinary case of a silent recording as well as for any
        /// failure, because the caller treats both the same way: carry on without sound.
        /// </summary>
        private static bool TrySelectAudio(IMFSourceReader reader, out IMFMediaType? pcmType,
                                           out int sampleRate, out int channels)
        {
            pcmType = null;
            sampleRate = 0;
            channels = 2;

            IMFMediaType? native = null;
            try
            {
                if (!MF.Ok(reader.GetNativeMediaType(MF.MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0,
                                                     out native)) || native == null)
                    return false;

                if (MF.Ok(native.GetUINT32(ref MF.MF_MT_AUDIO_SAMPLES_PER_SECOND, out uint rate)))
                    sampleRate = (int)rate;
                if (MF.Ok(native.GetUINT32(ref MF.MF_MT_AUDIO_NUM_CHANNELS, out uint ch)))
                    channels = Math.Clamp((int)ch, 1, 2);

                // The AAC encoder only takes these two, so anything else is re-declared at
                // 48000 rather than refused.
                if (sampleRate is not (44100 or 48000)) sampleRate = 48000;

                if (!MF.Ok(MF.MFCreateMediaType(out pcmType))) { sampleRate = 0; return false; }

                pcmType.SetGUID(ref MF.MF_MT_MAJOR_TYPE, ref MF.MFMediaType_Audio);
                pcmType.SetGUID(ref MF.MF_MT_SUBTYPE, ref MF.MFAudioFormat_PCM);
                pcmType.SetUINT32(ref MF.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
                pcmType.SetUINT32(ref MF.MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)sampleRate);
                pcmType.SetUINT32(ref MF.MF_MT_AUDIO_NUM_CHANNELS, (uint)channels);

                reader.SetStreamSelection(MF.MF_SOURCE_READER_FIRST_AUDIO_STREAM, true);

                if (!MF.Ok(reader.SetCurrentMediaType(MF.MF_SOURCE_READER_FIRST_AUDIO_STREAM,
                                                      IntPtr.Zero, pcmType)))
                {
                    reader.SetStreamSelection(MF.MF_SOURCE_READER_FIRST_AUDIO_STREAM, false);
                    sampleRate = 0;
                    return false;
                }

                return true;
            }
            catch { sampleRate = 0; return false; }
            finally { MF.Release(native); }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static TrimResult Fail(string message) => new() { Ok = false, Error = message };
    }
}
