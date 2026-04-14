using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Dalamud.Plugin.Services;

namespace UmbraNightlife.Services;

/// <summary>
/// Persistent per-character-agnostic preferences:
/// - <see cref="Favorites"/> — venues pinned to the top of the menu.
/// - <see cref="Hidden"/> — venues the player doesn't want to see at all.
///
/// Storage is a single JSON file in the plugin config dir. Read-through on every
/// access is fine (tiny file, HashSet lookups). Writes are synchronous and rare.
/// </summary>
public sealed class FavoritesStore
{
    private const string FileName = "preferences.json";

    private readonly string _filePath;
    private readonly IPluginLog _log;

    private HashSet<string> _favorites = new();
    private HashSet<string> _hidden = new();

    public FavoritesStore(string configDir, IPluginLog log)
    {
        _log = log;
        Directory.CreateDirectory(configDir);
        _filePath = Path.Combine(configDir, FileName);
        Load();
    }

    public IReadOnlyCollection<string> Favorites => _favorites;
    public IReadOnlyCollection<string> Hidden => _hidden;

    public bool IsFavorite(string id) => _favorites.Contains(id);
    public bool IsHidden(string id) => _hidden.Contains(id);

    /// <summary>Flips favorite state. Also removes from hidden if it was there.</summary>
    public void ToggleFavorite(string id)
    {
        if (!_favorites.Add(id)) _favorites.Remove(id);
        _hidden.Remove(id);
        Save();
    }

    /// <summary>Flips hidden state. Also removes from favorites if it was there.</summary>
    public void ToggleHidden(string id)
    {
        if (!_hidden.Add(id)) _hidden.Remove(id);
        _favorites.Remove(id);
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            var data = JsonSerializer.Deserialize<Persisted>(json);
            if (data is null) return;
            _favorites = new HashSet<string>(data.Favorites ?? new List<string>());
            _hidden = new HashSet<string>(data.Hidden ?? new List<string>());
        }
        catch (System.Exception ex)
        {
            _log.Warning($"[Nightlife] Could not load preferences: {ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            var data = new Persisted
            {
                Favorites = new List<string>(_favorites),
                Hidden = new List<string>(_hidden),
            };
            File.WriteAllText(_filePath, JsonSerializer.Serialize(data));
        }
        catch (System.Exception ex)
        {
            _log.Warning($"[Nightlife] Could not save preferences: {ex.Message}");
        }
    }

    private sealed class Persisted
    {
        public List<string>? Favorites { get; set; }
        public List<string>? Hidden { get; set; }
    }
}
