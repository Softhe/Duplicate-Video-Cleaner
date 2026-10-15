# FFmpeg notice

This product distributes `ffprobe` and its required shared libraries from FFmpeg under the GNU Lesser General Public License, version 2.1 or later.

- Upstream: https://ffmpeg.org/
- Exact FFmpeg source revision: https://github.com/FFmpeg/FFmpeg/tree/46d899d7a02bc63237472cc4897258d5ddf274ac
- Exact source archive: https://github.com/FFmpeg/FFmpeg/archive/46d899d7a02bc63237472cc4897258d5ddf274ac.tar.gz
- Windows build release: https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-10-15-13-03
- Binary artifact: `ffmpeg-n8.1.3-18-g46d899d7a0-win64-lgpl-shared-8.1.zip`
- Expected binary archive SHA256: `4acd82323c2cad9c7ffea5b8e335ebcda39beae83a3138d729545e5a7666a3fd`
- Variant: Windows x64, LGPL shared; GPL/nonfree dependencies disabled.
- Build system and dependency recipes: https://github.com/BtbN/FFmpeg-Builds/tree/63ac3265b0789ea16c7b1f8351cd2ad195a28722

The exact configure line is reported by `tools\ffprobe.exe -version`. The included `FFMPEG-LICENSE.txt` contains the distributor's license text. `FFMPEG-BUILD.json` records the reviewed dependency pin, and `SHA256SUMS.txt` lists the installed payload files. Download the corresponding source revision above and consult the build system for the build configuration and dependency recipes.

Duplicate Video Cleaner launches ffprobe as a separate process and does not modify FFmpeg. Its shared libraries remain separate files in `tools` and may be replaced with compatible LGPL builds.
