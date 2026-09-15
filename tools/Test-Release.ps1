#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'Release-Helpers.ps1')
$version = Get-ReleaseVersion $repo
Write-Host "Checking Redux Better AA $version"
# Include untracked scripts so this also validates work before its first commit.
$scripts = @(Invoke-ReleaseGit $repo @('ls-files', '--cached', '--others', '--exclude-standard', '--', '*.ps1'))
foreach ($path in ($scripts | Sort-Object -Unique)) {
    if (-not (Test-Path -LiteralPath (Join-Path $repo $path))) { continue }
    $parseErrors = $null
    $tokens = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile((Join-Path $repo $path), [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count) { throw "PowerShell syntax error in ${path}: $parseErrors" }
}
& python -X utf8 -m unittest discover -s (Join-Path $repo 'tests\Release')
if ($LASTEXITCODE -ne 0) { throw 'Release tests failed.' }
& (Join-Path $repo 'tests\Release\Test-SourceGuard.ps1')
& git -C $repo diff --check
if ($LASTEXITCODE -ne 0) { throw 'Whitespace check failed.' }
Write-Host 'Portable release checks passed.'
