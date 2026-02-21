using Gtk;
using MP3Player.Audio;
using MP3Player.Library;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Timers;
using SysPath = System.IO.Path;

namespace MP3Player
{
    public class MainWindow : Window
    {
        // ── Çekirdek ──────────────────────────────────────────────────────────
        private readonly AudioEngine     _audio   = new();
        private readonly MusicLibrary    _library = new();
        private readonly PlaylistManager _plMgr   = new();

        // ── Aktif playlist & şarkılar ─────────────────────────────────────────
        private PlaylistModel  _activePlaylist;
        private List<string>   _queue = new();
        private int            _currentIndex = -1;
        private bool           _shuffle = false;
        private bool           _repeat  = false;

        // ── Sol panel – playlist listesi ──────────────────────────────────────
        private readonly ListBox   _sidebarList;

        // ── Orta panel – şarkı listesi ────────────────────────────────────────
        private readonly ListStore  _trackStore;    // (isPlaying:bool, idx:string, name:string, duration:string, path:string)
        private readonly TreeView   _trackView;
        private readonly Label      _lblPlaylistName;
        private readonly Label      _lblPlaylistSubtitle;

        // ── Alt player bar ────────────────────────────────────────────────────
        private readonly Label  _lblTrackName;
        private readonly Label  _lblTrackArtist;
        private readonly Label  _lblTimeElapsed;
        private readonly Label  _lblTimeTotal;
        private readonly Scale  _scaleProgress;
        private readonly Scale  _scaleVolume;
        private readonly Button _btnPlay;
        private readonly Button _btnShuffle;
        private readonly Button _btnRepeat;
        private readonly Gtk.Image _albumArtImage = new Gtk.Image();

        // ── Zamanlayıcı ───────────────────────────────────────────────────────
        private readonly System.Timers.Timer _uiTimer;
        private bool _seeking = false;
        private double _prevVolume = 50;  // Mute toggle için önceki ses seviyesi

