using System.IO;
using System.Text.Json;
using Key2Gif.Models;

namespace Key2Gif.Services;

public class RecentTracker
{
    private readonly string _filePath;
    private readonly int _maxItems;
    private List<string> _recent = [];

    public IReadOnlyList<string> RecentEmojis => _recent;
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

    public void Add(string emoji)
    {
        _recent.Remove(emoji);
        _recent.Insert(0, emoji);
        if (_recent.Count > _maxItems)
            _recent.RemoveAt(_recent.Count - 1);
        Save();
        Changed?.Invoke();
    }

    public EmojiCategory GetRecentCategory(EmojiDatabase db)
    {
        var allEmojis = db.GetAllEmojis()
            .GroupBy(e => e.Emoji)
            .ToDictionary(g => g.Key, g => g.First());
        var category = new EmojiCategory
        {
            Name = "Recent",
            Icon = "🕐"
        };
        foreach (var emoji in _recent)
        {
            if (allEmojis.TryGetValue(emoji, out var item))
                category.Emojis.Add(item);
            else
                category.Emojis.Add(new EmojiItem { Emoji = emoji, Name = "", Category = "Recent" });
        }
        return category;
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                _recent = JsonSerializer.Deserialize<List<string>>(json) ?? [];
            }
        }
        catch { _recent = []; }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_recent);
            File.WriteAllText(_filePath, json);
        }
        catch { }
    }
}
