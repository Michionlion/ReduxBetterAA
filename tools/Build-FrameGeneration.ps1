#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BuildDirectory,
    [Parameter(Mandatory)][string]$UnityPluginApi,
    [ValidateSet('nvidia', 'amd')][string[]]$Vendors = @('nvidia', 'amd'),
    [string]$FidelityFxSdk,
    [string]$StreamlineSdk,
    [string]$StreamlineRuntime,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$CMake = 'cmake',
    [string]$Python = 'python',
    [switch]$RunTests,
    [string]$PackageDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourcePrefix = $sourceRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
function ExternalPath([string]$Path, [bool]$MustExist = $true) {
    if (-not $Path) { throw 'An explicit external input/output path is required.' }
    $absolute = if ($MustExist) { (Resolve-Path -LiteralPath $Path).Path } else { [IO.Path]::GetFullPath($Path) }
    if ($absolute.Equals($sourceRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $absolute.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Native FG inputs/output must stay outside the source checkout: $absolute"
    }
    return $absolute
}
function NativeCommand([string[]]$Arguments) {
    & $CMake @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Native FG CMake failed ($LASTEXITCODE)." }
}
function ContractTests([string]$Directory) {
    $ctest = Join-Path (Split-Path -Parent (Get-Command $CMake).Source) 'ctest.exe'
    & $ctest --test-dir $Directory -C $Configuration --output-on-failure
    if ($LASTEXITCODE -ne 0) { throw "Native FG CPU contract tests failed ($LASTEXITCODE)." }
}
$selected = @($Vendors | ForEach-Object { $_.ToLowerInvariant() } | Sort-Object -Unique)
if (-not $selected.Count -or $selected.Count -ne $Vendors.Count) { throw 'Select at least one vendor, without duplicates.' }
$nativeBuild = ExternalPath $BuildDirectory $false
$unityApi = ExternalPath $UnityPluginApi
$amdSdk = if ($selected -contains 'amd') { ExternalPath $FidelityFxSdk } else { $null }
$nvSdk = if ($selected -contains 'nvidia') { ExternalPath $StreamlineSdk } else { $null }
$packageRoot = if ($PackageDirectory) { ExternalPath $PackageDirectory $false } else { $null }
$nvRuntime = if ($PackageDirectory -and $selected -contains 'nvidia') { ExternalPath $StreamlineRuntime } else { $null }
if ($PackageDirectory -and $Configuration -ne 'Release') { throw 'Only Release FG builds may be packaged.' }

$presentationBuild = Join-Path $nativeBuild 'presentation'
NativeCommand @('-S', (Join-Path $sourceRoot 'Native/Presentation'), '-B', $presentationBuild,
    '-G', 'Visual Studio 17 2022', '-A', 'x64', "-DUNITY_PLUGIN_API=$unityApi",
    '-DRBA_BUILD_FRAME_GENERATION_COORDINATOR=ON', '-DRBA_PRESENT_GPU_TESTS=OFF')
$targets = @('GfxPluginReduxBetterAAFrameGeneration')
if ($RunTests) { $targets += @('FrameGenerationCoordinatorTests', 'FrameGenerationCoordinatorLifetimeTests', 'ChildWindowHostTests', 'PresentationProbeTests') }
NativeCommand (@('--build', $presentationBuild, '--config', $Configuration, '--parallel', '--target') + $targets)
if ($RunTests) { ContractTests $presentationBuild }
$coordinator = Join-Path $presentationBuild "$Configuration/GfxPluginReduxBetterAAFrameGeneration.dll"

$nvProvider = $null
if ($selected -contains 'nvidia') {
    $nvBuild = Join-Path $nativeBuild 'streamline'
    NativeCommand @('-S', (Join-Path $sourceRoot 'Native/Streamline'), '-B', $nvBuild,
        '-G', 'Visual Studio 17 2022', '-A', 'x64', "-DSTREAMLINE_SDK_ROOT=$nvSdk")
    $targets = @('ReduxBetterAA.StreamlineProvider')
    if ($RunTests) { $targets += @('StreamlineProbeTests', 'StreamlineProbeInternalTests', 'StreamlineProviderTests') }
    NativeCommand (@('--build', $nvBuild, '--config', $Configuration, '--parallel', '--target') + $targets)
    if ($RunTests) { ContractTests $nvBuild }
    $nvProvider = Join-Path $nvBuild "$Configuration/ReduxBetterAA.StreamlineProvider.dll"
}
$amdProvider = $null
if ($selected -contains 'amd') {
    $amdBuild = Join-Path $nativeBuild 'amd'
    NativeCommand @('-S', (Join-Path $sourceRoot 'Native/FrameGeneration'), '-B', $amdBuild,
        '-G', 'Visual Studio 17 2022', '-A', 'x64', "-DFFX_SDK_ROOT=$amdSdk")
    NativeCommand @('--build', $amdBuild, '--config', $Configuration, '--parallel', '--target', 'ReduxBetterAA.AmdFrameGeneration')
    $amdProvider = Join-Path $amdBuild "$Configuration/ReduxBetterAA.AmdFrameGeneration.dll"
    # The AMD CTest entries execute GPU work. They are deliberately not run by
    # this CPU build/package path, even on a machine with supported hardware.
}
if ($RunTests) {
    & $Python -B -X utf8 -m unittest discover -s (Join-Path $PSScriptRoot 'tests')
    if ($LASTEXITCODE -ne 0) { throw 'Native FG static package tests failed.' }
}
if ($packageRoot) {
    $metadata = Get-Content -LiteralPath (Join-Path $sourceRoot 'Assets/ReduxBetterAA/Copied/swinfo.json') -Raw | ConvertFrom-Json
    $label = $selected -join '-'
    $archive = Join-Path $packageRoot "BetterAA-FrameGeneration-$($metadata.version)-Redux-0.2.9.0-$label-win-x64.zip"
    $arguments = @('-B', '-X', 'utf8', (Join-Path $PSScriptRoot 'package-frame-generation.py'), 'build',
        '--output', $archive, '--coordinator', $coordinator, '--unity-plugin-api', $unityApi, '--vendors') + $selected
    if ($nvProvider) { $arguments += @('--nvidia-provider', $nvProvider, '--nvidia-sdk', $nvSdk, '--nvidia-runtime', $nvRuntime) }
    if ($amdProvider) { $arguments += @('--amd-provider', $amdProvider, '--amd-sdk', $amdSdk, '--amd-runtime', $amdSdk) }
    & $Python @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Optional FG packaging/pin verification failed.' }
    Write-Output "Verified optional FG companion: $archive"
}
Write-Output "Built optional FG coordinator: $coordinator"
Write-Output 'No GPU tests, Unity/player launches, vendor downloads, installations, or main mod package changes were performed.'
