using Gtk;
using MP3Player.Library;
using System;
using System.Threading;

namespace MP3Player
{
    /// <summary>
    /// Modern download dialog supporting YouTube and Spotify URLs.
    /// Clean progress display — no percentage spam.
    /// </summary>
    public class YouTubeDownloadDialog : Dialog
    {
        private readonly YouTubeDownloader _downloader = new();
        private CancellationTokenSource?   _cts;

        // ── UI elements ───────────────────────────────────────────────────────
        private readonly Entry         _entryUrl;
        private readonly ProgressBar   _progressBar;
        private readonly Label         _lblStatus;
        private readonly Label         _lblTrackInfo;
        private readonly TextView      _tvLog;
        private readonly Button        _btnDownload;
        private readonly Button        _btnCancel;
        private readonly Box           _statusCard;

        public event Action<string>? FileReady;

        public YouTubeDownloadDialog(Window parent)
            : base("YouTube & Spotify Download", parent, DialogFlags.DestroyWithParent)
        {
            SetDefaultSize(620, 480);
            Resizable = true;
            Name = "yt-dialog";

            var vbox = new Box(Orientation.Vertical, 8);
            vbox.Margin = 16;
            ContentArea.Add(vbox);

            // ── Header ────────────────────────────────────────────────────────
            var headerBox = new Box(Orientation.Horizontal, 8);
            headerBox.Name = "yt-header";
            var headerLabel = new Label("<span size='large' weight='bold'>♫ YouTube &amp; Spotify Download</span>");
            headerLabel.UseMarkup = true;
            headerLabel.Xalign = 0;
            headerBox.PackStart(headerLabel, true, true, 0);
            vbox.PackStart(headerBox, false, false, 0);

            // ── Source info label ─────────────────────────────────────────────
            var sourceInfo = new Label("<small>Supports: YouTube · YouTube Playlists · Spotify Tracks · Spotify Albums · Spotify Playlists</small>");
            sourceInfo.UseMarkup = true;
            sourceInfo.Xalign = 0;
            sourceInfo.Name = "yt-source-info";
            vbox.PackStart(sourceInfo, false, false, 0);

            // ── URL Input ─────────────────────────────────────────────────────
            _entryUrl = new Entry
            {
                PlaceholderText = "Paste YouTube or Spotify URL here…",
                Hexpand = true
            };
            _entryUrl.Name = "yt-entry";
            _entryUrl.Activated += OnDownloadClicked;
            _entryUrl.Changed += OnUrlChanged;
            vbox.PackStart(_entryUrl, false, false, 0);

            // ── Destination label ─────────────────────────────────────────────
            var lblDest = new Label("")
            {
                UseMarkup = true,
                Xalign    = 0,
                Ellipsize = Pango.EllipsizeMode.Middle
            };
            lblDest.Markup =
                $"<small>» {GLib.Markup.EscapeText(MusicLibrary.LibraryDir)}</small>";
            vbox.PackStart(lblDest, false, false, 0);

            // ── Separator ─────────────────────────────────────────────────────
            vbox.PackStart(new Separator(Orientation.Horizontal), false, false, 4);

            // ── Status Card ───────────────────────────────────────────────────
            _statusCard = new Box(Orientation.Vertical, 6);
            _statusCard.Name = "yt-status-card";
            _statusCard.Margin = 0;

            // Track info (shows current track name)
            _lblTrackInfo = new Label("Ready to download");
            _lblTrackInfo.Name = "yt-track-info";
            _lblTrackInfo.Xalign = 0;
            _lblTrackInfo.Ellipsize = Pango.EllipsizeMode.End;
            _statusCard.PackStart(_lblTrackInfo, false, false, 0);

            // Progress bar
            _progressBar = new ProgressBar
            {
                ShowText = false,
                Fraction = 0
            };
            _progressBar.Name = "yt-progress";
            _statusCard.PackStart(_progressBar, false, false, 0);

            // Status label (percentage, speed, ETA — single line, updates in place)
            _lblStatus = new Label("Waiting…");
            _lblStatus.Name = "yt-status-label";
            _lblStatus.Xalign = 0;
            _lblStatus.Ellipsize = Pango.EllipsizeMode.End;
            _statusCard.PackStart(_lblStatus, false, false, 0);

            vbox.PackStart(_statusCard, false, false, 0);

            // ── Log view (compact) ────────────────────────────────────────────
            var logExpander = new Expander("▸ Details");
            logExpander.Name = "yt-log-expander";

            _tvLog = new TextView
            {
                Editable    = false,
                WrapMode    = WrapMode.WordChar,
                Monospace   = true
            };
            _tvLog.Name = "yt-log";
            var scroll = new ScrolledWindow { ShadowType = ShadowType.In };
            scroll.Add(_tvLog);
            scroll.SetSizeRequest(-1, 120);
            logExpander.Add(scroll);
            vbox.PackStart(logExpander, true, true, 0);

            // ── Buttons ───────────────────────────────────────────────────────
            var hbox = new Box(Orientation.Horizontal, 8) { Halign = Align.End };

            _btnCancel = new Button("Close");
            _btnCancel.Name = "yt-btn";
            _btnCancel.Clicked += OnCancelClicked;

            _btnDownload = new Button("↓ Download")
            {
                CanDefault = true
            };
            _btnDownload.Name = "yt-btn-download";
            _btnDownload.Clicked += OnDownloadClicked;

            hbox.PackStart(_btnCancel,   false, false, 0);
            hbox.PackStart(_btnDownload, false, false, 0);
            vbox.PackStart(hbox, false, false, 4);

            // ── Downloader events ─────────────────────────────────────────────
            _downloader.StatusMessage += msg =>
                Application.Invoke((_, _) =>
                {
                    _lblStatus.Text = msg;
                });

            _downloader.ProgressPercent += pct =>
                Application.Invoke((_, _) =>
                {
                    _progressBar.Fraction = Math.Clamp(pct / 100.0, 0, 1);
                });

            _downloader.LogMessage += msg =>
                Application.Invoke((_, _) => AppendLog(msg));

            _downloader.DownloadCompleted += path =>
                Application.Invoke((_, _) =>
                {
                    FileReady?.Invoke(path);
                });

            _downloader.AllCompleted += () =>
                Application.Invoke((_, _) =>
                {
                    _lblTrackInfo.Markup = "<b>All downloads completed!</b>";
                    _lblStatus.Text = "Done";
                    _progressBar.Fraction = 1.0;
                    SetDownloading(false);
                });

            _downloader.DownloadFailed += err =>
                Application.Invoke((_, _) =>
                {
                    AppendLog($"Error: {err}");
                    _lblStatus.Text = "Download failed";
                    _lblTrackInfo.Markup = "<b>Download failed</b>";
                    SetDownloading(false);
                });

            ShowAll();
            _btnDownload.GrabDefault();
        }

