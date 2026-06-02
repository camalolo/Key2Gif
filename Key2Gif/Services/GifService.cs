using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Key2Gif.Models;

namespace Key2Gif.Services;

public class GifService
{
    private readonly HttpClient _httpClient;
    private const string ApiKey = "REDACTED";
    private const string BaseUrl = "https://api.giphy.com/v1/gifs";
    private int _totalCount;
    private int _offset;

    public GifService()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public int TotalCount => _totalCount;
    public int Offset => _offset;

    public async Task<List<GifResult>> SearchAsync(string query, int limit = 20, int offset = 0)
    {
        var url = $"{BaseUrl}/search?api_key={ApiKey}&q={Uri.EscapeDataString(query)}&limit={limit}&offset={offset}&rating=pg-13";
        Log.Info($"GIPHY API request: {url}");

        var response = await _httpClient.GetStringAsync(url);
        Log.Info($"GIPHY API response length: {response.Length}");

        using var doc = JsonDocument.Parse(response);
        var pagination = doc.RootElement.GetProperty("pagination");
        _totalCount = pagination.GetProperty("total_count").GetInt32();
        _offset = pagination.GetProperty("offset").GetInt32() + limit;

        var results = new List<GifResult>();
        foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var gif = new GifResult
            {
                Id = item.GetProperty("id").GetString() ?? "",
                Title = item.GetProperty("title").GetString() ?? ""
            };

            var images = item.GetProperty("images");

            // Preview: fixed_width (animated, 200px wide)
            if (images.TryGetProperty("fixed_width", out var preview))
            {
                gif.PreviewUrl = preview.GetProperty("url").GetString() ?? "";
                gif.Width = int.TryParse(preview.GetProperty("width").GetString(), out var w) ? w : 200;
                gif.Height = int.TryParse(preview.GetProperty("height").GetString(), out var h) ? h : 150;
            }

            // Tiny: fixed_width_small (100px wide)
            if (images.TryGetProperty("fixed_width_small", out var tiny))
            {
                gif.TinyUrl = tiny.GetProperty("url").GetString() ?? "";
            }

            // Full: original
            if (images.TryGetProperty("original", out var full))
            {
                gif.FullUrl = full.GetProperty("url").GetString() ?? "";
            }

            results.Add(gif);
        }

        Log.Info($"GIPHY returned {results.Count} gifs (total={_totalCount}, nextOffset={_offset})");
        return results;
    }

    public async Task<List<GifResult>> GetTrendingAsync(int limit = 20, int offset = 0)
    {
        var url = $"{BaseUrl}/trending?api_key={ApiKey}&limit={limit}&offset={offset}&rating=pg-13";
        Log.Info($"GIPHY trending request: {url}");

        var response = await _httpClient.GetStringAsync(url);
        Log.Info($"GIPHY trending response length: {response.Length}");

        using var doc = JsonDocument.Parse(response);
        var pagination = doc.RootElement.GetProperty("pagination");
        _totalCount = pagination.GetProperty("total_count").GetInt32();
        _offset = pagination.GetProperty("offset").GetInt32() + limit;

        var results = new List<GifResult>();
        foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var gif = new GifResult
            {
                Id = item.GetProperty("id").GetString() ?? "",
                Title = item.GetProperty("title").GetString() ?? ""
            };

            var images = item.GetProperty("images");

            if (images.TryGetProperty("fixed_width", out var preview))
            {
                gif.PreviewUrl = preview.GetProperty("url").GetString() ?? "";
                gif.Width = int.TryParse(preview.GetProperty("width").GetString(), out var w) ? w : 200;
                gif.Height = int.TryParse(preview.GetProperty("height").GetString(), out var h) ? h : 150;
            }

            if (images.TryGetProperty("fixed_width_small", out var tiny))
                gif.TinyUrl = tiny.GetProperty("url").GetString() ?? "";

            if (images.TryGetProperty("original", out var full))
                gif.FullUrl = full.GetProperty("url").GetString() ?? "";

            results.Add(gif);
        }

        Log.Info($"GIPHY trending returned {results.Count} gifs");
        return results;
    }
}
