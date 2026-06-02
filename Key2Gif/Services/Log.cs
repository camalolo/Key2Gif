using System;
using System.IO;

namespace Key2Gif.Services;

public static class Log
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Key2Gif", "key2gif.log");

    private static readonly object _lock = new();

    public static void Info(string message)
    {
        Write("INFO", message);
    }

    public static void Error(string message, Exception? ex = null)
    {
        Write("ERROR", message + (ex != null ? $"\n{ex}" : ""));
    }

    public static void Warning(string message)
    {
        Write("WARN", message);
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}\n");
            }
        }
        catch { }
    }
}
