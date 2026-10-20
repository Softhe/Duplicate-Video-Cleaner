#requires -Version 7.0
param([Parameter(Mandatory)][string]$SetupPath, [string]$PreviousSetupPath, [switch]$RequireVirtualMachine, [switch]$RunUiTests, [string]$EvidencePath)
$ErrorActionPreference = 'Stop'
$appId = '{792F3FDE-9875-4D6F-A27E-529BBC1600B1}_is1'
$registryPaths = @("HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$appId", "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$appId", "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\$appId")
if ($registryPaths | Where-Object { Test-Path -LiteralPath $_ }) { throw 'An existing installation was found. Acceptance testing refused to change it.' }
$setup = (Resolve-Path -LiteralPath $SetupPath).Path
$computer = Get-CimInstance Win32_ComputerSystem
$isVirtual = "$($computer.Manufacturer) $($computer.Model)" -match 'Virtual Machine|VMware|VirtualBox|KVM|QEMU|Xen|Amazon EC2|Google Compute'
if ($RequireVirtualMachine -and -not $isVirtual) { throw 'Release qualification requires a disposable virtual machine. This host was not identified as one.' }
$sandbox = [IO.Path]::GetFullPath((Join-Path $env:TEMP ('dvc-install-test-' + [guid]::NewGuid().ToString('N'))))
$install = Join-Path $sandbox 'app'
$application = $null
function Run-Setup([string]$Executable, [string]$Arguments) {
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) { throw "Setup process exited with code $($process.ExitCode)." }
}
function Save-SettingsWithInstalledApp([string]$Executable, [string]$SettingsPath) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.Environment['DVC_SETTINGS_PATH'] = $SettingsPath
    $start.Environment.Remove('FFPROBE_PATH') | Out-Null
    $script:application = [Diagnostics.Process]::Start($start)
    if (-not $application.WaitForInputIdle(15000) -or $application.HasExited) { throw 'Installed application did not become ready.' }
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $application.Refresh()
        if ($application.MainWindowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 100
    } while (-not $application.HasExited -and [DateTime]::UtcNow -lt $deadline)
    if ($application.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Installed application did not show its main window.' }
    if (-not $application.CloseMainWindow() -or -not $application.WaitForExit(10000)) { throw 'Installed application did not close normally.' }
    $application.Dispose(); $script:application = $null
}
try {
    [IO.Directory]::CreateDirectory($sandbox) | Out-Null
    if ($PreviousSetupPath) {
        $previous = (Resolve-Path -LiteralPath $PreviousSetupPath).Path
        Run-Setup $previous "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOICONS /TASKS= /DIR=`"$install`" /LOG=`"$(Join-Path $sandbox 'previous-install.log')`""
    }
    $settingsFixture = Join-Path $sandbox 'settings.json'
    [IO.File]::WriteAllText($settingsFixture, (@{ LastFolder = $sandbox; ThresholdSeconds = 123; KeepMode = 1; IncludeSubfolders = $true; PermanentDeletion = $false } | ConvertTo-Json))
    $previousVersion = $null
    if ($PreviousSetupPath) {
        $previousVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $install 'DuplicateVideoCleaner.exe')).FileVersion
        Save-SettingsWithInstalledApp (Join-Path $install 'DuplicateVideoCleaner.exe') $settingsFixture
        $oldSettings = Get-Content -LiteralPath $settingsFixture -Raw | ConvertFrom-Json
        if ($oldSettings.ThresholdSeconds -ne 123 -or $oldSettings.KeepMode -ne 1 -or -not $oldSettings.IncludeSubfolders) { throw 'Previous application did not save non-default upgrade settings.' }
    }
    Run-Setup $setup "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOICONS /TASKS= /DIR=`"$install`" /LOG=`"$(Join-Path $sandbox 'install.log')`""
    [xml]$project = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../DuplicateVideoCleaner/DuplicateVideoCleaner.csproj')
    $expectedVersion = [string]$project.Project.PropertyGroup.FileVersion
    $installedVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $install 'DuplicateVideoCleaner.exe')).FileVersion
    if ($installedVersion -ne $expectedVersion) { throw "Installed version $installedVersion does not match candidate $expectedVersion." }
    if ($PreviousSetupPath -and $previousVersion -eq $installedVersion) { throw 'Upgrade acceptance requires distinct previous and candidate versions.' }
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
    Save-SettingsWithInstalledApp (Join-Path $install 'DuplicateVideoCleaner.exe') $settingsFixture
    $savedSettings = Get-Content -LiteralPath $settingsFixture -Raw | ConvertFrom-Json
    if ($savedSettings.LastFolder -ne $sandbox -or $savedSettings.ThresholdSeconds -ne 123 -or $savedSettings.KeepMode -ne 1 -or -not $savedSettings.IncludeSubfolders) { throw 'Installed app did not preserve the settings fixture.' }
    if ($RunUiTests) {
        $priorAppOverride = $env:DVC_TEST_APP_PATH
        $priorVideoEvidence = $env:DVC_TEST_VIDEO_EVIDENCE
        try {
            $env:DVC_TEST_APP_PATH = Join-Path $install 'DuplicateVideoCleaner.exe'
            $env:DVC_TEST_VIDEO_EVIDENCE = Join-Path $sandbox 'sandboxed-video.json'
            dotnet test (Join-Path $PSScriptRoot '../DuplicateVideoCleaner.UiTests') -c Release --filter 'FullyQualifiedName~WorkspaceWorkflowTests' --logger "trx;LogFileName=$(Join-Path $sandbox 'installed-ui.trx')"
            if ($LASTEXITCODE -ne 0) { throw 'Installed application UI acceptance failed.' }
            dotnet test (Join-Path $PSScriptRoot '../DuplicateVideoCleaner.UiTests') -c Release --filter 'FullyQualifiedName~InstalledVideoWorkflowTests' --logger "trx;LogFileName=$(Join-Path $sandbox 'installed-video.trx')"
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $env:DVC_TEST_VIDEO_EVIDENCE)) { throw 'Installed sandboxed video acceptance failed or did not execute.' }
            $videoEvidence = Get-Content -LiteralPath $env:DVC_TEST_VIDEO_EVIDENCE -Raw | ConvertFrom-Json
            if ($videoEvidence.status -ne 'passed' -or $videoEvidence.executable -ne $env:DVC_TEST_APP_PATH) { throw 'Sandboxed video evidence does not match the installed candidate.' }
        } finally { $env:DVC_TEST_APP_PATH = $priorAppOverride; $env:DVC_TEST_VIDEO_EVIDENCE = $priorVideoEvidence }
    }
    Run-Setup (Join-Path $install 'unins000.exe') '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
    if ($registryPaths | Where-Object { Test-Path -LiteralPath $_ }) { throw 'Uninstall registration remained after acceptance test.' }
    if (Test-Path -LiteralPath $install) { throw 'Install directory remained after uninstall.' }
    $acceptance = [ordered]@{
        status = 'passed'; checkedAtUtc = [DateTime]::UtcNow.ToString('o')
        setupPath = $setup; setupSha256 = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
        payloadHashes = 'passed'; ffprobeFixture = 'passed'; isolatedAppLaunch = 'passed'; uninstallCleanup = 'passed'
        settingsPreserved = 'passed'; upgrade = $(if ($PreviousSetupPath) { 'passed' } else { 'not-exercised' })
        installedUi = $(if ($RunUiTests) { 'passed' } else { 'not-exercised' })
        sandboxedVideo = $(if ($RunUiTests) { 'passed' } else { 'not-exercised' })
        upgradeSettings = $(if ($PreviousSetupPath) { 'passed' } else { 'not-exercised' })
        previousVersion = $previousVersion; installedVersion = $installedVersion
        virtualMachine = [bool]$isVirtual; machineManufacturer = $computer.Manufacturer; machineModel = $computer.Model
    }
    $acceptance | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $sandbox 'acceptance.json')
    if ($EvidencePath) { $acceptance | ConvertTo-Json | Set-Content -LiteralPath $EvidencePath }
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
    if ($EvidencePath -and (Test-Path -LiteralPath $sandbox)) {
        $evidenceDirectory = Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($EvidencePath))) 'acceptance-evidence'
        [IO.Directory]::CreateDirectory($evidenceDirectory) | Out-Null
        Get-ChildItem -LiteralPath $sandbox -File | Where-Object Extension -in '.log','.trx','.json' | Copy-Item -Destination $evidenceDirectory
    }
    Write-Host "Acceptance evidence: $sandbox"
}
