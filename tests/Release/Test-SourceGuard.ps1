$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\..\tools\Release-Helpers.ps1')
$testRepo = Join-Path ([IO.Path]::GetTempPath()) ('betteraa-release-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRepo | Out-Null
function Expect-Rejection([scriptblock] $Action, [string] $Expected) {
    try { & $Action; throw 'Expected release guard to reject this state.' }
    catch { if ($_.Exception.Message -notlike "*$Expected*") { throw } }
}
try {
    Invoke-ReleaseGit $testRepo @('init', '-q') | Out-Null
    Invoke-ReleaseGit $testRepo @('config', 'user.email', 'release-test@example.invalid') | Out-Null
    Invoke-ReleaseGit $testRepo @('config', 'user.name', 'Release test') | Out-Null
    'source' | Set-Content -LiteralPath (Join-Path $testRepo 'source.txt')
    Invoke-ReleaseGit $testRepo @('add', 'source.txt') | Out-Null
    Invoke-ReleaseGit $testRepo @('commit', '-qm', 'fixture') | Out-Null
    $commit = Assert-ReleaseSource $testRepo
    'dirty' | Set-Content -LiteralPath (Join-Path $testRepo 'source.txt')
    Expect-Rejection { Assert-ReleaseSource $testRepo } 'clean repository'
    Invoke-ReleaseGit $testRepo @('add', 'source.txt') | Out-Null
    Expect-Rejection { Assert-ReleaseSource $testRepo } 'clean repository'
    Invoke-ReleaseGit $testRepo @('commit', '-qm', 'changed') | Out-Null
    Expect-Rejection { Assert-ReleaseSource $testRepo $commit } 'HEAD changed'
    'untracked' | Set-Content -LiteralPath (Join-Path $testRepo 'new.txt')
    Expect-Rejection { Assert-ReleaseSource $testRepo } 'clean repository'
    Write-Host 'Release guards passed: clean source, unstaged, staged, untracked and changed HEAD.'
} finally {
    $resolved = [IO.Path]::GetFullPath($testRepo)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -or (Split-Path -Leaf $resolved) -notlike 'betteraa-release-test-*') { throw 'Unsafe test cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
