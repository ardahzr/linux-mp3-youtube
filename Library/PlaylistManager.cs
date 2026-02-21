using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MP3Player.Library
{
    public class PlaylistModel
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [JsonPropertyName("name")]
        public string Name { get; set; } = "New Playlist";

        [JsonPropertyName("tracks")]
        public List<string> Tracks { get; set; } = new();

        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
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
                        // Silinmiş dosyaları filtrele
                        pl.Tracks = pl.Tracks.Where(File.Exists).ToList();
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
            if (pl.Tracks.Contains(filePath)) return false;
            pl.Tracks.Add(filePath);
            Save(pl);
            return true;
        }

        // ── Track kaldır ──────────────────────────────────────────────────────
        public void RemoveTrack(PlaylistModel pl, string filePath)
        {
            pl.Tracks.Remove(filePath);
            Save(pl);
        }

        // ── Kütüphanedeki tüm şarkıları ilk playlist'e senkronize et ─────────
        public void SyncLibraryToDefault(IEnumerable<string> libraryFiles)
        {
            var first = _playlists[0];
            bool changed = false;
            foreach (var f in libraryFiles)
            {
                if (!first.Tracks.Contains(f))
                {
                    first.Tracks.Add(f);
                    changed = true;
                }
            }
            if (changed) Save(first);
        }

        private static string PlaylistPath(PlaylistModel pl) =>
            System.IO.Path.Combine(PlaylistsDir, $"{pl.Id}.json");
    }
}
