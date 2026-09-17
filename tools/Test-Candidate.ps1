#Requires -Version 7.4
[CmdletBinding()]
param(
    [string] $EnvFile = (Join-Path $PSScriptRoot '..\.env'),
    [Parameter(Mandatory)] [string] $FsrRuntimeZip
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'Release-Helpers.ps1')

function Invoke-Checked([string] $Command, [string[]] $Arguments, [string] $Log) {
    & $Command @Arguments 2>&1 | Tee-Object -FilePath $Log | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$Command exited $LASTEXITCODE; see $Log" }
}
function Copy-Tree([string] $From, [string] $To, [string] $Log) {
    & robocopy $From $To /E /COPY:DAT /DCOPY:DAT /R:1 /W:1 /XJ /NP /NFL /NDL "/LOG:$Log" | Out-Host
    if ($LASTEXITCODE -ge 8) { throw "Copy failed; see $Log" }
}
function Invoke-Redux([string[]] $Arguments) {
    $json = & $paths.REDUX_CLI @Arguments --json --no-update-check 2>> (Join-Path $run 'redux-cli.log')
    if ($LASTEXITCODE -ne 0) { throw "Redux CLI failed: $($Arguments -join ' ')" }
    return ($json -join "`n" | ConvertFrom-Json)
}
function Wait-CandidateExit {
    $owned = @(Get-Process KSP2_x64 -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $game 'KSP2_x64.exe') })
    foreach ($process in $owned) {
        if (-not $process.WaitForExit(30000)) {
            $process.Kill()
            if (-not $process.WaitForExit(10000)) { throw "Candidate process $($process.Id) did not exit." }
            throw 'Candidate did not shut down within 30 seconds; its process was stopped.'
        }
    }
}

