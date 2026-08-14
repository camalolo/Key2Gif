using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Key2Gif.Services;

/// <summary>
/// Disk-backed GIF cache. Stores downloaded image bytes in %TEMP%\Key2Gif
/// keyed by a SHA256 hash of the URL, so repeated thumbnail views and
/// clipboard insertions for the same GIF avoid re-downloading.
/// Deduplicates concurrent downloads of the same URL.
/// </summary>
public static class GifCache
{
    private static readonly string CacheDir = Path.Combine(Path.GetTempPath(), "Key2Gif");

    private static readonly HttpClient _client = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly ConcurrentDictionary<string, Task<byte[]>> _inFlight = new();

    public static string GetFilePath(string url) => Path.Combine(CacheDir, HashUrl(url) + ".gif");

    public static async Task<byte[]> GetOrDownloadAsync(string url)
    {
        var path = GetFilePath(url);

        // Fast path: already cached on disk
        if (File.Exists(path))
        {
            try { return await File.ReadAllBytesAsync(path); }
            catch (IOException) { /* fall through to re-download */ }
        }

        // Deduplicate concurrent requests for the same URL
        var task = _inFlight.GetOrAdd(url, async _key =>
        {
            try
            {
                var bytes = await _client.GetByteArrayAsync(url);
                await WriteCacheAsync(path, bytes);
                return bytes;
            }
            finally
            {
                _inFlight.TryRemove(url, out _);
            }
        });

        return await task;
    }

    private static async Task WriteCacheAsync(string path, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            await File.WriteAllBytesAsync(path, bytes);
        }
        catch (Exception ex)
        {
            Log.Error($"GifCache write failed: {ex.Message}", ex);
        }
    }

    private static string HashUrl(string url)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(hash)[..16];
    }
}
