[CmdletBinding()]
param([Parameter(Mandatory)] [string] $Directory, [string] $Ffmpeg = 'ffmpeg')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Directory = (Resolve-Path -LiteralPath $Directory).Path
$images = @(Get-ChildItem -LiteralPath $Directory -Recurse -Filter '*.png' -File | Sort-Object FullName)
if ($images.Count -eq 0) { throw 'No captured PNGs found.' }
$html = [Text.StringBuilder]::new()
[void]$html.AppendLine('<!doctype html><html lang="en"><meta charset="utf-8"><title>Better AA visual review</title><style>body{font:16px system-ui;background:#161b22;color:#eee;margin:2rem}section{margin-bottom:3rem}img,video{max-width:100%;height:auto}a{color:#85c9ff}summary{cursor:pointer}</style><h1>Better AA visual review</h1><p>Manual review required. PNGs are lossless. Videos are sampled frame sequences, not real-time or performance measurements. Match each label to its report snapshot before comparing.</p>')
$ffmpegCommand = Get-Command $Ffmpeg -ErrorAction SilentlyContinue
foreach ($folder in ($images | Group-Object DirectoryName)) {
    $panGroups = @($folder.Group | Where-Object BaseName -match 'pan-.+-\d{4}$' |
        Group-Object { $_.BaseName -replace '-\d{4}$', '' })
    foreach ($group in $panGroups) {
        if (-not $ffmpegCommand) { continue }
        $first = $group.Group | Sort-Object Name | Select-Object -First 1
        $prefix = $first.BaseName -replace '\d{4}$', ''
        $video = Join-Path $first.DirectoryName ($prefix.TrimEnd('-') + '.mp4')
        & $ffmpegCommand.Source -hide_banner -loglevel error -y -framerate 30 -start_number 1 `
            -i (Join-Path $first.DirectoryName ($prefix + '%04d.png')) -an -c:v libx264 -crf 16 -pix_fmt yuv420p `
            -vf 'pad=ceil(iw/2)*2:ceil(ih/2)*2' $video
        if ($LASTEXITCODE -ne 0) { throw "Video encoding failed: $video" }
    }
}
foreach ($video in Get-ChildItem -LiteralPath $Directory -Recurse -Filter '*.mp4' -File | Sort-Object FullName) {
    $relative = [IO.Path]::GetRelativePath($Directory, $video.FullName).Replace('\', '/')
    $encoded = [Net.WebUtility]::HtmlEncode($relative)
    [void]$html.AppendLine("<section><h2>$encoded</h2><video controls preload='metadata' src='$encoded'></video></section>")
}
[void]$html.AppendLine('<details open><summary>Still images and individual sequence frames</summary>')
foreach ($file in $images) {
    $relative = [IO.Path]::GetRelativePath($Directory, $file.FullName).Replace('\', '/')
    $encoded = [Net.WebUtility]::HtmlEncode($relative)
    [void]$html.AppendLine("<section><h3>$encoded</h3><a href='$encoded'><img loading='lazy' src='$encoded' alt='$encoded'></a></section>")
}
[void]$html.AppendLine('</details></html>')
$html.ToString() | Set-Content -LiteralPath (Join-Path $Directory 'index.html') -Encoding utf8
if (-not $ffmpegCommand -and @($images | Where-Object BaseName -match 'pan-.+-\d{4}$').Count -gt 0) {
    Write-Warning 'FFmpeg unavailable; PNG sequences and gallery were retained. Install FFmpeg and rerun this script to encode videos.'
}
