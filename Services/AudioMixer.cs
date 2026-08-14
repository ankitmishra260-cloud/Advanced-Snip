using System;

namespace AdvancedSnip.Services
{
    /// <summary>What the recorder was asked to capture, and at what level.</summary>
    internal sealed class AudioRequest
    {
        internal bool System { get; init; }
        internal bool Microphone { get; init; }
        internal string? SystemDeviceId { get; init; }
        internal string? MicrophoneDeviceId { get; init; }

        /// <summary>1.0 is unity. Applied before mixing, so the two can be balanced.</summary>
        internal float SystemGain { get; init; } = 1.0f;
        internal float MicrophoneGain { get; init; } = 1.0f;

        internal bool Any => System || Microphone;
    }

    /// <summary>
    /// Puts the two capture streams onto one timeline and hands out 16-bit PCM.
    ///
    /// **The recording's stopwatch is the clock, not either device.** <see cref="Fill"/>
    /// is asked for the audio up to a given moment and produces exactly that much, taking
    /// what the devices have supplied and padding the rest with silence. Nothing is ever
    /// stretched or dropped to make the numbers work, so the audio position in the file is
    /// always the video position: a two-hour recording ends as synchronised as it started.
    ///
    /// The alternative — writing whatever arrives, when it arrives — is simpler and drifts,
    /// because a sound card's idea of 48000 samples per second is its own crystal's and
    /// nobody else's.
    ///
    /// **Silence is not a failure.** A loopback device reports nothing at all when no
    /// application is playing, and a muted microphone reports silence flags. Both are
    /// normal, and both come out of here as digital silence at the right length.
    /// </summary>
    internal sealed class AudioMixer : IDisposable
    {
        /// <summary>
        /// Stereo throughout. Mono would halve the bitrate but loses which side of a demo
        /// a sound came from, and the AAC encoder charges almost nothing for the second
        /// channel on speech and UI sounds.
        /// </summary>
        internal const int Channels = 2;

        private WasapiCapture? _system;
        private WasapiCapture? _mic;
        private AudioRing? _systemRing;
        private AudioRing? _micRing;

        private float[] _a = Array.Empty<float>();
        private float[] _b = Array.Empty<float>();
        private byte[] _pcm = Array.Empty<byte>();

        private readonly float _systemGain;
        private readonly float _micGain;

        private long _framesWritten;
        private float _peak;

        internal int SampleRate { get; private set; } = 48000;

        /// <summary>True once at least one device is running.</summary>
        internal bool Active => _system != null || _mic != null;

        /// <summary>Set when a requested source could not be opened. Never fatal.</summary>
        internal string? Warning { get; private set; }

        /// <summary>
        /// Silences the output without stopping capture.
        ///
        /// The devices keep running and the frame counter keeps advancing, so a muted
        /// stretch comes out as silence of exactly the right length. Stopping capture
        /// instead would leave a hole that everything after it has to slide across.
        /// </summary>
        internal bool Muted { get; set; }

        /// <summary>Loudest sample since the last read, 0..1, for the HUD's level meter.</summary>
        internal float ReadPeak()
        {
            float p = _peak;
            _peak = 0;
            return p;
        }

        internal AudioMixer(AudioRequest request)
        {
            _systemGain = Math.Clamp(request.SystemGain, 0f, 4f);
            _micGain = Math.Clamp(request.MicrophoneGain, 0f, 4f);
        }

        /// <summary>
        /// Opens the requested devices. Returns false only when nothing could be opened at
        /// all, and even then the caller records video and carries on.
        /// </summary>
        internal bool Start(AudioRequest request)
        {
            if (!request.Any) return false;

            // Master rate comes from the system device when there is one. Recording what
            // the machine is playing at the rate it is playing it means the higher-quality
            // source is never resampled; the microphone, which is speech, is the one that
            // can afford interpolation.
            int rate = 48000;

            WasapiCapture? system = null;
            WasapiCapture? mic = null;

            try
            {
                if (request.System)
                {
                    system = new WasapiCapture(Channels);
                    if (system.Open(request.SystemDeviceId, loopback: true))
                    {
                        // AAC accepts 44100 and 48000. Anything else — a 96 kHz interface,
                        // say — is resampled to 48000 rather than refused by the encoder.
                        int deviceRate = system.DeviceRate;
                        rate = deviceRate is 44100 or 48000 ? deviceRate : 48000;
                    }
                    else
                    {
                        Warning = system.Error ?? "System audio could not be recorded.";
                        system.Dispose();
                        system = null;
                    }
                }

                // Two seconds of ring per source. Generous, because the cost is about a
                // megabyte and the failure it prevents is a hole in the audio.
                int ringSamples = rate * Channels * 2;

                if (system != null)
                {
                    _systemRing = new AudioRing(ringSamples);
                    system.SetTarget(_systemRing, rate);
                    if (!system.Start(loopback: true))
                    {
                        Warning = system.Error ?? "System audio could not be recorded.";
                        system.Dispose();
                        system = null;
                        _systemRing = null;
                    }
                }

                if (request.Microphone)
                {
                    mic = new WasapiCapture(Channels);
                    if (mic.Open(request.MicrophoneDeviceId, loopback: false))
                    {
                        _micRing = new AudioRing(ringSamples);
                        mic.SetTarget(_micRing, rate);
                    }

                    if (_micRing == null || !mic.Start(loopback: false))
                    {
                        Warning ??= mic.Error ?? "The microphone could not be recorded.";
                        mic.Dispose();
                        mic = null;
                        _micRing = null;
                    }
                }

                if (system == null && mic == null)
                {
                    Warning ??= "No audio source could be opened.";
                    return false;
                }

                SampleRate = rate;
                _system = system;
                _mic = mic;
                _framesWritten = 0;
                return true;
            }
            catch (Exception ex)
            {
                Warning ??= ex.Message;
                try { system?.Dispose(); } catch { }
                try { mic?.Dispose(); } catch { }
                return false;
            }
        }

