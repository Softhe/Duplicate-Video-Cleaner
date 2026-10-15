# FFmpeg notice

This product distributes `ffprobe` and its required shared libraries from FFmpeg under the GNU Lesser General Public License, version 2.1 or later.

- Upstream: https://ffmpeg.org/
- Source release: https://ffmpeg.org/releases/ffmpeg-8.1.2.tar.xz
- Windows build system: https://github.com/BtbN/FFmpeg-Builds
- Binary artifact: `ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip`
- Binary version: `n8.1.2-34-g9b6c8969e0-20260801`
- Artifact SHA-256: `D0E27B409599525395A0982C88A4597D0F764EF038AEB20ED5DBCA74B3654FBF`
- Variant: Windows x64, LGPL shared; GPL/nonfree dependencies disabled.

The exact configure line is reported by `tools\ffprobe.exe -version`. The included `FFMPEG-LICENSE.txt` contains the distributor's license text. Corresponding FFmpeg source is available from the upstream release URL; the BtbN build scripts and configuration are available from the build-system URL.

Duplicate Video Cleaner launches `ffprobe` as a separate process and does not modify FFmpeg.
