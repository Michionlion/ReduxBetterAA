#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $Unity = 'C:\Program Files\Unity\Hub\Editor\6000.5.8f1\Editor\Unity.exe',
    [string] $Ksp2Root,
    [switch] $Publish,
    [switch] $Stable
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Release-Helpers.ps1')
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$commit = Assert-ReleaseSource $repo
if ((Get-ReleaseVersion $repo) -cne $Version) { throw 'Requested version differs from the source versions.' }
$tag = "v$Version"
$repository = 'Michionlion/ReduxBetterAA'
$url = "https://github.com/$repository"
$notesPath = Join-Path $repo "docs\releases\$tag.md"
if (-not (Test-Path -LiteralPath $notesPath)) { throw "Write and commit release notes first: $notesPath" }
$branch = (Invoke-ReleaseGit $repo @('symbolic-ref', '--short', 'HEAD')) -join ''
if ($branch -ne 'main') { throw 'Publish releases from main.' }
$remote = (Invoke-ReleaseGit $repo @('remote', 'get-url', '--push', 'origin')) -join ''
if ($remote -notin @("git@github.com:$repository.git", "$url.git", $url)) { throw 'origin does not point to ReduxBetterAA.' }

function Invoke-Gh([string[]] $Arguments) {
    $result = @(& gh @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "GitHub command failed: gh $($Arguments -join ' ')" }
    return $result
}

# Read-only preflight also ensures authentication and history are available before a long build.
Invoke-ReleaseGit $repo @('fetch', 'origin', 'main', '--tags') | Out-Host
Invoke-ReleaseGit $repo @('merge-base', '--is-ancestor', 'origin/main', $commit) | Out-Null
$pages = ((Invoke-Gh @('api', "repos/$repository/releases", '--paginate', '--slurp')) -join "`n") | ConvertFrom-Json -NoEnumerate
$allReleases = @($pages | ForEach-Object { foreach ($release in $_) { $release } })
$existing = @($allReleases | Where-Object tag_name -eq $tag)
if ($existing.Count -and -not $existing[0].draft) { throw "$tag is already published; choose a new version." }
$previous = @($allReleases | Where-Object { -not $_.draft -and $_.tag_name -ne $tag } | Sort-Object published_at -Descending | Select-Object -First 1)
$range = $commit
if ($previous.Count) {
    $previousTag = $previous[0].tag_name
    Invoke-ReleaseGit $repo @('merge-base', '--is-ancestor', "$previousTag^{}", $commit) | Out-Null
    $range = "$previousTag..$commit"
}
$existingTag = @(Invoke-ReleaseGit $repo @('tag', '--list', $tag))
if ($existingTag.Count) {
    $tagCommit = (Invoke-ReleaseGit $repo @('rev-parse', "$tag^{}")) -join ''
    if ($tagCommit -ne $commit) { throw 'The version tag already points to another commit.' }
}

& (Join-Path $PSScriptRoot 'Test-Release.ps1')
& (Join-Path $PSScriptRoot 'Build.ps1') -Unity $Unity -Ksp2Root $Ksp2Root
[void](Assert-ReleaseSource $repo $commit)
$output = Join-Path $repo ('Deploy\releases\' + $tag + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
& (Join-Path $PSScriptRoot 'Package.ps1') -OutputDirectory $output -SourceCommit $commit
$format = "--format=- %s ([%h]($url/commit/%H))"
$history = @(Invoke-ReleaseGit $repo @('log', '--reverse', $format, $range))
$heading = if ($previous.Count) { "Changes since $previousTag" } else { 'Changes through the first public release' }
@("# $tag", '', $heading, '') + $history | Set-Content -LiteralPath (Join-Path $output 'CHANGELOG.md') -Encoding utf8NoBOM
[xml]$tests = Get-Content -LiteralPath (Join-Path $repo 'Logs\beta-editmode.xml') -Raw
$info = [ordered]@{
    version = $Version; sourceCommit = $commit; sourceClean = $true
    unityEditor = ((Get-Content -LiteralPath (Join-Path $repo 'ProjectSettings\ProjectVersion.txt'))[0] -replace '^m_EditorVersion: ', '')
    editModePassed = [int]$tests.'test-run'.passed; editModeFailed = [int]$tests.'test-run'.failed
    portableChecksPassed = $true; nativeLibrariesIncluded = $false
    previousRelease = $(if ($previous.Count) { $previousTag } else { $null })
}
$info | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'build-info.json') -Encoding utf8NoBOM
$assets = @(Get-ChildItem -LiteralPath $output -File | Sort-Object Name)
$checksums = @($assets | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name })
$checksums | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding utf8NoBOM
$assets = @(Get-ChildItem -LiteralPath $output -File | Sort-Object Name)
$expectedHashes = @{}
foreach ($file in $assets) { $expectedHashes[$file.Name] = (Get-FileHash -LiteralPath $file.FullName).Hash }
$notes = (Get-Content -LiteralPath $notesPath -Raw).TrimEnd() + "`n`n[Full changelog]($url/releases/download/$tag/CHANGELOG.md) · [Source]($url/tree/$tag)`n"
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
