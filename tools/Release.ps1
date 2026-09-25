#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $Unity = 'C:\Program Files\Unity\Hub\Editor\6000.6.0f1\Editor\Unity.exe',
    [string] $Ksp2Root,
    [string] $LegacyUnity = 'C:\Program Files\Unity\Hub\Editor\6000.4.1f1\Editor\Unity.exe',
    [Parameter(Mandatory)] [string] $LegacyKsp2Root,
    [string] $LegacyWorkspace,
    [string[]] $RuntimeEditors,
    [Parameter(Mandatory)] [string] $FsrRuntimeZip,
    [switch] $Publish,
    [switch] $Stable
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Release-Helpers.ps1')
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $RuntimeEditors) {
    $RuntimeEditors = @((Split-Path $Unity), (Split-Path $LegacyUnity))
}
# Each complete download is built by the editor matching its Redux player. The pinned
# editor builds in this checkout; the legacy editor builds the same commit in a clone.
$engine = Get-ProjectEngine $repo
$legacyEngine = (Get-EditorEngine $LegacyUnity)[0]
$targets = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'runtime-targets.json') -Raw | ConvertFrom-Json -AsHashtable
if ((Get-EditorEngine $Unity)[0] -ne $engine) { throw "Use the pinned Unity $engine editor." }
$supported = @($targets.Keys | Sort-Object) -join ' '
if ($supported -ne (@(@($engine, $legacyEngine) | Sort-Object) -join ' ')) { throw "Supported engines are $supported; editors given for $engine and $legacyEngine." }
if ($Ksp2Root) { Assert-PlayerEngine $Ksp2Root $engine }
Assert-PlayerEngine $LegacyKsp2Root $legacyEngine
# Validate all supported runtime sets before building. No downloads or game writes.
& python -X utf8 (Join-Path $PSScriptRoot 'package-runtimes.py') --editors @RuntimeEditors
if ($LASTEXITCODE -ne 0) { throw 'Runtime source validation failed.' }
& python -X utf8 (Join-Path $PSScriptRoot 'package-runtimes.py') --validate-fsr-runtime $FsrRuntimeZip --mod-version $Version
if ($LASTEXITCODE -ne 0) { throw 'FSR source validation failed.' }
$commit = Assert-ReleaseSource $repo
if ((Get-ReleaseVersion $repo) -cne $Version) { throw 'Requested version differs from the source versions.' }
$tag = "v$Version"
$repository = 'Michionlion/ReduxBetterAA'
$url = "https://github.com/$repository"
$notesPath = Join-Path $repo "docs\releases\$tag.md"
if (-not (Test-Path -LiteralPath $notesPath)) { throw "Write and commit release notes first: $notesPath" }
function Invoke-Gh([string[]] $Arguments) {
    $result = @(& gh @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "GitHub command failed: gh $($Arguments -join ' ')" }
    return $result
}

