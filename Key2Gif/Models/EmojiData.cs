namespace Key2Gif.Models;

public class EmojiItem
{
    public string Emoji { get; set; } = "";
    public string Name { get; set; } = "";
    public string[] Keywords { get; set; } = [];
    public string Category { get; set; } = "";
}

public class EmojiCategory
{
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "";
    public List<EmojiItem> Emojis { get; set; } = [];
}
