#Requires -Version 7.0
[CmdletBinding()]
param(
    [string] $ModZip = (Join-Path $PSScriptRoot '..\Deploy\ReduxBetterAA.zip'),
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\Deploy'),
    [string] $SourceCommit
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $SourceCommit) {
    $SourceCommit = & git -C $repo rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source commit.' }
}
# Repeated local builds replace their public ZIP only after validation succeeds.
# Release.ps1 supplies a fresh output directory and retains the strict no-overwrite rule.
$packageOutput = $OutputDirectory
if (-not $PSBoundParameters.ContainsKey('OutputDirectory')) {
    $packageOutput = Join-Path $repo ('Library\BetterAA\package-' + [Guid]::NewGuid().ToString('N'))
}
& python (Join-Path $PSScriptRoot 'package-release.py') build --input $ModZip --output $packageOutput --commit $SourceCommit
if ($LASTEXITCODE -ne 0) { throw 'Release package validation failed.' }
if ($packageOutput -ne $OutputDirectory) {
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    Get-ChildItem -LiteralPath $packageOutput -Filter '*.zip' -File |
        Copy-Item -Destination $OutputDirectory -Force
}
