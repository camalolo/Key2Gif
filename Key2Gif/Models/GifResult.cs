namespace Key2Gif.Models;

public class GifResult
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string PreviewUrl { get; set; } = "";   // small/preview animated gif
    public string FullUrl { get; set; } = "";       // full-size gif for insertion
    public string TinyUrl { get; set; } = "";       // tiny static preview for grid
    public int Width { get; set; }
    public int Height { get; set; }
}
