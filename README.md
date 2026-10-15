# Duplicate Video Cleaner

Find videos recorded close together, review the suggestions, and choose what to remove.

Duplicate Video Cleaner groups `.mp4` and `.mkv` files by their timestamps. Duration, resolution, and file size help you compare each group. It does **not** compare video content, so a suggested duplicate may be a different recording. Review each selection before removing it.

![Scan workspace](docs/screenshots/scan.png)

## Download

Windows x64. Both downloads include .NET and ffprobe, so you do not need to install either separately.

| Download | How to use it |
| --- | --- |
| [Setup EXE](https://github.com/Softhe/Duplicate-Video-Cleaner/releases/download/v1.1.1/DuplicateVideoCleaner-1.1.1-win-x64-setup.exe) | Run setup, then open the app from the Start menu. Installs for your Windows account without administrator rights. |
| [Portable ZIP](https://github.com/Softhe/Duplicate-Video-Cleaner/releases/download/v1.1.1/DuplicateVideoCleaner-1.1.1-win-x64-portable.zip) | Extract the entire archive and run `DuplicateVideoCleaner.exe`. Keep the `tools` folder beside it. |

[Download checksums](https://github.com/Softhe/Duplicate-Video-Cleaner/releases/download/v1.1.1/SHA256SUMS.txt) · [Release notes](https://github.com/Softhe/Duplicate-Video-Cleaner/releases/tag/v1.1.1)

The downloads are unsigned. Both versions save preferences in your Windows user profile. Uninstall the setup version through Windows Installed apps; remove the extracted folder to remove the portable version.

## Review your videos

1. In **Scan**, choose a folder, set the time window, and choose whether to keep the oldest or newest file. Include subfolders if needed, then press **Scan videos**.
2. In **Review**, expand each group and compare its files. Check paths, timestamps, duration, resolution, and size. Use **Suggested**, **All**, or **None** to change the selection.
3. Use **Export dry run** to save CSV and text reports before making changes.
4. Choose the removal method, press **Move selected**, and review the confirmation. Recycle Bin is the default; permanent deletion is also available. The confirmation defaults to Cancel.

![Review workspace](docs/screenshots/review.png)

<details>
<summary>See group details and the confirmation dialog</summary>

![Expanded group details](docs/screenshots/review-details.png)

![Removal confirmation](docs/screenshots/confirmation.png)

</details>

## What the suggestions mean

The app first looks for a timestamp in the filename, such as `2026-10-15 14-30-00.mp4`. If it cannot find a valid timestamp, it uses the file's creation time. It sorts the files and groups neighboring timestamps within your chosen window, which defaults to 60 seconds.

Groups can span longer than that window. With a 60-second window, recordings at 12:00:00, 12:00:50, and 12:01:40 belong to the same group because each neighboring pair is within 60 seconds.

Confidence compares each file with the suggested keeper:

| Confidence | Comparison |
| --- | --- |
| High | Duration, resolution, and byte size match. |
| Medium | Two of those three match. |
| Low | One or none match, or metadata is missing. |

Confidence is advisory. Even a High match does not establish that two files contain the same video.

<details>
<summary>See a review with several groups</summary>

![Review with six groups](docs/screenshots/review-six-groups.png)

</details>

## Safety and activity

Before removing a file, the app checks its size, creation time, last-write time, and Windows file identity against the scan. Changed files, missing scan snapshots, and symbolic links or reparse points are rejected. These checks do not lock the path through deletion; avoid changing or replacing files while cleanup runs.

Removal intent and outcome are recorded for each file in `DuplicateCleanupLog.txt` in the scanned folder. If the audit log cannot be written, the app stops further removals and keeps the results of completed removals accurate. After removal, the remaining groups recompute their suggested keeper and confidence.

Results retain the folder and rules used for the scan. Editing the Scan controls does not redirect cleanup logs or change an existing dry-run report's settings. Each export gets distinct filenames. Canceling a scan discards its partial result and stops active metadata probes.

The **Activity** workspace shows session events, including scan entries that could not be read.

![Activity workspace](docs/screenshots/activity.png)

<details>
<summary>Build and test from source</summary>

You need the .NET 9 SDK on Windows. Release packaging also requires PowerShell 7 and Inno Setup 6.

```powershell
dotnet run --project DuplicateVideoCleaner.Tests/DuplicateVideoCleaner.Tests.csproj -c Release
dotnet test DuplicateVideoCleaner.UiTests/DuplicateVideoCleaner.UiTests.csproj -c Release
./build/BuildRelease.ps1
```

`BuildRelease.ps1` runs the tests, publishes the self-contained app, verifies FFmpeg, and builds the setup EXE and portable ZIP from the same payload. Outputs are under `release`; `release/SHA256SUMS.txt` covers both downloads. Use `-Force` to replace generated outputs, or `-InnoSetupCompiler <path>` if `ISCC.exe` is not on PATH. The payload directory is intermediate build output.

The reviewed FFmpeg version, dated asset, expected SHA256, source revision, and build-system revision are in `build/FFmpeg.json`. Downloads and cached archives must match the expected hash before extraction. Upstream may retire dated assets; retain the verified cache for rebuilds. An unavailable or changed asset stops the build.

```powershell
./build/Test-BuildScripts.ps1 -VerifyDependency
./build/Test-InstalledSetup.ps1 -SetupPath ./release/DuplicateVideoCleaner-1.1.1-win-x64-setup.exe
./build/Test-PortablePackage.ps1 -ArchivePath ./release/DuplicateVideoCleaner-1.1.1-win-x64-portable.zip
```

The setup check refuses to run if this app is already installed. It uses a temporary install directory and isolated preferences, verifies the payload and bundled ffprobe, launches the app, and checks uninstall cleanup. The portable check verifies extraction, hashes, screenshot files, ffprobe, and isolated app launch. Both preserve their temporary acceptance evidence.

To test real video metadata through the UI, use a valid MP4 fixture. Run this in a separate PowerShell session so test overrides end with that session:

```powershell
$env:DVC_TEST_FFPROBE = (Resolve-Path release/DuplicateVideoCleaner-1.1.1-win-x64-payload/tools/ffprobe.exe).Path
$env:DVC_TEST_MEDIA = (Resolve-Path C:/path/to/sample.mp4).Path
dotnet test DuplicateVideoCleaner.UiTests/DuplicateVideoCleaner.UiTests.csproj -c Release --filter MetadataSuccessReachesReviewConfidenceAndExport
Remove-Item Env:DVC_TEST_FFPROBE, Env:DVC_TEST_MEDIA
```

To refresh the README screenshots, run the capture tool in an interactive Windows session with a packaged app and a valid MP4 fixture:

```powershell
dotnet run --project build/ReadmeScreenshots -c Release -- <packaged-app.exe> <sample.mp4> docs/screenshots
```

The tool captures the app with PrintWindow, without including the desktop background.

The core checks cover scanning, settings, metadata, confidence, export, and removal failures. The UI checks cover navigation, selection, scan cancellation, confirmation defaults, and review/export behavior.

</details>

## Licenses

Duplicate Video Cleaner is available under the [MIT license](LICENSE.txt).

Both packages include FFmpeg's `ffprobe` and shared libraries under LGPL 2.1 or later. The app runs ffprobe as a separate process and does not modify it. See [FFmpeg notice and corresponding source](FFMPEG-NOTICE.md).
