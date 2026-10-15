param([string]$Configuration = "Release", [switch]$Force)

$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$releaseRoot = Join-Path $root "release\DuplicateVideoCleaner-1.1.0-win-x64-portable"
$archive = Join-Path $env:TEMP "ffmpeg-n8.1-win64-lgpl-shared.zip"
$ffmpegUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip"

dotnet run --project (Join-Path $root "DuplicateVideoCleaner.Tests\DuplicateVideoCleaner.Tests.csproj") -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Unit/integration tests failed." }
dotnet test (Join-Path $root "DuplicateVideoCleaner.UiTests\DuplicateVideoCleaner.UiTests.csproj") -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "UI tests failed." }
dotnet publish (Join-Path $root "DuplicateVideoCleaner\DuplicateVideoCleaner.csproj") -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $root "DuplicateVideoCleaner\publish") --source "https://api.nuget.org/v3/index.json"
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri $ffmpegUrl -OutFile $archive }
Write-Host "FFmpeg archive SHA256: $((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash)"
$extract = Join-Path $env:TEMP ("dvc-ffmpeg-" + [guid]::NewGuid().ToString("N"))
Expand-Archive -LiteralPath $archive -DestinationPath $extract
$ffmpegRoot = Get-ChildItem -LiteralPath $extract -Directory | Select-Object -First 1
$version = & (Join-Path $ffmpegRoot.FullName "bin\ffprobe.exe") -version
if ($version[0] -notmatch "ffprobe version n8\.1" -or $version[2] -match "enable-(gpl|nonfree)") { throw "Unexpected FFmpeg build." }

if (Test-Path -LiteralPath $releaseRoot) {
    if (-not $Force) { throw "Release directory already exists: $releaseRoot (use -Force to replace generated release artifacts)" }
    $expectedReleaseParent = [System.IO.Path]::GetFullPath((Join-Path $root "release"))
    $resolvedRelease = [System.IO.Path]::GetFullPath($releaseRoot)
    if ([System.IO.Path]::GetDirectoryName($resolvedRelease) -ne $expectedReleaseParent -or [System.IO.Path]::GetFileName($resolvedRelease) -ne "DuplicateVideoCleaner-1.1.0-win-x64-portable") { throw "Refusing unexpected release cleanup target: $resolvedRelease" }
    Remove-Item -LiteralPath $resolvedRelease -Recurse -Force
}
New-Item -ItemType Directory -Path (Join-Path $releaseRoot "tools") -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root "DuplicateVideoCleaner\publish\DuplicateVideoCleaner.exe") -Destination $releaseRoot
Copy-Item -LiteralPath (Join-Path $ffmpegRoot.FullName "bin\ffprobe.exe") -Destination (Join-Path $releaseRoot "tools")
Get-ChildItem -LiteralPath (Join-Path $ffmpegRoot.FullName "bin") -Filter "*.dll" | Copy-Item -Destination (Join-Path $releaseRoot "tools")
Copy-Item -LiteralPath (Join-Path $root "README.md"),(Join-Path $root "CHANGELOG.md"),(Join-Path $root "LICENSE.txt"),(Join-Path $root "FFMPEG-NOTICE.md") -Destination $releaseRoot
Copy-Item -LiteralPath (Join-Path $ffmpegRoot.FullName "LICENSE.txt") -Destination (Join-Path $releaseRoot "FFMPEG-LICENSE.txt")
$checksums = Get-ChildItem -LiteralPath $releaseRoot -Recurse -File | Sort-Object FullName | ForEach-Object { "{0}  {1}" -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, [System.IO.Path]::GetRelativePath($releaseRoot, $_.FullName) }
[System.IO.File]::WriteAllLines((Join-Path $releaseRoot "SHA256SUMS.txt"), $checksums)
$zipPath = Join-Path $root "release\DuplicateVideoCleaner-1.1.0-win-x64-portable.zip"
if (Test-Path -LiteralPath $zipPath) {
    if (-not $Force) { throw "Release archive already exists: $zipPath" }
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -LiteralPath $releaseRoot -DestinationPath $zipPath -CompressionLevel Optimal
Write-Host "Release created: $zipPath"
