param([switch]$VerifyDependency)
$ErrorActionPreference = 'Stop'
foreach ($script in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1') {
    $tokens = $null; $errors = $null
    [Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$errors) | Out-Null
    if ($errors.Count) { throw "Parse failure in $($script.Name): $errors" }
}
$cache = Join-Path $env:TEMP ('dvc-cache-test-' + [guid]::NewGuid().ToString('N'))
try {
    [IO.Directory]::CreateDirectory($cache) | Out-Null
    $pin = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'FFmpeg.json') -Raw | ConvertFrom-Json
    [IO.File]::WriteAllText((Join-Path $cache $pin.archive), 'corrupted test fixture')
    $rejected = $false
    try { & (Join-Path $PSScriptRoot 'Get-FFmpeg.ps1') -CacheDirectory $cache | Out-Null }
    catch { if ($_.Exception.Message -notlike '*SHA256 mismatch*') { throw }; $rejected = $true }
    if (-not $rejected) { throw 'Corrupted cache was accepted.' }
    $setup = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Setup.iss') -Raw
    foreach ($required in @('PrivilegesRequired=lowest', 'DefaultDirName={localappdata}\Programs\Duplicate Video Cleaner', 'ArchitecturesAllowed=x64compatible', 'recursesubdirs', 'skipifsilent')) {
        if (-not $setup.Contains($required)) { throw "Missing setup requirement: $required" }
    }
    $build = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'BuildRelease.ps1') -Raw
    if ($build -match 'releases/download/latest/') { throw 'Release script must use the pinned dependency.' }
    foreach ($required in @('win-x64-portable', 'ZipFileExtensions', 'release\SHA256SUMS.txt', 'docs\screenshots')) {
        if (-not $build.Contains($required)) { throw "Missing release packaging requirement: $required" }
    }
    if ($VerifyDependency) {
        $first = & (Join-Path $PSScriptRoot 'Get-FFmpeg.ps1')
        $second = & (Join-Path $PSScriptRoot 'Get-FFmpeg.ps1')
        if ($first -ne $second -or (Get-FileHash -LiteralPath $second -Algorithm SHA256).Hash -ne $pin.sha256) { throw 'Verified cache reuse failed.' }
    }
    Write-Host 'Build script parsing, setup configuration, and corrupted-cache rejection passed.'
} finally {
    $resolved = [IO.Path]::GetFullPath($cache)
    if ([IO.Path]::GetDirectoryName($resolved) -eq [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') -and [IO.Path]::GetFileName($resolved) -match '^dvc-cache-test-[a-f0-9]{32}$') {
        if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
}
