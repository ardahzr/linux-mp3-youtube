using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MP3Player.Audio
{
    /// <summary>
    /// VLC gerektirmeyen, tamamen Linux dahili ses yığını kullanan motor:
    ///   libmpg123  → MP3 decode (Arch: pacman -S mpg123)
    ///   libpulse-simple → PulseAudio / PipeWire PCM çıkışı
    /// </summary>
    public class AudioEngine : IDisposable
    {
        // ── libmpg123 P/Invoke ────────────────────────────────────────────────
        private const string Mpg123Lib = "libmpg123.so.0";

        [DllImport(Mpg123Lib)] private static extern int    mpg123_init();
        [DllImport(Mpg123Lib)] private static extern IntPtr mpg123_new(IntPtr decoder, out int error);
        [DllImport(Mpg123Lib)] private static extern int    mpg123_open(IntPtr mh, string path);
        [DllImport(Mpg123Lib)] private static extern int    mpg123_getformat(IntPtr mh, out long rate, out int channels, out int encoding);
        [DllImport(Mpg123Lib)] private static extern int    mpg123_read(IntPtr mh, byte[] buf, IntPtr size, out IntPtr done);
        [DllImport(Mpg123Lib)] private static extern int    mpg123_seek(IntPtr mh, long sampleoff, int whence);
        [DllImport(Mpg123Lib)] private static extern long   mpg123_tell(IntPtr mh);
        [DllImport(Mpg123Lib)] private static extern long   mpg123_length(IntPtr mh);
        [DllImport(Mpg123Lib)] private static extern void   mpg123_close(IntPtr mh);
        [DllImport(Mpg123Lib)] private static extern void   mpg123_delete(IntPtr mh);
        [DllImport(Mpg123Lib)] private static extern void   mpg123_exit();
        [DllImport(Mpg123Lib)] private static extern int    mpg123_volume(IntPtr mh, double vol);

        private const int MPG123_OK  = 0;
        private const int MPG123_DONE = -12;

        // ── libpulse-simple P/Invoke ──────────────────────────────────────────
        private const string PulseLib = "libpulse-simple.so.0";

        [StructLayout(LayoutKind.Sequential)]
        private struct PaSampleSpec
        {
            public uint   format;   // PA_SAMPLE_S16LE = 3
            public uint   rate;
            public byte   channels;
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

        // ── Durum ─────────────────────────────────────────────────────────────
        private IntPtr _mh   = IntPtr.Zero;
        private IntPtr _pa   = IntPtr.Zero;
        private CancellationTokenSource? _cts;
        private Task?   _playTask;

        private long  _totalSamples = 0;
        private long  _rate         = 44100;
        private int   _channels     = 2;
        private double _volume      = 1.0;
        private double _speed       = 1.0;

        private volatile bool _paused = false;
        private readonly SemaphoreSlim _pauseGate = new(1, 1);

        public bool IsPlaying => _playTask != null && !_playTask.IsCompleted;
        public bool IsPaused  => _paused;

        /// <summary>0.0 – 2.0 arası ses seviyesi</summary>
        public double Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0.0, 2.0);
                if (_mh != IntPtr.Zero)
                    mpg123_volume(_mh, _volume);
            }
        }

        /// <summary>Playback speed multiplier (0.5 – 2.0). Requires restart of current track to take effect fully.</summary>
        public double Speed
        {
            get => _speed;
            set => _speed = Math.Clamp(value, 0.25, 3.0);
        }

        /// <summary>Toplam süre (saniye). Dosya açılmadan önce 0.</summary>
        public double TotalSeconds
        {
            get
            {
                if (_mh == IntPtr.Zero || _rate == 0) return 0;
                return (double)_totalSamples / _rate;
            }
        }

        /// <summary>Geçen süre (saniye).</summary>
        public double PositionSeconds
        {
            get
            {
                if (_mh == IntPtr.Zero || _rate == 0) return 0;
                var pos = mpg123_tell(_mh);
                return pos < 0 ? 0 : (double)pos / _rate;
            }
        }

        /// <summary>Çalma tamamlandığında fırlatılır (UI thread değil).</summary>
        public event EventHandler? TrackEnded;

        // ── Başlangıç ─────────────────────────────────────────────────────────
        static AudioEngine() => mpg123_init();

        // ── Oynat ─────────────────────────────────────────────────────────────
        public void Play(string filePath)
        {
            Stop();

            _mh = mpg123_new(IntPtr.Zero, out int err);
            if (_mh == IntPtr.Zero)
                throw new InvalidOperationException($"mpg123_new hatası: {err}");

            if (mpg123_open(_mh, filePath) != MPG123_OK)
                throw new InvalidOperationException("Dosya açılamadı: " + filePath);

            mpg123_getformat(_mh, out _rate, out _channels, out _);
            _totalSamples = mpg123_length(_mh);

            mpg123_volume(_mh, _volume);

            // Apply speed by adjusting the PulseAudio playback rate
            uint playbackRate = (uint)(_rate * _speed);

            var spec = new PaSampleSpec
            {
                format   = PA_SAMPLE_S16LE,
                rate     = playbackRate,
                channels = (byte)_channels
            };
            _pa = pa_simple_new(null, "MP3Player", PA_STREAM_PLAYBACK,
                                null, "Müzik", ref spec,
                                IntPtr.Zero, IntPtr.Zero, out int paErr);
            if (_pa == IntPtr.Zero)
                throw new InvalidOperationException($"PulseAudio bağlantı hatası: {paErr}");

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _playTask = Task.Run(() => DecodeLoop(token), token);
        }

        // ── Decode / oynatma döngüsü ──────────────────────────────────────────
        private void DecodeLoop(CancellationToken token)
        {
            const int BufSize = 4096 * 4;
            var buf = new byte[BufSize];

            while (!token.IsCancellationRequested)
            {
                // Duraklat desteği
                if (_paused)
                {
                    Thread.Sleep(20);
                    continue;
                }

                int ret = mpg123_read(_mh, buf, (IntPtr)BufSize, out IntPtr done);

                if (ret == MPG123_DONE || (ret != MPG123_OK && done == IntPtr.Zero))
                    break;

                if (done == IntPtr.Zero) continue;

                // Ses seviyesi yazılımsal uygulaması (zaten mpg123_volume kullanıyor,
                // bu blok ekstra güvenlik için)
                pa_simple_write(_pa, buf, done, out _);
            }

            if (!token.IsCancellationRequested)
            {
                pa_simple_drain(_pa, out _);
                TrackEnded?.Invoke(this, EventArgs.Empty);
            }
        }

        // ── Duraklat / Devam ──────────────────────────────────────────────────
        public void Pause()  => _paused = true;
        public void Resume() => _paused = false;
        public void TogglePause() { if (_paused) Resume(); else Pause(); }

        /// <summary>
        /// Apply speed change live by reconnecting PulseAudio with new rate.
        /// Call this after setting Speed property during playback.
        /// </summary>
        public void ApplySpeed()
        {
            if (_pa == IntPtr.Zero || _mh == IntPtr.Zero) return;

            bool wasPaused = _paused;
            _paused = true;
            Thread.Sleep(50); // let decode loop pause

            // Reconnect PulseAudio with new rate
            pa_simple_free(_pa);
            uint playbackRate = (uint)(_rate * _speed);
            var spec = new PaSampleSpec
            {
                format   = PA_SAMPLE_S16LE,
                rate     = playbackRate,
                channels = (byte)_channels
            };
            _pa = pa_simple_new(null, "MP3Player", PA_STREAM_PLAYBACK,
                                null, "Müzik", ref spec,
                                IntPtr.Zero, IntPtr.Zero, out _);

            _paused = wasPaused;
        }

        // ── Pozisyon atla ─────────────────────────────────────────────────────
        public void SeekTo(double seconds)
        {
            if (_mh == IntPtr.Zero) return;
            long sample = (long)(seconds * _rate);
            mpg123_seek(_mh, sample, 0 /* SEEK_SET */);
        }

        // ── Durdur ────────────────────────────────────────────────────────────
        public void Stop()
        {
            _cts?.Cancel();
            try { _playTask?.Wait(500); } catch { /* ignore */ }
            _cts?.Dispose();
            _cts = null;
            _playTask = null;
            _paused = false;

            if (_mh != IntPtr.Zero) { mpg123_close(_mh); mpg123_delete(_mh); _mh = IntPtr.Zero; }
            if (_pa != IntPtr.Zero) { pa_simple_free(_pa); _pa = IntPtr.Zero; }
        }

        // ── Temizlik ──────────────────────────────────────────────────────────
        public void Dispose()
        {
            Stop();
            mpg123_exit();
            _pauseGate.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
