[CmdletBinding()]
param(
    [ValidateSet('Menu', 'Flight', 'All', 'Maintenance')] [string] $Scene = 'All',
    [string] $GameRoot = 'G:\SteamLibrary\steamapps\common\Kerbal Space Program 2',
    [string] $HarnessRoot = (Join-Path $PSScriptRoot '..\..\ReduxTestHarness'),
    [string] $UnityRoot = 'C:\Program Files\Unity\Hub\Editor\6000.4.1f1',
    [string] $Ffmpeg = 'ffmpeg',
    [switch] $KeepOpen
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$GameRoot = [IO.Path]::GetFullPath($GameRoot)
$encoder = if ($Scene -in @('Flight', 'All')) { Get-Command $Ffmpeg -CommandType Application -ErrorAction Stop } else { $null }
if (Get-Process KSP2_x64 -ErrorAction SilentlyContinue) { throw 'Close KSP2 before installing the visual-test adapter.' }
$managed = Join-Path $GameRoot 'KSP2_x64_Data\Managed'
$mod = Join-Path $GameRoot 'mods\ReduxBetterAA\ReduxBetterAA.dll'
if (-not (Test-Path -LiteralPath $mod)) { throw 'Install the newly built Better AA beta first.' }
if ((Get-Item -LiteralPath $mod).VersionInfo.FileVersion -ne '0.6.0.0') { throw 'Install Better AA 0.6.0 before running this suite.' }
# Redux 2.9 changed camera method signatures; compile the harness against this player.
& (Join-Path $HarnessRoot 'scripts\install-mod.ps1') -GameRoot $GameRoot -UnityRoot $UnityRoot
if ($LASTEXITCODE -ne 0) { throw 'Could not build/install the matching test harness.' }
$cli = Join-Path $HarnessRoot 'redux-test.ps1'
if (-not (Get-Command $cli).Parameters.ContainsKey('ResponseTimeoutSeconds')) {
    throw 'Update ReduxTestHarness: the visual suite requires configurable response timeouts for GPU captures.'
}
$output = Join-Path $repo '.build\visual-adapter'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$dll = Join-Path $output 'ReduxBetterAA.VisualTests.dll'
$refs = @('0Harmony.dll', 'Assembly-CSharp.dll', 'MoonSharp.Interpreter.dll', 'ReduxLib.dll', 'SpaceWarp2.dll',
    'netstandard.dll', 'UnityEngine.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.IMGUIModule.dll',
    'UnityEngine.UIElementsModule.dll', 'UnityEngine.ScreenCaptureModule.dll',
    'Unity.Postprocessing.Runtime.dll') | ForEach-Object { Join-Path $managed $_ }
$refs += $mod
$refs += Join-Path $GameRoot 'mods\ReduxTestHarness\ReduxTestHarness.dll'
foreach ($file in $refs) { if (-not (Test-Path -LiteralPath $file)) { throw "Missing reference: $file" } }
$argsList = @('/nologo', '/target:library', '/langversion:7.3', '/deterministic+', '/debug:portable', "/out:$dll")
$argsList += $refs | ForEach-Object { "/reference:$_" }
$argsList += Join-Path $repo 'tests\Visual\VisualTestMod.cs'
$argsList += Join-Path $repo 'tests\Visual\SampledVideo.cs'
& (Join-Path $UnityRoot 'Editor\Data\MonoBleedingEdge\bin\mono.exe') `
    (Join-Path $UnityRoot 'Editor\Data\MonoBleedingEdge\lib\mono\4.5\csc.exe') @argsList
if ($LASTEXITCODE -ne 0) { throw 'Visual adapter compilation failed.' }
$install = Join-Path $GameRoot 'mods\ReduxBetterAAVisualTests'
New-Item -ItemType Directory -Force -Path $install | Out-Null
Copy-Item -LiteralPath $dll -Destination $install
Copy-Item -LiteralPath (Join-Path $repo 'tests\Visual\swinfo.json') -Destination $install
$run = Join-Path $repo ('Artifacts\visual-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force -Path $run | Out-Null
$scripts = switch ($Scene) {
    'Menu' { 'menu-settings' }
    'Flight' { 'flight-motion' }
    'All' { 'menu-settings'; 'flight-motion' }
    'Maintenance' { 'maintenance' }
}
$priorEncoder = $env:RBAA_VISUAL_FFMPEG
$priorVideo = $env:RBAA_VISUAL_VIDEO
try {
    if ($encoder) {
        $env:RBAA_VISUAL_FFMPEG = $encoder.Source
        $env:RBAA_VISUAL_VIDEO = Join-Path $run 'videos\dlaa-pan.mp4'
    }
    foreach ($script in $scripts) {
        & $cli run (Join-Path $repo "tests\Visual\$script.lua") `
            -Launch -GameRoot $GameRoot -Timeout 600 -ResponseTimeoutSeconds 120 -Results (Join-Path $run $script) -KeepOpen:$KeepOpen
        if ($LASTEXITCODE -ne 0) { throw "Visual suite failed: $script (see $run)" }
        if (-not $KeepOpen) {
            foreach ($process in @(Get-Process KSP2_x64 -ErrorAction SilentlyContinue)) {
                if (-not $process.WaitForExit(30000)) { throw 'KSP2 did not finish shutting down between suites.' }
            }
        }
    }
} finally {
    $env:RBAA_VISUAL_FFMPEG = $priorEncoder
    $env:RBAA_VISUAL_VIDEO = $priorVideo
}
if ($encoder -and -not (Test-Path -LiteralPath (Join-Path $run 'videos\dlaa-pan.mp4'))) {
    throw "The DLAA video was not completed. See $run"
}
$videoBytes = 0
$videoEncoding = 'none'
if ($encoder) {
    $videoPath = Join-Path $run 'videos\dlaa-pan.mp4'
    $videoEncoding = 'H.264 CRF 16, medium preset, native resolution'
    $videoBytes = (Get-Item -LiteralPath $videoPath).Length
    Write-Host "Video: $([math]::Round($videoBytes / 1000000, 2)) MB; $videoEncoding"
}
& (Join-Path $PSScriptRoot 'Build-VisualGallery.ps1') -Directory $run -Ffmpeg $Ffmpeg
$images = @(Get-ChildItem -LiteralPath $run -Recurse -Filter '*.png' -File)
$expectedImages = switch ($Scene) { 'Menu' { 3 }; 'Flight' { 7 }; 'All' { 10 }; 'Maintenance' { 11 } }
if ($images.Count -ne $expectedImages) { throw "Expected $expectedImages screenshots; found $($images.Count). See $run" }
@{
    suite = $Scene; scene = $Scene; screenshots = $images.Count
    videos = @($(if ($encoder) { 'videos/dlaa-pan.mp4' }))
    videoBytes = $videoBytes; videoEncoding = $videoEncoding
    modSha256 = (Get-FileHash -LiteralPath $mod -Algorithm SHA256).Hash
    sourceCommit = (& git -C $repo rev-parse HEAD)
    sourceHasChanges = [bool](& git -C $repo status --porcelain)
    note = if ($Scene -eq 'Maintenance') { 'All public modes, resource recovery, repeated switches, map override; report ZIP paths are recorded in the harness report. No performance claim.' } else { 'DLAA M only. Native settings controls, settled flight/map stills and a six-second 30 FPS sampled camera-pan video when Flight is included. No performance claim.' }
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'capture.json') -Encoding utf8
$zip = "$run.zip"
Compress-Archive -LiteralPath $run -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Visual artifacts: $run"
Write-Host "Download bundle: $zip ($([math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1)) MiB)"
Write-Host 'The test-only adapter remains installed. Remove mods\ReduxBetterAAVisualTests after testing.'
