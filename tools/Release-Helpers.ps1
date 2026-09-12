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
    $asset = Get-Content -LiteralPath (Join-Path $Repository 'Assets\ReduxBetterAA\swinfo.asset') -Raw
    if ($asset -notmatch ('(?m)^  version: ' + [regex]::Escape($version) + '\r?$')) { throw 'swinfo.asset disagrees with swinfo.json.' }
    return $version
}