$paths = @{}
foreach ($line in Get-Content -LiteralPath $EnvFile) {
    $line = $line.Trim()
    if (-not $line -or $line.StartsWith('#')) { continue }
    if ($line -notmatch '^([A-Z_][A-Z0-9_]*)=(.*)$') { throw "Invalid .env line: $line" }
    $key, $value = $Matches[1], $Matches[2].Trim().Trim('"').Trim("'")
    if ($paths.ContainsKey($key)) { throw "Duplicate .env key: $key" }
    if (-not [IO.Path]::IsPathFullyQualified($value)) { throw "$key must be an absolute path." }
    if ([IO.Path]::GetFullPath($value) -eq [IO.Path]::GetPathRoot($value)) { throw "$key must not be a drive root." }
    $paths[$key] = [IO.Path]::GetFullPath($value).TrimEnd('\')
}
$required = @('KSP2_SOURCE', 'KSP2_LEGACY_ROOT', 'TEST_WORKSPACE', 'REDUX_CLI', 'REDUX_CONFIG', 'UNITY_EDITOR', 'UNITY_EDITOR_LEGACY', 'TEST_HARNESS', 'TEST_FIXTURE', 'KSP2_PROFILE')
foreach ($key in $required) {
    if (-not $paths.ContainsKey($key)) { throw "Set $key in $EnvFile." }
    $existing = $paths[$key]
    if ($key -ne 'TEST_WORKSPACE' -and -not (Test-Path -LiteralPath $existing)) { throw "Set an existing $key path in $EnvFile." }
    while (-not (Test-Path -LiteralPath $existing)) { $existing = Split-Path -Parent $existing }
    $item = Get-Item -LiteralPath $existing
    if ($key -eq 'TEST_WORKSPACE' -and $item -isnot [IO.DirectoryInfo]) { throw 'TEST_WORKSPACE must be a directory.' }
    while ($null -ne $item) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked paths are not supported: $($item.FullName)" }
        $item = if ($item -is [IO.DirectoryInfo]) { $item.Parent } else { $item.Directory }
    }
}
$separate = @($repo, $paths.KSP2_SOURCE, $paths.KSP2_LEGACY_ROOT, $paths.TEST_WORKSPACE, $paths.KSP2_PROFILE)
for ($i = 0; $i -lt $separate.Count; $i++) {
    for ($j = $i + 1; $j -lt $separate.Count; $j++) {
        $a = $separate[$i].TrimEnd('\') + '\'
        $b = $separate[$j].TrimEnd('\') + '\'
        if ($a.StartsWith($b, 'OrdinalIgnoreCase') -or $b.StartsWith($a, 'OrdinalIgnoreCase')) { throw 'Source, legacy game, test workspace, player profile and repository must not overlap.' }
    }
}
foreach ($name in @('mods', 'BepInEx', 'Redux', 'KSP2_x64_Data\Managed\ReduxLib.dll', 'AMDUnityPlugin.dll', 'NVUnityPlugin.dll', 'nvngx_dlss.dll')) {
    if (Test-Path -LiteralPath (Join-Path $paths.KSP2_SOURCE $name)) { throw "KSP2_SOURCE is not stock: $name" }
}
foreach ($file in @('KSP2_x64.exe', 'KSP2_x64_Data\Managed\Assembly-CSharp.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $paths.KSP2_SOURCE $file))) { throw "KSP2_SOURCE is missing $file" }
}
# The legacy component compiles against an installed Redux player of the older engine;
# it is only read, never launched or modified here.
Assert-PlayerEngine $paths.KSP2_LEGACY_ROOT (Get-EditorEngine $paths.UNITY_EDITOR_LEGACY)[0]
if (Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -eq 'KSP2_x64' -or $_.ProcessName -like 'Ksp2Redux*' -or $_.ProcessName -eq 'Unity' }) { throw 'Close KSP2, the Redux updater and Unity first.' }
if (Get-NetTCPConnection -State Listen -LocalPort 28542 -ErrorAction SilentlyContinue) { throw 'The test bridge port 28542 is already in use.' }
$harnessCli = Join-Path $paths.TEST_HARNESS 'redux-test.ps1'
if (-not (Test-Path -LiteralPath $harnessCli)) { throw 'TEST_HARNESS must contain the ready-to-run redux-test.ps1 CLI.' }
$commit = Assert-ReleaseSource $repo
$harnessCommit = Assert-ReleaseSource $paths.TEST_HARNESS
$version = Get-ReleaseVersion $repo
& python -X utf8 (Join-Path $PSScriptRoot 'package-runtimes.py') --validate-fsr-runtime $FsrRuntimeZip --mod-version $version
if ($LASTEXITCODE -ne 0) { throw 'FSR runtime validation failed.' }
$FsrRuntimeZip = (Resolve-Path -LiteralPath $FsrRuntimeZip).Path
$editorVersion = (Get-Content -LiteralPath (Join-Path $repo 'ProjectSettings\ProjectVersion.txt'))[0] -replace '^m_EditorVersion: ', ''
$target = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'runtime-targets.json') -Raw | ConvertFrom-Json -AsHashtable)[$editorVersion]
if (-not $target) { throw "No verified native runtime target for $editorVersion" }
$nativeRoot = Join-Path (Split-Path $paths.UNITY_EDITOR -Parent) 'Data\PlaybackEngines\windowsstandalonesupport\Variations\win64_player_nondevelopment_mono'
foreach ($name in $target.hashes.Keys) {
    if ((Get-FileHash -LiteralPath (Join-Path $nativeRoot $name)).Hash -ne $target.hashes[$name]) { throw "Native runtime hash differs: $name" }
}

