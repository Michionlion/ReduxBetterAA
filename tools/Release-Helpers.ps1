Set-StrictMode -Version Latest

function Invoke-ReleaseGit {
    param([string] $Repository, [string[]] $Arguments)
    $result = @(& git -C $Repository @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "Git failed: $($Arguments -join ' ')" }
    return $result
}

function Assert-ReleaseSource {
    param([string] $Repository, [string] $ExpectedCommit)
    $status = @(Invoke-ReleaseGit $Repository @('status', '--porcelain', '--untracked-files=all'))
    if ($status.Count) { throw "Release requires a clean repository. Commit or remove these changes first:`n$($status -join "`n")" }
    $commit = (Invoke-ReleaseGit $Repository @('rev-parse', 'HEAD')) -join ''
    if ($ExpectedCommit -and $commit -ne $ExpectedCommit) { throw 'HEAD changed during the release build.' }
    return $commit
}

function Get-ReleaseVersion {
    param([string] $Repository)
    $version = (Get-Content -LiteralPath (Join-Path $Repository 'Assets\ReduxBetterAA\Copied\swinfo.json') -Raw | ConvertFrom-Json).version
    if ($version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw 'Release version must be major.minor.patch.' }
    $assembly = Get-Content -LiteralPath (Join-Path $Repository 'Assets\ReduxBetterAA\Code\AssemblyInfo.cs') -Raw
    foreach ($attribute in @('AssemblyVersion', 'AssemblyFileVersion')) {
        if ($assembly -notmatch ($attribute + '\("' + [regex]::Escape($version) + '\.0"\)')) {
            throw "$attribute disagrees with swinfo.json."
        }
    }
    return $version
}

function Get-ProjectEngine {
    param([string] $Repository)
    return (Get-Content -LiteralPath (Join-Path $Repository 'ProjectSettings\ProjectVersion.txt'))[0] -replace '^m_EditorVersion: ', ''
}

function Get-EditorEngine {
    # Returns the editor's Unity version and build revision, e.g. 6000.4.1f1 and 336a400b9ea2.
    param([string] $Unity)
    if (-not (Test-Path -LiteralPath $Unity)) { throw "Unity editor not found: $Unity" }
    $product = (Get-Item -LiteralPath $Unity).VersionInfo.ProductVersion
    if ($product -notmatch '^(\d+\.\d+\.\d+[abfpx]\d+)_([0-9a-f]{12})$') { throw "Unrecognized Unity editor version: $product" }
    return @($Matches[1], $Matches[2])
}

function Assert-PlayerEngine {
    # The installed Redux player must run the same Unity version as the editor that builds for it.
    param([string] $Ksp2Root, [string] $Engine)
    if (-not (Test-Path -LiteralPath (Join-Path $Ksp2Root 'KSP2_x64_Data\Managed\ReduxLib.dll'))) { throw "Ksp2Root must point to an installed Redux player: $Ksp2Root" }
    $player = (Get-Item -LiteralPath (Join-Path $Ksp2Root 'UnityPlayer.dll')).VersionInfo.ProductVersion
    if ($player -notlike "$Engine *" -and $player -ne $Engine) { throw "$Ksp2Root runs Unity $player, not the Unity $Engine editor's player." }
}