        /// <summary>
        /// Produces the audio that should exist by <paramref name="elapsedSeconds"/> and
        /// has not been produced yet, as interleaved 16-bit PCM.
        ///
        /// Returns null when there is nothing to write. The byte array is reused between
        /// calls — copy it if you need to keep it, which the encoder does not.
        /// </summary>
        internal byte[]? Fill(double elapsedSeconds, out int byteCount, out long startFrame)
        {
            byteCount = 0;
            startFrame = _framesWritten;

            if (!Active) return null;

            long want = (long)(elapsedSeconds * SampleRate);
            int frames = (int)(want - _framesWritten);

            // A quarter of a second at a time at most. A single enormous write after a
            // stall would be one AAC sample spanning seconds, which some players show as
            // a gap.
            int cap = SampleRate / 4;
            if (frames > cap) frames = cap;
            if (frames <= 0) return null;

            int samples = frames * Channels;
            if (_a.Length < samples) _a = new float[samples];
            if (_b.Length < samples) _b = new float[samples];
            if (_pcm.Length < samples * 2) _pcm = new byte[samples * 2];

            Array.Clear(_a, 0, samples);
            Array.Clear(_b, 0, samples);

            if (_systemRing != null)
            {
                _systemRing.Read(_a, 0, samples);
                // Never let a device that runs fast build up a lead. Half a second of
                // backlog is already more delay than anyone wants between the two tracks.
                _systemRing.TrimTo(SampleRate * Channels / 2);
            }

            if (_micRing != null)
            {
                _micRing.Read(_b, 0, samples);
                _micRing.TrimTo(SampleRate * Channels / 2);
            }

            // Anything a device did not supply stays zero — that is the silence padding,
            // and it is what keeps the timeline honest.
            //
            // Muting happens here rather than upstream: the rings were still drained above,
            // so nothing backs up, and the output is real silence rather than a gap.
            float systemGain = Muted ? 0f : _systemGain;
            float micGain = Muted ? 0f : _micGain;

            float peak = 0;
            for (int i = 0; i < samples; i++)
            {
                float v = _a[i] * systemGain + _b[i] * micGain;

                // Clamp rather than normalise. Normalising would need the whole recording
                // in advance, and a limiter that pumps is worse than the occasional clipped
                // peak on a screen recording.
                if (v > 1f) v = 1f;
                else if (v < -1f) v = -1f;

                float mag = v < 0 ? -v : v;
                if (mag > peak) peak = mag;

                short s = (short)(v * 32767f);
                _pcm[i * 2] = (byte)(s & 0xFF);
                _pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }

            if (peak > _peak) _peak = peak;

            _framesWritten += frames;
            byteCount = samples * 2;
            return _pcm;
        }

        /// <summary>
        /// Drops everything captured so far and re-bases the timeline. Called on resume:
        /// the sound that arrived while the recording was paused belongs to a moment that
        /// is not in the video, and the frames counter has to match the video's own paused
        /// clock or everything after the pause is offset.
        /// </summary>
        internal void Resync(double elapsedSeconds)
        {
            _systemRing?.Clear();
            _micRing?.Clear();
            _framesWritten = (long)(elapsedSeconds * SampleRate);
        }

        internal void Stop()
        {
            try { _system?.Stop(); } catch { }
            try { _mic?.Stop(); } catch { }
        }

        public void Dispose()
        {
            Stop();
            try { _system?.Dispose(); } catch { }
            try { _mic?.Dispose(); } catch { }
            _system = null;
            _mic = null;
            _systemRing = null;
            _micRing = null;
        }
    }
}
