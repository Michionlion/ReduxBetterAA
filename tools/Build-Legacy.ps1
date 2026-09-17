#Requires -Version 7.0
<#
Build the managed component for a legacy Redux engine in a disposable clone.
Bundles and assemblies must come from the editor matching that engine's player;
the clone is pinned to the legacy editor while the tracked source stays untouched.
#>
[CmdletBinding()]
param(
    [string] $Unity = 'C:\Program Files\Unity\Hub\Editor\6000.4.1f1\Editor\Unity.exe',
    [Parameter(Mandatory)] [string] $Ksp2Root,
    [string] $Workspace,
    [string] $Commit
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Release-Helpers.ps1')
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$pinned = Get-ProjectEngine $repo
$engine, $revision = Get-EditorEngine $Unity
if ($engine -eq $pinned) { throw "Use Build.ps1 for the pinned Unity $pinned editor." }
$targets = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'runtime-targets.json') -Raw | ConvertFrom-Json -AsHashtable
if (-not $targets.ContainsKey($engine)) { throw "No verified runtime target for Unity $engine." }
Assert-PlayerEngine $Ksp2Root $engine
if (-not $Commit) { $Commit = (Invoke-ReleaseGit $repo @('rev-parse', 'HEAD')) -join '' }
if (-not $Workspace) { $Workspace = Join-Path $repo "Library\BetterAA\engine-$engine" }
$Workspace = [IO.Path]::GetFullPath($Workspace).TrimEnd('\')
# Unity's Mono cannot open the staged bundle beyond MAX_PATH: the deepest build path is
# Library\BetterAA\<32-hex>\payload\addressables\StandaloneWindows64\<81-char bundle> (172 chars).
if ($Workspace.Length -gt 86) { throw "Legacy workspace path is too long for Unity's staged bundle; pass a shorter -Workspace than $Workspace" }
if (Get-Process Unity -ErrorAction SilentlyContinue) { throw 'Close Unity before running the batch pipeline.' }

if (Test-Path -LiteralPath (Join-Path $Workspace '.git')) {
    # Reuse the clone's Unity import cache; everything else returns to the requested commit.
    Invoke-ReleaseGit $Workspace @('fetch', '-q', 'origin') | Out-Null
    & git -C $Workspace cat-file -e "$Commit^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) { throw "Legacy workspace cannot reach $Commit; remove $Workspace to clone again." }
    Invoke-ReleaseGit $Workspace @('reset', '-q', '--hard', $Commit) | Out-Null
    Invoke-ReleaseGit $Workspace @('clean', '-qfdx', '-e', '/Library/', '-e', '/Packages/KSP2_x64/') | Out-Null
} else {
    if (Test-Path -LiteralPath $Workspace) { throw "Workspace exists without a clone: $Workspace" }
    New-Item -ItemType Directory -Force -Path (Split-Path $Workspace) | Out-Null
    Invoke-ReleaseGit $repo @('clone', '-q', '--no-local', '--', $repo, $Workspace) | Out-Null
    Invoke-ReleaseGit $Workspace @('checkout', '-q', '--detach', $Commit) | Out-Null
}
if (((Invoke-ReleaseGit $Workspace @('rev-parse', 'HEAD')) -join '') -ne $Commit) { throw 'Legacy workspace is not at the requested commit.' }
@("m_EditorVersion: $engine", "m_EditorVersionWithRevision: $engine ($revision)") |
    Set-Content -LiteralPath (Join-Path $Workspace 'ProjectSettings\ProjectVersion.txt') -Encoding utf8NoBOM
Write-Host "Building the Unity $engine component for Redux $($targets[$engine].redux) in $Workspace"
& (Join-Path $Workspace 'tools\Build.ps1') -Unity $Unity -Ksp2Root $Ksp2Root

# The legacy editor may re-resolve its own built-in packages, but nothing else may differ
# from the tracked source that was built: same code, shaders and pinned registry packages.
$allowed = @('ProjectSettings/ProjectVersion.txt', 'Packages/packages-lock.json')
$changed = @(Invoke-ReleaseGit $Workspace @('status', '--porcelain', '--untracked-files=all') |
    Where-Object { $_ -notmatch '^ M (.+)$' -or $Matches[1] -notin $allowed })
if ($changed.Count) { throw "The legacy editor changed tracked source; review before releasing:`n$($changed -join "`n")" }
$manifest = Get-Content -LiteralPath (Join-Path $Workspace 'Packages\manifest.json') -Raw | ConvertFrom-Json -AsHashtable
$lock = (Get-Content -LiteralPath (Join-Path $Workspace 'Packages\packages-lock.json') -Raw | ConvertFrom-Json -AsHashtable).dependencies
foreach ($name in $manifest.dependencies.Keys) {
    $resolved = $lock[$name]
    if ($null -eq $resolved) { throw "Unity $engine did not resolve $name." }
    if ($resolved.source -ne 'builtin' -and $resolved.version -ne $manifest.dependencies[$name]) {
        throw "Unity $engine resolved $name $($resolved.version) instead of the pinned $($manifest.dependencies[$name])."
    }
}
$version = Get-ReleaseVersion $Workspace
$built = Join-Path $Workspace "Deploy\ReduxBetterAA-$version.zip"
& python -X utf8 (Join-Path $PSScriptRoot 'package-release.py') verify $built --commit $Commit --version $version
if ($LASTEXITCODE -ne 0) { throw 'Legacy component verification failed.' }
$output = Join-Path $repo "Deploy\engines\$engine"
New-Item -ItemType Directory -Force -Path $output | Out-Null
Copy-Item -LiteralPath $built -Destination $output -Force
New-Item -ItemType Directory -Force -Path (Join-Path $repo 'Logs') | Out-Null
Copy-Item -LiteralPath (Join-Path $Workspace 'Logs\beta-editmode.xml') -Destination (Join-Path $repo "Logs\beta-editmode-$engine.xml") -Force
Write-Host "Legacy component: $(Join-Path $output "ReduxBetterAA-$version.zip")"
