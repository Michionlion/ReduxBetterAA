[CmdletBinding()]
param([string] $Unity = 'C:\Program Files\Unity\Hub\Editor\6000.4.1f1\Editor\Unity.exe')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$logs = Join-Path $repo 'Logs'
New-Item -ItemType Directory -Force -Path $logs | Out-Null
if (Get-Process Unity -ErrorAction SilentlyContinue) { throw 'Close Unity before running the batch pipeline.' }
function Invoke-UnityStep([string] $Name, [string] $Arguments) {
    Write-Host "Unity: $Name"
    $log = Join-Path $logs "beta-$Name.log"
    $process = Start-Process -FilePath $Unity -ArgumentList "-batchmode -projectPath `"$repo`" -logFile `"$log`" $Arguments" `
        -WindowStyle Hidden -PassThru -Wait
    if (Select-String -LiteralPath $log -Pattern 'error CS\d+|Shader error in|Halted execution|Aborting batchmode' -Quiet) {
        throw "Unity $Name failed; see $log"
    }
    if ($Name -ne 'package' -and $process.ExitCode -ne 0) { throw "Unity $Name exited $($process.ExitCode); see $log" }
}
Invoke-UnityStep 'prepare' '-quit -executeMethod Utilities.Editor.PrepareReduxBetterAAMod.Run'
$results = Join-Path $logs 'beta-editmode.xml'
if (Test-Path -LiteralPath $results) { Remove-Item -LiteralPath $results }
# Better AA has no Burst jobs. Imported player assemblies cannot be recompiled by
# the Editor's Entities/Burst tooling; keep that unrelated compiler out of this run.
Invoke-UnityStep 'editmode' "--burst-disable-compilation -runTests -testPlatform EditMode -testResults `"$results`""
[xml]$tests = Get-Content -LiteralPath $results -Raw
if ($tests.'test-run'.result -ne 'Passed' -or [int]$tests.'test-run'.failed -ne 0) { throw 'EditMode tests did not pass.' }
Write-Host "$($tests.'test-run'.passed) EditMode tests passed."
$packageStart = Get-Date
Invoke-UnityStep 'package' '-quit -executeMethod ThunderKit.Core.Pipelines.Pipeline.BatchModeExecutePipeline -pipeline="Assets/ReduxBetterAA/Pipelines/Deploy to Zip File.asset"'
$zip = Join-Path $repo 'Deploy\ReduxBetterAA.zip'
if (-not (Test-Path -LiteralPath $zip) -or (Get-Item -LiteralPath $zip).LastWriteTime -lt $packageStart) { throw 'No fresh SDK archive was produced.' }
$pipelineLogs = @(Get-ChildItem -LiteralPath (Join-Path $repo 'Assets\ThunderKitSettings\Logs') -Filter '*.asset' -File -Recurse |
    Where-Object LastWriteTime -ge $packageStart)
if (-not ($pipelineLogs | Select-String -Pattern 'Finished execution' -Quiet)) { throw 'ThunderKit did not record successful completion.' }
Get-FileHash -LiteralPath $zip -Algorithm SHA256