        // ── URL changed — auto detect source ─────────────────────────────────
        private void OnUrlChanged(object? sender, EventArgs e)
        {
            var url = _entryUrl.Text.Trim();

            if (YouTubeDownloader.IsSpotifyUrl(url))
            {
                _lblTrackInfo.Markup = "<span color='#1DB954'>● Spotify link detected — auto-detecting content</span>";
            }
            else if (url.Contains("youtube.com") || url.Contains("youtu.be"))
            {
                bool isList = url.Contains("list=") || url.Contains("/playlist");
                _lblTrackInfo.Markup = isList
                    ? "<span color='#FF0000'>▶ YouTube playlist detected</span>"
                    : "<span color='#FF0000'>▶ YouTube link detected</span>";
            }
            else
            {
                _lblTrackInfo.Text = "Ready to download";
            }
        }

        // ── Download button clicked ───────────────────────────────────────────
        private void OnDownloadClicked(object? sender, EventArgs e)
        {
            var url = _entryUrl.Text.Trim();
            if (string.IsNullOrEmpty(url))
            {
                AppendLog("Please enter a URL.");
                return;
            }

            _cts = new CancellationTokenSource();
            SetDownloading(true);

            _lblTrackInfo.Markup = "<b>Starting download…</b>";
            _lblStatus.Text = "Connecting…";
            _progressBar.Fraction = 0;

            AppendLog($"URL: {url}");

            // Auto-detect playlist mode from URL
            bool isPlaylist = url.Contains("list=") || url.Contains("/playlist");
            _ = _downloader.DownloadAsync(url, MusicLibrary.LibraryDir,
                isPlaylist, _cts.Token);
        }

        // ── Cancel ────────────────────────────────────────────────────────────
        private void OnCancelClicked(object? sender, EventArgs e)
        {
            if (_cts is { IsCancellationRequested: false })
            {
                _cts.Cancel();
                AppendLog("⛔ Download cancelled.");
                _lblStatus.Text = "Cancelled";
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
            _btnCancel.Label       = active ? "⛔ Stop" : "Close";
        }

        private void AppendLog(string msg)
        {
            var buf  = _tvLog.Buffer;
            var iter = buf.EndIter;
            buf.Insert(ref iter, msg + "\n");
            var endIter = buf.EndIter;
            _tvLog.ScrollToIter(endIter, 0, false, 0, 0);
        }
    }
}
