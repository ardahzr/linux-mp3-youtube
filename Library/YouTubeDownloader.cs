using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MP3Player.Library
{
    /// <summary>
    /// yt-dlp + ffmpeg kullanarak YouTube (ve diğer desteklenen siteler) linklerinden
    /// en yüksek kalitede ses indirir, MP3'e dönüştürür ve ~/Documents/MP3Player'a kaydeder.
    /// </summary>
    public class YouTubeDownloader
    {
        private static readonly string YtDlpBin  = "/usr/bin/yt-dlp";
        private static readonly string FfmpegBin = "/usr/bin/ffmpeg";

        /// <summary>İlerleme mesajı (UI'ye yansıtılır).</summary>
        public event Action<string>? ProgressMessage;

        /// <summary>İndirme tamamlandığında kaydedilen dosyanın yolu döner.</summary>
        public event Action<string>? DownloadCompleted;

        /// <summary>Hata olduğunda mesaj döner.</summary>
        public event Action<string>? DownloadFailed;

        // ── Tek URL indir ─────────────────────────────────────────────────────
        public Task DownloadAsync(string url, string outputDir,
                                  CancellationToken ct = default)
            => Task.Run(() => Download(url, outputDir, ct), ct);

        private void Download(string url, string outputDir, CancellationToken ct)
        {
            Directory.CreateDirectory(outputDir);

            // Çıktı şablonu: <başlık>.mp3
            // yt-dlp -x --audio-format mp3 --audio-quality 0 (en yüksek VBR)
            // --ffmpeg-location ile ffmpeg konumunu belirt
            // --embed-thumbnail --add-metadata → kapak ve etiket ekle
            var args = string.Join(" ",
                $"\"{url}\"",
                "-x",
                "--audio-format mp3",
                "--audio-quality 0",
                $"--ffmpeg-location \"{FfmpegBin}\"",
                "--embed-thumbnail",
                "--add-metadata",
                "--parse-metadata \"%(title)s:%(meta_title)s\"",
                "--no-playlist",
                $"-o \"{outputDir}/%(title)s.%(ext)s\"",
                "--newline"          // her satırda ilerleme
            );

            var psi = new ProcessStartInfo(YtDlpBin, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            string? lastFile = null;

            using var proc = new Process { StartInfo = psi };

            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                var line = e.Data.Trim();
                if (string.IsNullOrEmpty(line)) return;

                // "Destination:" satırından dosya adını yakala
                var destMatch = Regex.Match(line, @"Destination:\s*(.+\.mp3)", RegexOptions.IgnoreCase);
                if (destMatch.Success)
                    lastFile = destMatch.Groups[1].Value.Trim();

                // "[ExtractAudio] Destination:" satırından da yakala
                var extMatch = Regex.Match(line, @"\[ExtractAudio\] Destination:\s*(.+\.mp3)", RegexOptions.IgnoreCase);
                if (extMatch.Success)
                    lastFile = extMatch.Groups[1].Value.Trim();

                // İlerleme yüzdesi: "[download]  45.2% ..."
                var pctMatch = Regex.Match(line, @"\[download\]\s+([\d\.]+)%");
                if (pctMatch.Success)
                    ProgressMessage?.Invoke($"⬇ İndiriliyor: %{pctMatch.Groups[1].Value}");
                else if (line.StartsWith("[ffmpeg]") || line.StartsWith("[ExtractAudio]"))
                    ProgressMessage?.Invoke($"🔄 Dönüştürülüyor…");
                else if (line.StartsWith("[download]") && line.Contains("100%"))
                    ProgressMessage?.Invoke("✅ İndirme tamamlandı, dönüştürülüyor…");
            };

            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is { Length: > 0 } msg)
                    ProgressMessage?.Invoke($"⚠ {msg}");
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

            if (proc.ExitCode != 0)
            {
                DownloadFailed?.Invoke($"yt-dlp çıkış kodu: {proc.ExitCode}");
                return;
            }

            // Dosyayı bul (lastFile boşsa dizini tara)
            if (lastFile is null || !File.Exists(lastFile))
            {
                var mp3s = Directory.GetFiles(outputDir, "*.mp3");
                if (mp3s.Length > 0)
                {
                    // En yeni dosyayı al
                    Array.Sort(mp3s, (a, b) =>
                        File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)));
                    lastFile = mp3s[0];
                }
            }

            if (lastFile is not null && File.Exists(lastFile))
                DownloadCompleted?.Invoke(lastFile);
            else
                DownloadFailed?.Invoke("MP3 dosyası bulunamadı.");
        }

        // ── Playlist / kanal indirme ──────────────────────────────────────────
        public Task DownloadPlaylistAsync(string url, string outputDir,
                                          CancellationToken ct = default)
            => Task.Run(() => DownloadPlaylist(url, outputDir, ct), ct);

        private void DownloadPlaylist(string url, string outputDir, CancellationToken ct)
        {
            Directory.CreateDirectory(outputDir);

            var args = string.Join(" ",
                $"\"{url}\"",
                "-x",
                "--audio-format mp3",
                "--audio-quality 0",
                $"--ffmpeg-location \"{FfmpegBin}\"",
                "--embed-thumbnail",
                "--add-metadata",
                "--yes-playlist",
                $"-o \"{outputDir}/%(playlist_index)02d - %(title)s.%(ext)s\"",
                "--newline"
            );

            var psi = new ProcessStartInfo(YtDlpBin, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            using var proc = new Process { StartInfo = psi };

            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                var line = e.Data.Trim();
                if (string.IsNullOrEmpty(line)) return;

                var extMatch = Regex.Match(line, @"\[ExtractAudio\] Destination:\s*(.+\.mp3)", RegexOptions.IgnoreCase);
                if (extMatch.Success)
                    DownloadCompleted?.Invoke(extMatch.Groups[1].Value.Trim());

                var pctMatch = Regex.Match(line, @"\[download\]\s+([\d\.]+)%");
                if (pctMatch.Success)
                    ProgressMessage?.Invoke($"⬇ İndiriliyor: %{pctMatch.Groups[1].Value}");
                else if (line.StartsWith("[ffmpeg]") || line.StartsWith("[ExtractAudio]"))
                    ProgressMessage?.Invoke("🔄 Dönüştürülüyor…");
            };

            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is { Length: > 0 } msg)
                    ProgressMessage?.Invoke($"⚠ {msg}");
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            while (!proc.HasExited)
            {
                if (ct.IsCancellationRequested) { proc.Kill(true); return; }
                Thread.Sleep(100);
            }

            proc.WaitForExit();
            if (proc.ExitCode != 0)
                DownloadFailed?.Invoke($"yt-dlp çıkış kodu: {proc.ExitCode}");
        }

        public static bool IsAvailable() => File.Exists(YtDlpBin) && File.Exists(FfmpegBin);
    }
}
