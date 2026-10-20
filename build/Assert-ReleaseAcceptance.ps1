param([Parameter(Mandatory)][string]$SetupPath, [Parameter(Mandatory)][string]$EvidencePath)
$ErrorActionPreference = 'Stop'
$record = Get-Content -LiteralPath $EvidencePath -Raw | ConvertFrom-Json
if ($record.status -ne 'passed' -or $record.virtualMachine -ne $true) { throw 'Release requires successful acceptance in a disposable VM.' }
foreach ($field in @('payloadHashes','ffprobeFixture','isolatedAppLaunch','uninstallCleanup','settingsPreserved','upgrade','installedUi','sandboxedVideo','upgradeSettings')) {
    if ($record.$field -ne 'passed') { throw "Required acceptance failed or missing: $field" }
}
if ($record.setupSha256 -ne (Get-FileHash -LiteralPath $SetupPath -Algorithm SHA256).Hash) { throw 'Acceptance does not match the exact setup EXE.' }
Write-Host 'Exact setup artifact passed the required VM, upgrade, UI, and uninstall acceptance checks.'
