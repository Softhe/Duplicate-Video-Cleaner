#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$FfprobePath,
    [string]$OutputPath = (Join-Path $PSScriptRoot '../validation/performance.json')
)
$ErrorActionPreference = 'Stop'
$probe = (Resolve-Path -LiteralPath $FfprobePath).Path
$fixture = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../DuplicateVideoCleaner.UiTests/Fixtures/one-second.mp4')).Path
dotnet run --project (Join-Path $PSScriptRoot '../DuplicateVideoCleaner.Tests/DuplicateVideoCleaner.Tests.csproj') -c Release -- --measure-performance $probe $fixture ([IO.Path]::GetFullPath($OutputPath))
if ($LASTEXITCODE -ne 0) { throw 'Synthetic performance measurement failed.' }
