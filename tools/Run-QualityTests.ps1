[CmdletBinding()]
param(
    [string] $GameRoot = 'G:\SteamLibrary\steamapps\common\Kerbal Space Program 2',
    [string] $HarnessRoot = (Join-Path $PSScriptRoot '..\..\ReduxTestHarness'),
    [string] $UnityRoot = 'C:\Program Files\Unity\Hub\Editor\6000.4.1f1',
    [string] $Label = 'comparison',
    [switch] $LiveComparison,
    [switch] $VideoComparison,
    [switch] $Shimmer,
    [switch] $ShimmerSweep,
    [switch] $ShimmerReplay,
    [switch] $ShimmerJitterSweep,
    [ValidateSet('pan','reverse-diagonal')][string] $ShimmerPath = 'pan'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($ShimmerSweep -and !$Shimmer) { throw '-ShimmerSweep requires -Shimmer.' }
if ($ShimmerReplay -and !$Shimmer) { throw '-ShimmerReplay requires -Shimmer.' }
if ($ShimmerJitterSweep -and !$Shimmer) { throw '-ShimmerJitterSweep requires -Shimmer.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (Get-Process KSP2_x64 -ErrorAction SilentlyContinue) { throw 'Close KSP2 before installing the test adapter.' }
if ($Label -notmatch '^[a-zA-Z0-9_-]+$') { throw 'Use a simple alphanumeric run label.' }
& (Join-Path $HarnessRoot 'scripts\install-mod.ps1') -GameRoot $GameRoot -UnityRoot $UnityRoot
if ($LASTEXITCODE -ne 0) { throw 'Harness installation failed.' }
$run = Join-Path $repo ('Artifacts\quality-' + $Label + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force -Path $run | Out-Null
$managed = Join-Path $GameRoot 'KSP2_x64_Data\Managed'
$mod = Join-Path $GameRoot 'mods\ReduxBetterAA\ReduxBetterAA.dll'
$refs = @('0Harmony.dll','Assembly-CSharp.dll','MoonSharp.Interpreter.dll','ReduxLib.dll','SpaceWarp2.dll',
    'Unity.Collections.dll','UnityEngine.UI.dll','UnityEngine.ImageConversionModule.dll','netstandard.dll','UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.JSONSerializeModule.dll','Unity.Postprocessing.Runtime.dll') |
    ForEach-Object { Join-Path $managed $_ }
$refs += $mod
$refs += Join-Path $GameRoot 'mods\ReduxTestHarness\ReduxTestHarness.dll'
$dll = Join-Path $run 'ReduxBetterAA.VisualTests.dll'
$compilerArgs = @('/nologo','/target:library','/langversion:7.3','/deterministic+',"/out:$dll")
$compilerArgs += $refs | ForEach-Object { "/reference:$_" }
$compilerArgs += Join-Path $repo 'Tests\Quality\QualityTestMod.cs'
$compilerArgs += Join-Path $repo 'tests\Quality\ComparisonVideo.cs'
$compilerArgs += Join-Path $repo 'tests\Quality\ShimmerCapture.cs'
& (Join-Path $UnityRoot 'Editor\Data\MonoBleedingEdge\bin\mono.exe') `
    (Join-Path $UnityRoot 'Editor\Data\MonoBleedingEdge\lib\mono\4.5\csc.exe') @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Quality adapter compilation failed.' }
$install = Join-Path $GameRoot 'mods\ReduxBetterAAVisualTests'
$backup = Join-Path $run 'prior-adapter'
if (Test-Path -LiteralPath $install) { Copy-Item -LiteralPath $install -Destination $backup -Recurse }
New-Item -ItemType Directory -Force -Path $install | Out-Null
Copy-Item -LiteralPath $dll -Destination $install
Copy-Item -LiteralPath (Join-Path $repo 'Tests\Visual\swinfo.json') -Destination $install
$scriptPath = Join-Path $repo $(if ($Shimmer) { 'tests\Quality\shimmer.lua' } elseif ($VideoComparison) { 'tests\Quality\comparison-videos.lua' } elseif ($LiveComparison) { 'tests\Quality\live-comparison.lua' } else { 'tests\Quality\native-aa.lua' })
$prior = $env:RBAA_QUALITY_OUTPUT
$priorEncoder = $env:RBAA_VISUAL_FFMPEG
$priorSweep = $env:RBAA_SHIMMER_SWEEP
$priorReplay = $env:RBAA_SHIMMER_REPLAY
$priorJitterSweep = $env:RBAA_SHIMMER_JITTER_SWEEP
$priorShimmerPath = $env:RBAA_SHIMMER_PATH
try {
    $env:RBAA_SHIMMER_PATH = $ShimmerPath
    $env:RBAA_SHIMMER_JITTER_SWEEP = $(if ($ShimmerJitterSweep) { '1' } else { '0' })
    $env:RBAA_SHIMMER_REPLAY = $(if ($ShimmerReplay) { '1' } else { '0' })
    $env:RBAA_SHIMMER_SWEEP = $(if ($ShimmerSweep) { '1' } else { '0' })
    if ($VideoComparison) { $env:RBAA_VISUAL_FFMPEG = (Get-Command ffmpeg -CommandType Application -ErrorAction Stop).Source }
    $env:RBAA_QUALITY_OUTPUT = Join-Path $run 'pixels'
    & (Join-Path $HarnessRoot 'redux-test.ps1') run $scriptPath `
        -Launch -GameRoot $GameRoot -Timeout 1200 -ResponseTimeoutSeconds 120 -Results (Join-Path $run 'harness')
    $result = $LASTEXITCODE
} finally {
    $env:RBAA_SHIMMER_PATH = $priorShimmerPath
    $env:RBAA_SHIMMER_JITTER_SWEEP = $priorJitterSweep
    $env:RBAA_SHIMMER_REPLAY = $priorReplay
    $env:RBAA_SHIMMER_SWEEP = $priorSweep
    $env:RBAA_VISUAL_FFMPEG = $priorEncoder
    $env:RBAA_QUALITY_OUTPUT = $prior
    foreach ($p in @(Get-Process KSP2_x64 -ErrorAction SilentlyContinue)) {
        if (-not $p.WaitForExit(30000)) { throw "Game still running; adapter retained at $install" }
    }
    # Only remove the exact two files installed by this runner; no recursive deletion.
    Remove-Item -LiteralPath (Join-Path $install 'ReduxBetterAA.VisualTests.dll'),(Join-Path $install 'swinfo.json') -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $backup) { Copy-Item -Path (Join-Path $backup '*') -Destination $install -Recurse -Force }
}
$catalog=Join-Path (Split-Path $mod) 'addressables\catalog.json'
@{ label=$Label; modSha256=(Get-FileHash -LiteralPath $mod).Hash;
    shaderCatalogSha256=$(if(Test-Path -LiteralPath $catalog) { (Get-FileHash -LiteralPath $catalog).Hash } else { $null });
    sourceCommit=(& git -C $repo rev-parse HEAD);
    sourceHasChanges=[bool](& git -C $repo status --porcelain); gameRoot=$GameRoot; exitCode=$result;
    shimmerSweep=[bool]$ShimmerSweep;
    shimmerReplay=[bool]$ShimmerReplay;
    shimmerJitterSweep=[bool]$ShimmerJitterSweep;
    shimmerPath=$ShimmerPath;
    scriptSha256=(Get-FileHash -LiteralPath $scriptPath).Hash;
    capture=$(if ($Shimmer) { '128 consecutive fixed-step normal-renderer frames per arm; native linear RGBA16F structure and terrain crops; sparse same-frame input and rejection/reactivity/history diagnostics' } elseif ($VideoComparison) { 'Continuous fixed-step 60 FPS comparison frames; lossless RGB; same camera path; no performance measurement' } elseif ($LiveComparison) { 'Live pre-UI A/B output previews, finite-pixel and temporal-state checks; no performance measurements' } else { 'same-frame pre-UI input/output; linear RGBA16F; no readbacks during timing' }) } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'run.json') -Encoding utf8
Write-Host "Quality artifacts: $run"
if ($result -ne 0) { throw "Quality suite failed; inspect $run" }
if ($LiveComparison -or $VideoComparison -or $Shimmer) { return }
& python (Join-Path $repo 'tools\analyze-aa-quality.py') $run
if ($LASTEXITCODE -ne 0) { throw 'Pixel analysis failed.' }
