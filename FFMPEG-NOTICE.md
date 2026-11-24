# FFmpeg notice

This product distributes `ffprobe`, `ffmpeg` and their required shared libraries from FFmpeg under the GNU Lesser General Public License, version 3 or later. The build is configured with `--enable-version3`, which applies LGPL version 3 to the libraries.

- Upstream: https://ffmpeg.org/
- Exact FFmpeg source revision: https://github.com/FFmpeg/FFmpeg/tree/660b5aa7dbce1f95cb04b3c7b8e94d79f893d15d
- Exact source archive: https://github.com/FFmpeg/FFmpeg/archive/660b5aa7dbce1f95cb04b3c7b8e94d79f893d15d.tar.gz
- Windows build release: https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-11-22-12-36
- Binary artifact: `ffmpeg-n8.1.3-39-g660b5aa7db-win64-lgpl-shared-8.1.zip`
- Expected binary archive SHA256: `fd6d485f1ed515b1ca30c99f934dbb4a9c18de2949d5f5c2a03b069a8420ce38`
- Variant: Windows x64, LGPL shared; GPL/nonfree dependencies disabled.
- Build system and dependency recipes: https://github.com/BtbN/FFmpeg-Builds/tree/7f2a6b1050d302144de8366afa37a75e46f326aa

The exact configure line is reported by `tools\ffprobe.exe -version` and `tools\ffmpeg.exe -version`. The included `FFMPEG-LICENSE.txt` contains the distributor's license text. `FFMPEG-BUILD.json` records the reviewed dependency pin, and `SHA256SUMS.txt` lists the installed payload files. Download the corresponding source revision above and consult the build system for the build configuration and dependency recipes.

Duplicate Video Cleaner launches ffprobe (metadata) and ffmpeg (one thumbnail frame per video) as separate processes and does not modify FFmpeg. The shared libraries remain separate files in `tools` and may be replaced with compatible LGPL builds.
