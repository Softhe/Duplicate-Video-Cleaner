# Duplicate Video Cleaner

Duplicate Video Cleaner scans a folder for `.mp4` and `.mkv` files recorded close together. You review each group and approve every removal.

![Scan workspace](docs/screenshots/scan.png)

## How it works

The app reads each file name for a timestamp in the form `yyyy-MM-dd HH-mm-ss`. If the name has no valid timestamp, the app uses file creation time. It sorts files by time and groups neighbors that fall inside the time window you set. The default window is 60 seconds.

![Review workspace](docs/screenshots/review.png)

## Install

1. Download `DuplicateVideoCleaner-1.1.0-win-x64-portable.zip` from Releases.
2. Extract the full zip and keep the `tools` folder next to the exe.
3. Run `DuplicateVideoCleaner.exe`. No admin rights and no separate .NET install are required.

## Use it

1. Open Scan, set a folder, set the time window, pick Oldest or Newest to keep, and press Scan videos.
2. Open Review. Expand a group to see paths, timestamps, duration, resolution, size, and confidence.
3. Select files with Suggested, All, or None. Shortcuts are `Ctrl+1`, `Ctrl+2`, `Ctrl+3` for workspaces and `Alt+S`, `Alt+A`, `Alt+N` for selection.
4. Press Export dry run to write CSV and text reports, or press Move selected to remove files.

![Group details](docs/screenshots/review-details.png)
![Confirmation dialog](docs/screenshots/confirmation.png)

## Confidence levels

Confidence is advisory. Grouping stays timestamp based.

- High. Duration, resolution, and byte size match the kept file.
- Medium. Exactly two of the three match the kept file.
- Low. One or fewer match, or ffprobe metadata is missing.

## Safety and logs

Recycle Bin is the default. Permanent deletion is available in the removal method list. The confirmation dialog defaults to Cancel. The app writes scan and removal results to `DuplicateCleanupLog.txt` in the scanned folder. Session activity also appears in the Activity workspace.

![Session activity](docs/screenshots/activity.png)

## Build from source

You need .NET 9 SDK on Windows.

```powershell
dotnet run --project DuplicateVideoCleaner.Tests/DuplicateVideoCleaner.Tests.csproj -c Release
dotnet test DuplicateVideoCleaner.UiTests/DuplicateVideoCleaner.UiTests.csproj -c Release
dotnet publish DuplicateVideoCleaner/DuplicateVideoCleaner.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

`build/BuildRelease.ps1` runs the same steps and assembles the portable zip with ffprobe.

## Tests

- `DuplicateVideoCleaner.Tests` holds 13 checks for scanning, settings, metadata parsing, confidence, dry run output, and removal.
- `DuplicateVideoCleaner.UiTests` holds 4 checks for navigation, selection state, summaries, and confirmation defaults.

## FFmpeg license

The portable zip includes `ffprobe.exe` and its DLLs from FFmpeg under LGPL 2.1 or later. See `FFMPEG-NOTICE.md`. The app runs ffprobe as a separate process and does not modify it.

## License

MIT. See `LICENSE.txt`.
