using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MP3Player.Library
{
    public class TrackEntry
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = "";

        [JsonPropertyName("addedAt")]
        public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    }

    public class PlaylistModel
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [JsonPropertyName("name")]
        public string Name { get; set; } = "New Playlist";

        /// <summary>Legacy flat track list — kept for migration compatibility.</summary>
        [JsonPropertyName("tracks")]
        public List<string> Tracks { get; set; } = new();

        /// <summary>New track entries with date added.</summary>
        [JsonPropertyName("trackEntries")]
        public List<TrackEntry> TrackEntries { get; set; } = new();

        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Migration: merge legacy Tracks list into TrackEntries.
        /// Called after deserialization.
        /// </summary>
        public void MigrateToEntries()
        {
            if (Tracks.Count > 0 && TrackEntries.Count == 0)
            {
                foreach (var t in Tracks)
                    TrackEntries.Add(new TrackEntry { Path = t, AddedAt = CreatedAt });
            }
            // Keep Tracks in sync for any legacy consumers
            Tracks = TrackEntries.Select(e => e.Path).ToList();
        }

        /// <summary>Get the date a track was added to the playlist.</summary>
        public DateTime? GetDateAdded(string path)
        {
            var entry = TrackEntries.FirstOrDefault(e => e.Path == path);
            return entry?.AddedAt;
        }
    }

    /// <summary>
    /// Çoklu playlist yönetimi.
    /// ~/Documents/MP3Player/playlists/ altında her playlist ayrı JSON dosyası.
    /// </summary>
    public class PlaylistManager
    {
        public static readonly string PlaylistsDir =
            System.IO.Path.Combine(MusicLibrary.LibraryDir, "playlists");

        private readonly List<PlaylistModel> _playlists = new();
        public IReadOnlyList<PlaylistModel> Playlists => _playlists;
        public int IndexOf(PlaylistModel pl) => _playlists.IndexOf(pl);

        public PlaylistManager()
        {
            Directory.CreateDirectory(PlaylistsDir);
            Load();
        }

        // ── Yükle ────────────────────────────────────────────────────────────
        public void Load()
        {
            _playlists.Clear();
            foreach (var file in Directory.GetFiles(PlaylistsDir, "*.json").OrderBy(f => f))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var pl   = JsonSerializer.Deserialize<PlaylistModel>(json);
                    if (pl != null)
                    {
                        // Migrate legacy tracks to entries
                        pl.MigrateToEntries();
                        // Filter out deleted files
                        pl.TrackEntries = pl.TrackEntries.Where(e => File.Exists(e.Path)).ToList();
                        pl.Tracks = pl.TrackEntries.Select(e => e.Path).ToList();
                        _playlists.Add(pl);
                    }
                }
                catch { /* bozuk dosyayı atla */ }
            }

            // Hiç playlist yoksa varsayılan bir tane oluştur
            if (_playlists.Count == 0)
            {
                var def = new PlaylistModel { Name = "All Songs" };
                _playlists.Add(def);
                Save(def);
            }

            // Migration: Türkçe playlist isimlerini İngilizce'ye çevir
            MigrateTurkishNames();
        }

        // ── Kaydet ───────────────────────────────────────────────────────────
        public void Save(PlaylistModel pl)
        {
            var path = PlaylistPath(pl);
            var json = JsonSerializer.Serialize(pl,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }

        public void SaveAll() => _playlists.ForEach(Save);

        // ── Oluştur ───────────────────────────────────────────────────────────
        public PlaylistModel Create(string name)
        {
            var pl = new PlaylistModel { Name = name };
            _playlists.Add(pl);
            Save(pl);
            return pl;
        }

        // ── Sil ──────────────────────────────────────────────────────────────
        public void Delete(PlaylistModel pl)
        {
            _playlists.Remove(pl);
            var path = PlaylistPath(pl);
            if (File.Exists(path)) File.Delete(path);
        }

        // ── Yeniden adlandır ──────────────────────────────────────────────────
        public void Rename(PlaylistModel pl, string newName)
        {
            var oldPath = PlaylistPath(pl);
            pl.Name = newName;
            // Eski dosyayı sil, yeni isimle kaydet
            if (File.Exists(oldPath)) File.Delete(oldPath);
            Save(pl);
        }

        // ── Track ekle ────────────────────────────────────────────────────────
        public bool AddTrack(PlaylistModel pl, string filePath)
        {
            if (pl.TrackEntries.Any(e => e.Path == filePath)) return false;
            pl.TrackEntries.Add(new TrackEntry { Path = filePath, AddedAt = DateTime.UtcNow });
            pl.Tracks = pl.TrackEntries.Select(e => e.Path).ToList();
            Save(pl);
            return true;
        }

        // ── Track kaldır ──────────────────────────────────────────────────────
        public void RemoveTrack(PlaylistModel pl, string filePath)
        {
            pl.TrackEntries.RemoveAll(e => e.Path == filePath);
            pl.Tracks = pl.TrackEntries.Select(e => e.Path).ToList();
            Save(pl);
        }

        // ── Kütüphanedeki tüm şarkıları ilk playlist'e senkronize et ─────────
        public void SyncLibraryToDefault(IEnumerable<string> libraryFiles)
        {
            var first = _playlists[0];
            bool changed = false;
            foreach (var f in libraryFiles)
            {
                if (!first.TrackEntries.Any(e => e.Path == f))
                {
                    first.TrackEntries.Add(new TrackEntry { Path = f, AddedAt = DateTime.UtcNow });
                    changed = true;
                }
            }
            if (changed)
            {
                first.Tracks = first.TrackEntries.Select(e => e.Path).ToList();
                Save(first);
            }
        }

        private static string PlaylistPath(PlaylistModel pl) =>
            System.IO.Path.Combine(PlaylistsDir, $"{pl.Id}.json");

        /// <summary>
        /// Migration: Türkçe playlist isimlerini İngilizce'ye çevir
        /// </summary>
        private void MigrateTurkishNames()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Tüm Şarkılar",   "All Songs" },
                { "🎵 Tüm Şarkılar", "🎵 All Songs" },
                { "Yeni Playlist",   "New Playlist" },
            };

            foreach (var pl in _playlists)
            {
                if (map.TryGetValue(pl.Name, out var eng))
                {
                    pl.Name = eng;
                    Save(pl);
                }
            }
        }
    }
}
