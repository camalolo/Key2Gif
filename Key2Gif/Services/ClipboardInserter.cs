using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Key2Gif.Services;

public static class ClipboardInserter
{
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    private const int KEYEVENTF_KEYDOWN = 0x0000;
    private const int KEYEVENTF_KEYUP = 0x0002;
    private const byte VK_CONTROL = 0x11;
    private const byte VK_V = 0x56;

    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint CF_HDROP = 15;

    public static void InsertText(string text)
    {
        // Save current clipboard
        string? prevText = null;
        try { prevText = Clipboard.ContainsText() ? Clipboard.GetText() : null; } catch { }

        Clipboard.SetText(text);

        Thread.Sleep(50);
        SendCtrlV();

        // Restore clipboard after a delay
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Thread.Sleep(500);
            try
            {
                if (prevText != null)
                    Clipboard.SetText(prevText);
            }
            catch { }
        });
    }

    public static async Task InsertGifAsync(string url)
    {
        Log.Info($"Downloading GIF from: {url}");
        using var httpClient = new System.Net.Http.HttpClient();
        var bytes = await httpClient.GetByteArrayAsync(url);
        Log.Info($"Downloaded GIF: {bytes.Length} bytes");

        // Save to temp file
        var tempFile = Path.Combine(Path.GetTempPath(), $"key2gif_{Guid.NewGuid()}.gif");
        await File.WriteAllBytesAsync(tempFile, bytes);

        // Put GIF on clipboard using native Win32 — single session, no WPF clipboard after
        try
        {
            CopyGifToClipboardNative(bytes, tempFile);
        }
        catch (Exception ex)
        {
            Log.Error($"Native clipboard failed: {ex.Message}", ex);
            // Last resort: WPF file drop
            Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { tempFile });
        }

        Thread.Sleep(80);
        SendCtrlV();

        // Clean up temp file after delay
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Thread.Sleep(5000);
            try { File.Delete(tempFile); } catch { }
        });
    }

    private static void CopyGifToClipboardNative(byte[] gifBytes, string tempFile)
    {
        // Get a window handle for clipboard ownership
        IntPtr hwnd = IntPtr.Zero;
        try
        {
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                foreach (Window w in Application.Current.Windows)
                {
                    hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                    if (hwnd != IntPtr.Zero) break;
                }
            });
        }
        catch { }

        if (!OpenClipboard(hwnd))
        {
            Log.Error("OpenClipboard failed");
            throw new InvalidOperationException("Cannot open clipboard");
        }

        try
        {
            EmptyClipboard();

            // 1) Set the GIF binary format (preserves animation in many apps)
            uint gifFormat = RegisterClipboardFormat("GIF");
            if (gifFormat != 0)
            {
                var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)gifBytes.Length);
                if (hMem != IntPtr.Zero)
                {
                    var ptr = GlobalLock(hMem);
                    Marshal.Copy(gifBytes, 0, ptr, gifBytes.Length);
                    GlobalUnlock(hMem);

                    var result = SetClipboardData(gifFormat, hMem);
                    Log.Info($"SetClipboardData(GIF format={gifFormat}) = {result != IntPtr.Zero}, size={gifBytes.Length}");
                }
            }

            // 2) Also write as CF_HDROP (file drop) so apps that only accept file drops can use it
            // Build the DROPFILES structure + file path in Unicode
            string filePath = tempFile + "\0"; // null-terminated
            byte[] pathBytes = System.Text.Encoding.Unicode.GetBytes(filePath);

            // DROPFILES struct: 20 bytes header + file paths
            int totalSize = 20 + pathBytes.Length;
            var dropMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)totalSize);
            if (dropMem != IntPtr.Zero)
            {
                var dropPtr = GlobalLock(dropMem);
                // Write DROPFILES header
                Marshal.WriteInt32(dropPtr, 0, 20);          // pFiles offset
                Marshal.WriteInt32(dropPtr, 4, 0);            // pt.x
                Marshal.WriteInt32(dropPtr, 8, 0);            // pt.y
                Marshal.WriteInt32(dropPtr, 12, 0);           // fNC
                Marshal.WriteInt32(dropPtr, 16, 1);           // fWide = TRUE (Unicode)
                // Write file path after header
                Marshal.Copy(pathBytes, 0, dropPtr + 20, pathBytes.Length);
                GlobalUnlock(dropMem);

                var dropResult = SetClipboardData(CF_HDROP, dropMem);
                Log.Info($"SetClipboardData(CF_HDROP) = {dropResult != IntPtr.Zero}");
            }

            CloseClipboard();
            Log.Info("Clipboard closed — GIF data should be available");
        }
        catch (Exception ex)
        {
            Log.Error($"CopyGifToClipboardNative error: {ex.Message}", ex);
            CloseClipboard();
        }
    }

    private static void SendCtrlV()
    {
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
        keybd_event(VK_V, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
        keybd_event(VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }
}
