using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MP3Player.Audio
{
    /// <summary>
    /// Universal audio engine using ffmpeg for decoding (supports opus, mp3, flac, ogg, aac, wav, m4a)
    /// and PulseAudio (libpulse-simple) for PCM output.
    /// No libmpg123 dependency — works with any format ffmpeg supports.
    /// </summary>
    public class AudioEngine : IDisposable
    {
        private const string FfmpegBin  = "/usr/bin/ffmpeg";
        private const string FfprobeBin = "/usr/bin/ffprobe";
        private Process? _ffmpeg;

        // ── libpulse-simple P/Invoke ──────────────────────────────────────────
        private const string PulseLib = "libpulse-simple.so.0";

        [StructLayout(LayoutKind.Sequential)]
        private struct PaSampleSpec
        {
            public uint format;
            public uint rate;
            public byte channels;
        }

        [DllImport(PulseLib)] private static extern IntPtr pa_simple_new(
            string? server, string name, int dir,
            string? dev, string streamName,
            ref PaSampleSpec ss, IntPtr map,
            IntPtr attr, out int error);

        [DllImport(PulseLib)] private static extern int pa_simple_write(
            IntPtr s, byte[] data, IntPtr bytes, out int error);

        [DllImport(PulseLib)] private static extern int pa_simple_drain(IntPtr s, out int error);
        [DllImport(PulseLib)] private static extern void pa_simple_free(IntPtr s);

        private const int PA_STREAM_PLAYBACK = 1;
        private const uint PA_SAMPLE_S16LE   = 3;

        // ── State ─────────────────────────────────────────────────────────────
        private IntPtr _pa = IntPtr.Zero;
        private CancellationTokenSource? _cts;
        private Task? _playTask;

        private double _totalSeconds;
        private long   _bytesWritten;
        private uint   _sampleRate = 44100;
        private int    _channels   = 2;
        private double _volume     = 1.0;
        private double _speed      = 1.0;
        private string? _currentFile;

        private volatile bool _paused;
        private volatile bool _seekRequested;
        private double _seekTarget;

        public bool IsPlaying => _playTask != null && !_playTask.IsCompleted;
        public bool IsPaused  => _paused;

        public double Volume
        {
            get => _volume;
            set => _volume = Math.Clamp(value, 0.0, 2.0);
        }

        public double Speed
        {
            get => _speed;
            set => _speed = Math.Clamp(value, 0.25, 3.0);
        }

        public double TotalSeconds => _totalSeconds;

        public double PositionSeconds
        {
            get
            {
                if (_sampleRate == 0 || _channels == 0) return 0;
                double bytesPerSecond = _sampleRate * _channels * 2.0;
                return _bytesWritten / bytesPerSecond;
            }
        }

        public event EventHandler? TrackEnded;

        // ── Play ──────────────────────────────────────────────────────────────
        public void Play(string filePath)
        {
            Stop();
            _currentFile   = filePath;
            _bytesWritten  = 0;
            _paused        = false;
            _seekRequested = false;

            ProbeFile(filePath);
            StartFfmpeg(filePath, 0);
            OpenPulse();

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _playTask = Task.Run(() => DecodeLoop(token), token);
        }

        private void ProbeFile(string filePath)
        {
            _sampleRate   = 44100;
            _channels     = 2;
            _totalSeconds = 0;

            try
            {
                var psi = new ProcessStartInfo(FfprobeBin,
                    $"-v quiet -print_format json -show_format -show_streams \"{filePath}\"")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow  = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) return;

                var json = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(3000);

                var srMatch = System.Text.RegularExpressions.Regex.Match(json,
                    @"""sample_rate"":\s*""(\d+)""");
                if (srMatch.Success && uint.TryParse(srMatch.Groups[1].Value, out var sr))
                    _sampleRate = sr;

                var chMatch = System.Text.RegularExpressions.Regex.Match(json,
                    @"""channels"":\s*(\d+)");
                if (chMatch.Success && int.TryParse(chMatch.Groups[1].Value, out var ch))
                    _channels = ch;

                var durMatch = System.Text.RegularExpressions.Regex.Match(json,
                    @"""duration"":\s*""([\d\.]+)""");
                if (durMatch.Success && double.TryParse(durMatch.Groups[1].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var dur))
                    _totalSeconds = dur;
            }
            catch { /* fallback to defaults */ }
        }

        private void StartFfmpeg(string filePath, double seekTo)
        {
            if (_ffmpeg != null && !_ffmpeg.HasExited)
            {
                try { _ffmpeg.Kill(true); } catch { }
            }
            _ffmpeg?.Dispose();

            var seekArg = seekTo > 0 ? $"-ss {seekTo:F2}" : "";
            string filterArg;
            if (Math.Abs(_speed - 1.0) > 0.01)
                filterArg = $"-af atempo={_speed:F2}";
            else
                filterArg = "";

            var filterPart = filterArg.Length > 0 ? filterArg : "";
            var args = $"{seekArg} -i \"{filePath}\" {filterPart} -f s16le -acodec pcm_s16le -ar {_sampleRate} -ac {_channels} -v quiet pipe:1";

            var psi = new ProcessStartInfo(FfmpegBin, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute = false,
                CreateNoWindow  = true
            };

            _ffmpeg = new Process { StartInfo = psi };
            _ffmpeg.Start();
            _ffmpeg.BeginErrorReadLine();
        }

        private void OpenPulse()
        {
            if (_pa != IntPtr.Zero)
            {
                pa_simple_free(_pa);
                _pa = IntPtr.Zero;
            }

            var spec = new PaSampleSpec
            {
                format   = PA_SAMPLE_S16LE,
                rate     = _sampleRate,
                channels = (byte)_channels
            };
            _pa = pa_simple_new(null, "LMP", PA_STREAM_PLAYBACK,
                                null, "Music", ref spec,
                                IntPtr.Zero, IntPtr.Zero, out int paErr);
            if (_pa == IntPtr.Zero)
                throw new InvalidOperationException($"PulseAudio connection error: {paErr}");
        }

        private void DecodeLoop(CancellationToken token)
        {
            const int BufSize = 4096 * 4;
            var buf = new byte[BufSize];

            while (!token.IsCancellationRequested)
            {
                if (_paused) { Thread.Sleep(20); continue; }

                if (_seekRequested)
                {
                    _seekRequested = false;
                    StartFfmpeg(_currentFile!, _seekTarget);
                    double bytesPerSecond = _sampleRate * _channels * 2.0;
                    _bytesWritten = (long)(_seekTarget * bytesPerSecond);
                    continue;
                }

                if (_ffmpeg == null || _ffmpeg.HasExited) break;

                int read;
                try { read = _ffmpeg.StandardOutput.BaseStream.Read(buf, 0, BufSize); }
                catch { break; }

                if (read <= 0) break;

                // ── Apply volume in software (real-time, no ffmpeg restart) ──
                ApplyVolume(buf, read);

                pa_simple_write(_pa, buf, (IntPtr)read, out _);
                _bytesWritten += read;
            }

            if (!token.IsCancellationRequested)
            {
                if (_pa != IntPtr.Zero) pa_simple_drain(_pa, out _);
                TrackEnded?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Applies volume scaling to S16LE PCM data in-place.
        /// Reads _volume directly so slider changes take effect immediately.
        /// </summary>
        private void ApplyVolume(byte[] buf, int count)
        {
            double vol = _volume;
            // Skip processing if volume is exactly 1.0 (no change needed)
            if (Math.Abs(vol - 1.0) < 0.001) return;

            // S16LE: each sample is 2 bytes, little-endian
            for (int i = 0; i + 1 < count; i += 2)
            {
                short sample = (short)(buf[i] | (buf[i + 1] << 8));
                int scaled = (int)(sample * vol);
                // Clamp to Int16 range to avoid distortion
                if (scaled > 32767) scaled = 32767;
                else if (scaled < -32768) scaled = -32768;
                buf[i]     = (byte)(scaled & 0xFF);
                buf[i + 1] = (byte)((scaled >> 8) & 0xFF);
            }
        }

        public void Pause()  => _paused = true;
        public void Resume() => _paused = false;
        public void TogglePause() { if (_paused) Resume(); else Pause(); }

        public void ApplySpeed()
        {
            if (_currentFile == null || !IsPlaying) return;
            bool wasPaused = _paused;
            _paused = true;
            Thread.Sleep(50);
            var currentPos = PositionSeconds;
            StartFfmpeg(_currentFile, currentPos);
            double bytesPerSecond = _sampleRate * _channels * 2.0;
            _bytesWritten = (long)(currentPos * bytesPerSecond);
            _paused = wasPaused;
        }

        public void SeekTo(double seconds)
        {
            if (_currentFile == null) return;
            _seekTarget    = Math.Max(0, Math.Min(seconds, _totalSeconds));
            _seekRequested = true;
        }

        public void Stop()
        {
            _cts?.Cancel();
            try { _playTask?.Wait(500); } catch { /* ignore */ }
            _cts?.Dispose();
            _cts = null;
            _playTask = null;
            _paused = false;

            if (_ffmpeg != null && !_ffmpeg.HasExited)
            {
                try { _ffmpeg.Kill(true); } catch { }
            }
            _ffmpeg?.Dispose();
            _ffmpeg = null;

            if (_pa != IntPtr.Zero) { pa_simple_free(_pa); _pa = IntPtr.Zero; }
        }

        public void Dispose()
        {
            Stop();
            GC.SuppressFinalize(this);
        }
    }
}
