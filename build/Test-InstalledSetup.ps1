#requires -Version 7.0
param([Parameter(Mandatory)][string]$SetupPath)
$ErrorActionPreference = 'Stop'
$appId = '{792F3FDE-9875-4D6F-A27E-529BBC1600B1}_is1'
$registryPaths = @("HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$appId", "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$appId", "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$appId")
if ($registryPaths | Where-Object { Test-Path -LiteralPath $_ }) { throw 'An existing installation was found. Acceptance testing refused to change it.' }
$setup = (Resolve-Path -LiteralPath $SetupPath).Path
$sandbox = [IO.Path]::GetFullPath((Join-Path $env:TEMP ('dvc-install-test-' + [guid]::NewGuid().ToString('N'))))
$install = Join-Path $sandbox 'app'
$application = $null
function Run-Setup([string]$Executable, [string]$Arguments) {
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) { throw "Setup process exited with code $($process.ExitCode)." }
}
try {
    [IO.Directory]::CreateDirectory($sandbox) | Out-Null
    Run-Setup $setup "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOICONS /TASKS= /DIR=`"$install`" /LOG=`"$(Join-Path $sandbox 'install.log')`""
    foreach ($required in @('DuplicateVideoCleaner.exe', 'tools\ffprobe.exe', 'FFMPEG-LICENSE.txt', 'FFMPEG-BUILD.json', 'SHA256SUMS.txt', 'unins000.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $install $required))) { throw "Installed file missing: $required" }
    }
    foreach ($entry in Get-Content -LiteralPath (Join-Path $install 'SHA256SUMS.txt')) {
        if ($entry -notmatch '^([A-Fa-f0-9]{64})  (.+)$') { throw 'Invalid installed hash manifest.' }
        $expected = $Matches[1]; $relative = $Matches[2]
        $target = [IO.Path]::GetFullPath((Join-Path $install $relative))
        if (-not $target.StartsWith($install + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Hash entry escapes the install directory.' }
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $expected) { throw "Installed hash mismatch: $relative" }
    }
    # A generated one-second mono PCM WAV exercises the installed ffprobe and DLLs.
    $fixture = Join-Path $sandbox 'sample.wav'
    $writer = [IO.BinaryWriter]::new([IO.File]::Create($fixture))
    try {
        $writer.Write([Text.Encoding]::ASCII.GetBytes('RIFF')); $writer.Write([int]16036)
        $writer.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt ')); $writer.Write([int]16)
        $writer.Write([short]1); $writer.Write([short]1); $writer.Write([int]8000); $writer.Write([int]16000)
        $writer.Write([short]2); $writer.Write([short]16)
        $writer.Write([Text.Encoding]::ASCII.GetBytes('data')); $writer.Write([int]16000); $writer.Write([byte[]]::new(16000))
    } finally { $writer.Dispose() }
    $probeOutput = & (Join-Path $install 'tools\ffprobe.exe') -v error -show_format -of json $fixture
    if ($LASTEXITCODE -ne 0 -or [double]($probeOutput | ConvertFrom-Json).format.duration -ne 1) { throw 'Installed ffprobe fixture check failed.' }
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $install 'DuplicateVideoCleaner.exe'))
    $start.UseShellExecute = $false
    $start.Environment['DVC_SETTINGS_PATH'] = Join-Path $sandbox 'settings.json'
    $start.Environment.Remove('FFPROBE_PATH') | Out-Null
    $application = [Diagnostics.Process]::Start($start)
    if (-not $application.WaitForInputIdle(15000) -or $application.HasExited) { throw 'Installed application did not become ready.' }
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $application.Refresh()
        if ($application.MainWindowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 100
    } while (-not $application.HasExited -and [DateTime]::UtcNow -lt $deadline)
    if ($application.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Installed application did not show its main window.' }
    if (-not $application.CloseMainWindow() -or -not $application.WaitForExit(10000)) { throw 'Installed application did not close normally.' }
    $application.Dispose(); $application = $null
    Run-Setup (Join-Path $install 'unins000.exe') '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
    if ($registryPaths | Where-Object { Test-Path -LiteralPath $_ }) { throw 'Uninstall registration remained after acceptance test.' }
    if (Test-Path -LiteralPath $install) { throw 'Install directory remained after uninstall.' }
    [ordered]@{
        status = 'passed'; checkedAtUtc = [DateTime]::UtcNow.ToString('o')
        setupPath = $setup; setupSha256 = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
        payloadHashes = 'passed'; ffprobeFixture = 'passed'; isolatedAppLaunch = 'passed'; uninstallCleanup = 'passed'
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $sandbox 'acceptance.json')
    Write-Host 'Isolated setup, payload hashes, ffprobe fixture, application launch, and uninstall passed.'
} finally {
    if ($application) {
        if (-not $application.HasExited) { $application.Kill(); $application.WaitForExit() }
        $application.Dispose()
    }
    $registration = Get-ItemProperty -LiteralPath $registryPaths[0] -ErrorAction SilentlyContinue
    if ($registration -and ([string]$registration.InstallLocation).TrimEnd('\') -eq $install -and (Test-Path -LiteralPath (Join-Path $install 'unins000.exe'))) {
        # Only clean up the new installation whose registry owner is our unique sandbox.
        try { Run-Setup (Join-Path $install 'unins000.exe') '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' }
        catch { Write-Warning "Temporary installation cleanup failed: $($_.Exception.Message)" }
    }
    # Preserve logs on failure. Never recursively remove an installed app or settings.
    Write-Host "Acceptance evidence: $sandbox"
}
