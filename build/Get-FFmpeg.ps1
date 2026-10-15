param([string]$CacheDirectory = (Join-Path $env:LOCALAPPDATA 'DuplicateVideoCleaner\BuildCache'))
$ErrorActionPreference = 'Stop'
$pin = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'FFmpeg.json') -Raw | ConvertFrom-Json
if ($pin.release -notmatch '^autobuild-[\d-]+$' -or $pin.archive -notmatch '^ffmpeg-[\w.-]+-win64-lgpl-shared-8\.1\.zip$' -or $pin.sha256 -notmatch '^[a-f0-9]{64}$') { throw 'Invalid FFmpeg pin.' }
[IO.Directory]::CreateDirectory($CacheDirectory) | Out-Null
$archive = Join-Path $CacheDirectory $pin.archive
function Assert-Archive([string]$Path) {
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $pin.sha256) { throw "FFmpeg SHA256 mismatch: $Path. Remove this cache entry and retry; extraction was refused." }
}
if (Test-Path -LiteralPath $archive) { Assert-Archive $archive }
else {
    $partial = "$archive.$([guid]::NewGuid().ToString('N')).partial"
    try {
        Invoke-WebRequest -Uri "https://github.com/BtbN/FFmpeg-Builds/releases/download/$($pin.release)/$($pin.archive)" -OutFile $partial
        Assert-Archive $partial
        Move-Item -LiteralPath $partial -Destination $archive
    } finally { if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force } }
}
# Return only a verified archive; each build extracts to its own temporary directory.
Write-Output $archive
