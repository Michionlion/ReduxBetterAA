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
& python (Join-Path $PSScriptRoot 'package-release.py') build --sdk $ModZip --output $OutputDirectory --commit $SourceCommit
if ($LASTEXITCODE -ne 0) { throw 'Release package validation failed.' }