$runName = 'candidate-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$run = Join-Path $paths.TEST_WORKSPACE $runName
$game = Join-Path $run 'game'
$source = Join-Path $run 'source'
$harness = Join-Path $run 'harness'
New-Item -ItemType Directory -Force -Path $paths.TEST_WORKSPACE | Out-Null
New-Item -ItemType Directory -Path $run | Out-Null
$summary = [ordered]@{ status = 'failed'; startedUtc = [DateTime]::UtcNow.ToString('o'); commit = $commit; version = $version; editor = $editorVersion; inputs = $paths; runtimes = $target.hashes; run = $run; phases = @() }
$summary.harnessCommit = $harnessCommit
$profileBackup = $paths.KSP2_PROFILE + '.' + $runName + '-original'
$profileEvidence = $paths.KSP2_PROFILE + '.' + $runName + '-results'
if ((Test-Path -LiteralPath $profileBackup) -or (Test-Path -LiteralPath $profileEvidence)) { throw 'Profile backup paths already exist.' }
$profileMoved = $false
$failure = $null
try {
    Copy-Tree $paths.KSP2_SOURCE $game (Join-Path $run 'copy-stock.log')
    $config = [IO.File]::ReadAllBytes($paths.REDUX_CONFIG)
    try {
        # CLI 0.5.1 applies updates to the active install, even when --install names another.
        $doctor = Invoke-Redux @('doctor')
        if ([IO.Path]::GetFullPath($doctor.configPath) -ne $paths.REDUX_CONFIG) { throw 'REDUX_CONFIG does not match the CLI configuration path.' }
        if ([IO.Path]::GetFullPath($doctor.gameDataFolder) -ne $paths.KSP2_PROFILE) { throw 'KSP2_PROFILE does not match the player data folder reported by Redux CLI.' }
        $summary.reduxCli = $doctor.version
        [void](Invoke-Redux @('installs', 'add', $game, '--activate', '--name', $runName, '--channel', 'beta'))
        $stock = Invoke-Redux @('current')
        if ([IO.Path]::GetFullPath($stock.installDir) -ne $game -or $stock.version -notlike '0.2.2.0.*') { throw 'Redux CLI must target this candidate copy of stock KSP2 0.2.2.0.' }
        $summary.stock = $stock.version
        $summary.reduxInstall = Invoke-Redux @('update', '--channel', 'beta')
        $redux = Invoke-Redux @('current')
        if ([IO.Path]::GetFullPath($redux.installDir) -ne $game -or $redux.version -notlike "$($target.redux).*") { throw 'Latest Redux is not compatible with the pinned build/runtime target.' }
        $summary.redux = $redux.version
    }
    finally { [IO.File]::WriteAllBytes($paths.REDUX_CONFIG, $config) }
    if ((Get-Item -LiteralPath (Join-Path $game 'UnityPlayer.dll')).VersionInfo.FileVersion -notlike (($editorVersion -replace 'f\d+$', '.') + '*')) { throw 'Redux and the pinned Unity editor have different player versions.' }
    # Redux 0.2.9.0 schema 1 records optional online-service choices. Decline them
    # in this disposable install so its first-run dialog does not cover captures.
    $offlinePreferences = Join-Path $repo 'tests\Release\offline-redux-config.json'
    Copy-Item -LiteralPath $offlinePreferences -Destination (Join-Path $game 'Redux\config.json') -Force
    $summary.offlinePreferencesSha256 = (Get-FileHash -LiteralPath $offlinePreferences).Hash
    Invoke-Checked git @('clone', '--no-local', '--', $repo, $source) (Join-Path $run 'clone.log')
    Invoke-Checked git @('-C', $source, 'checkout', '--detach', $commit) (Join-Path $run 'checkout.log')
    & (Join-Path $source 'tools\Release.ps1') -Version $version -Unity $paths.UNITY_EDITOR -Ksp2Root $game `
        -LegacyUnity $paths.UNITY_EDITOR_LEGACY -LegacyKsp2Root $paths.KSP2_LEGACY_ROOT -FsrRuntimeZip $FsrRuntimeZip `
        -LegacyWorkspace (Join-Path $run 'legacy') `
        -RuntimeEditors @((Split-Path $paths.UNITY_EDITOR), (Split-Path $paths.UNITY_EDITOR_LEGACY)) *>&1 |
        Tee-Object -FilePath (Join-Path $run 'release.log') | Out-Host
    $releases = @(Get-ChildItem -LiteralPath (Join-Path $source 'Deploy\releases') -Directory)
    if ($releases.Count -ne 1) { throw 'Expected one freshly prepared release.' }
    $summary.releaseDirectory = $releases[0].FullName
    $zip = Join-Path $summary.releaseDirectory "ReduxBetterAA-$version-Redux-$($target.redux).zip"
    $summary.package = @{ path = $zip; sha256 = (Get-FileHash -LiteralPath $zip).Hash }
    Expand-Archive -LiteralPath $zip -DestinationPath $game
    # Exercise missing-library fallback using only the exact manifest-listed
    # native payload from this disposable installation, then restore the full ZIP.
    $nativeHold = Join-Path $run 'withheld-natives'
    New-Item -ItemType Directory -Path $nativeHold | Out-Null
    $completeManifest = Get-Content -LiteralPath (Join-Path $game 'BetterAA-release-manifest.json') -Raw | ConvertFrom-Json
    foreach ($entry in $completeManifest.files) {
        if ($entry.path -in $target.hashes.Keys -or $entry.path.StartsWith('mods/ReduxBetterAA/native/')) {
            $installedPath = [IO.Path]::GetFullPath((Join-Path $game $entry.path))
            if (-not $installedPath.StartsWith($game.TrimEnd('\') + '\', 'OrdinalIgnoreCase')) { throw 'Native path escaped disposable game.' }
            Move-Item -LiteralPath $installedPath -Destination (Join-Path $nativeHold ([IO.Path]::GetFileName($installedPath)))
        }
    }
    $assembly = Join-Path $game 'mods\ReduxBetterAA\ReduxBetterAA.dll'
    $summary.assemblySha256 = (Get-FileHash -LiteralPath $assembly).Hash
    Invoke-Checked git @('clone', '--no-local', '--', $paths.TEST_HARNESS, $harness) (Join-Path $run 'clone-harness.log')
    Invoke-Checked git @('-C', $harness, 'checkout', '--detach', $harnessCommit) (Join-Path $run 'checkout-harness.log')
    $harnessCli = Join-Path $harness 'redux-test.ps1'
    Invoke-Checked pwsh @('-NoProfile', '-File', (Join-Path $harness 'scripts\install-mod.ps1'), '-GameRoot', $game, '-UnityRoot', (Split-Path (Split-Path $paths.UNITY_EDITOR -Parent) -Parent)) (Join-Path $run 'harness-build.log')
    $summary.harnessSha256 = (Get-FileHash -LiteralPath (Join-Path $game 'mods\ReduxTestHarness\ReduxTestHarness.dll')).Hash
    $fixtures = Join-Path $run 'fixtures'
    New-Item -ItemType Directory -Path $fixtures | Out-Null
    Copy-Item -LiteralPath $paths.TEST_FIXTURE -Destination (Join-Path $fixtures 'candidate.json')
    $summary.fixtureSha256 = (Get-FileHash -LiteralPath $paths.TEST_FIXTURE).Hash
    $settings = Get-Content -LiteralPath (Join-Path $paths.KSP2_PROFILE 'Global\Settings.json') -Raw | ConvertFrom-Json -AsHashtable
    $seed = @{}
    foreach ($key in @('VersionString', 'LanguageKey', 'PlayerEULALegalAcceptanceUTCTime', 'PlayerEULALegalAcceptanceVersion', 'PlayerPPLegalAcceptanceUTCTime', 'PlayerPPLegalAcceptanceVersion', 'PlayerTOSLegalAcceptanceUTCTime', 'PlayerTOSLegalAcceptanceVersion')) {
        if ($settings.ContainsKey($key)) { $seed[$key] = $settings[$key] }
    }
    Move-Item -LiteralPath $paths.KSP2_PROFILE -Destination $profileBackup
    $profileMoved = $true
    New-Item -ItemType Directory -Path (Join-Path $paths.KSP2_PROFILE 'Global') | Out-Null
    $seed | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $paths.KSP2_PROFILE 'Global\Settings.json') -Encoding utf8NoBOM
    foreach ($phase in @('core', 'native')) {
        $phaseRoot = Join-Path $run $phase
        New-Item -ItemType Directory -Path $phaseRoot | Out-Null
        if ($phase -eq 'native') {
            Expand-Archive -LiteralPath $zip -DestinationPath $game -Force
            foreach ($name in $target.hashes.Keys) {
                if ((Get-FileHash -LiteralPath (Join-Path $game $name)).Hash -ne $target.hashes[$name]) { throw "Installed runtime hash differs: $name" }
            }
        }
        $lua = Join-Path $phaseRoot 'suite.lua'
        "release_fixture = 'candidate'`nrelease_native = $($phase -eq 'native' | ConvertTo-Json)`nrelease_amd_runtime = $($phase -eq 'native' | ConvertTo-Json)`n" + (Get-Content -LiteralPath (Join-Path $source 'tests\Release\ingame.lua') -Raw) | Set-Content -LiteralPath $lua -Encoding utf8NoBOM
        try {
            # The player reports MainMenu before its startup logos finish fading.
            Invoke-Checked pwsh @('-NoProfile', '-File', $harnessCli, 'run', $lua, '-Launch', '-StartupSettleSeconds', '30', '-FailOnLogErrors', '-ResponseTimeoutSeconds', '120', '-Timeout', '1200', '-GameRoot', $game, '-Fixtures', $fixtures, '-Results', $phaseRoot) (Join-Path $phaseRoot 'harness.log')
        }
        finally { Wait-CandidateExit }
        $reports = @(Get-ChildItem -LiteralPath $phaseRoot -Filter 'report.json' -Recurse -File)
        if ($reports.Count -ne 1) { throw "Expected one harness report for $phase" }
        $diagnostics = Join-Path $game 'mods\ReduxBetterAA\diagnostics'
        $validationArgs = @('-X', 'utf8', (Join-Path $source 'tests\Release\validate-ingame.py'), '--report', $reports[0].FullName, '--diagnostics', $diagnostics, '--assembly', $assembly, '--phase', $phase, '--output', (Join-Path $phaseRoot 'validation.json'))
        if ($phase -eq 'native') { $validationArgs += '--expect-fsr-runtime' }
        Invoke-Checked python $validationArgs (Join-Path $phaseRoot 'validation.log')
        Move-Item -LiteralPath $diagnostics -Destination (Join-Path $phaseRoot 'diagnostics')
        $summary.phases += @{ phase = $phase; validation = (Get-Content -LiteralPath (Join-Path $phaseRoot 'validation.json') -Raw | ConvertFrom-Json) }
    }
    $summary.status = 'passed'
}
catch { $failure = $_; $summary.error = $_.Exception.Message }
finally {
    try { Wait-CandidateExit }
    catch { $summary.status = 'failed'; $summary.shutdownError = $_.Exception.Message; $failure = $_ }
    try {
        if ($profileMoved) {
            if (Get-Process KSP2_x64 -ErrorAction SilentlyContinue) { throw "KSP2 is still running; original profile is safe at $profileBackup" }
            if (Test-Path -LiteralPath $paths.KSP2_PROFILE) { Move-Item -LiteralPath $paths.KSP2_PROFILE -Destination $profileEvidence }
            Move-Item -LiteralPath $profileBackup -Destination $paths.KSP2_PROFILE
            $summary.profileEvidence = $profileEvidence
        }
    }
    catch { $summary.status = 'failed'; $summary.restoreError = $_.Exception.Message; $failure = $_ }
    try {
        [void](Assert-ReleaseSource $repo $commit)
        if (Test-Path -LiteralPath (Join-Path $source '.git')) { [void](Assert-ReleaseSource $source $commit) }
        [void](Assert-ReleaseSource $paths.TEST_HARNESS $harnessCommit)
        if (Test-Path -LiteralPath (Join-Path $harness '.git')) { [void](Assert-ReleaseSource $harness $harnessCommit) }
    }
    catch { $summary.status = 'failed'; $summary.sourceError = $_.Exception.Message; $failure = $_ }
    $summary.endedUtc = [DateTime]::UtcNow.ToString('o')
    $summary | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $run 'summary.json') -Encoding utf8NoBOM
    Write-Host "Candidate result: $run\summary.json"
}
if ($failure) { throw $failure }
