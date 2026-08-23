using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Key2Gif.Services;

public static class ClipboardInserter
{
    // --- SendInput structs ---

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL, wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_UNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUT_UNION u;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const byte VK_CONTROL = 0x11;
    private const byte VK_V = 0x56;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    // --- Clipboard P/Invoke ---

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

    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint CF_HDROP = 15;

    /// <summary>
    /// Paste as image (left-click). Puts GIF + DIB on clipboard, sends Ctrl+V.
    /// GIF preserves animation for Telegram; DIB is what Electron apps
    /// (Discord/Slack/Teams/Chrome) actually read — they ignore registered
    /// "PNG" format from external apps.
    /// </summary>
    public static async Task InsertGifAsync(string url)
    {
        Log.Info($"Paste: loading GIF: {url}");
        var bytes = await GifCache.GetOrDownloadAsync(url);
        var filePath = GifCache.GetFilePath(url);
        Log.Info($"GIF ready ({bytes.Length} bytes)");

        // Heavy decode/encode off the UI thread (keeps the LL keyboard hook
        // responsive) and BEFORE opening the clipboard (don't hold the global
        // lock while doing CPU work).
        var dibBytes = await Task.Run(() => ExtractFirstFrameAsDib(bytes));
        var pngBytes = await Task.Run(() => ExtractFirstFrameAsPng(bytes));

        IDataObject? prevClipboard = null;
        try { prevClipboard = Clipboard.GetDataObject(); }
        catch { }

        try { await CopyImageToClipboard(bytes, dibBytes, pngBytes); }
        catch (Exception ex)
        {
            Log.Error($"Clipboard set failed: {ex.Message}", ex);
            Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { filePath });
        }

        await Task.Delay(80);
        SendCtrlV();

