function Assert-SettingsRewritten {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][DateTime]$BeforeWriteUtc,
        [Parameter(Mandatory)][string]$ApplicationLabel
    )
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or
        (Get-Item -LiteralPath $Path).LastWriteTimeUtc -le $BeforeWriteUtc) {
        throw "$ApplicationLabel did not rewrite its settings fixture. Seeded values alone do not prove preferences were saved."
    }
}
