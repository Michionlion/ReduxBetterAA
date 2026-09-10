[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string] $Plan,
    [string] $Unity = 'C:\Program Files\Unity\Hub\Editor\6000.4.1f1\Editor\Unity.exe'
)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (Get-Process Unity,KSP2_x64 -ErrorAction SilentlyContinue) { throw 'Close Unity and KSP2 before shader replay.' }
$planPath=(Resolve-Path -LiteralPath $Plan).Path
$config=Get-Content -LiteralPath $planPath -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Force -Path $config.output | Out-Null
$log=Join-Path $config.output 'editor.log'
$complete=Join-Path $config.output 'complete.json'
if(Test-Path -LiteralPath $complete) { Remove-Item -LiteralPath $complete }
$prior=$env:RBAA_REPLAY_PLAN
try {
    $env:RBAA_REPLAY_PLAN=$planPath
    $p=Start-Process -FilePath $Unity -ArgumentList "-batchmode -quit --burst-disable-compilation -projectPath `"$repo`" -executeMethod Utilities.Editor.CustomTaaReplay.Run -logFile `"$log`"" -WindowStyle Hidden -PassThru -Wait
    if($p.ExitCode -ne 0 -or !(Test-Path -LiteralPath $complete)) {
        throw "Replay failed or did not finish: $log"
    }
    $result=Get-Content -LiteralPath $complete -Raw | ConvertFrom-Json
    if($result.framesPerArm -ne 128 -or $result.arms -ne $config.arms.Count) { throw "Incomplete replay: $log" }
} finally { $env:RBAA_REPLAY_PLAN=$prior }
Write-Host "Replay completed: $($config.output)"
