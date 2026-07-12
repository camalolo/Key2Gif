# Key2Gif

A lightweight Windows GIF picker that lets you search and insert animated GIFs into any application via a global hotkey.

![Screenshot](Key2Gif/icon.png)

## Features

- **Global hotkey** — Press `LeftCtrl+Shift+RightCtrl` to open the picker from anywhere
- **Search** — Type to search GIPHY's library of millions of GIFs
- **Single click to paste** — Click any GIF to insert it directly into your active window
- **Animated previews** — Thumbnails animate in the grid so you know what you're picking
- **Trending GIFs** — Browse trending GIFs when you open the picker
- **Recent history** — Last 8 used GIFs are saved for quick access
- **System tray** — Runs in the system tray, click to toggle or right-click for menu

## Requirements

- Windows 10 or 11
- .NET 8 runtime (included in the self-contained release build)

## Download

Grab the latest release from the [Releases](../../releases) page.

## Build from source

```
dotnet build Key2Gif/Key2Gif.csproj
```

To publish a self-contained single-file executable:

```
build.bat
```

## Configuration

On first launch, Key2Gif will prompt you for a GIPHY API key. Get a free key from [developers.giphy.com](https://developers.giphy.com/). The key is stored in `%AppData%\Key2Gif\settings.json`.

## License

MIT
