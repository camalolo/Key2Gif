using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Key2Gif.Services;

/// <summary>
/// Asynchronous queued file logger. Writes are enqueued and flushed on a
/// background thread, so logging never blocks the UI or hot paths.
/// </summary>
public static class Log
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Key2Gif", "key2gif.log");

    private static readonly BlockingCollection<string> _queue = new();
    private static readonly Thread _writer = new(WriterLoop) { IsBackground = true };

    static Log()
    {
        _writer.Start();
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", message + (ex != null ? $"\n{ex}" : ""));

    public static void Warning(string message) => Write("WARN", message);

    private static void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}\n";
        _queue.Add(line);
    }

    private static void WriterLoop()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            using var writer = new StreamWriter(LogPath, append: true);
            foreach (var line in _queue.GetConsumingEnumerable())
            {
                writer.Write(line);
                writer.Flush();
            }
        }
        catch
        {
            // Logging is best-effort; never crash the app
        }
    }
}
