[CmdletBinding()]
param(
    [string] $Unity = 'C:\Program Files\Unity\Hub\Editor\6000.5.8f1\Editor\Unity.exe',
    [string] $Ksp2Root
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$expectedEditor = ((Get-Content -LiteralPath (Join-Path $repo 'ProjectSettings\ProjectVersion.txt'))[0] -replace '^m_EditorVersion: ', '')
if (-not (Test-Path -LiteralPath $Unity) -or (Get-Item -LiteralPath $Unity).VersionInfo.ProductVersion -notlike "$expectedEditor*") {
    throw "Use the pinned Unity $expectedEditor editor."
}
$logs = Join-Path $repo 'Logs'
New-Item -ItemType Directory -Force -Path $logs | Out-Null
if (Get-Process Unity -ErrorAction SilentlyContinue) { throw 'Close Unity before running the batch pipeline.' }
function Invoke-UnityStep([string] $Name, [string] $Arguments) {
    Write-Host "Unity: $Name"
    $log = Join-Path $logs "beta-$Name.log"
    # Imported player assemblies are not valid inputs to Editor Burst jobs.
    # Disable that unrelated compiler during prepare and packaging as well as tests.
    $process = Start-Process -FilePath $Unity -ArgumentList "-batchmode --burst-disable-compilation -projectPath `"$repo`" -logFile `"$log`" $Arguments" `
        -WindowStyle Hidden -PassThru
    $process.WaitForExit()
    if (Select-String -LiteralPath $log -Pattern 'error CS\d+|Shader error in|Halted execution|Aborting batchmode|Timeout after \d+ seconds while waiting' -Quiet) {
        throw "Unity $Name failed; see $log"
    }
    if ($process.ExitCode -ne 0) {
        throw "Unity $Name exited $($process.ExitCode); see $log"
    }
}
if ($Ksp2Root -and -not (Test-Path -LiteralPath (Join-Path $Ksp2Root 'KSP2_x64_Data\Managed\ReduxLib.dll'))) {
    throw 'Ksp2Root must point to an installed Redux player.'
}
if (-not $Ksp2Root -and -not (Test-Path -LiteralPath (Join-Path $repo 'Packages\KSP2_x64\package.json'))) {
    throw 'On the first build, pass -Ksp2Root <installed Redux game folder>.'
}
$packageCache = Join-Path $repo 'Library\PackageCache'
$addressablesFilter = 'com.unity.addressables@*'
$addressables = @(Get-ChildItem -LiteralPath $packageCache -Directory -Filter $addressablesFilter -ErrorAction SilentlyContinue)
if ($addressables.Count -ne 1) {
    # Unity must resolve UPM packages before we can exclude assemblies those
    # packages provide. Compilation can fail here until game references exist.
    Write-Host 'Unity: resolve packages (the first import can report missing game references)'
    $resolveLog = Join-Path $logs 'beta-resolve.log'
    $process = Start-Process -FilePath $Unity -ArgumentList "-batchmode -quit --burst-disable-compilation -projectPath `"$repo`" -logFile `"$resolveLog`"" -WindowStyle Hidden -PassThru
    $process.WaitForExit()
    if (-not (Test-Path -LiteralPath (Join-Path $repo 'Library\PackageCache'))) {
        throw "Unity did not resolve packages; see $resolveLog"
    }
    $addressables = @(Get-ChildItem -LiteralPath $packageCache -Directory -Filter $addressablesFilter)
    if ($addressables.Count -ne 1) { throw 'The pinned Addressables package did not resolve.' }
}
if ($Ksp2Root) {
    & (Join-Path $PSScriptRoot 'Import-GameReferences.ps1') -Ksp2Root $Ksp2Root -UnityEditorRoot (Split-Path -Parent $Unity) -EditorVersion $expectedEditor
}
if (-not (Test-Path -LiteralPath (Join-Path $repo 'Packages\KSP2_x64\package.json'))) {
    throw 'On the first build, pass -Ksp2Root <installed Redux game folder>.'
}
Invoke-UnityStep 'prepare' '-quit -executeMethod ReduxBetterAA.Editor.BuildMod.Prepare'
$results = Join-Path $logs 'beta-editmode.xml'
if (Test-Path -LiteralPath $results) { Remove-Item -LiteralPath $results }
Invoke-UnityStep 'editmode' "-runTests -testPlatform EditMode -testResults `"$results`""
[xml]$tests = Get-Content -LiteralPath $results -Raw
if ($tests.'test-run'.result -ne 'Passed' -or [int]$tests.'test-run'.failed -ne 0 -or [int]$tests.'test-run'.passed -lt 1 -or [int]$tests.'test-run'.skipped -ne 0) {
    throw 'EditMode tests did not all pass.'
}
Write-Host "$($tests.'test-run'.passed) EditMode tests passed."
$packageStart = Get-Date
Invoke-UnityStep 'package' '-quit -executeMethod ReduxBetterAA.Editor.BuildMod.Package'
$zip = Join-Path $repo 'Deploy\ReduxBetterAA.zip'
if (-not (Test-Path -LiteralPath $zip) -or (Get-Item -LiteralPath $zip).LastWriteTime -lt $packageStart) { throw 'No fresh mod archive was produced.' }
if (-not (Select-String -LiteralPath (Join-Path $logs 'beta-package.log') -Pattern '\[ReduxBetterAA/Build\] Package complete' -Quiet)) { throw 'Unity did not record successful packaging.' }
Get-FileHash -LiteralPath $zip -Algorithm SHA256
& (Join-Path $PSScriptRoot 'Package.ps1')
