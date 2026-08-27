[CmdletBinding()]
param(
    [string]$Ksp2Root = "G:\SteamLibrary\steamapps\common\Kerbal Space Program 2",
    [switch]$Deploy
)

$ErrorActionPreference = "Stop"

$modId = "ReduxBetterAA"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $repositoryRoot "Assets\ReduxBetterAA\Code"
$manifestPath = Join-Path $repositoryRoot "Assets\ReduxBetterAA\Copied\swinfo.json"
$managedRoot = Join-Path $Ksp2Root "KSP2_x64_Data\Managed"
$buildRoot = Join-Path $repositoryRoot ".build"
$packageRoot = Join-Path $buildRoot $modId
$outputAssembly = Join-Path $packageRoot "$modId.dll"
$outputManifest = Join-Path $packageRoot "swinfo.json"
$outputArchive = Join-Path $buildRoot "$modId.zip"

function Assert-File {
    param(
        [Parameter(Mandatory = $true)]
        [string]$LiteralPath,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $LiteralPath -PathType Leaf)) {
        throw "$Description not found: $LiteralPath"
    }
}

function Get-ManagedReference {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $reference = Join-Path $managedRoot $Name
    Assert-File -LiteralPath $reference -Description "Redux managed assembly '$Name'"
    return $reference
}

$compilerPath = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path -LiteralPath $compilerPath -PathType Leaf)) {
    $compilerPath = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
}

Assert-File -LiteralPath $compilerPath -Description "C# compiler"
Assert-File -LiteralPath $manifestPath -Description "Redux swinfo manifest"
Assert-File -LiteralPath (Join-Path $sourceRoot "ReduxBetterAAMod.cs") -Description "Redux mod entry point"
Assert-File -LiteralPath (Join-Path $sourceRoot "AssemblyInfo.cs") -Description "Assembly metadata"

$references = @(
    Get-ManagedReference -Name "mscorlib.dll"
    Get-ManagedReference -Name "System.dll"
    Get-ManagedReference -Name "System.Core.dll"
    Get-ManagedReference -Name "UnityEngine.dll"
    Get-ManagedReference -Name "UnityEngine.CoreModule.dll"
    Get-ManagedReference -Name "ReduxLib.dll"
    Get-ManagedReference -Name "SpaceWarp2.dll"
)

$sourceFiles = @(
    Join-Path $sourceRoot "AssemblyInfo.cs"
    Join-Path $sourceRoot "ReduxBetterAAMod.cs"
)

New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null

$compilerArguments = @(
    "/nologo"
    "/noconfig"
    "/nostdlib+"
    "/target:library"
    "/optimize+"
    "/debug-"
    "/out:$outputAssembly"
)

foreach ($reference in $references) {
    $compilerArguments += "/reference:$reference"
}

$compilerArguments += $sourceFiles

& $compilerPath @compilerArguments
if ($LASTEXITCODE -ne 0) {
    throw "Bootstrap compilation failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath $manifestPath -Destination $outputManifest -Force
Compress-Archive -LiteralPath $packageRoot -DestinationPath $outputArchive -CompressionLevel Optimal -Force

Write-Host "Built loadable mod folder: $packageRoot"
Write-Host "Built distributable archive: $outputArchive"

if ($Deploy) {
    $modsRoot = Join-Path $Ksp2Root "mods"
    if (-not (Test-Path -LiteralPath $modsRoot -PathType Container)) {
        throw "Redux mods directory not found: $modsRoot"
    }

    $resolvedModsRoot = (Resolve-Path -LiteralPath $modsRoot).Path
    $deployRoot = Join-Path $resolvedModsRoot $modId
    New-Item -ItemType Directory -Path $deployRoot -Force | Out-Null
    Copy-Item -LiteralPath $outputAssembly -Destination (Join-Path $deployRoot "$modId.dll") -Force
    Copy-Item -LiteralPath $outputManifest -Destination (Join-Path $deployRoot "swinfo.json") -Force
    Write-Host "Deployed loader smoke-test package: $deployRoot"
}
