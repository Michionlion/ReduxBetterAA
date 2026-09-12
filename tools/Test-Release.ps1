#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'Release-Helpers.ps1')
$version = Get-ReleaseVersion $repo
Write-Host "Checking Redux Better AA $version"
foreach ($path in @(& git -C $repo ls-files -- '*.ps1')) {
    if (-not (Test-Path -LiteralPath (Join-Path $repo $path))) { continue }
    $parseErrors = $null
    $tokens = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile((Join-Path $repo $path), [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count) { throw "PowerShell syntax error in ${path}: $parseErrors" }
}
# Include newly added scripts before their first commit as well.
foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1') {
    $parseErrors = $null; $tokens = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count) { throw "PowerShell syntax error in $($file.Name): $parseErrors" }
}
foreach ($suite in @('Motion', 'Quality', 'Release')) {
    & python -m unittest discover -s (Join-Path $repo "tests\$suite")
    if ($LASTEXITCODE -ne 0) { throw "$suite tests failed." }
}
& (Join-Path $repo 'tests\Release\Test-SourceGuard.ps1')
& git -C $repo diff --check
if ($LASTEXITCODE -ne 0) { throw 'Whitespace check failed.' }
Write-Host 'Portable release checks passed.'
