[CmdletBinding()]
param(
    [string] $Script=(Join-Path $PSScriptRoot '..\tests\Motion\terrain-test-regression.lua'),
    [string] $Label='terrain',
    [string] $ArtifactRoot=(Join-Path $PSScriptRoot ('..\Artifacts\terrain-investigation-'+(Get-Date -Format 'yyyyMMdd'))),
    [string] $GameRoot='G:\SteamLibrary\steamapps\common\Kerbal Space Program 2',
    [string] $UnityRoot='C:\Program Files\Unity\Hub\Editor\6000.4.1f1',
    [switch] $VerifyFix
)
$ErrorActionPreference='Stop'
$run=@(& (Join-Path $PSScriptRoot 'Run-MotionTests.ps1') -Script $Script -Label $Label -ArtifactRoot $ArtifactRoot -GameRoot $GameRoot -UnityRoot $UnityRoot -AnalysisScript 'analyze-terrain-flicker.py')[-1]
if($VerifyFix -or [IO.Path]::GetFileName($Script) -eq 'terrain-test-regression.lua') {
    & python (Join-Path $PSScriptRoot 'verify-terrain-fix.py') $run
    if($LASTEXITCODE -ne 0) {throw 'Terrain regression failed; inspect terrain-verification.json.'}
}
Write-Output $run
