# Video acceptance fixture

`one-second.mp4` is an 881-byte synthetic black video, one second long at 64 x 48 pixels. It contains no user media or audio. It was generated for this project and may be used under the repository license.

Generation command:

```powershell
ffmpeg -hide_banner -loglevel error -f lavfi -i color=c=black:s=64x48:r=1:d=1 -an -c:v mpeg4 -q:v 5 -map_metadata -1 -fflags +bitexact -flags:v +bitexact -y one-second.mp4
```

Acceptance reads this checked-in file. Generating it again is optional and does not require shipping ffmpeg with the application.
