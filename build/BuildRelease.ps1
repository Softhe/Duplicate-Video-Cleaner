#requires -Version 7.0
param([string]$Configuration = "Release", [switch]$Force, [string]$InnoSetupCompiler = "ISCC.exe")

$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
[xml]$project = Get-Content -LiteralPath (Join-Path $root 'DuplicateVideoCleaner\DuplicateVideoCleaner.csproj')
$appVersion = [string]$project.Project.PropertyGroup.Version
if ($appVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid application version.' }
$compiler = (Get-Command $InnoSetupCompiler -ErrorAction Stop).Source
$releaseRoot = Join-Path $root "release\DuplicateVideoCleaner-$appVersion-win-x64-portable"
$setupPath = Join-Path $root "release\DuplicateVideoCleaner-$appVersion-win-x64-setup.exe"
$portablePath = "$releaseRoot.zip"
if ((Test-Path -LiteralPath $releaseRoot) -or (Test-Path -LiteralPath $setupPath) -or (Test-Path -LiteralPath $portablePath)) {
    if (-not $Force) { throw 'Release output exists. Use -Force to replace generated release files.' }
}
$pin = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'FFmpeg.json') -Raw | ConvertFrom-Json
& (Join-Path $PSScriptRoot 'Test-ReleaseAcceptance.ps1')
dotnet run --project (Join-Path $root "DuplicateVideoCleaner.Tests\DuplicateVideoCleaner.Tests.csproj") -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Unit/integration tests failed." }
dotnet test (Join-Path $root "DuplicateVideoCleaner.UiTests\DuplicateVideoCleaner.UiTests.csproj") -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "UI tests failed." }
dotnet publish (Join-Path $root "DuplicateVideoCleaner\DuplicateVideoCleaner.csproj") -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $root "DuplicateVideoCleaner\publish") --source "https://api.nuget.org/v3/index.json"
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

$archive = & (Join-Path $PSScriptRoot 'Get-FFmpeg.ps1')
$extract = Join-Path $env:TEMP ("dvc-ffmpeg-" + [guid]::NewGuid().ToString("N"))
try {
Expand-Archive -LiteralPath $archive -DestinationPath $extract
$ffmpegRoots = @(Get-ChildItem -LiteralPath $extract -Directory)
if ($ffmpegRoots.Count -ne 1) { throw 'Unexpected FFmpeg archive layout.' }
$ffmpegRoot = $ffmpegRoots[0]
$version = & (Join-Path $ffmpegRoot.FullName "bin\ffprobe.exe") -version 2>&1
if ($LASTEXITCODE -ne 0 -or $version[0] -notlike "ffprobe version $($pin.version)*" -or ($version -join ' ') -match 'enable-(gpl|nonfree)') { throw 'Unexpected FFmpeg build.' }
if (Test-Path -LiteralPath $releaseRoot) {
    if (-not $Force) { throw "Release directory already exists: $releaseRoot (use -Force to replace generated release artifacts)" }
    $expectedReleaseParent = [System.IO.Path]::GetFullPath((Join-Path $root "release"))
    $resolvedRelease = [System.IO.Path]::GetFullPath($releaseRoot)
    if ([System.IO.Path]::GetDirectoryName($resolvedRelease) -ne $expectedReleaseParent -or [System.IO.Path]::GetFileName($resolvedRelease) -ne "DuplicateVideoCleaner-$appVersion-win-x64-portable") { throw "Refusing unexpected release cleanup target: $resolvedRelease" }
    Remove-Item -LiteralPath $resolvedRelease -Recurse -Force
}
New-Item -ItemType Directory -Path (Join-Path $releaseRoot "tools") -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root "DuplicateVideoCleaner\publish\DuplicateVideoCleaner.exe") -Destination $releaseRoot
Copy-Item -LiteralPath (Join-Path $ffmpegRoot.FullName "bin\ffprobe.exe") -Destination (Join-Path $releaseRoot "tools")
Get-ChildItem -LiteralPath (Join-Path $ffmpegRoot.FullName "bin") -Filter "*.dll" | Copy-Item -Destination (Join-Path $releaseRoot "tools")
Copy-Item -LiteralPath (Join-Path $root "README.md"),(Join-Path $root "CHANGELOG.md"),(Join-Path $root "LICENSE.txt"),(Join-Path $root "FFMPEG-NOTICE.md") -Destination $releaseRoot
Copy-Item -LiteralPath (Join-Path $ffmpegRoot.FullName "LICENSE.txt") -Destination (Join-Path $releaseRoot "FFMPEG-LICENSE.txt")
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'FFmpeg.json') -Destination (Join-Path $releaseRoot 'FFMPEG-BUILD.json')
New-Item -ItemType Directory -Path (Join-Path $releaseRoot 'docs') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'docs\PERFORMANCE.md') -Destination (Join-Path $releaseRoot 'docs')
Copy-Item -LiteralPath (Join-Path $root 'docs\screenshots') -Destination (Join-Path $releaseRoot 'docs') -Recurse
$checksums = Get-ChildItem -LiteralPath $releaseRoot -Recurse -File | Sort-Object FullName | ForEach-Object { "{0}  {1}" -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, [System.IO.Path]::GetRelativePath($releaseRoot, $_.FullName) }
[System.IO.File]::WriteAllLines((Join-Path $releaseRoot "SHA256SUMS.txt"), $checksums)
& $compiler "/DAppVersion=$appVersion" "/DPayloadDirectory=$releaseRoot" "/O$(Join-Path $root 'release')" (Join-Path $PSScriptRoot 'Setup.iss')
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $setupPath)) { throw 'Setup compilation failed.' }
Compress-Archive -LiteralPath $releaseRoot -DestinationPath $portablePath -CompressionLevel Optimal -Force:$Force
$assetChecksums = @($setupPath, $portablePath) | ForEach-Object { '{0}  {1}' -f (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash, [IO.Path]::GetFileName($_) }
[IO.File]::WriteAllLines((Join-Path $root 'release\SHA256SUMS.txt'), $assetChecksums)
Write-Host "Setup created: $setupPath"
Write-Host "Setup SHA256: $((Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash)"
Write-Host "Portable created: $portablePath"
Write-Host "Portable SHA256: $((Get-FileHash -LiteralPath $portablePath -Algorithm SHA256).Hash)"
} finally {
    $resolvedExtract = [IO.Path]::GetFullPath($extract)
    if ([IO.Path]::GetDirectoryName($resolvedExtract) -eq [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') -and [IO.Path]::GetFileName($resolvedExtract) -match '^dvc-ffmpeg-[a-f0-9]{32}$') {
        if (Test-Path -LiteralPath $resolvedExtract) { Remove-Item -LiteralPath $resolvedExtract -Recurse -Force }
    }
}
