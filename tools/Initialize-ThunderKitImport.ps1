[CmdletBinding()]
param(
    [string]$Ksp2Root = 'G:\SteamLibrary\steamapps\common\Kerbal Space Program 2',
    [string]$UnityEditorRoot = 'C:\Program Files\Unity\Hub\Editor\6000.4.1f1\Editor'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
$gameExecutable = Join-Path $Ksp2Root 'KSP2_x64.exe'
$managedRoot = Join-Path $Ksp2Root 'KSP2_x64_Data\Managed'
$editorManagedRoot = Join-Path $UnityEditorRoot 'Data\Managed'
$packageRoot = Join-Path $projectRoot 'Packages\KSP2_x64'
$packageCacheRoot = Join-Path $projectRoot 'Library\PackageCache'

foreach ($requiredPath in @($gameExecutable, $managedRoot, $editorManagedRoot, $packageCacheRoot)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required path does not exist: $requiredPath"
    }
}

# ThunderKit normally constructs this blacklist from assemblies already loaded by
# the editor. A brand-new KSP2 project cannot compile the SDK until the first
# import exists, so reproduce that one filtering step from files already supplied
# by this exact Unity editor and the project's pinned packages.
$providedAssemblies = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)

Get-ChildItem -LiteralPath $editorManagedRoot -Filter '*.dll' -File -Recurse |
    ForEach-Object { [void]$providedAssemblies.Add($_.Name) }

Get-ChildItem -LiteralPath $packageCacheRoot -Filter '*.dll' -File -Recurse |
    ForEach-Object { [void]$providedAssemblies.Add($_.Name) }

New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null

$pluginImporterTemplate = @'
fileFormatVersion: 2
guid: {0}
PluginImporter:
  externalObjects: {{}}
  serializedVersion: 2
  iconMap: {{}}
  executionOrder: {{}}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 0
  isExplicitlyReferenced: 1
  validateReferences: 0
  platformData:
  - first:
      '': Any
    second:
      enabled: 0
      settings:
        Exclude Editor: 0
        Exclude Linux: 0
        Exclude Linux64: 0
        Exclude LinuxUniversal: 0
        Exclude OSXUniversal: 0
        Exclude Win: 0
        Exclude Win64: 0
  - first:
      Any:
    second:
      enabled: 1
      settings: {{}}
  - first:
      Editor: Editor
    second:
      enabled: 1
      settings:
        CPU: AnyCPU
        DefaultValueInitialized: true
        OS: AnyOS
  - first:
      Facebook: Win
    second:
      enabled: 0
      settings:
        CPU: AnyCPU
  - first:
      Facebook: Win64
    second:
      enabled: 0
      settings:
        CPU: AnyCPU
  - first:
      Standalone: Linux
    second:
      enabled: 1
      settings:
        CPU: x86
  - first:
      Standalone: Linux64
    second:
      enabled: 1
      settings:
        CPU: x86_64
  - first:
      Standalone: LinuxUniversal
    second:
      enabled: 1
      settings: {{}}
  - first:
      Standalone: OSXUniversal
    second:
      enabled: 1
      settings:
        CPU: AnyCPU
  - first:
      Standalone: Win
    second:
      enabled: 1
      settings:
        CPU: AnyCPU
  - first:
      Standalone: Win64
    second:
      enabled: 1
      settings:
        CPU: AnyCPU
  - first:
      Windows Store Apps: WindowsStoreApps
    second:
      enabled: 0
      settings:
        CPU: AnyCPU
  userData:
  assetBundleName:
  assetBundleVariant:
'@

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$md5 = [System.Security.Cryptography.MD5]::Create()
$imported = 0
$skipped = 0

try {
    foreach ($assembly in Get-ChildItem -LiteralPath $managedRoot -Filter '*.dll' -File -Recurse) {
        if ($providedAssemblies.Contains($assembly.Name)) {
            $skipped++
            continue
        }

        $destination = Join-Path $packageRoot $assembly.Name
        Copy-Item -LiteralPath $assembly.FullName -Destination $destination -Force

        $nameBytes = [System.Text.Encoding]::UTF8.GetBytes($assembly.BaseName)
        $guid = ([System.Guid]::new($md5.ComputeHash($nameBytes))).ToString('N').ToLowerInvariant()
        $metadata = $pluginImporterTemplate -f $guid
        [System.IO.File]::WriteAllText("$destination.meta", $metadata, $utf8NoBom)
        $imported++
    }
}
finally {
    $md5.Dispose()
}

$packageManifest = @{
    name = 'ksp2_x64'
    displayName = 'KSP2 x64'
    version = '0.0.1'
    unity = '6000.4'
    description = 'Generated bootstrap package for the supported ThunderKit import.'
    author = @{ name = 'Intercept Games' }
} | ConvertTo-Json -Depth 3 -Compress

[System.IO.File]::WriteAllText(
    (Join-Path $packageRoot 'package.json'),
    $packageManifest,
    $utf8NoBom)

Write-Host "Seeded $imported game assemblies in $packageRoot; skipped $skipped editor/package-provided assemblies."
Write-Host 'Open Unity next and run the normal ThunderKit import configuration so it can replace and complete this generated package.'
