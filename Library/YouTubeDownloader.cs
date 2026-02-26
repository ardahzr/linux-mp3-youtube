using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MP3Player.Library
{
    /// <summary>
    /// Downloads audio from YouTube and Spotify URLs as Opus (highest quality, smaller size).
    /// YouTube: uses yt-dlp directly.
    /// Spotify: scrapes embed page for track info, then searches YouTube via yt-dlp.
    /// </summary>
    public class YouTubeDownloader
    {
        private static readonly string YtDlpBin  = "/usr/bin/yt-dlp";
        private static readonly string FfmpegBin = "/usr/bin/ffmpeg";
        private static readonly HttpClient Http  = new();

        // ── Speed flags shared by ALL yt-dlp invocations ──────────────────────
        private static readonly string SpeedFlags = string.Join(" ",
            "--concurrent-fragments 8",     // download 8 fragments at once per video
            "--buffer-size 64K",            // larger download buffer
            "--no-check-certificates",      // skip SSL verify (faster handshake)
            "--extractor-retries 3",        // retry on transient errors
            "--socket-timeout 15",          // don't hang on slow connections
            "--retries 3",                  // retry download on failure
            "--no-warnings"                 // suppress noisy output
        );

        // ── Events ────────────────────────────────────────────────────────────
        /// <summary>Progress percentage 0‑100 (for progress bar).</summary>
        public event Action<double>? ProgressPercent;

        /// <summary>Status message (shown in status label, replaces old line).</summary>
        public event Action<string>? StatusMessage;

        /// <summary>Log message (appended to log area — only important events).</summary>
        public event Action<string>? LogMessage;

        /// <summary>Fires for each completed audio file path.</summary>
        public event Action<string>? DownloadCompleted;

        /// <summary>Error message.</summary>
        public event Action<string>? DownloadFailed;

        /// <summary>All downloads finished.</summary>
        public event Action? AllCompleted;

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Detect URL type and download accordingly.
        /// </summary>
        public Task DownloadAsync(string url, string outputDir,
                                  bool isPlaylist, CancellationToken ct = default)
            => Task.Run(async () =>
            {
                try
                {
                    if (IsSpotifyUrl(url))
                        await DownloadSpotify(url, outputDir, ct);
                    else if (isPlaylist)
                        DownloadYouTubePlaylist(url, outputDir, ct);
                    else
                        DownloadYouTube(url, outputDir, ct);

                    if (!ct.IsCancellationRequested)
                        AllCompleted?.Invoke();
                }
                catch (OperationCanceledException) { /* cancelled */ }
                catch (Exception ex)
                {
                    DownloadFailed?.Invoke(ex.Message);
                }
            }, ct);

        public static bool IsAvailable() => File.Exists(YtDlpBin) && File.Exists(FfmpegBin);

        public static bool IsSpotifyUrl(string url)
            => url.Contains("open.spotify.com/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("spotify.com/", StringComparison.OrdinalIgnoreCase);

        // ══════════════════════════════════════════════════════════════════════
        //  YOUTUBE — Single video
        // ══════════════════════════════════════════════════════════════════════
        private void DownloadYouTube(string url, string outputDir, CancellationToken ct)
        {
            Directory.CreateDirectory(outputDir);
            LogMessage?.Invoke($"Downloading from YouTube…");

            var args = string.Join(" ",
                $"\"{url}\"",
                "-f \"bestaudio[acodec=opus]/bestaudio\"",
                "-x",
                "--audio-format opus",
                "--audio-quality 0",
                $"--ffmpeg-location \"{FfmpegBin}\"",
                "--embed-thumbnail",
                "--add-metadata",
                "--parse-metadata \"%(title)s:%(meta_title)s\"",
                "--no-playlist",
                $"-o \"{outputDir}/%(title)s.%(ext)s\"",
                "--newline",
                "--progress-template \"download:%(progress._percent_str)s %(progress._speed_str)s %(progress._eta_str)s\"",
                SpeedFlags);

            string? lastFile = null;
            RunYtDlp(args, ref lastFile, null, outputDir, ct);

            if (ct.IsCancellationRequested) return;

            if (lastFile is null || !File.Exists(lastFile))
                lastFile = FindNewestAudio(outputDir);

            if (lastFile is not null && File.Exists(lastFile))
                DownloadCompleted?.Invoke(lastFile);
            else
                DownloadFailed?.Invoke("Audio file not found.");
        }

        // ══════════════════════════════════════════════════════════════════════
        //  YOUTUBE — Playlist (parallel download)
        // ══════════════════════════════════════════════════════════════════════
        private void DownloadYouTubePlaylist(string url, string outputDir, CancellationToken ct)
        {
            Directory.CreateDirectory(outputDir);
            LogMessage?.Invoke($"Fetching YouTube playlist info…");
            StatusMessage?.Invoke("Fetching playlist video list…");

            // Step 1: Extract video URLs with --flat-playlist
            var videoUrls = GetYouTubePlaylistUrls(url, ct);

            if (videoUrls.Count == 0)
            {
                // Fallback: use old sequential method
                LogMessage?.Invoke("Could not extract playlist URLs, falling back to sequential…");
                DownloadYouTubePlaylistSequential(url, outputDir, ct);
                return;
            }

            _totalCount = videoUrls.Count;
            _completedCount = 0;
            LogMessage?.Invoke($"Found {_totalCount} video(s) — downloading {MaxParallel} at a time");

            // Step 2: Parallel download with semaphore throttle
            var semaphore = new SemaphoreSlim(MaxParallel);
            var tasks = new List<Task>();

            for (int i = 0; i < videoUrls.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                semaphore.Wait(ct);

                var idx = i;
                var vidUrl = videoUrls[i];

                var task = Task.Run(() =>
                {
                    try
                    {
                        DownloadOneYouTubeVideo(vidUrl, idx, outputDir, ct);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, ct);

                tasks.Add(task);
            }

            Task.WaitAll(tasks.ToArray(), ct);
            ProgressPercent?.Invoke(100);
        }

        private void DownloadOneYouTubeVideo(string videoUrl, int index,
            string outputDir, CancellationToken ct)
        {
            var args = string.Join(" ",
                $"\"{videoUrl}\"",
                "-f \"bestaudio[acodec=opus]/bestaudio\"",
                "-x",
                "--audio-format opus",
                "--audio-quality 0",
                $"--ffmpeg-location \"{FfmpegBin}\"",
                "--embed-thumbnail",
                "--add-metadata",
                "--parse-metadata \"%(title)s:%(meta_title)s\"",
                "--no-playlist",
                $"-o \"{outputDir}/%(title)s.%(ext)s\"",
                "--newline",
                "--progress-template \"download:%(progress._percent_str)s %(progress._speed_str)s %(progress._eta_str)s\"",
                SpeedFlags);

            string? lastFile = null;
            RunYtDlp(args, ref lastFile, $"[{index + 1}/{_totalCount}]", outputDir, ct);

            if (ct.IsCancellationRequested) return;

            if (lastFile is null || !File.Exists(lastFile))
                lastFile = FindNewestAudio(outputDir);

            if (lastFile is not null && File.Exists(lastFile))
            {
                DownloadCompleted?.Invoke(lastFile);
                var done = Interlocked.Increment(ref _completedCount);
                ProgressPercent?.Invoke((double)done / _totalCount * 100.0);
                StatusMessage?.Invoke($"✓ {done}/{_totalCount} completed");
                LogMessage?.Invoke($"✓ [{done}/{_totalCount}] {Path.GetFileName(lastFile)}");
            }
        }

        /// <summary>
        /// Uses yt-dlp --flat-playlist to quickly get all video URLs without downloading.
        /// </summary>
        private List<string> GetYouTubePlaylistUrls(string playlistUrl, CancellationToken ct)
        {
            var urls = new List<string>();

            var args = string.Join(" ",
                $"\"{playlistUrl}\"",
                "--flat-playlist",
                "--print url",
                "--yes-playlist",
                "--no-warnings",
                "--no-check-certificates");

            var psi = new ProcessStartInfo(YtDlpBin, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            while (!proc.StandardOutput.EndOfStream)
            {
                if (ct.IsCancellationRequested)
                {
                    proc.Kill(true);
                    return urls;
                }

                var line = proc.StandardOutput.ReadLine()?.Trim();
                if (!string.IsNullOrEmpty(line) && line.StartsWith("http"))
                    urls.Add(line);
            }

            proc.WaitForExit();
            return urls;
        }

        /// <summary>Fallback: old sequential download via single yt-dlp process.</summary>
        private void DownloadYouTubePlaylistSequential(string url, string outputDir, CancellationToken ct)
        {
            var args = string.Join(" ",
                $"\"{url}\"",
                "-f \"bestaudio[acodec=opus]/bestaudio\"",
                "-x",
                "--audio-format opus",
                "--audio-quality 0",
                $"--ffmpeg-location \"{FfmpegBin}\"",
                "--embed-thumbnail",
                "--add-metadata",
                "--yes-playlist",
                $"-o \"{outputDir}/%(playlist_index)02d - %(title)s.%(ext)s\"",
                "--newline",
                "--progress-template \"download:%(progress._percent_str)s %(progress._speed_str)s %(progress._eta_str)s\"",
                SpeedFlags);

            string? lastFile = null;
            RunYtDlp(args, ref lastFile, null, outputDir, ct);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  SPOTIFY — Track / Album / Playlist (parallel download)
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>Max concurrent yt-dlp downloads (8 × 8 fragments = 64 connections).</summary>
        private const int MaxParallel = 16;

        private int _completedCount;
        private int _totalCount;

        private async Task DownloadSpotify(string url, string outputDir, CancellationToken ct)
        {
            Directory.CreateDirectory(outputDir);
            LogMessage?.Invoke("Fetching Spotify track info…");
            StatusMessage?.Invoke("Connecting to Spotify…");

            var tracks = await GetSpotifyTracks(url, ct);

            if (tracks.Count == 0)
            {
                DownloadFailed?.Invoke("Could not fetch track info from Spotify.");
                return;
            }

            _totalCount = tracks.Count;
            _completedCount = 0;

            LogMessage?.Invoke($"Found {_totalCount} track(s) — downloading {MaxParallel} at a time");

            // Single track → just download directly
            if (tracks.Count == 1)
            {
                await DownloadOneSpotifyTrack(tracks[0].title, tracks[0].artist,
                    0, outputDir, ct);
                ProgressPercent?.Invoke(100);
                return;
            }

            // Parallel download with semaphore throttle
            using var semaphore = new SemaphoreSlim(MaxParallel);
            var tasks = new List<Task>();

            for (int i = 0; i < tracks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                await semaphore.WaitAsync(ct);

                var idx = i;
                var (title, artist) = tracks[i];

                var task = Task.Run(async () =>
                {
                    try
                    {
                        await DownloadOneSpotifyTrack(title, artist, idx, outputDir, ct);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, ct);

                tasks.Add(task);
            }

            await Task.WhenAll(tasks);
            ProgressPercent?.Invoke(100);
        }

        private Task DownloadOneSpotifyTrack(string title, string artist,
            int index, string outputDir, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                var searchQuery = string.IsNullOrEmpty(artist)
                    ? title
                    : $"{artist} - {title}";

                LogMessage?.Invoke($"» [{index + 1}/{_totalCount}] {searchQuery}");

                // Sanitize filename
                var safeFilename = SanitizeFilename(searchQuery);

                var args = string.Join(" ",
                    $"\"ytsearch1:{EscapeArg(searchQuery)}\"",
                    "-f \"bestaudio[acodec=opus]/bestaudio\"",
                    "-x",
                    "--audio-format opus",
                    "--audio-quality 0",
                    $"--ffmpeg-location \"{FfmpegBin}\"",
                    "--add-metadata",
                    $"--parse-metadata \"%(title)s:%(meta_title)s\"",
                    $"-o \"{outputDir}/{safeFilename}.%(ext)s\"",
                    "--newline",
                    "--progress-template \"download:%(progress._percent_str)s %(progress._speed_str)s %(progress._eta_str)s\"",
                    SpeedFlags);

                string? lastFile = null;
                RunYtDlp(args, ref lastFile, searchQuery, outputDir, ct);

                if (ct.IsCancellationRequested) return;

                if (lastFile is null || !File.Exists(lastFile))
                {
                    // Search for file by name pattern (opus preferred, fallback to others)
                    foreach (var ext in new[] { "opus", "ogg", "webm", "m4a" })
                    {
                        var matches = Directory.GetFiles(outputDir, $"{safeFilename}*.{ext}");
                        if (matches.Length > 0) { lastFile = matches[0]; break; }
                    }
                }

                if (lastFile is not null && File.Exists(lastFile))
                {
                    DownloadCompleted?.Invoke(lastFile);
                    var done = Interlocked.Increment(ref _completedCount);
                    ProgressPercent?.Invoke((double)done / _totalCount * 100.0);
                    StatusMessage?.Invoke($"✓ {done}/{_totalCount} completed");
                    LogMessage?.Invoke($"✓ [{done}/{_totalCount}] {Path.GetFileName(lastFile)}");
                }
                else
                {
                    LogMessage?.Invoke($"Could not find: {searchQuery}");
                }
            }, ct);
        }

        /// <summary>
        /// Scrapes the Spotify embed page (no API key needed) to extract track info.
        /// Works for tracks, albums, and playlists.
        /// </summary>
        private async Task<List<(string title, string artist)>> GetSpotifyTracks(
            string url, CancellationToken ct)
        {
            var result = new List<(string title, string artist)>();

            try
            {
                // Convert URL to embed URL
                // https://open.spotify.com/track/xxx → https://open.spotify.com/embed/track/xxx
                var embedUrl = url.Replace("open.spotify.com/", "open.spotify.com/embed/");
                if (embedUrl.Contains("?"))
                    embedUrl = embedUrl.Substring(0, embedUrl.IndexOf('?'));

                var html = await Http.GetStringAsync(embedUrl, ct);

                // Extract __NEXT_DATA__ JSON
                var match = Regex.Match(html, @"<script id=""__NEXT_DATA__""[^>]*>(.+?)</script>");
                if (!match.Success) return result;

                using var doc = JsonDocument.Parse(match.Groups[1].Value);
                var entity = doc.RootElement
                    .GetProperty("props")
                    .GetProperty("pageProps")
                    .GetProperty("state")
                    .GetProperty("data")
                    .GetProperty("entity");

                var entityType = entity.GetProperty("type").GetString() ?? "";

                if (entityType == "track")
                {
                    // Single track: get title + artists
                    var title = entity.GetProperty("title").GetString() ?? "";
                    var artist = "";

                    if (entity.TryGetProperty("artists", out var artists) &&
                        artists.ValueKind == JsonValueKind.Array &&
                        artists.GetArrayLength() > 0)
                    {
                        artist = artists[0].GetProperty("name").GetString() ?? "";
                    }
                    else if (entity.TryGetProperty("subtitle", out var sub) &&
                             sub.ValueKind == JsonValueKind.String)
                    {
                        artist = sub.GetString() ?? "";
                    }

                    if (!string.IsNullOrEmpty(title))
                        result.Add((title, artist));
                }
                else if (entityType is "playlist" or "album")
                {
                    // Multiple tracks from trackList
                    if (entity.TryGetProperty("trackList", out var trackList) &&
                        trackList.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var track in trackList.EnumerateArray())
                        {
                            var title = track.GetProperty("title").GetString() ?? "";
                            var artist = "";

                            if (track.TryGetProperty("subtitle", out var sub) &&
                                sub.ValueKind == JsonValueKind.String)
                            {
                                artist = sub.GetString() ?? "";
                            }

                            if (!string.IsNullOrEmpty(title))
                                result.Add((title, artist));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"Spotify scrape error: {ex.Message}");
            }

            return result;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  yt-dlp runner — shared between YouTube & Spotify
        // ══════════════════════════════════════════════════════════════════════
        private void RunYtDlp(string args, ref string? lastFile,
                              string? trackLabel, string outputDir,
                              CancellationToken ct)
        {
            var psi = new ProcessStartInfo(YtDlpBin, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            string? captured = lastFile;

            using var proc = new Process { StartInfo = psi };

            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                var line = e.Data.Trim();
                if (string.IsNullOrEmpty(line)) return;

                // Capture destination file (any audio extension)
                var destMatch = Regex.Match(line, @"Destination:\s*(.+\.(opus|ogg|webm|m4a|mp3))", RegexOptions.IgnoreCase);
                if (destMatch.Success)
                    captured = destMatch.Groups[1].Value.Trim();

                var extMatch = Regex.Match(line, @"\[ExtractAudio\] Destination:\s*(.+\.(opus|ogg|webm|m4a|mp3))", RegexOptions.IgnoreCase);
                if (extMatch.Success)
                    captured = extMatch.Groups[1].Value.Trim();

                // Progress: "download: 45.2% 1.5MiB/s 00:12"
                var pctMatch = Regex.Match(line, @"download:\s*([\d\.]+)%\s*(.*)");
                if (pctMatch.Success)
                {
                    if (double.TryParse(pctMatch.Groups[1].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var pct))
                    {
                        ProgressPercent?.Invoke(pct);
                    }

                    var extra = pctMatch.Groups[2].Value.Trim();
                    var label = trackLabel ?? "Downloading";
                    StatusMessage?.Invoke($"↓ {label}  {pctMatch.Groups[1].Value}%  {extra}");
                    return;
                }

                // Legacy format: "[download]  45.2% of 5.2MiB at 1.5MiB/s ETA 00:12"
                var legacyPct = Regex.Match(line, @"\[download\]\s+([\d\.]+)%\s+of\s+(.+)");
                if (legacyPct.Success)
                {
                    if (double.TryParse(legacyPct.Groups[1].Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var pct))
                    {
                        ProgressPercent?.Invoke(pct);
                    }
                    var label = trackLabel ?? "Downloading";
                    StatusMessage?.Invoke($"↓ {label}  {legacyPct.Groups[1].Value}%  {legacyPct.Groups[2].Value}");
                    return;
                }

                // Conversion stage
                if (line.StartsWith("[ffmpeg]") || line.StartsWith("[ExtractAudio]"))
                {
                    StatusMessage?.Invoke("Converting to Opus…");
                    return;
                }

                // Download finished line
                if (line.StartsWith("[download]") && line.Contains("100%"))
                {
                    ProgressPercent?.Invoke(100);
                    StatusMessage?.Invoke("✓ Download complete, converting…");
                    return;
                }

                // Track title from [download]
                var titleMatch = Regex.Match(line, @"\[download\] Downloading item (\d+) of (\d+)");
                if (titleMatch.Success)
                {
                    LogMessage?.Invoke($"Track {titleMatch.Groups[1].Value} of {titleMatch.Groups[2].Value}");
                    return;
                }
            };

            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is { Length: > 0 } msg)
                {
                    // Suppress noisy warnings
                    if (msg.Contains("WARNING:", StringComparison.OrdinalIgnoreCase)) return;
                    LogMessage?.Invoke($"! {msg}");
                }
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            while (!proc.HasExited)
            {
                if (ct.IsCancellationRequested)
                {
                    proc.Kill(true);
                    return;
                }
                Thread.Sleep(100);
            }

            proc.WaitForExit();
            lastFile = captured;

            // If file was captured (download succeeded) but exit code is non-zero,
            // it's likely a harmless postprocessing error (e.g. opus→opus conversion).
            // Only report failure if we have NO file at all.
            if (proc.ExitCode != 0 && !ct.IsCancellationRequested)
            {
                if (captured is null || !File.Exists(captured))
                {
                    // Also check outputDir for any new audio file
                    var fallback = FindNewestAudio(outputDir);
                    if (fallback is not null)
                        lastFile = fallback;
                    else
                        DownloadFailed?.Invoke($"yt-dlp exit code: {proc.ExitCode}");
                }
                // else: file exists, swallow the postprocessing error
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string? FindNewestAudio(string dir)
        {
            var extensions = new[] { "*.opus", "*.ogg", "*.webm", "*.m4a", "*.mp3" };
            var allFiles = new List<string>();
            foreach (var ext in extensions)
                allFiles.AddRange(Directory.GetFiles(dir, ext));
            if (allFiles.Count == 0) return null;
            allFiles.Sort((a, b) =>
                File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)));
            return allFiles[0];
        }

        private static string SanitizeFilename(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var clean = string.Join("", name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c));
            return clean.Length > 200 ? clean.Substring(0, 200) : clean;
        }

        private static string EscapeArg(string s)
            => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
