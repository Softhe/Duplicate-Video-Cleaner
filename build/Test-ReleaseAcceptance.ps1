$ErrorActionPreference = 'Stop'
$fixture = Join-Path $env:TEMP ('dvc-acceptance-guard-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture) | Out-Null
try {
    $setup = Join-Path $fixture 'fixture.exe'
    $evidence = Join-Path $fixture 'acceptance.json'
    [IO.File]::WriteAllText($setup, 'Non-executable checksum test fixture')
    $record = @{
        status = 'passed'; virtualMachine = $true
        payloadHashes = 'passed'; ffprobeFixture = 'passed'; isolatedAppLaunch = 'passed'
        uninstallCleanup = 'passed'; settingsPreserved = 'passed'; upgrade = 'passed'; installedUi = 'passed'
        sandboxedVideo = 'passed'; upgradeSettings = 'passed'
        setupSha256 = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
    }
    function Check-Rejected([string]$Expected) {
        $record | ConvertTo-Json | Set-Content -LiteralPath $evidence
        $rejected = $false
        try { & (Join-Path $PSScriptRoot 'Assert-ReleaseAcceptance.ps1') -SetupPath $setup -EvidencePath $evidence }
        catch { if ($_.Exception.Message -notlike $Expected) { throw }; $rejected = $true }
        if (-not $rejected) { throw 'Invalid release acceptance was accepted.' }
    }
    $record.virtualMachine = $false
    Check-Rejected '*disposable VM*'
    $record.virtualMachine = $true
    $record.upgrade = 'not-exercised'
    Check-Rejected '*upgrade*'
    $record.upgrade = 'passed'
    $record.installedUi = 'not-exercised'
    Check-Rejected '*installedUi*'
    $record.installedUi = 'passed'
    $record.sandboxedVideo = 'not-exercised'
    Check-Rejected '*sandboxedVideo*'
    $record.sandboxedVideo = 'passed'
    $record.upgradeSettings = 'not-exercised'
    Check-Rejected '*upgradeSettings*'
    $record.upgradeSettings = 'passed'
    $record.setupSha256 = '0' * 64
    Check-Rejected '*exact setup EXE*'
    $record.setupSha256 = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
    $record | ConvertTo-Json | Set-Content -LiteralPath $evidence
    & (Join-Path $PSScriptRoot 'Assert-ReleaseAcceptance.ps1') -SetupPath $setup -EvidencePath $evidence
    Write-Host 'Release acceptance guard rejects physical-host, missing-upgrade, missing-UI, missing-video, missing-upgrade-settings, and changed-artifact evidence.'
} finally {
    $target = [IO.Path]::GetFullPath($fixture)
    if ([IO.Path]::GetDirectoryName($target) -ne [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') -or [IO.Path]::GetFileName($target) -notmatch '^dvc-acceptance-guard-[a-f0-9]{32}$') { throw 'Unexpected guard fixture cleanup path.' }
    Remove-Item -LiteralPath $target -Recurse -Force
}
