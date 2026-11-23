param([string]$CacheDirectory = (Join-Path $env:LOCALAPPDATA 'DuplicateVideoCleaner\BuildCache'))
$ErrorActionPreference = 'Stop'
$pin = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'FFmpeg.json') -Raw | ConvertFrom-Json
if ($pin.release -notmatch '^autobuild-[\d-]+$' -or $pin.archive -notmatch '^ffmpeg-[\w.-]+-win64-lgpl-shared-8\.1\.zip$' -or $pin.sha256 -notmatch '^[a-f0-9]{64}$') { throw 'Invalid FFmpeg pin.' }
# BtbN keeps dated autobuilds for a few weeks only. Mirrors are HTTPS copies of the same
# archive (for example an asset on this project's own releases); the hash decides.
$sources = @("https://github.com/BtbN/FFmpeg-Builds/releases/download/$($pin.release)/$($pin.archive)") + @($pin.mirrors | Where-Object { $_ })
foreach ($source in $sources) { if ($source -notmatch '^https://[^\s]+$') { throw "Invalid FFmpeg source: $source" } }
[IO.Directory]::CreateDirectory($CacheDirectory) | Out-Null
$archive = Join-Path $CacheDirectory $pin.archive
function Assert-Archive([string]$Path) {
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $pin.sha256) { throw "FFmpeg SHA256 mismatch: $Path. Remove this cache entry and retry; extraction was refused." }
}
if (Test-Path -LiteralPath $archive) { Assert-Archive $archive }
else {
    $failures = @()
    foreach ($source in $sources) {
        $partial = "$archive.$([guid]::NewGuid().ToString('N')).partial"
        try {
            Invoke-WebRequest -Uri $source -OutFile $partial
            Assert-Archive $partial
            Move-Item -LiteralPath $partial -Destination $archive
            break
        }
        catch { $failures += "$source -> $($_.Exception.Message)" }
        finally { if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force } }
    }
    if (-not (Test-Path -LiteralPath $archive)) {
        throw "The pinned FFmpeg archive is unavailable from every source. Re-pin build/FFmpeg.json to a current BtbN autobuild or add a mirror.`n$($failures -join "`n")"
    }
}
# Return only a verified archive; each build extracts to its own temporary directory.
Write-Output $archive
