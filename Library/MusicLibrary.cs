using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MP3Player.Library
{
    /// <summary>
    /// ~/Documents/MP3Player  klasörünü yönetir.
    /// Buraya kopyalanan tüm MP3 / ses dosyaları kalıcı kütüphane olarak saklanır.
    /// Playlist JSON olarak aynı klasörde persist edilir.
    /// </summary>
    public class MusicLibrary
    {
        // ── Sabit yollar ─────────────────────────────────────────────────────
        public static readonly string LibraryDir =
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "MP3Player");

        private static readonly string PlaylistFile =
            System.IO.Path.Combine(LibraryDir, "playlist.json");

        public static readonly string[] SupportedExtensions =
            { ".mp3", ".flac", ".ogg", ".wav", ".m4a", ".aac", ".opus" };

        // ── Kütüphane başlat ─────────────────────────────────────────────────
        public MusicLibrary()
        {
            Directory.CreateDirectory(LibraryDir);
        }

        // ── Klasördeki tüm ses dosyalarını tara ───────────────────────────────
        public List<string> ScanLibrary()
        {
            return Directory.EnumerateFiles(LibraryDir, "*.*", SearchOption.AllDirectories)
                .Where(f => SupportedExtensions.Contains(
                    System.IO.Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f)
                .ToList();
        }

        /// <summary>
        /// Dosyaları kütüphane klasörüne kopyalar (zaten oradaysa kopyalamaz).
        /// Kopyalanan dosyaların yeni yollarını döner.
        /// </summary>
        public List<string> ImportFiles(IEnumerable<string> sourcePaths)
        {
            var imported = new List<string>();
            foreach (var src in sourcePaths)
            {
                if (!File.Exists(src)) continue;

                var ext = System.IO.Path.GetExtension(src).ToLowerInvariant();
                if (!SupportedExtensions.Contains(ext)) continue;

                var destName = System.IO.Path.GetFileName(src);
                var dest     = System.IO.Path.Combine(LibraryDir, destName);

                // Aynı isimde dosya varsa _2, _3 ... ekle
                int counter = 1;
                while (File.Exists(dest) && !IsSameFile(src, dest))
                {
                    var nameOnly = System.IO.Path.GetFileNameWithoutExtension(src);
                    dest = System.IO.Path.Combine(LibraryDir, $"{nameOnly}_{++counter}{ext}");
                }

                if (!File.Exists(dest))
                    File.Copy(src, dest);

                imported.Add(dest);
            }
            return imported;
        }

        // ── Playlist kaydet ───────────────────────────────────────────────────
        public void SavePlaylist(IEnumerable<string> paths)
        {
            var json = JsonSerializer.Serialize(paths.ToList(),
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(PlaylistFile, json);
        }

        // ── Playlist yükle ────────────────────────────────────────────────────
        public List<string> LoadPlaylist()
        {
            if (!File.Exists(PlaylistFile)) return new List<string>();
            try
            {
                var json = File.ReadAllText(PlaylistFile);
                var list = JsonSerializer.Deserialize<List<string>>(json);
                // Silinmiş dosyaları filtrele
                return list?.Where(File.Exists).ToList() ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        // ── Dosyayı kütüphaneden sil ──────────────────────────────────────────
        public void DeleteFile(string path)
        {
            if (File.Exists(path) && path.StartsWith(LibraryDir))
                File.Delete(path);
        }

        // ── Yardımcı: aynı dosya mı? ──────────────────────────────────────────
        private static bool IsSameFile(string a, string b)
        {
            var ia = new FileInfo(a);
            var ib = new FileInfo(b);
            return ia.Length == ib.Length && ia.LastWriteTimeUtc == ib.LastWriteTimeUtc;
        }
    }
}