        // ─────────────────────────────────────────────────────────────────────
        public MainWindow() : base("LMP – Libuntu Music Player")
        {
            // Varsayılan playlist
            _activePlaylist = _plMgr.Playlists[0];

            SetDefaultSize(1100, 680);
            SetPosition(WindowPosition.Center);
            DeleteEvent += (_, _) => { Cleanup(); Application.Quit(); };

            // ── CSS Teması yükle ──────────────────────────────────────────────
            LoadTheme();

            // ── TrackEnded ────────────────────────────────────────────────────
            _audio.TrackEnded += (_, _) =>
                Application.Invoke((_, _) => PlayNext());

            // ══ Ana layout: üst bar + orta (sidebar+içerik) + alt player ═════
            var rootBox = new Box(Orientation.Vertical, 0);
            Add(rootBox);

            // ── Üst araç çubuğu ───────────────────────────────────────────────
            rootBox.PackStart(BuildToolbar(), false, false, 0);

            // ── Orta alan (sidebar + içerik) ──────────────────────────────────
            var midPane = new Paned(Orientation.Horizontal);
            midPane.Name = "mid-pane";
            rootBox.PackStart(midPane, true, true, 0);

            // Sol sidebar
            var sidebar = BuildSidebar(out _sidebarList);
            midPane.Pack1(sidebar, false, false);
            midPane.Position = 230;

            // Sağ içerik
            var content = BuildContent(out _trackStore, out _trackView, out _lblPlaylistName, out _lblPlaylistSubtitle);
            midPane.Pack2(content, true, true);

            // ── Alt player bar ────────────────────────────────────────────────
            var playerBar = BuildPlayerBar(
                out _lblTrackName, out _lblTrackArtist,
                out _lblTimeElapsed, out _lblTimeTotal,
                out _scaleProgress, out _scaleVolume,
                out _btnPlay, out _btnShuffle, out _btnRepeat,
                out _albumArtImage);
            rootBox.PackEnd(playerBar, false, false, 0);

            // ── UI zamanlayıcı ────────────────────────────────────────────────
            _uiTimer = new System.Timers.Timer(500);
            _uiTimer.Elapsed += OnTimerElapsed;
            _uiTimer.Start();

            // ── Klavye kısayolları ────────────────────────────────────────────
            KeyPressEvent += OnKeyPress;

            ShowAll();

            // ── Sidebar'ı doldur, aktif playlist'i yükle ─────────────────────
            RefreshSidebar();
            LoadPlaylistIntoView(_activePlaylist);
            
            // ── Başlangıç ses seviyesi ────────────────────────────────────────
            _audio.Volume = 0.5;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  BUILDER FONKSİYONLARI
        // ══════════════════════════════════════════════════════════════════════

        // ── Üst araç çubuğu ───────────────────────────────────────────────────
        private Widget BuildToolbar()
        {
            var bar = new Box(Orientation.Horizontal, 6);
            bar.Name = "toolbar";
            bar.Margin = 0;

            // Left: app name
            var lblApp = new Label("<b>🎵 LMP</b>")
            {
                UseMarkup = true,
                Margin = 8
            };
            bar.PackStart(lblApp, false, false, 4);

            // Right: action buttons
            var btnImport = ToolbarBtn("📂 Add Files", OnImportFiles);
            var btnScan   = ToolbarBtn("🔄 Scan Library", OnScanLibrary);
            var btnOpenDir = ToolbarBtn("📁 Open Folder", (_, _) =>
                System.Diagnostics.Process.Start("xdg-open", MusicLibrary.LibraryDir));

            var btnYt = new Button("▶ YouTube Download");
            btnYt.Name = "toolbar-btn-youtube";
            btnYt.Clicked += OnYouTubeDownload;
            if (!YouTubeDownloader.IsAvailable())
            {
                btnYt.Sensitive   = false;
                btnYt.TooltipText = "yt-dlp or ffmpeg missing: sudo pacman -S yt-dlp ffmpeg";
            }

            bar.PackEnd(btnYt,      false, false, 4);
            bar.PackEnd(btnOpenDir, false, false, 0);
            bar.PackEnd(btnScan,    false, false, 0);
            bar.PackEnd(btnImport,  false, false, 0);

            return bar;
        }

        // ── Sol Sidebar ───────────────────────────────────────────────────────
        private Widget BuildSidebar(out ListBox listBox)
        {
            var vbox = new Box(Orientation.Vertical, 0);
            vbox.Name = "sidebar";

            // Header
            var header = new Box(Orientation.Horizontal, 0);
            header.Name = "sidebar-header";
            var lblLib = new Label("YOUR LIBRARY") { Xalign = 0, Hexpand = true };
            header.PackStart(lblLib, true, true, 0);

            // New playlist button
            var btnNew = new Button("+") { TooltipText = "New Playlist" };
            btnNew.Name = "btn-new-playlist";
            btnNew.Clicked += OnNewPlaylist;
            header.PackEnd(btnNew, false, false, 0);
            vbox.PackStart(header, false, false, 0);

            // Playlist listesi
            listBox = new ListBox();
            listBox.Name = "playlist-list";
            listBox.SelectionMode = SelectionMode.Single;
            listBox.RowSelected += OnSidebarRowSelected;

            var scroll = new ScrolledWindow
            {
                ShadowType = ShadowType.None,
                HscrollbarPolicy = PolicyType.Never
            };
            scroll.Add(listBox);
            vbox.PackStart(scroll, true, true, 0);

            return vbox;
        }

        // ── Orta içerik alanı ─────────────────────────────────────────────────
        private Widget BuildContent(out ListStore store, out TreeView view, out Label plName, out Label plSubtitle)
        {
            var vbox = new Box(Orientation.Vertical, 0);
            vbox.Name = "content-area";

            // İçerik header
            var header = new Box(Orientation.Vertical, 4);
            header.Name = "content-header";
            header.MarginBottom = 8;

            plName = new Label("") { UseMarkup = true, Xalign = 0 };
            plName.Name = "now-playing-title";

            plSubtitle = new Label("Playlist") { Xalign = 0 };
            plSubtitle.Name = "now-playing-subtitle";

            header.PackStart(plName, false, false, 0);
            header.PackStart(plSubtitle, false, false, 0);
            vbox.PackStart(header, false, false, 0);

            // Inner toolbar (playlist actions)
            var innerToolbar = new Box(Orientation.Horizontal, 6);
            innerToolbar.Margin = 8;

            var btnAddToPlaylist = ToolbarBtn("➕ Add Songs", OnAddTrackToPlaylist);
            var btnRemoveTrack   = ToolbarBtn("➖ Remove Selected", OnRemoveTrack);
            var btnRenamePlaylist = ToolbarBtn("✏ Rename", OnRenamePlaylist);
            var btnDeletePlaylist = ToolbarBtn("🗑 Delete Playlist", OnDeletePlaylist);

            innerToolbar.PackStart(btnAddToPlaylist,   false, false, 0);
            innerToolbar.PackStart(btnRemoveTrack,     false, false, 0);
            innerToolbar.PackEnd  (btnDeletePlaylist,  false, false, 0);
            innerToolbar.PackEnd  (btnRenamePlaylist,  false, false, 0);
            vbox.PackStart(innerToolbar, false, false, 0);

            // Şarkı listesi
            // Kolonlar: ▶(oynuyor), #, Ad, Süre
            store = new ListStore(
                typeof(string),   // 0: oynatma göstergesi "▶" veya ""
                typeof(string),   // 1: index
                typeof(string),   // 2: şarkı adı
                typeof(string),   // 3: süre (henüz yok)
                typeof(string));  // 4: tam yol (gizli)

            view = new TreeView(store)
            {
                HeadersVisible = true,
                EnableSearch   = true,
                SearchColumn   = 2
            };
            view.Name = "track-list";

            // ▶ kolonu
            var rendPlay = new CellRendererText
            {
                ForegroundRgba = new Gdk.RGBA { Red = 0.11f, Green = 0.73f, Blue = 0.33f, Alpha = 1f }
            };
            var colPlay = new TreeViewColumn("", rendPlay, "text", 0) { MinWidth = 24 };
            view.AppendColumn(colPlay);

            // # kolonu
            var rendIdx = new CellRendererText();
            rendIdx.Foreground = "#B3B3B3";
            var colIdx = new TreeViewColumn("#", rendIdx, "text", 1) { MinWidth = 36 };
            view.AppendColumn(colIdx);

            // Şarkı adı kolonu
            var rendName = new CellRendererText { Ellipsize = Pango.EllipsizeMode.End };
            var colName  = new TreeViewColumn("TITLE", rendName, "text", 2)
            { Expand = true, Resizable = true, MinWidth = 200 };
            view.AppendColumn(colName);

            view.RowActivated += OnTrackActivated;

            // Sağ tık menüsü
            view.ButtonPressEvent += OnTrackViewButtonPress;

            var scrolled = new ScrolledWindow { ShadowType = ShadowType.None };
            scrolled.Name = "track-list-scroll";
            scrolled.Add(view);
            vbox.PackStart(scrolled, true, true, 0);

            return vbox;
        }

        // ── Bottom Player Bar ─────────────────────────────────────────────────
        private Widget BuildPlayerBar(
            out Label trackName, out Label trackArtist,
            out Label timeElapsed, out Label timeTotal,
            out Scale progress, out Scale volume,
            out Button btnPlay, out Button btnShuf, out Button btnRep,
            out Gtk.Image albumArt)
        {
            var bar = new Box(Orientation.Horizontal, 0);
            bar.Name = "player-bar";

            // ── Left: album art + track info ──────────────────────────────────
            var leftBox = new Box(Orientation.Horizontal, 8)
            {
                WidthRequest = 260,
                Valign = Align.Center,
                Margin = 0
            };

            albumArt = new Gtk.Image();
            albumArt.Name = "player-album-art";
            albumArt.WidthRequest  = 56;
            albumArt.HeightRequest = 56;
            leftBox.PackStart(albumArt, false, false, 0);

            var infoBox = new Box(Orientation.Vertical, 2)
            {
                Valign = Align.Center
            };
            infoBox.Name = "player-track-info";

            trackName = new Label("—") { Xalign = 0, Ellipsize = Pango.EllipsizeMode.End };
            trackName.Name = "player-track-name";

            trackArtist = new Label("") { Xalign = 0, Ellipsize = Pango.EllipsizeMode.End };
            trackArtist.Name = "player-track-artist";

            infoBox.PackStart(trackName,   false, false, 0);
            infoBox.PackStart(trackArtist, false, false, 0);
            leftBox.PackStart(infoBox, true, true, 0);
            bar.PackStart(leftBox, false, false, 16);

            // ── Orta: kontroller + ilerleme ───────────────────────────────────
            var centerBox = new Box(Orientation.Vertical, 4)
            {
                Hexpand = true,
                Halign  = Align.Fill,
                Valign  = Align.Center
            };

            // Kontrol butonları
            var ctrlBox = new Box(Orientation.Horizontal, 4)
            {
                Halign = Align.Center,
                Valign = Align.Center
            };

            btnShuf = PlayerBtn("⇄", "Shuffle",   OnToggleShuffle); btnShuf.Name = "control-btn";
            var btnPrev = PlayerBtn("⏮", "Previous", OnPrev);          btnPrev.Name = "control-btn";
            btnPlay     = PlayerBtn("▶", "Play",     OnPlayPause);     btnPlay.Name = "control-btn-play";
            var btnNext = PlayerBtn("⏭", "Next",     OnNext);          btnNext.Name = "control-btn";
            btnRep      = PlayerBtn("↻", "Repeat",   OnToggleRepeat);  btnRep.Name  = "control-btn";
            var btnStop = PlayerBtn("⏹", "Stop",     OnStop);          btnStop.Name = "control-btn";

            ctrlBox.PackStart(btnShuf, false, false, 0);
            ctrlBox.PackStart(btnPrev, false, false, 0);
            ctrlBox.PackStart(btnPlay, false, false, 8);
            ctrlBox.PackStart(btnNext, false, false, 0);
            ctrlBox.PackStart(btnStop, false, false, 0);
            ctrlBox.PackStart(btnRep,  false, false, 0);
            centerBox.PackStart(ctrlBox, false, false, 0);

            // İlerleme çubuğu + zaman
            var progBox = new Box(Orientation.Horizontal, 8) { Halign = Align.Fill };

            timeElapsed = new Label("0:00") { WidthRequest = 40, Xalign = 1 };
            timeElapsed.Name = "time-label";

            progress = new Scale(Orientation.Horizontal, 0, 1000, 1)
            {
                DrawValue = false,
                Hexpand   = true
            };
            progress.Name = "progress-scale";
            progress.ButtonPressEvent   += (_, _) => _seeking = true;
            progress.ButtonReleaseEvent += OnProgressReleased;

            timeTotal = new Label("0:00") { WidthRequest = 40, Xalign = 0 };
            timeTotal.Name = "time-label";

            progBox.PackStart(timeElapsed, false, false, 0);
            progBox.PackStart(progress,    true,  true,  0);
            progBox.PackStart(timeTotal,   false, false, 0);
            centerBox.PackStart(progBox, false, false, 0);

            bar.PackStart(centerBox, true, true, 16);

            // ── Sağ: ses seviyesi ─────────────────────────────────────────────
            var volBox = new Box(Orientation.Horizontal, 6)
            {
                WidthRequest = 180,
                Valign = Align.Center
            };
            var lblVol = new Label("🔊") { Xalign = 1 };
            volume = new Scale(Orientation.Horizontal, 0, 100, 1)
            {
                DrawValue = false,
                Hexpand   = true
            };
            volume.Name = "volume-scale";
            volume.Value = 50;
            volume.TooltipText = "50%";
            volume.ValueChanged += OnVolumeChanged;

            volBox.PackStart(lblVol, false, false, 0);
            volBox.PackStart(volume, true,  true,  0);
            bar.PackEnd(volBox, false, false, 16);

            return bar;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  SIDEBAR / PLAYLIST MANAGEMENT
        // ══════════════════════════════════════════════════════════════════════

        private void RefreshSidebar()
        {
            foreach (var child in _sidebarList.Children)
                _sidebarList.Remove(child);

            foreach (var pl in _plMgr.Playlists)
            {
                var row  = new ListBoxRow();
                var hbox = new Box(Orientation.Horizontal, 8);
                var icon = new Label("🎵") { Valign = Align.Center };
                var lbl  = new Label(pl.Name)
                {
                    Xalign = 0,
                    Margin = 2,
                    Ellipsize = Pango.EllipsizeMode.End,
                    Hexpand = true
                };
                var count = new Label($"{pl.Tracks.Count}")
                {
                    Valign = Align.Center
                };
                count.Name = "now-playing-subtitle";
                hbox.PackStart(icon,  false, false, 0);
                hbox.PackStart(lbl,   true,  true,  0);
                hbox.PackEnd  (count, false, false, 0);
                row.Add(hbox);
                row.Data["playlist"] = pl;
                _sidebarList.Add(row);
            }

            _sidebarList.ShowAll();

            // Select active playlist
            var idx = _plMgr.IndexOf(_activePlaylist);
            if (idx >= 0)
                _sidebarList.SelectRow(_sidebarList.GetRowAtIndex(idx));
        }

        private void OnSidebarRowSelected(object? sender, RowSelectedArgs e)
        {
            if (e.Row?.Data["playlist"] is PlaylistModel pl)
            {
                _activePlaylist = pl;
                LoadPlaylistIntoView(pl);
            }
        }

        private void OnNewPlaylist(object? sender, EventArgs e)
        {
            var name = AskText("Playlist Name", "New playlist name:", "New Playlist");
            if (string.IsNullOrWhiteSpace(name)) return;

            var pl = _plMgr.Create(name);
            _activePlaylist = pl;
            RefreshSidebar();
            LoadPlaylistIntoView(pl);
        }

        private void OnRenamePlaylist(object? sender, EventArgs e)
        {
            var name = AskText("Rename Playlist",
                $"New name for '{_activePlaylist.Name}':", _activePlaylist.Name);
            if (string.IsNullOrWhiteSpace(name)) return;

            _plMgr.Rename(_activePlaylist, name);
            RefreshSidebar();
            UpdatePlaylistHeader();
        }

        private void OnDeletePlaylist(object? sender, EventArgs e)
        {
            if (_plMgr.Playlists.Count == 1)
            {
                ShowInfo("Cannot delete the last playlist.");
                return;
            }

            if (!Confirm($"Delete '{_activePlaylist.Name}'?")) return;

            _plMgr.Delete(_activePlaylist);
            _activePlaylist = _plMgr.Playlists[0];
            RefreshSidebar();
            LoadPlaylistIntoView(_activePlaylist);
        }

        // ── Playlist içeriğini TreeView'e yükle ───────────────────────────────
        private void LoadPlaylistIntoView(PlaylistModel pl)
        {
            _trackStore.Clear();
            _queue = new List<string>(pl.Tracks);

            for (int i = 0; i < _queue.Count; i++)
            {
                var path = _queue[i];
                var name = SysPath.GetFileNameWithoutExtension(path);
                var indicator = (i == _currentIndex && (_audio.IsPlaying || _audio.IsPaused))
                    ? "▶" : "";
                _trackStore.AppendValues(indicator, (i + 1).ToString(), name, "", path);
            }

            UpdatePlaylistHeader();
        }

        private void UpdatePlaylistHeader()
        {
            _lblPlaylistName.Markup =
                $"<b>{GLib.Markup.EscapeText(_activePlaylist.Name)}</b>";
            var count = _activePlaylist.Tracks.Count;
            _lblPlaylistSubtitle.Text = $"Playlist • {count} {(count == 1 ? "song" : "songs")}";
        }

        // ── Şarkı listesi – çift tıkla oynat ─────────────────────────────────
        private void OnTrackActivated(object sender, RowActivatedArgs e)
        {
            int idx = e.Path.Indices[0];
            if (idx >= 0 && idx < _queue.Count)
                PlayAt(idx);
        }

        // ── Sağ tık menüsü ────────────────────────────────────────────────────
        [GLib.ConnectBefore]
        private void OnTrackViewButtonPress(object o, ButtonPressEventArgs args)
        {
            if (args.Event.Button != 3) return; // right-click only

            _trackView.GetPathAtPos((int)args.Event.X, (int)args.Event.Y,
                out TreePath? treePath, out _, out _, out _);
            if (treePath == null) return;

            _trackView.Selection.SelectPath(treePath);
            int idx = treePath.Indices[0];
            if (idx < 0 || idx >= _queue.Count) return;

            var menu = new Menu();

            var itemPlay = new MenuItem($"▶ Play");
            itemPlay.Activated += (_, _) => PlayAt(idx);
            menu.Append(itemPlay);

            var itemAddTo = new MenuItem("➕ Add to Playlist");
            itemAddTo.Activated += (_, _) => ShowAddToPlaylistMenu(idx);
            menu.Append(itemAddTo);

            menu.Append(new SeparatorMenuItem());

            var itemRemove = new MenuItem("➖ Remove from List");
            itemRemove.Activated += (_, _) => RemoveTrackAt(idx);
            menu.Append(itemRemove);

            menu.ShowAll();
            menu.Popup();
        }

        private void ShowAddToPlaylistMenu(int trackIdx)
        {
            var trackPath = _queue[trackIdx];
            var menu = new Menu();

            foreach (var pl in _plMgr.Playlists)
            {
                var localPl  = pl;
                var item = new MenuItem(localPl.Name);
                item.Activated += (_, _) =>
                {
                    if (_plMgr.AddTrack(localPl, trackPath))
                        ShowInfo($"'{SysPath.GetFileNameWithoutExtension(trackPath)}'\n→ Added to '{localPl.Name}'.");
                    else
                        ShowInfo("This track is already in that playlist.");
                };
                menu.Append(item);
            }

            menu.ShowAll();
            menu.Popup();
        }

        // ── Add songs to playlist (toolbar button) ────────────────────────────
        private void OnAddTrackToPlaylist(object? sender, EventArgs e)
        {
            using var dlg = new FileChooserDialog(
                "Select Audio Files", this, FileChooserAction.Open,
                "Cancel", ResponseType.Cancel,
                "Add",    ResponseType.Accept)
            { SelectMultiple = true };

            var f = new FileFilter { Name = "Audio Files" };
            foreach (var ext in MusicLibrary.SupportedExtensions)
                f.AddPattern($"*{ext}");
            dlg.AddFilter(f);

            if (dlg.Run() != (int)ResponseType.Accept) return;

            // Copy to library, then add to active playlist
            var imported = _library.ImportFiles(dlg.Filenames);
            foreach (var p in imported)
                _plMgr.AddTrack(_activePlaylist, p);

            LoadPlaylistIntoView(_activePlaylist);
        }

        // ── Seçili şarkıyı kaldır ────────────────────────────────────────────
        private void OnRemoveTrack(object? sender, EventArgs e)
        {
            _trackView.Selection.GetSelected(out _, out TreeIter iter);
            if (!_trackStore.IterIsValid(iter)) return;
            var path = (string)_trackStore.GetValue(iter, 4);
            RemoveTrackByPath(path);
        }

        private void RemoveTrackAt(int idx)
        {
            if (idx < 0 || idx >= _queue.Count) return;
            RemoveTrackByPath(_queue[idx]);
        }

        private void RemoveTrackByPath(string path)
        {
            _plMgr.RemoveTrack(_activePlaylist, path);
            LoadPlaylistIntoView(_activePlaylist);
        }

        // ── Scan library ──────────────────────────────────────────────────────
        private void OnScanLibrary(object? sender, EventArgs e)
        {
            var files = _library.ScanLibrary();
            _plMgr.SyncLibraryToDefault(files);
            if (_activePlaylist == _plMgr.Playlists[0])
                LoadPlaylistIntoView(_activePlaylist);
            ShowInfo($"Library scanned: {files.Count} files found.");
        }

        // ── Import files ──────────────────────────────────────────────────────
        private void OnImportFiles(object? sender, EventArgs e)
        {
            using var dlg = new FileChooserDialog(
                "Select Audio Files", this, FileChooserAction.Open,
                "Cancel",  ResponseType.Cancel,
                "Import",  ResponseType.Accept)
            { SelectMultiple = true };

            var f = new FileFilter { Name = "Audio Files" };
            foreach (var ext in MusicLibrary.SupportedExtensions)
                f.AddPattern($"*{ext}");
            dlg.AddFilter(f);
            dlg.AddFilter(new FileFilter { Name = "All Files" }.Also(ff => ff.AddPattern("*")));

            if (dlg.Run() != (int)ResponseType.Accept) return;

            var imported = _library.ImportFiles(dlg.Filenames);
            foreach (var p in imported)
                _plMgr.AddTrack(_activePlaylist, p);

            LoadPlaylistIntoView(_activePlaylist);
            ShowInfo($"{imported.Count} file(s) imported.");
        }

        // ── YouTube ───────────────────────────────────────────────────────────
        private void OnYouTubeDownload(object? sender, EventArgs e)
        {
            var dlg = new YouTubeDownloadDialog(this);
            dlg.FileReady += path =>
            {
                _plMgr.AddTrack(_activePlaylist, path);
                LoadPlaylistIntoView(_activePlaylist);
            };
            dlg.ShowAll();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  PLAYBACK LOGIC
        // ══════════════════════════════════════════════════════════════════════

        private void PlayAt(int index)
        {
            if (index < 0 || index >= _queue.Count) return;
            _currentIndex = index;

            try
            {
                _audio.Play(_queue[index]);
                _audio.Volume = _scaleVolume.Value / 100.0;
            }
            catch (Exception ex)
            {
                ShowError("Playback failed: " + ex.Message);
                return;
            }

            var name = SysPath.GetFileNameWithoutExtension(_queue[index]);
            _lblTrackName.Text   = name;
            _lblTrackArtist.Text = "LMP";
            _btnPlay.Label = "⏸";

            // Load album art thumbnail
            LoadAlbumArt(_queue[index]);

            // Update playing indicator
            RefreshPlayingIndicator();
        }

        private void PlayNext()
        {
            if (_queue.Count == 0) return;

            if (_repeat)
            {
                PlayAt(_currentIndex);
                return;
            }

            int next;
            if (_shuffle)
            {
                var rnd = new Random();
                do { next = rnd.Next(_queue.Count); }
                while (_queue.Count > 1 && next == _currentIndex);
            }
            else
            {
                next = (_currentIndex + 1) % _queue.Count;
            }

            PlayAt(next);
        }

        private void PlayPrev()
        {
            if (_queue.Count == 0) return;

            // Eğer 3 saniyeden fazla çaldıysa şarkıyı başa sar
            if (_audio.PositionSeconds > 3)
            {
                _audio.SeekTo(0);
                return;
            }

            int prev;
            if (_shuffle)
            {
                var rnd = new Random();
                do { prev = rnd.Next(_queue.Count); }
                while (_queue.Count > 1 && prev == _currentIndex);
            }
            else
            {
                prev = (_currentIndex - 1 + _queue.Count) % _queue.Count;
            }

            PlayAt(prev);
        }

        private void OnPlayPause(object? sender, EventArgs e)
        {
            if (_audio.IsPlaying)
            {
                _audio.TogglePause();
                _btnPlay.Label = _audio.IsPaused ? "▶" : "⏸";
            }
            else if (_currentIndex >= 0)
                PlayAt(_currentIndex);
            else if (_queue.Count > 0)
                PlayAt(0);
        }

        private void OnStop(object? sender, EventArgs e)
        {
            _audio.Stop();
            _btnPlay.Label = "▶";
            _scaleProgress.Value = 0;
            _lblTimeElapsed.Text = "0:00";
            _lblTimeTotal.Text   = "0:00";
            RefreshPlayingIndicator();
        }

        private void OnPrev(object? sender, EventArgs e)
        {
            if (_queue.Count == 0) return;
            PlayAt(_currentIndex <= 0 ? _queue.Count - 1 : _currentIndex - 1);
        }

        private void OnNext(object? sender, EventArgs e)
        {
            if (_queue.Count == 0) return;
            PlayAt((_currentIndex + 1) % _queue.Count);
        }

        private void OnToggleShuffle(object? sender, EventArgs e)
        {
            _shuffle = !_shuffle;
            _btnShuffle.Name = _shuffle ? "control-btn-play" : "control-btn";
        }

        private void OnToggleRepeat(object? sender, EventArgs e)
        {
            _repeat = !_repeat;
            _btnRepeat.Name = _repeat ? "control-btn-play" : "control-btn";
        }

        private void OnVolumeChanged(object? sender, EventArgs e)
        {
            _audio.Volume = _scaleVolume.Value / 100.0;
            _scaleVolume.TooltipText = $"{(int)_scaleVolume.Value}%";
        }

        private void OnProgressReleased(object? o, ButtonReleaseEventArgs e)
        {
            if (_audio.TotalSeconds > 0)
                _audio.SeekTo(_scaleProgress.Value / 1000.0 * _audio.TotalSeconds);
            _seeking = false;
        }

        // ── UI zamanlayıcı ────────────────────────────────────────────────────
        private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            Application.Invoke((_, _) =>
            {
                double total   = _audio.TotalSeconds;
                double elapsed = _audio.PositionSeconds;

                if (!_seeking && _audio.IsPlaying && total > 0)
                    _scaleProgress.Value = elapsed / total * 1000.0;

                _lblTimeElapsed.Text = TimeSpan.FromSeconds(elapsed).ToString(@"m\:ss");
                _lblTimeTotal.Text   = TimeSpan.FromSeconds(total).ToString(@"m\:ss");
            });
        }

        // ── Oynatma göstergesi ────────────────────────────────────────────────
        private void RefreshPlayingIndicator()
        {
            if (!_trackStore.GetIterFirst(out TreeIter iter)) return;
            int i = 0;
            do
            {
                _trackStore.SetValue(iter, 0, i == _currentIndex ? "▶" : "");
                i++;
            } while (_trackStore.IterNext(ref iter));
        }

        // ══════════════════════════════════════════════════════════════════════
        //  HELPERS
        // ══════════════════════════════════════════════════════════════════════

        private static Button ToolbarBtn(string lbl, EventHandler h)
        {
            var b = new Button(lbl);
            b.Name = "toolbar-btn";
            b.Clicked += h;
            return b;
        }

        private static Button PlayerBtn(string lbl, string tip, EventHandler h)
        {
            var b = new Button(lbl) { TooltipText = tip };
            b.Clicked += h;
            return b;
        }

        private void LoadAlbumArt(string filePath)
        {
            try
            {
                using var tagFile = TagLib.File.Create(filePath);
                var pics = tagFile.Tag.Pictures;
                if (pics != null && pics.Length > 0)
                {
                    var data = pics[0].Data.Data;
                    using var loader = new Gdk.PixbufLoader();
                    loader.Write(data);
                    loader.Close();
                    var pixbuf = loader.Pixbuf?.ScaleSimple(56, 56, Gdk.InterpType.Bilinear);
                    _albumArtImage.Pixbuf = pixbuf;
                    return;
                }
            }
            catch { /* ignore tag read errors */ }

            // No embedded art – show a music note placeholder
            _albumArtImage.Pixbuf = null;
            _albumArtImage.SetFromIconName("audio-x-generic", IconSize.Dialog);
        }

        private void LoadTheme()
        {
            // Force dark theme preference at GTK settings level.
            // This overrides system theme on any desktop (GNOME, KDE, XFCE, Wayland, X11).
            var settings = Gtk.Settings.Default;
            if (settings != null)
            {
                settings.ApplicationPreferDarkTheme = true;
            }

            var css = new CssProvider();
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream("MP3Player.Styles.theme.css");
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                css.LoadFromData(reader.ReadToEnd());
                // USER priority = highest possible, beats system/app themes
                StyleContext.AddProviderForScreen(
                    Gdk.Screen.Default, css,
                    Gtk.StyleProviderPriority.User);
            }
        }

        private string? AskText(string title, string prompt, string defaultVal = "")
        {
            using var dlg = new Dialog(title, this, DialogFlags.Modal);
            dlg.AddButton("Cancel", ResponseType.Cancel);
            dlg.AddButton("OK",     ResponseType.Ok);

            var entry = new Entry { Text = defaultVal, ActivatesDefault = true };
            dlg.ContentArea.Add(new Label(prompt) { Xalign = 0, MarginBottom = 4 });
            dlg.ContentArea.Add(entry);
            dlg.ContentArea.Margin = 12;
            dlg.ContentArea.ShowAll();
            dlg.DefaultResponse = ResponseType.Ok;

            return dlg.Run() == (int)ResponseType.Ok ? entry.Text.Trim() : null;
        }

        private bool Confirm(string msg)
        {
            using var d = new MessageDialog(this, DialogFlags.Modal,
                MessageType.Question, ButtonsType.YesNo, msg);
            var r = d.Run();
            d.Destroy();
            return r == (int)ResponseType.Yes;
        }

        private void ShowInfo(string msg)
        {
            using var d = new MessageDialog(this, DialogFlags.Modal,
                MessageType.Info, ButtonsType.Ok, msg);
            d.Run(); d.Destroy();
        }

        private void ShowError(string msg)
        {
            using var d = new MessageDialog(this, DialogFlags.Modal,
                MessageType.Error, ButtonsType.Ok, msg);
            d.Run(); d.Destroy();
        }

        private void Cleanup()
        {
            _uiTimer.Stop();
            _uiTimer.Dispose();
            _audio.Stop();
            _audio.Dispose();
            _plMgr.SaveAll();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  KLAVYE KISAYOLLARI
        // ══════════════════════════════════════════════════════════════════════
        [GLib.ConnectBefore]
        private void OnKeyPress(object sender, KeyPressEventArgs e)
        {
            switch (e.Event.Key)
            {
                case Gdk.Key.space:
                    // Space = Play/Pause
                    OnPlayPause(null, EventArgs.Empty);
                    e.RetVal = true;
                    break;
                    
                case Gdk.Key.Left:
                    // Left arrow = Seek back 5 seconds
                    if (_audio.TotalSeconds > 0)
                    {
                        double newPos = Math.Max(0, _audio.PositionSeconds - 5);
                        _audio.SeekTo(newPos);
                    }
                    e.RetVal = true;
                    break;
                    
                case Gdk.Key.Right:
                    // Right arrow = Seek forward 5 seconds
                    if (_audio.TotalSeconds > 0)
                    {
                        double newPos = Math.Min(_audio.TotalSeconds, _audio.PositionSeconds + 5);
                        _audio.SeekTo(newPos);
                    }
                    e.RetVal = true;
                    break;
                    
                case Gdk.Key.Up:
                    // Up arrow = Volume up 5%
                    _scaleVolume.Value = Math.Min(100, _scaleVolume.Value + 5);
                    e.RetVal = true;
                    break;
                    
                case Gdk.Key.Down:
                    // Down arrow = Volume down 5%
                    _scaleVolume.Value = Math.Max(0, _scaleVolume.Value - 5);
                    e.RetVal = true;
                    break;
                    
                case Gdk.Key.m:
                case Gdk.Key.M:
                    // M = Mute/Unmute toggle
                    if (_scaleVolume.Value > 0)
                    {
                        _prevVolume = _scaleVolume.Value;
                        _scaleVolume.Value = 0;
                    }
                    else
                    {
                        _scaleVolume.Value = _prevVolume > 0 ? _prevVolume : 50;
                    }
                    e.RetVal = true;
                    break;
                    
                case Gdk.Key.n:
                case Gdk.Key.N:
                    // N = Next track
                    PlayNext();
                    e.RetVal = true;
                    break;
                    
                case Gdk.Key.b:
                case Gdk.Key.B:
                    // B = Back/Previous track
                    PlayPrev();
                    e.RetVal = true;
                    break;
                    
                case Gdk.Key.s:
                case Gdk.Key.S:
                    // S = Toggle shuffle
                    OnToggleShuffle(null, EventArgs.Empty);
                    e.RetVal = true;
                    break;
                    
                case Gdk.Key.r:
                case Gdk.Key.R:
                    // R = Toggle repeat
                    OnToggleRepeat(null, EventArgs.Empty);
                    e.RetVal = true;
                    break;
            }
        }
    }

    internal static class Ext
    {
        public static T Also<T>(this T self, Action<T> action) { action(self); return self; }
    }
}
