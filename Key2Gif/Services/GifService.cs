using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Key2Gif.Models;

namespace Key2Gif.Services;

/// <summary>
/// Result of a GIPHY API query — stateless, no instance mutation.
/// </summary>
public record GifSearchResult(List<GifResult> Gifs, int NextOffset);

public class GifService
{
    private const string BaseUrl = "https://api.giphy.com/v1/gifs";
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public GifService(string apiKey)
    {
        _apiKey = apiKey;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public Task<GifSearchResult> SearchAsync(string query, int limit = 40, int offset = 0, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/search?api_key={_apiKey}&q={Uri.EscapeDataString(query)}&limit={limit}&offset={offset}&rating=pg-13";
        Log.Info($"GIPHY search: q='{query}' limit={limit} offset={offset}");
        return QueryAsync(url, limit, ct);
    }

    public Task<GifSearchResult> GetTrendingAsync(int limit = 40, int offset = 0, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/trending?api_key={_apiKey}&limit={limit}&offset={offset}&rating=pg-13";
        Log.Info($"GIPHY trending: limit={limit} offset={offset}");
        return QueryAsync(url, limit, ct);
    }

    private async Task<GifSearchResult> QueryAsync(string url, int limit, CancellationToken ct)
    {
        var response = await _httpClient.GetStringAsync(url, ct);

        using var doc = JsonDocument.Parse(response);
        var pagination = doc.RootElement.GetProperty("pagination");
        var nextOffset = pagination.GetProperty("offset").GetInt32() + limit;

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
                gif.PreviewUrl = preview.GetProperty("url").GetString() ?? "";

            if (images.TryGetProperty("fixed_width_small", out var tiny))
                gif.TinyUrl = tiny.GetProperty("url").GetString() ?? "";

            if (images.TryGetProperty("original", out var full))
                gif.FullUrl = full.GetProperty("url").GetString() ?? "";

            results.Add(gif);
        }

        Log.Info($"GIPHY returned {results.Count} gifs (nextOffset={nextOffset})");
        return new GifSearchResult(results, nextOffset);
    }
}
