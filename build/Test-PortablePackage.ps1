#requires -Version 7.0
param([Parameter(Mandatory)][string]$ArchivePath, [switch]$RequireVirtualMachine, [switch]$RunUiTests, [string]$EvidencePath)
$ErrorActionPreference = 'Stop'
$archive = (Resolve-Path -LiteralPath $ArchivePath).Path
$folderName = [IO.Path]::GetFileNameWithoutExtension($archive)
if ($folderName -notmatch '^DuplicateVideoCleaner-\d+\.\d+\.\d+-win-x64-portable$') { throw 'Unexpected portable archive filename.' }
$sandbox = [IO.Path]::GetFullPath((Join-Path $env:TEMP ('dvc-portable-test-' + [guid]::NewGuid().ToString('N'))))
$application = $null
$computer = Get-CimInstance Win32_ComputerSystem
$isVirtual = "$($computer.Manufacturer) $($computer.Model)" -match 'Virtual|VMware|KVM|QEMU|Xen|Hyper-V|Parallels'
if ($RequireVirtualMachine -and -not $isVirtual) { throw 'Portable release qualification requires a disposable VM.' }
try {
    Expand-Archive -LiteralPath $archive -DestinationPath $sandbox
    $payload = Join-Path $sandbox $folderName
    foreach ($required in @('DuplicateVideoCleaner.exe', 'tools\ffprobe.exe', 'SHA256SUMS.txt', 'FFMPEG-LICENSE.txt', 'FFMPEG-BUILD.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $payload $required))) { throw "Portable file missing: $required" }
    }
    foreach ($entry in Get-Content -LiteralPath (Join-Path $payload 'SHA256SUMS.txt')) {
        if ($entry -notmatch '^([A-Fa-f0-9]{64})  (.+)$') { throw 'Invalid portable payload hash manifest.' }
        $expected = $Matches[1]
        $target = [IO.Path]::GetFullPath((Join-Path $payload $Matches[2]))
        if (-not $target.StartsWith($payload + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Hash entry escapes portable directory.' }
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $expected) { throw "Portable hash mismatch: $target" }
    }
    foreach ($image in @('scan.png', 'review.png', 'review-details.png', 'confirmation.png', 'activity.png')) {
        if (-not (Test-Path -LiteralPath (Join-Path $payload "docs\screenshots\$image"))) { throw "Bundled README image missing: $image" }
    }
    & (Join-Path $payload 'tools\ffprobe.exe') -version | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Portable ffprobe failed.' }
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $payload 'DuplicateVideoCleaner.exe'))
    $start.UseShellExecute = $false
    $start.Environment['DVC_SETTINGS_PATH'] = Join-Path $sandbox 'settings.json'
    $start.Environment.Remove('FFPROBE_PATH') | Out-Null
    $application = [Diagnostics.Process]::Start($start)
    if (-not $application.WaitForInputIdle(15000) -or $application.HasExited) { throw 'Portable app did not become ready.' }
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $application.Refresh()
        if ($application.MainWindowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 100
    } while (-not $application.HasExited -and [DateTime]::UtcNow -lt $deadline)
    if ($application.MainWindowHandle -eq [IntPtr]::Zero -or -not $application.CloseMainWindow() -or -not $application.WaitForExit(10000)) { throw 'Portable app did not show and close its main window.' }
    if ($RunUiTests) {
        $priorApp = $env:DVC_TEST_APP_PATH
        $priorEvidence = $env:DVC_TEST_VIDEO_EVIDENCE
        try {
            $env:DVC_TEST_APP_PATH = Join-Path $payload 'DuplicateVideoCleaner.exe'
            $env:DVC_TEST_VIDEO_EVIDENCE = Join-Path $sandbox 'sandboxed-video.json'
            dotnet test (Join-Path $PSScriptRoot '../DuplicateVideoCleaner.UiTests') -c Release --filter 'FullyQualifiedName~InstalledVideoWorkflowTests' --logger "trx;LogFileName=$(Join-Path $sandbox 'portable-video.trx')"
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $env:DVC_TEST_VIDEO_EVIDENCE)) { throw 'Portable sandboxed video acceptance failed or did not execute.' }
            $video = Get-Content -LiteralPath $env:DVC_TEST_VIDEO_EVIDENCE -Raw | ConvertFrom-Json
            if ($video.status -ne 'passed' -or $video.executable -ne $env:DVC_TEST_APP_PATH) { throw 'Portable video evidence does not match the extracted app.' }
        } finally { $env:DVC_TEST_APP_PATH = $priorApp; $env:DVC_TEST_VIDEO_EVIDENCE = $priorEvidence }
    }
    $record = [ordered]@{
        status = 'passed'; checkedAtUtc = [DateTime]::UtcNow.ToString('o')
        archivePath = $archive; archiveSha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
        payloadHashes = 'passed'; bundledScreenshots = 'passed'; ffprobeLaunch = 'passed'; isolatedAppLaunch = 'passed'
        sandboxedVideo = $(if ($RunUiTests) { 'passed' } else { 'not-exercised' }); virtualMachine = [bool]$isVirtual
    }
    $record | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $sandbox 'acceptance.json')
    if ($EvidencePath) { $record | ConvertTo-Json | Set-Content -LiteralPath $EvidencePath }
    Write-Host 'Portable extraction, hashes, screenshots, ffprobe, and isolated application launch passed.'
} finally {
    if ($application) {
        if (-not $application.HasExited) { $application.Kill(); $application.WaitForExit() }
        $application.Dispose()
    }
    if ($EvidencePath -and (Test-Path -LiteralPath $sandbox)) {
        $evidenceDirectory = Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($EvidencePath))) 'acceptance-evidence/portable'
        [IO.Directory]::CreateDirectory($evidenceDirectory) | Out-Null
        Get-ChildItem -LiteralPath $sandbox -File | Where-Object Extension -in '.log','.trx','.json' | Copy-Item -Destination $evidenceDirectory
    }
    Write-Host "Portable acceptance evidence: $sandbox"
}
