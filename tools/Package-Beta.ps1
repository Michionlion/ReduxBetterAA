[CmdletBinding()]
param(
    [string] $ModZip = (Join-Path $PSScriptRoot '..\Deploy\ReduxBetterAA.zip'),
    [string] $RuntimeDirectory,
    [string] $RuntimeReceipt,
    [ValidateSet('2.8.5', '2.9')] [string] $ReduxTarget = '2.9',
    [switch] $LocalValidationOnly,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\Deploy')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$unityVersion = if ($ReduxTarget -eq '2.9') { '6000.5.8f1' } else { '6000.4.1f1' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$stage = Join-Path $repo ('.build\package-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $stage 'mods\ReduxBetterAA'
New-Item -ItemType Directory -Force -Path $payload | Out-Null
# Only unpack entries that stay inside the mod folder. SDK zip must not contain game DLLs.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ModZip).Path)
try {
    foreach ($entry in $archive.Entries) {
        $target = [IO.Path]::GetFullPath((Join-Path $payload $entry.FullName))
        if (-not $target.StartsWith($payload + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Unsafe SDK archive path: $($entry.FullName)"
        }
        if (-not $entry.Name) { continue }
        if ($entry.FullName -notmatch '^(ReduxBetterAA\.dll|swinfo\.json|addressables/(catalog\.(json|hash)|settings\.json|AddressablesLink/link\.xml|StandaloneWindows64/[^/]+\.bundle))$') {
            throw "Unexpected SDK payload entry: $($entry.FullName)"
        }
        if ($entry.Name -like '*.dll' -and $entry.Name -ne 'ReduxBetterAA.dll') { throw "Unexpected bundled dependency: $($entry.Name)" }
        if ($entry.FullName -match '(^|/)(diagnostics|config|tests)(/|$)') { throw "Unexpected user/developer files: $($entry.FullName)" }
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $false)
    }
} finally { $archive.Dispose() }
$modInfo = Get-Content -LiteralPath (Join-Path $payload 'swinfo.json') -Raw | ConvertFrom-Json
$version = $modInfo.version
$dll = Get-Item -LiteralPath (Join-Path $payload 'ReduxBetterAA.dll')
if ($dll.VersionInfo.FileVersion -ne "$version.0") { throw 'Assembly and manifest versions disagree.' }
if (@(Get-ChildItem -LiteralPath $payload -Recurse -Filter '*.bundle').Count -ne 1) { throw 'Expected exactly one mod asset bundle.' }
$variant = 'portable'
$runtimeHashes = @()
if ($RuntimeDirectory) {
    $variant = 'full'
    $receipt = $null
    if (-not $LocalValidationOnly) {
        if (-not $RuntimeReceipt) { throw 'Full release requires a reviewed runtime receipt; see docs/distribution.md.' }
        $receipt = Get-Content -LiteralPath $RuntimeReceipt -Raw | ConvertFrom-Json
        if ($receipt.unityVersion -ne $unityVersion -or $receipt.redistributionApproved -ne $true) {
            throw 'Runtime receipt does not approve this exact Unity runtime for redistribution.'
        }
        if (-not $receipt.provenance -or @($receipt.notices).Count -eq 0) { throw 'Runtime provenance and license notices are required.' }
    }
    foreach ($name in @('NVUnityPlugin.dll', 'nvngx_dlss.dll', 'AMDUnityPlugin.dll')) {
        $file = Join-Path $RuntimeDirectory $name
        $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($receipt) {
            $approved = @($receipt.files | Where-Object name -eq $name)
            if ($approved.Count -ne 1 -or $approved[0].sha256 -ne $hash) { throw "Unapproved runtime content: $name" }
        }
        Copy-Item -LiteralPath $file -Destination (Join-Path $stage $name)
        $runtimeHashes += [ordered]@{ name = $name; sha256 = $hash }
    }
    if ($receipt) {
        $notices = Join-Path $stage 'licenses\vendor'
        New-Item -ItemType Directory -Force -Path $notices | Out-Null
        foreach ($notice in $receipt.notices) {
            Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $RuntimeReceipt) $notice) -Destination $notices
        }
        Copy-Item -LiteralPath $RuntimeReceipt -Destination (Join-Path $stage 'runtime-receipt.json')
    }
}
if ($LocalValidationOnly) {
    $variant += '-local-only'
    'LOCAL VALIDATION ONLY. Redistribution clearance has not been established. Do not publish this archive.' |
        Set-Content -LiteralPath (Join-Path $stage 'LOCAL-VALIDATION-ONLY.txt')
}
elseif (-not (Test-Path -LiteralPath (Join-Path $repo 'LICENSE'))) {
    throw 'The mod license has not been chosen. A public package requires LICENSE; local validation remains available.'
}
foreach ($name in @('README.md', 'THIRD-PARTY-NOTICES.md', 'LICENSE')) {
    $source = Join-Path $repo $name
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $stage }
}
@"
Redux Better AA $version beta - Redux $ReduxTarget / Unity $unityVersion / Windows x64

1. Install the matching Redux version yourself, then close KSP2.
2. Extract this ZIP into the folder containing KSP2_x64.exe.
3. Open Settings > Mods > Redux Better AA and choose an AA mode.

To report a problem, keep it visible and press F10, or use Generate issue report ZIP
in mod settings. Wait for the saved notification, review the ZIP under
mods/ReduxBetterAA/diagnostics/reports, and send it with reproduction steps.
Capture can pause the game for several seconds. Nothing is uploaded by Better AA.

The full variant contains optional native vendor runtimes for this exact player.
Do not use its native DLLs with a different Unity version. Redux is not bundled.
Keep backups of existing native DLLs outside the game folder before replacing them.
For updates, replace the mod payload but retain your configuration and reports;
never keep two folders with the ReduxBetterAA mod ID in mods.

Source, complete documentation and issue tracker:
https://github.com/Michionlion/ReduxBetterAA
"@ | Set-Content -LiteralPath (Join-Path $stage 'INSTALL.txt') -Encoding utf8
if (Test-Path -LiteralPath (Join-Path $repo 'licenses')) { Copy-Item -LiteralPath (Join-Path $repo 'licenses') -Destination $stage -Recurse -Force }
$files = @(Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path = [IO.Path]::GetRelativePath($stage, $_.FullName).Replace('\','/'); bytes = $_.Length;
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
[ordered]@{ schemaVersion = 1; version = $version; variant = $variant; unityVersion = $unityVersion;
    reduxTarget = $ReduxTarget; includesRedux = $false; runtimes = $runtimeHashes; files = $files } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $stage 'package-manifest.json') -Encoding utf8
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$output = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) "ReduxBetterAA-$version-beta-redux$ReduxTarget-$variant.zip"
if (Test-Path -LiteralPath $output) { throw "Output exists; select another output directory: $output" }
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $output, [IO.Compression.CompressionLevel]::Optimal, $false)
Get-FileHash -LiteralPath $output -Algorithm SHA256
