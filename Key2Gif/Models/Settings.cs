namespace Key2Gif.Models;

public class AppSettings
{
    public string TenorApiKey { get; set; } = "REDACTED"; // public demo key
    public List<string> RecentEmojis { get; set; } = [];
    public int MaxRecentItems { get; set; } = 50;
}