        RestoreClipboard(prevClipboard);
    }

    /// <summary>
    /// Paste as file (right-click). Puts CF_HDROP (file path) on clipboard,
    /// sends Ctrl+V. Discord/Slack/Teams treat this as a file upload,
    /// preserving the animated GIF.
    /// </summary>
    public static async Task InsertFileAsync(string url)
    {
        Log.Info($"Drop: loading GIF: {url}");
        await GifCache.GetOrDownloadAsync(url);
        var filePath = GifCache.GetFilePath(url);
        Log.Info($"File ready: {filePath}");

        IDataObject? prevClipboard = null;
        try { prevClipboard = Clipboard.GetDataObject(); }
        catch { }

        try { await CopyFileToClipboard(filePath); }
        catch (Exception ex)
        {
            Log.Error($"File clipboard set failed: {ex.Message}", ex);
        }

        await Task.Delay(80);
        SendCtrlV();

        RestoreClipboard(prevClipboard);
    }

    private static async void RestoreClipboard(IDataObject? prevClipboard)
    {
        if (prevClipboard == null) return;
        await Task.Delay(500);
        try { Clipboard.SetDataObject(prevClipboard); }
        catch { }
    }

    /// <summary>
    /// Places GIF + DIB on the clipboard (no file paths).
    /// Frame extraction must already be done — the clipboard is only held
    /// open for the fast SetClipboardData calls.
    /// </summary>
    private static async Task CopyImageToClipboard(byte[] gifBytes, byte[]? dibBytes, byte[]? pngBytes)
    {
        var hwnd = GetWindowHandle();
        if (!await TryOpenClipboardAsync(hwnd))
            throw new InvalidOperationException("Cannot open clipboard");

        try
        {
            EmptyClipboard();

            // 1) GIF format — preserves animation for Telegram
            uint gifFormat = RegisterClipboardFormat("GIF");
            if (gifFormat != 0)
            {
                SetRawData(gifFormat, gifBytes);
                Log.Info($"SetClipboardData(GIF), size={gifBytes.Length}");
            }

            // 2) CF_DIB — bitmap that Electron/Chrome actually reads
            if (dibBytes != null)
            {
                SetRawData(8 /* CF_DIB */, dibBytes); // Windows synthesizes CF_BITMAP, CF_PALETTE
                Log.Info($"SetClipboardData(CF_DIB), size={dibBytes.Length}");
            }

            // 3) PNG — some apps (Office, modern editors) prefer this
            if (pngBytes != null)
            {
                uint pngFormat = RegisterClipboardFormat("PNG");
                if (pngFormat != 0)
                {
                    SetRawData(pngFormat, pngBytes);
                    Log.Info($"SetClipboardData(PNG), size={pngBytes.Length}");
                }
            }

            CloseClipboard();
        }
        catch
        {
            CloseClipboard();
            throw;
        }
    }

    /// <summary>
    /// Places CF_HDROP (file path) on the clipboard only.
    /// </summary>
    private static async Task CopyFileToClipboard(string filePath)
    {
        var hwnd = GetWindowHandle();
        if (!await TryOpenClipboardAsync(hwnd))
            throw new InvalidOperationException("Cannot open clipboard");

        try
        {
            EmptyClipboard();

            string path = filePath + "\0\0";  // double-null-terminated (string + list terminator)
            byte[] pathBytes = System.Text.Encoding.Unicode.GetBytes(path);

            int totalSize = 20 + pathBytes.Length;
            var dropMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)totalSize);
            if (dropMem != IntPtr.Zero)
            {
                var dropPtr = GlobalLock(dropMem);
                Marshal.WriteInt32(dropPtr, 0, 20);   // pFiles
                Marshal.WriteInt32(dropPtr, 4, 0);    // pt.x
                Marshal.WriteInt32(dropPtr, 8, 0);    // pt.y
                Marshal.WriteInt32(dropPtr, 12, 0);   // fNC
                Marshal.WriteInt32(dropPtr, 16, 1);   // fWide = Unicode
                Marshal.Copy(pathBytes, 0, dropPtr + 20, pathBytes.Length);
                GlobalUnlock(dropMem);

                if (SetClipboardData(CF_HDROP, dropMem) == IntPtr.Zero)
                    GlobalFree(dropMem);
                else
                    Log.Info($"SetClipboardData(CF_HDROP): '{filePath}' exists={File.Exists(filePath)}");
            }

            CloseClipboard();
        }
        catch
        {
            CloseClipboard();
            throw;
        }
    }

    private static void SetRawData(uint format, byte[] data)
    {
        var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (hMem == IntPtr.Zero) return;

        var ptr = GlobalLock(hMem);
        Marshal.Copy(data, 0, ptr, data.Length);
        GlobalUnlock(hMem);

        if (SetClipboardData(format, hMem) == IntPtr.Zero)
            GlobalFree(hMem);
    }

    /// <summary>
    /// Extracts the first frame of a GIF as a CF_DIB-compatible BITMAPINFO
    /// (BITMAPINFOHEADER + pixel data). Windows synthesizes CF_BITMAP and
    /// CF_PALETTE from this automatically.
    /// </summary>
    private static byte[]? ExtractFirstFrameAsDib(byte[] gifBytes)
    {
        try
        {
            using var ms = new MemoryStream(gifBytes);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;

            var frame = decoder.Frames[0];
            // CopyPixelBuffer gives raw BGRA pixel data
            var pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4];
            frame.CopyPixels(pixels, frame.PixelWidth * 4, 0);

            // Build BITMAPINFOHEADER (40 bytes) + pixel data
            var header = new byte[40];
            WriteInt32(header, 0, 40);              // biSize
            WriteInt32(header, 4, frame.PixelWidth); // biWidth
            WriteInt32(header, 8, frame.PixelHeight); // biHeight (positive = bottom-up)
            WriteInt16(header, 12, 1);              // biPlanes
            WriteInt16(header, 14, 32);             // biBitCount (BGRA)
            WriteInt32(header, 16, 0);              // biCompression = BI_RGB
            WriteInt32(header, 20, pixels.Length);  // biSizeImage
            WriteInt32(header, 24, 0);              // biXPelsPerMeter
            WriteInt32(header, 28, 0);              // biYPelsPerMeter
            WriteInt32(header, 32, 0);              // biClrUsed
            WriteInt32(header, 36, 0);              // biClrImportant

            var result = new byte[40 + pixels.Length];
            Array.Copy(header, result, 40);
            Array.Copy(pixels, 0, result, 40, pixels.Length);
            return result;
        }
        catch (Exception ex)
        {
            Log.Error($"DIB extraction failed: {ex.Message}", ex);
            return null;
        }
    }

    private static byte[]? ExtractFirstFrameAsPng(byte[] gifBytes)
    {
        try
        {
            using var ms = new MemoryStream(gifBytes);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(decoder.Frames[0]);
            using var pngStream = new MemoryStream();
            encoder.Save(pngStream);
            return pngStream.ToArray();
        }
        catch (Exception ex)
        {
            Log.Error($"PNG extraction failed: {ex.Message}", ex);
            return null;
        }
    }

    private static void WriteInt32(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
        buf[offset + 2] = (byte)(value >> 16);
        buf[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteInt16(byte[] buf, int offset, short value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
    }

    private static IntPtr GetWindowHandle()
    {
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
        return hwnd;
    }

    /// <summary>
    /// Retries opening the clipboard with async waits so the UI thread keeps
    /// pumping messages (a blocking Thread.Sleep here would starve the
    /// low-level keyboard hook past Windows' LowLevelHooksTimeout).
    /// </summary>
    private static async Task<bool> TryOpenClipboardAsync(IntPtr hwnd)
    {
        for (int i = 0; i < 10; i++)
        {
            if (OpenClipboard(hwnd))
                return true;
            await Task.Delay(50);
        }
        return false;
    }

    private static void SendCtrlV()
    {
        var size = Marshal.SizeOf<INPUT>();
        var inputs = new[]
        {
            new INPUT { type = INPUT_KEYBOARD, u = new INPUT_UNION { ki = new KEYBDINPUT { wVk = VK_CONTROL } } },
            new INPUT { type = INPUT_KEYBOARD, u = new INPUT_UNION { ki = new KEYBDINPUT { wVk = VK_V } } },
            new INPUT { type = INPUT_KEYBOARD, u = new INPUT_UNION { ki = new KEYBDINPUT { wVk = VK_V, dwFlags = KEYEVENTF_KEYUP } } },
            new INPUT { type = INPUT_KEYBOARD, u = new INPUT_UNION { ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP } } },
        };
        SendInput((uint)inputs.Length, inputs, size);
    }
}