$previousTag = $null
if ($Publish) {
    $branch = (Invoke-ReleaseGit $repo @('symbolic-ref', '--short', 'HEAD')) -join ''
    if ($branch -ne 'main') { throw 'Publish releases from main.' }
    $remote = (Invoke-ReleaseGit $repo @('remote', 'get-url', '--push', 'origin')) -join ''
    if ($remote -notin @("git@github.com:$repository.git", "$url.git", $url)) { throw 'origin does not point to ReduxBetterAA.' }
    Invoke-ReleaseGit $repo @('fetch', 'origin', 'main', '--tags') | Out-Host
    Invoke-ReleaseGit $repo @('merge-base', '--is-ancestor', 'origin/main', $commit) | Out-Null
    $pages = ((Invoke-Gh @('api', "repos/$repository/releases", '--paginate', '--slurp')) -join "`n") | ConvertFrom-Json -NoEnumerate
    $allReleases = @($pages | ForEach-Object { foreach ($release in $_) { $release } })
    $existing = @($allReleases | Where-Object tag_name -eq $tag)
    if ($existing.Count -and -not $existing[0].draft) { throw "$tag is already published; choose a new version." }
    $previous = @($allReleases | Where-Object { -not $_.draft -and $_.tag_name -ne $tag } | Sort-Object published_at -Descending | Select-Object -First 1)
    if ($previous.Count) {
        $previousTag = $previous[0].tag_name
    }
    $existingTag = @(Invoke-ReleaseGit $repo @('tag', '--list', $tag))
    if ($existingTag.Count) {
        $tagCommit = (Invoke-ReleaseGit $repo @('rev-parse', "$tag^{}")) -join ''
        if ($tagCommit -ne $commit) { throw 'The version tag already points to another commit.' }
    }
} else {
    $tagPatterns = @()
    foreach ($candidateTag in (Invoke-ReleaseGit $repo @('tag', '--merged', $commit))) {
        if ($candidateTag -eq $tag) { continue }
        if ($candidateTag -notmatch '^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { continue }
        if (((Invoke-ReleaseGit $repo @('rev-parse', "$candidateTag^{}")) -join '') -ne $commit) {
            $tagPatterns += @('--match', $candidateTag)
        }
    }
    if ($tagPatterns.Count) {
        $previousTag = (Invoke-ReleaseGit $repo (@('describe', '--tags', '--abbrev=0') + $tagPatterns + @($commit))) -join ''
    }
    if (-not $previousTag) {
        # A source-history cleanup can leave the published tag in old history.
        # Keep that tag intact and use curated release notes in that case.
        $previousTag = @(Invoke-ReleaseGit $repo @('tag', '--sort=-version:refname') |
            Where-Object { $_ -match '^v\d+\.\d+\.\d+$' -and $_ -ne $tag } | Select-Object -First 1) -join ''
    }
}
& (Join-Path $PSScriptRoot 'Test-Release.ps1')
& (Join-Path $PSScriptRoot 'Build.ps1') -Unity $Unity -Ksp2Root $Ksp2Root
[void](Assert-ReleaseSource $repo $commit)
$legacyArguments = @{ Unity = $LegacyUnity; Ksp2Root = $LegacyKsp2Root; Commit = $commit }
if ($LegacyWorkspace) { $legacyArguments.Workspace = $LegacyWorkspace }
& (Join-Path $PSScriptRoot 'Build-Legacy.ps1') @legacyArguments
[void](Assert-ReleaseSource $repo $commit)
$components = @{
    $engine = Join-Path $repo "Deploy\ReduxBetterAA-$Version.zip"
    $legacyEngine = Join-Path $repo "Deploy\engines\$legacyEngine\ReduxBetterAA-$Version.zip"
}
$output = Join-Path $repo ('Deploy\releases\' + $tag + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$modArguments = @($components.Keys | Sort-Object | ForEach-Object { @('--mod', "$_=$($components[$_])") })
& python -X utf8 (Join-Path $PSScriptRoot 'package-complete.py') @modArguments `
    --editors @RuntimeEditors --fsr-runtime $FsrRuntimeZip --output $output --commit $commit --version $Version
if ($LASTEXITCODE -ne 0) { throw 'Complete release packaging failed.' }
$completeAssets = @(Get-ChildItem -LiteralPath $output -Filter 'ReduxBetterAA-*.zip' -File | Sort-Object Name)
# Public changelogs describe player-visible changes; Git retains development history.
@("# $tag", '', (Get-Content -LiteralPath $notesPath -Raw).TrimEnd()) |
    Set-Content -LiteralPath (Join-Path $output 'CHANGELOG.md') -Encoding utf8NoBOM
$editModeResults = [ordered]@{}
foreach ($built in @(@($engine, 'Logs\beta-editmode.xml'), @($legacyEngine, "Logs\beta-editmode-$legacyEngine.xml"))) {
    [xml]$tests = Get-Content -LiteralPath (Join-Path $repo $built[1]) -Raw
    if ([int]$tests.'test-run'.failed -ne 0 -or [int]$tests.'test-run'.passed -lt 1) { throw "EditMode tests did not pass on Unity $($built[0])." }
    $editModeResults[$built[0]] = [ordered]@{ passed = [int]$tests.'test-run'.passed; failed = [int]$tests.'test-run'.failed }
}
$info = [ordered]@{
    version = $Version; sourceCommit = $commit; sourceClean = $true
    unityEditor = $engine; legacyUnityEditor = $legacyEngine
    engines = [ordered]@{}
    editModePassed = $editModeResults[$engine].passed; editModeFailed = $editModeResults[$engine].failed
    portableChecksPassed = $true; nativeLibrariesIncluded = $true
    completePackages = @($completeAssets.Name)
    fsrRuntimeSha256 = (Get-FileHash -LiteralPath $FsrRuntimeZip).Hash.ToLowerInvariant()
    previousRelease = $previousTag
    changelogSource = 'curated-release-notes'
}
foreach ($built in ($components.Keys | Sort-Object)) {
    $info.engines[$built] = [ordered]@{
        reduxVersion = $targets[$built].redux
        package = "ReduxBetterAA-$Version-Redux-$($targets[$built].redux).zip"
        componentSha256 = (Get-FileHash -LiteralPath $components[$built]).Hash.ToLowerInvariant()
        editModePassed = $editModeResults[$built].passed; editModeFailed = $editModeResults[$built].failed
    }
}
$info | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'build-info.json') -Encoding utf8NoBOM
$assets = @(Get-ChildItem -LiteralPath $output -File | Sort-Object Name)
$checksums = @($assets | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name })
$checksums | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding utf8NoBOM
$assets = @(Get-ChildItem -LiteralPath $output -File | Sort-Object Name)
$expectedHashes = @{}
foreach ($file in $assets) { $expectedHashes[$file.Name] = (Get-FileHash -LiteralPath $file.FullName).Hash }
$notes = (Get-Content -LiteralPath $notesPath -Raw).TrimEnd()
foreach ($package in $completeAssets) {
    $notes = $notes.Replace(('`' + $package.Name + '`'), "[$($package.Name)]($url/releases/download/$tag/$($package.Name))")
}
$notes += "`n`n[Source]($url/tree/$tag) · [Checksums]($url/releases/download/$tag/SHA256SUMS.txt)`n"
$finalNotes = Join-Path $repo "Logs\release-$tag.md"
$notes | Set-Content -LiteralPath $finalNotes -Encoding utf8NoBOM
[void](Assert-ReleaseSource $repo $commit)
Write-Host "Validated release files: $output"
if (-not $Publish) { Write-Host 'Build complete. Nothing was pushed or published. Add -Publish to build and release.'; return }

if (-not $existingTag.Count) { Invoke-ReleaseGit $repo @('tag', '-a', $tag, $commit, '-m', "Redux Better AA $Version") | Out-Host }
Invoke-ReleaseGit $repo @('push', '--atomic', 'origin', "${commit}:refs/heads/main", "refs/tags/$tag") | Out-Host
if (-not $existing.Count) {
    Invoke-Gh @('release', 'create', $tag, '--repo', $repository, '--verify-tag', '--draft', '--title', "Redux Better AA $Version", '--notes-file', $finalNotes) | Out-Host
} else {
    Invoke-Gh @('release', 'edit', $tag, '--repo', $repository, '--title', "Redux Better AA $Version", '--notes-file', $finalNotes) | Out-Host
}
Invoke-Gh (@('release', 'upload', $tag, '--repo', $repository, '--clobber') + @($assets.FullName)) | Out-Host
# Verify the server's copies before making the draft visible. Failed uploads leave a retryable draft.
$download = Join-Path $output 'download-verification'
Invoke-Gh @('release', 'download', $tag, '--repo', $repository, '--dir', $download) | Out-Host
$downloaded = @(Get-ChildItem -LiteralPath $download -File)
if ($downloaded.Count -ne $assets.Count) { throw 'Draft has unexpected or missing attachments.' }
foreach ($file in $downloaded) {
    if (-not $expectedHashes.ContainsKey($file.Name) -or (Get-FileHash -LiteralPath $file.FullName).Hash -ne $expectedHashes[$file.Name]) {
        throw "Draft attachment hash mismatch: $($file.Name)"
    }
}
[void](Assert-ReleaseSource $repo $commit)
Invoke-Gh @('release', 'edit', $tag, '--repo', $repository, '--draft=false', "--prerelease=$(-not $Stable)", '--latest=false') | Out-Host
Write-Host "Published $url/releases/tag/$tag"
