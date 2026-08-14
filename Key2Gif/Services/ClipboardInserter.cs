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

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    private const int KEYEVENTF_KEYDOWN = 0x0000;
    private const int KEYEVENTF_KEYUP = 0x0002;
    private const byte VK_CONTROL = 0x11;
    private const byte VK_V = 0x56;

    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint CF_HDROP = 15;

    public static async Task InsertGifAsync(string url)
    {
        Log.Info($"Loading GIF from cache/download: {url}");
        var bytes = await GifCache.GetOrDownloadAsync(url);
        var filePath = GifCache.GetFilePath(url);
        Log.Info($"GIF ready ({bytes.Length} bytes) from cache: {filePath}");

        // Save current clipboard contents so we can restore after paste
        IDataObject? prevClipboard = null;
        try { prevClipboard = Clipboard.GetDataObject(); }
        catch { }

        try
        {
            CopyGifToClipboardNative(bytes, filePath);
        }
        catch (Exception ex)
        {
            Log.Error($"Native clipboard failed: {ex.Message}", ex);
            Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { filePath });
        }

        await Task.Delay(80);
        SendCtrlV();

        // Restore clipboard after the target app has had time to process the paste
        if (prevClipboard != null)
        {
            await Task.Delay(500);
            try { Clipboard.SetDataObject(prevClipboard); }
            catch { }
        }
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

        // Retry opening clipboard — another app may hold it briefly
        if (!TryOpenClipboard(hwnd))
        {
            Log.Error("OpenClipboard failed after retries");
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

                    if (SetClipboardData(gifFormat, hMem) == IntPtr.Zero)
                    {
                        GlobalFree(hMem);
                        Log.Error("SetClipboardData(GIF) failed");
                    }
                    else
                        Log.Info($"SetClipboardData(GIF format={gifFormat}), size={gifBytes.Length}");
                }
            }

            // 2) Also write as CF_HDROP (file drop) so apps that only accept file drops can use it
            string filePath = tempFile + "\0";
            byte[] pathBytes = System.Text.Encoding.Unicode.GetBytes(filePath);

            int totalSize = 20 + pathBytes.Length;
            var dropMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)totalSize);
            if (dropMem != IntPtr.Zero)
            {
                var dropPtr = GlobalLock(dropMem);
                Marshal.WriteInt32(dropPtr, 0, 20);
                Marshal.WriteInt32(dropPtr, 4, 0);
                Marshal.WriteInt32(dropPtr, 8, 0);
                Marshal.WriteInt32(dropPtr, 12, 0);
                Marshal.WriteInt32(dropPtr, 16, 1);
                Marshal.Copy(pathBytes, 0, dropPtr + 20, pathBytes.Length);
                GlobalUnlock(dropMem);

                if (SetClipboardData(CF_HDROP, dropMem) == IntPtr.Zero)
                {
                    GlobalFree(dropMem);
                    Log.Error("SetClipboardData(CF_HDROP) failed");
                }
                else
                    Log.Info("SetClipboardData(CF_HDROP) ok");
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

    private static bool TryOpenClipboard(IntPtr hwnd)
    {
        for (int i = 0; i < 10; i++)
        {
            if (OpenClipboard(hwnd))
                return true;
            Thread.Sleep(50);
        }
        return false;
    }

    private static void SendCtrlV()
    {
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
        keybd_event(VK_V, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
        keybd_event(VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }
}
