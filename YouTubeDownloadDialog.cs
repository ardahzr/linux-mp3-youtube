using Gtk;
using MP3Player.Library;
using System;
using System.Threading;

namespace MP3Player
{
    /// <summary>
    /// Dialog for entering a YouTube URL and downloading as MP3 at highest quality.
    /// </summary>
    public class YouTubeDownloadDialog : Dialog
    {
        private readonly YouTubeDownloader _downloader = new();
        private CancellationTokenSource?   _cts;

        private readonly Entry         _entryUrl;
        private readonly TextView      _tvLog;
        private readonly ProgressBar   _progressBar;
        private readonly Button        _btnDownload;
        private readonly Button        _btnCancel;
        private readonly CheckButton   _chkPlaylist;

        public event Action<string>? FileReady;   // Downloaded MP3 path

        public YouTubeDownloadDialog(Window parent)
            : base("Download from YouTube", parent, DialogFlags.DestroyWithParent)
        {
            SetDefaultSize(600, 420);
            Resizable = true;
            Name = "yt-dialog";

            var vbox = new Box(Orientation.Vertical, 6);
            vbox.Margin = 12;
            ContentArea.Add(vbox);

            // ── URL Input ─────────────────────────────────────────────────────
            vbox.PackStart(new Label("YouTube URL or Playlist Link:") { Xalign = 0 },
                false, false, 0);

            _entryUrl = new Entry
            {
                PlaceholderText = "https://www.youtube.com/watch?v=...",
                Hexpand = true
            };
            _entryUrl.Activated += OnDownloadClicked;
            vbox.PackStart(_entryUrl, false, false, 0);

            // ── Options ───────────────────────────────────────────────────────
            _chkPlaylist = new CheckButton("Playlist / Channel — download all videos");
            vbox.PackStart(_chkPlaylist, false, false, 0);

            // ── Save location ─────────────────────────────────────────────────
            var lblDest = new Label("")
            {
                UseMarkup = true,
                Xalign    = 0,
                Ellipsize = Pango.EllipsizeMode.Middle
            };
            lblDest.Markup =
                $"<small>📁 Destination: <b>{GLib.Markup.EscapeText(MusicLibrary.LibraryDir)}</b></small>";
            vbox.PackStart(lblDest, false, false, 0);

            // ── Progress bar ──────────────────────────────────────────────────
            _progressBar = new ProgressBar { ShowText = true, Text = "Waiting…" };
            _progressBar.Name = "yt-progress";
            vbox.PackStart(_progressBar, false, false, 0);

            // ── Log view ──────────────────────────────────────────────────────
            _tvLog = new TextView
            {
                Editable    = false,
                WrapMode    = WrapMode.WordChar,
                Monospace   = true
            };
            _tvLog.Name = "yt-log";
            var scroll = new ScrolledWindow { ShadowType = ShadowType.In };
            scroll.Add(_tvLog);
            scroll.SetSizeRequest(-1, 160);
            vbox.PackStart(scroll, true, true, 0);

            // ── Buttons ───────────────────────────────────────────────────────
            var hbox = new Box(Orientation.Horizontal, 8) { Halign = Align.End };

            _btnCancel = new Button("Cancel");
            _btnCancel.Name = "yt-btn";
            _btnCancel.Clicked += OnCancelClicked;

            _btnDownload = new Button("⬇ Download")
            {
                CanDefault = true
            };
            _btnDownload.Name = "yt-btn-download";
            _btnDownload.Clicked += OnDownloadClicked;

            hbox.PackStart(_btnCancel,   false, false, 0);
            hbox.PackStart(_btnDownload, false, false, 0);
            vbox.PackStart(hbox, false, false, 4);

            // ── Downloader events ─────────────────────────────────────────────
            _downloader.ProgressMessage += msg =>
                Application.Invoke((_, _) => AppendLog(msg));

            _downloader.DownloadCompleted += path =>
                Application.Invoke((_, _) =>
                {
                    AppendLog($"✅ Saved: {path}");
                    _progressBar.Text     = "Done!";
                    _progressBar.Fraction = 1.0;
                    FileReady?.Invoke(path);
                    SetDownloading(false);
                });

            _downloader.DownloadFailed += err =>
                Application.Invoke((_, _) =>
                {
                    AppendLog($"❌ Error: {err}");
                    _progressBar.Text = "Download failed";
                    SetDownloading(false);
                });

            ShowAll();
            _btnDownload.GrabDefault();
        }

        // ── Download button clicked ───────────────────────────────────────────
        private void OnDownloadClicked(object? sender, EventArgs e)
        {
            var url = _entryUrl.Text.Trim();
            if (string.IsNullOrEmpty(url))
            {
                AppendLog("⚠ Please enter a URL.");
                return;
            }

            _cts = new CancellationTokenSource();
            SetDownloading(true);
            AppendLog($"🎵 Starting download…\n   URL: {url}");
            _progressBar.Text     = "Connecting…";
            _progressBar.Fraction = 0;
            _progressBar.Pulse();

            // Pulse animasyonu zamanlayıcısı
            var pulseTimer = new System.Timers.Timer(300);
            pulseTimer.Elapsed += (_, _) =>
                Application.Invoke((_, _) =>
                {
                    if (_progressBar.Fraction < 1.0)
                        _progressBar.Pulse();
                });
            pulseTimer.Start();

            var token = _cts.Token;

            if (_chkPlaylist.Active)
            {
                _ = _downloader.DownloadPlaylistAsync(url, MusicLibrary.LibraryDir, token)
                    .ContinueWith(_ => pulseTimer.Stop());
            }
            else
            {
                _ = _downloader.DownloadAsync(url, MusicLibrary.LibraryDir, token)
                    .ContinueWith(_ => pulseTimer.Stop());
            }
        }

        // ── Cancel ────────────────────────────────────────────────────────────
        private void OnCancelClicked(object? sender, EventArgs e)
        {
            if (_cts is { IsCancellationRequested: false })
            {
                _cts.Cancel();
                AppendLog("⛔ Download cancelled.");
                SetDownloading(false);
            }
            else
            {
                Hide();
            }
        }

        // ── UI helpers ────────────────────────────────────────────────────────
        private void SetDownloading(bool active)
        {
            _btnDownload.Sensitive = !active;
            _entryUrl.Sensitive    = !active;
            _chkPlaylist.Sensitive = !active;
            _btnCancel.Label       = active ? "⛔ Stop" : "Close";
        }

        private void AppendLog(string msg)
        {
            var buf  = _tvLog.Buffer;
            var iter = buf.EndIter;
            buf.Insert(ref iter, msg + "\n");
            // En alta kaydır
            var endIter = buf.EndIter;
            _tvLog.ScrollToIter(endIter, 0, false, 0, 0);
        }
    }
}
