using System.IO;
using System.Text.Json;
using Key2Gif.Models;

namespace Key2Gif.Services;

public class RecentGifEntry
{
    public string PreviewUrl { get; set; } = "";
    public string TinyUrl { get; set; } = "";
    public string FullUrl { get; set; } = "";
    public string Title { get; set; } = "";
}

public class RecentTracker
{
    private readonly string _filePath;
    private readonly int _maxItems;
    private List<RecentGifEntry> _recentGifs = [];

    public IReadOnlyList<RecentGifEntry> RecentGifs => _recentGifs;
    public event Action? Changed;

    public RecentTracker(int maxItems = 50)
    {
        _maxItems = maxItems;
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Key2Gif");
        Directory.CreateDirectory(appData);
        _filePath = Path.Combine(appData, "recent.json");
        Load();
    }

    public void AddGif(RecentGifEntry gif)
    {
        // Remove existing entry with same FullUrl (LRU pattern)
        _recentGifs.RemoveAll(g => g.FullUrl == gif.FullUrl);
        _recentGifs.Insert(0, gif);
        if (_recentGifs.Count > _maxItems)
            _recentGifs.RemoveAt(_recentGifs.Count - 1);
        Save();
        Changed?.Invoke();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Array)
                {
                    // Legacy format: just a list of emoji strings — skip
                    _recentGifs = [];
                }
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("gifs", out var gifsProp))
                        _recentGifs = JsonSerializer.Deserialize<List<RecentGifEntry>>(gifsProp.GetRawText()) ?? [];
                    else
                        _recentGifs = [];
                }
            }
        }
        catch
        {
            _recentGifs = [];
        }
    }

    private void Save()
    {
        try
        {
            var data = new { gifs = _recentGifs };
            var json = JsonSerializer.Serialize(data);
            File.WriteAllText(_filePath, json);
        }
        catch { }
    }
}
