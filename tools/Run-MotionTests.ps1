[CmdletBinding()]
param(
    [string] $GameRoot='G:\SteamLibrary\steamapps\common\Kerbal Space Program 2',
    [string] $UnityRoot='C:\Program Files\Unity\Hub\Editor\6000.4.1f1',
    [string] $Label='baseline',
    [string] $Case,
    [string] $Script,
    [string] $ArtifactRoot,
    [string] $AnalysisScript='analyze-motion-series.py',
    [string] $Python='python'
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$harness=Join-Path $repo '..\ReduxTestHarness'
if(!$ArtifactRoot) {$ArtifactRoot=Join-Path $repo ('Artifacts\motion-investigation-'+(Get-Date -Format 'yyyyMMdd'))}
$ArtifactRoot=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ArtifactRoot)
if(!$Script) {$Script=Join-Path $repo 'tests\Motion\motion.lua'}
$Script=(Resolve-Path -LiteralPath $Script).Path
if(!$Case) {$Case=[IO.Path]::GetFileNameWithoutExtension($Script)}
if(Get-Process KSP2_x64 -ErrorAction SilentlyContinue) { throw 'Close KSP2 before installing the test adapter.' }
if($Label -notmatch '^[a-zA-Z0-9_-]+$') { throw 'Invalid label.' }
$run=Join-Path $ArtifactRoot ($Label+'-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $run | Out-Null
& (Join-Path $harness 'scripts\install-mod.ps1') -GameRoot $GameRoot -UnityRoot $UnityRoot
if($LASTEXITCODE -ne 0) { throw 'Harness install failed.' }
$managed=Join-Path $GameRoot 'KSP2_x64_Data\Managed'
$mod=Join-Path $GameRoot 'mods\ReduxBetterAA\ReduxBetterAA.dll'
$modBefore=(Get-FileHash -LiteralPath $mod).Hash
$sourceHash=(Get-FileHash -LiteralPath (Join-Path $repo 'tests\Motion\MotionTestMod.cs')).Hash
$scriptHash=(Get-FileHash -LiteralPath $Script).Hash
$fixtures=@{}
foreach($match in [regex]::Matches((Get-Content -LiteralPath $Script -Raw),'load_save\("([^"]+)"\)')) {
    $fixture=Join-Path $harness ('fixtures\'+$match.Groups[1].Value+'.json')
    $fixtures[$match.Groups[1].Value]=(Get-FileHash -LiteralPath $fixture).Hash
}
$refs=@('0Harmony.dll','Assembly-CSharp.dll','MoonSharp.Interpreter.dll','ReduxLib.dll','SpaceWarp2.dll',
    'Unity.Collections.dll','netstandard.dll','UnityEngine.dll','UnityEngine.AssetBundleModule.dll','UnityEngine.CoreModule.dll','UnityEngine.PhysicsModule.dll','UnityEngine.JSONSerializeModule.dll','Unity.Postprocessing.Runtime.dll') | ForEach-Object {Join-Path $managed $_}
$refs+=$mod
$refs+=Join-Path $GameRoot 'mods\ReduxTestHarness\ReduxTestHarness.dll'
$dll=Join-Path $run 'ReduxBetterAA.VisualTests.dll'
$argsCsc=@('/nologo','/target:library','/langversion:7.3','/deterministic+',"/out:$dll")
$argsCsc+=$refs | ForEach-Object {"/reference:$_"}
$adapterSources=@{}
foreach($source in Get-ChildItem -LiteralPath (Join-Path $repo 'tests\Motion') -Filter '*.cs' -File | Sort-Object Name) {
    $argsCsc+=$source.FullName
    $adapterSources[$source.Name]=(Get-FileHash -LiteralPath $source.FullName).Hash
}
& (Join-Path $UnityRoot 'Editor\Data\MonoBleedingEdge\bin\mono.exe') (Join-Path $UnityRoot 'Editor\Data\MonoBleedingEdge\lib\mono\4.5\csc.exe') @argsCsc
if($LASTEXITCODE -ne 0) {throw 'Motion adapter compilation failed.'}
$install=Join-Path $GameRoot 'mods\ReduxBetterAAVisualTests'
$backup=Join-Path $run 'prior-adapter'
if(Test-Path -LiteralPath $install) {Copy-Item -LiteralPath $install -Destination $backup -Recurse}
$config=Join-Path (Split-Path $mod) 'ReduxBetterAA-config.json'
$hadConfig=Test-Path -LiteralPath $config
if($hadConfig) {Copy-Item -LiteralPath $config -Destination (Join-Path $run 'settings-before.json')}
$priorOutput=$env:RBAA_MOTION_OUTPUT; $priorCase=$env:RBAA_MOTION_CASE
try {
    New-Item -ItemType Directory -Force -Path $install | Out-Null
    Copy-Item -LiteralPath $dll -Destination $install
    Copy-Item -LiteralPath (Join-Path $repo 'tests\Visual\swinfo.json') -Destination $install
    $env:RBAA_MOTION_OUTPUT=Join-Path $run 'samples'; $env:RBAA_MOTION_CASE=$Case
    & (Join-Path $harness 'redux-test.ps1') run $Script -Launch -GameRoot $GameRoot -Timeout 900 -ResponseTimeoutSeconds 120 -Results (Join-Path $run 'harness')
    $result=$LASTEXITCODE
} finally {
    $env:RBAA_MOTION_OUTPUT=$priorOutput; $env:RBAA_MOTION_CASE=$priorCase
    foreach($process in @(Get-Process KSP2_x64 -ErrorAction SilentlyContinue)) {
        if(!$process.WaitForExit(30000)) {throw "Game still running; restore pending in $run"}
    }
    if($hadConfig) {Copy-Item -LiteralPath (Join-Path $run 'settings-before.json') -Destination $config -Force}
    elseif(Test-Path -LiteralPath $config) {Remove-Item -LiteralPath $config}
    Remove-Item -LiteralPath (Join-Path $install 'ReduxBetterAA.VisualTests.dll'),(Join-Path $install 'swinfo.json') -ErrorAction SilentlyContinue
    if(Test-Path -LiteralPath $backup) {Copy-Item -Path (Join-Path $backup '*') -Destination $install -Recurse -Force}
}
$configRestored=if($hadConfig) {(Get-FileHash -LiteralPath $config).Hash -eq (Get-FileHash -LiteralPath (Join-Path $run 'settings-before.json')).Hash} else {!(Test-Path -LiteralPath $config)}
$modUnchanged=$modBefore -eq (Get-FileHash -LiteralPath $mod).Hash
@{label=$Label;case=$Case;exitCode=$result;modSha256=$modBefore;modUnchanged=$modUnchanged;settingsRestored=$configRestored;
    adapterSha256=(Get-FileHash $dll).Hash;adapterSourceSha256=$sourceHash;adapterSources=$adapterSources;scriptSha256=$scriptHash;fixtureSha256=$fixtures;
    sourceCommit=(& git -C $repo rev-parse HEAD);capture='Every rendered frame, bounded async readbacks, real render and physics timing; no fixed-time override'} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'run.json') -Encoding utf8
Write-Host "Render evidence: $run"
if(!$configRestored -or !$modUnchanged) {throw 'Production state did not match its pre-test hashes.'}
if($result -ne 0) {throw 'Render test failed.'}
& $Python (Join-Path $repo ('tools\'+$AnalysisScript)) $run > (Join-Path $run 'analysis.log')
if($LASTEXITCODE -ne 0) {throw 'Render capture validation/analysis failed; inspect evidence.'}
Write-Host "Validated render analysis in: $run"
Write-Output $run
