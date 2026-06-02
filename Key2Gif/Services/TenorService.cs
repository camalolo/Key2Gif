using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Key2Gif.Models;

namespace Key2Gif.Services;

public class TenorService
{
    private readonly HttpClient _httpClient;
    private const string ApiKey = "REDACTED";
    private const string BaseUrl = "https://tenor.googleapis.com/v2";
    private string? _nextPos;

    public TenorService()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task<List<GifResult>> SearchAsync(string query, int limit = 20, string? pos = null)
    {
        var url = $"{BaseUrl}/search?key={ApiKey}&q={Uri.EscapeDataString(query)}&limit={limit}&client_key=Key2Gif/1.0&media_filter=tinygif,mediumgif,gif";
        if (pos != null) url += $"&pos={pos}";

        Log.Info($"Tenor API request: {url}");
        var response = await _httpClient.GetStringAsync(url);
        Log.Info($"Tenor API response length: {response.Length} chars");
        using var doc = JsonDocument.Parse(response);
        var results = new List<GifResult>();

        // Capture pagination position
        if (doc.RootElement.TryGetProperty("next", out var nextVal))
            _nextPos = nextVal.GetString();

        foreach (var result in doc.RootElement.GetProperty("results").EnumerateArray())
        {
            var gif = new GifResult
            {
                Id = result.GetProperty("id").GetString() ?? "",
                Title = result.GetProperty("content_description").GetString() ?? ""
            };

            var formats = result.GetProperty("media_formats");

            if (formats.TryGetProperty("tinygif", out var tiny))
            {
                gif.TinyUrl = tiny.GetProperty("url").GetString() ?? "";
            }
            if (formats.TryGetProperty("mediumgif", out var medium))
            {
                gif.PreviewUrl = medium.GetProperty("url").GetString() ?? "";
                if (medium.TryGetProperty("dims", out var dims) && dims.GetArrayLength() >= 2)
                {
                    gif.Width = dims[0].GetInt32();
                    gif.Height = dims[1].GetInt32();
                }
                else
                {
                    gif.Width = 220;
                    gif.Height = 160;
                }
            }
            if (formats.TryGetProperty("gif", out var full))
            {
                gif.FullUrl = full.GetProperty("url").GetString() ?? "";
            }

            results.Add(gif);
        }

        return results;
    }

    public string? NextPosition => _nextPos;

    public async Task<List<GifResult>> GetTrendingAsync(int limit = 20, string? pos = null)
    {
        var url = $"{BaseUrl}/featured?key={ApiKey}&limit={limit}&client_key=Key2Gif/1.0&media_filter=tinygif,mediumgif,gif";
        if (pos != null) url += $"&pos={pos}";

        var response = await _httpClient.GetStringAsync(url);
        using var doc = JsonDocument.Parse(response);
        var results = new List<GifResult>();

        if (doc.RootElement.TryGetProperty("next", out var nextVal))
            _nextPos = nextVal.GetString();

        foreach (var result in doc.RootElement.GetProperty("results").EnumerateArray())
        {
            var gif = new GifResult
            {
                Id = result.GetProperty("id").GetString() ?? "",
                Title = result.GetProperty("content_description").GetString() ?? ""
            };

            var formats = result.GetProperty("media_formats");
            if (formats.TryGetProperty("tinygif", out var tiny))
                gif.TinyUrl = tiny.GetProperty("url").GetString() ?? "";
            if (formats.TryGetProperty("mediumgif", out var medium))
            {
                gif.PreviewUrl = medium.GetProperty("url").GetString() ?? "";
                if (medium.TryGetProperty("dims", out var dims) && dims.GetArrayLength() >= 2)
                {
                    gif.Width = dims[0].GetInt32();
                    gif.Height = dims[1].GetInt32();
                }
                else
                {
                    gif.Width = 220;
                    gif.Height = 160;
                }
            }
            if (formats.TryGetProperty("gif", out var full))
                gif.FullUrl = full.GetProperty("url").GetString() ?? "";

            results.Add(gif);
        }

        return results;
    }
}
