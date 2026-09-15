[CmdletBinding()]
param(
    [string]$FidelityFxSdk,
    [Parameter(Mandatory)][string]$BuildDirectory,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$CMake = 'cmake',
    [switch]$RunTests,
    [string]$PackageDirectory,
    [switch]$FrameGeneration,
    [ValidateSet('nvidia', 'amd')][string[]]$FrameGenerationVendors = @('nvidia', 'amd'),
    [string]$StreamlineSdk,
    [string]$StreamlineRuntime,
    [string]$UnityPluginApi,
    [string]$Python = 'python'
)
$ErrorActionPreference = 'Stop'
if ($FrameGeneration) {
    & (Join-Path $PSScriptRoot 'Build-FrameGeneration.ps1') -BuildDirectory $BuildDirectory -Configuration $Configuration `
        -Vendors $FrameGenerationVendors -FidelityFxSdk $FidelityFxSdk -StreamlineSdk $StreamlineSdk `
        -StreamlineRuntime $StreamlineRuntime -UnityPluginApi $UnityPluginApi -CMake $CMake -Python $Python `
        -RunTests:$RunTests -PackageDirectory $PackageDirectory
    return
}
if (-not $FidelityFxSdk) { throw 'The FSR bridge build requires -FidelityFxSdk; use -FrameGeneration for the separate FG companion.' }
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$nativeSource = Join-Path $sourceRoot 'Native'
$sdkRoot = (Resolve-Path -LiteralPath $FidelityFxSdk).Path
$nativeBuild = [IO.Path]::GetFullPath($BuildDirectory)
$sourcePrefix = $sourceRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
foreach ($candidate in @($sdkRoot, $nativeBuild)) {
    if ($candidate.Equals($sourceRoot, [StringComparison]::OrdinalIgnoreCase) -or $candidate.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'SDK and native build output must stay outside the Better AA source checkout.'
    }
}
$manifest = Get-Content -LiteralPath (Join-Path $nativeSource 'runtime-manifest.json') -Raw | ConvertFrom-Json
foreach ($runtime in $manifest.files) {
    $path = Join-Path $sdkRoot $runtime.source
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $runtime.sha256) {
        throw "Official runtime hash mismatch: $($runtime.filename)"
    }
}
& $CMake -S $nativeSource -B $nativeBuild -G 'Visual Studio 17 2022' -A x64 "-DFFX_SDK_ROOT=$sdkRoot"
if ($LASTEXITCODE -ne 0) { throw "Native configure failed ($LASTEXITCODE)." }
& $CMake --build $nativeBuild --config $Configuration --parallel
if ($LASTEXITCODE -ne 0) { throw "Native build failed ($LASTEXITCODE)." }
$library = Join-Path $nativeBuild "$Configuration/ReduxBetterAA.FsrBridge.dll"
if (-not (Test-Path -LiteralPath $library -PathType Leaf)) { throw 'Native build did not produce the bridge DLL.' }
Write-Output "Built optional FSR bridge: $library"
if ($RunTests) {
    $testProgram = Join-Path $nativeBuild "$Configuration/FsrBridgeTests.exe"
    & $testProgram (Join-Path $sdkRoot 'Kits/FidelityFX/signedbin')
    if ($LASTEXITCODE -ne 0) { throw "Native GPU tests failed ($LASTEXITCODE)." }
}
if ($PackageDirectory) {
    if ($Configuration -ne 'Release') { throw 'Only Release builds may be packaged.' }
    $packageRoot = [IO.Path]::GetFullPath($PackageDirectory)
    if ($packageRoot.Equals($sourceRoot, [StringComparison]::OrdinalIgnoreCase) -or $packageRoot.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Optional native runtime archives must be created outside the source checkout.'
    }
    New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
    $metadata = Get-Content -LiteralPath (Join-Path $sourceRoot 'Assets/ReduxBetterAA/Copied/swinfo.json') -Raw | ConvertFrom-Json
    if ($metadata.mod_id -cne 'ReduxBetterAA' -or $metadata.version -notmatch '^\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?$') {
        throw 'Unexpected mod identity or invalid package version.'
    }
    $nativePrefix = 'mods/ReduxBetterAA/native/'
    $noticePath = Join-Path $nativeBuild 'THIRD-PARTY-NOTICES-FSR.txt'
    $licensePath = Join-Path $sdkRoot 'docs/license.md'
    $thirdPartyPath = Join-Path $sdkRoot '3rdpartynotice.md'
    & git -C $sdkRoot diff --quiet HEAD -- docs/license.md 3rdpartynotice.md
    if ($LASTEXITCODE -ne 0) { throw 'AMD runtime license or notices differ from the pinned SDK.' }
    $notice = "Redux Better AA optional native FSR runtime`n`n" +
        "Bridge: ReduxBetterAA.FsrBridge.dll. Better AA MIT license follows.`n`n" +
        (Get-Content -LiteralPath (Join-Path $sourceRoot 'LICENSE') -Raw) +
        "`n`nAMD FSR SDK 2.3.0, commit $($manifest.sdk.commit).`n" +
        "The original amd_fidelityfx_upscaler_dx12.dll is distributed unmodified. Its AMD binary terms and SDK component notices follow.`n`n" +
        (Get-Content -LiteralPath $licensePath -Raw) + "`n`n" + (Get-Content -LiteralPath $thirdPartyPath -Raw)
    [IO.File]::WriteAllText($noticePath, $notice, [Text.UTF8Encoding]::new($false))
    $packageManifest = [ordered]@{
        schemaVersion = 1; abiVersion = $manifest.abiVersion; modVersion = $metadata.version
        sdk = $manifest.sdk; graphicsApi = $manifest.graphicsApi
        files = @(
            [ordered]@{ filename = 'ReduxBetterAA.FsrBridge.dll'; sha256 = (Get-FileHash -LiteralPath $library -Algorithm SHA256).Hash.ToLowerInvariant() }
        ) + @($manifest.files)
        validation = 'Standalone synthetic D3D11 interop test is available with -RunTests. Player rendering and AMD FSR4 hardware need separate validation.'
    }
    $packageManifestPath = Join-Path $nativeBuild 'fsr-runtime-manifest.json'
    [IO.File]::WriteAllText($packageManifestPath, ($packageManifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    $archivePath = Join-Path $packageRoot "BetterAA-FSR-Runtime-$($metadata.version)-win-x64.zip"
    $archiveFiles = [ordered]@{
        ($nativePrefix + 'ReduxBetterAA.FsrBridge.dll') = $library
        ($nativePrefix + 'amd_fidelityfx_upscaler_dx12.dll') = (Join-Path $sdkRoot $manifest.files[0].source)
        ($nativePrefix + 'THIRD-PARTY-NOTICES-FSR.txt') = $noticePath
        ($nativePrefix + 'fsr-runtime-manifest.json') = $packageManifestPath
    }
    $archiveStream = [IO.File]::Open($archivePath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new($archiveStream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($entry in $archiveFiles.GetEnumerator()) {
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $entry.Value, $entry.Key, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        } finally { $archive.Dispose() }
    } finally { $archiveStream.Dispose() }
    $verify = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        if ($verify.Entries.Count -ne $archiveFiles.Count) { throw 'Unexpected optional runtime archive contents.' }
        foreach ($entry in $verify.Entries) {
            if (-not $archiveFiles.Contains($entry.FullName)) { throw "Unexpected runtime archive entry: $($entry.FullName)" }
            $entryStream = $entry.Open()
            $hasher = [Security.Cryptography.SHA256]::Create()
            try { $actual = [Convert]::ToHexString($hasher.ComputeHash($entryStream)) }
            finally { $hasher.Dispose(); $entryStream.Dispose() }
            if ($actual -cne (Get-FileHash -LiteralPath $archiveFiles[$entry.FullName] -Algorithm SHA256).Hash) {
                throw "Runtime archive verification failed: $($entry.FullName)"
            }
        }
    } finally { $verify.Dispose() }
    Write-Output "Verified optional runtime archive: $archivePath"
}
Write-Output 'No game files or public release packages were modified.'
