# FFmpeg notice

This product distributes `ffprobe` and its required shared libraries from FFmpeg under the GNU Lesser General Public License, version 2.1 or later.

- Upstream: https://ffmpeg.org/
- Source release: https://ffmpeg.org/releases/ffmpeg-8.1.2.tar.xz
- Windows build system: https://github.com/BtbN/FFmpeg-Builds
- Binary artifact: `ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip`
- Binary version: n8.1 series, LGPL shared. Run `tools\ffprobe.exe -version` for the exact build in your download.
- Variant: Windows x64, LGPL shared; GPL/nonfree dependencies disabled.
- Per-file SHA-256 hashes for your download are listed in `SHA256SUMS.txt`.

The exact configure line is reported by `tools\ffprobe.exe -version`. The included `FFMPEG-LICENSE.txt` contains the distributor's license text. Corresponding FFmpeg source is available from the upstream release URL; the BtbN build scripts and configuration are available from the build-system URL.

Duplicate Video Cleaner launches `ffprobe` as a separate process and does not modify FFmpeg.
