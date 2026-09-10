[CmdletBinding()]
param([Parameter(Mandatory)] [string] $Zip)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Zip).Path)
try {
    $entry = $archive.GetEntry('manifest.json')
    if (-not $entry) { throw 'Missing report manifest.' }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($manifest.status -notin @('complete', 'partial')) { throw 'Report is not finalized.' }
    if ($manifest.schemaVersion -ge 2 -and $manifest.inputStage -notin
        @('before-temporal-resolve', 'after-ppv2', 'unavailable')) { throw 'Invalid input capture stage.' }
    if ($manifest.status -eq 'complete' -and
        ($manifest.inputFrame -lt 0 -or $manifest.inputFrame -ne $manifest.outputFrame -or
         $manifest.outputFrame -ne $manifest.screenshotFrame)) { throw 'Complete report has inconsistent frames.' }
    foreach ($file in $manifest.files) {
        $entry = $archive.GetEntry($file.path)
        if (-not $entry -or $entry.Length -ne $file.bytes) { throw "Missing/truncated file: $($file.path)" }
        $stream = $entry.Open()
        $hash = [Security.Cryptography.SHA256]::Create()
        try { $actual = [Convert]::ToHexString($hash.ComputeHash($stream)).ToLowerInvariant() }
        finally { $hash.Dispose(); $stream.Dispose() }
        if ($actual -ne $file.sha256) { throw "Hash mismatch: $($file.path)" }
    }
    foreach ($buffer in $manifest.buffers) {
        if ($buffer.status -ne 'captured') { continue }
        foreach ($path in @($buffer.rawFile, $buffer.previewFile)) {
            if (-not $path -or -not $archive.GetEntry($path)) { throw "Missing image for $($buffer.name)" }
        }
        if ($manifest.status -eq 'complete' -and $buffer.frame -ne $manifest.outputFrame) {
            throw "Buffer frame differs: $($buffer.name)"
        }
    }
    [pscustomobject]@{ Zip = $Zip; Status = $manifest.status; Frame = $manifest.outputFrame;
        Files = $manifest.files.Count; Captured = @($manifest.buffers | Where-Object status -eq captured).Count;
        Unavailable = @($manifest.buffers | Where-Object status -eq unavailable).Count }
} finally { $archive.Dispose() }
