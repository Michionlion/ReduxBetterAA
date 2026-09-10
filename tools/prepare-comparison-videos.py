"""Verify consecutive captures and create labelled, browser-playable review copies."""
import argparse
import csv
import hashlib
import html
import json
import subprocess
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

import numpy as np
from PIL import Image

PAIRS = {
    "taa-dlaa": ("Custom TAA", "NVIDIA DLAA"),
    "taa-none": ("Custom TAA", "None"),
    "taa-supersampling": ("Custom TAA", "Supersampling 200%"),
    "dlaa-supersampling": ("NVIDIA DLAA", "Supersampling 200%"),
}
SAMPLES = (0, 300, 839)


def run(*args):
    result = subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=True)
    if result.stderr.strip():
        raise RuntimeError(result.stderr.decode(errors="replace"))
    return result.stdout


def probe(path):
    data = json.loads(run("ffprobe", "-v", "error", "-threads", "8", "-select_streams", "v:0",
                          "-count_frames", "-show_entries",
                          "stream=width,height,nb_read_frames,r_frame_rate,pix_fmt:format=duration",
                          "-of", "json", str(path)))
    stream = data["streams"][0]
    assert int(stream["nb_read_frames"]) == 840, data
    assert stream["r_frame_rate"] == "60/1", data
    assert abs(float(data["format"]["duration"]) - 14) < .02, data
    return data


def sha256(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def prepare(folder, slug, labels):
    master = folder / (slug + ".lossless.mkv")
    print(f"Checking {slug}", flush=True)
    master_info = probe(master)
    stream = master_info["streams"][0]
    width, height = stream["width"], stream["height"]
    assert (width, height) == (2560, 1440), stream
    with Path(str(master) + ".frames.csv").open() as source:
        frames = list(csv.DictReader(source))
    assert len(frames) == 840
    for column in ("video_frame", "unity_frame", "comparison_frame"):
        values = [int(row[column]) for row in frames]
        assert values == list(range(values[0], values[0] + 840)), column
    assert int(frames[0]["video_frame"]) == 0
    assert len({row["history_resets"] for row in frames}) == 1
    yaw = [float(row["yaw_degrees"]) for row in frames]
    for n, actual in enumerate(yaw):
        t = n / 60
        expected = -10 if t < 2 or t >= 12 else (-10 + (t - 2) * 4 if t < 7 else 10 - (t - 7) * 4)
        assert abs(actual - expected) < .000001, (n, actual, expected)

    # Decoded RGB must match the PNG made from the same Unity readback exactly.
    selected = "+".join(f"eq(n\\,{n})" for n in SAMPLES)
    decoded = run("ffmpeg", "-v", "error", "-threads", "8", "-i", str(master), "-vf", "select=" + selected,
                  "-fps_mode", "passthrough", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1")
    arrays = np.frombuffer(decoded, np.uint8).reshape(len(SAMPLES), height, width, 3)
    for n, actual in zip(SAMPLES, arrays):
        with Image.open(str(master) + f".{n:04}.png") as source:
            reference = np.asarray(source.convert("RGB"))
        assert np.array_equal(actual, reference), f"{slug}: RGB mismatch at frame {n}"
        assert np.all(actual[:, width // 2] == 255), "Missing white divider"
        assert np.all(actual[:, [width // 2 - 1, width // 2 + 1]] == 0), "Missing divider borders"
    motion_mae = float(np.abs(arrays[0].astype(float) - arrays[1]).mean())
    assert motion_mae > 1, "Recording appears stationary"

    preview = folder / (slug + ".mp4")
    # Add a label band above the scene; every scene pixel retains its position/size.
    filters = ["pad=iw:ih+64:0:64:color=0x11161e"]
    for label, x in zip(labels, ("32", "w/2+32")):
        filters.append(f"drawtext=fontfile='C\\:/Windows/Fonts/segoeui.ttf':expansion=none:text='{label}':"
                       f"fontsize=30:fontcolor=white:x={x}:y=12")
    filters += ["scale=in_range=full:out_range=tv:out_color_matrix=bt709", "format=yuv420p"]
    print(f"Encoding {slug}", flush=True)
    run("ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-threads", "8", "-i", str(master),
        "-vf", ",".join(filters), "-an", "-c:v", "libx264", "-crf", "10", "-preset", "medium",
        "-threads", "8", "-colorspace", "bt709", "-color_primaries", "bt709",
        "-color_trc", "iec61966-2-1", "-color_range", "tv", "-movflags", "+faststart", str(preview))
    preview_info = probe(preview)
    assert (preview_info["streams"][0]["width"], preview_info["streams"][0]["height"]) == (2560, 1504)
    # Poster is a real encoded frame, including labels.
    run("ffmpeg", "-v", "error", "-y", "-ss", "5", "-i", str(preview), "-frames:v", "1",
        str(folder / (slug + ".poster.png")))
    print(f"Verified {slug}: 840 frames, three exact RGB matches", flush=True)
    return {"clip": slug, "left": labels[0], "right": labels[1], "frames": 840,
            "fps": 60, "duration_seconds": 14, "lossless": master_info, "preview": preview_info,
            "exact_rgb_frames": list(SAMPLES), "pan_vs_start_rgb_mae": motion_mae,
            "history_resets_during_clip": 0, "consecutive_frames": True,
            "master_sha256": sha256(master), "preview_sha256": sha256(preview),
            "master_bytes": master.stat().st_size, "preview_bytes": preview.stat().st_size}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run", type=Path)
    args = parser.parse_args()
    folder = args.run / "videos"
    reports = list((args.run / "harness").glob("*/comparison-videos/report.json"))
    assert len(reports) == 1, reports
    report = json.loads(reports[0].read_text(encoding="utf-8-sig"))
    assert report["status"] == "passed", report["status"]
    settings = report["values"]["settings"]
    sharpness = settings["_sharpnessEntry"]
    stability = settings["_taaStabilityEntry"]
    preset = settings["_dlaaPresetEntry"]
    pairs = {key: tuple(x + f" {preset}" if x == "NVIDIA DLAA" else x for x in labels)
             for key, labels in PAIRS.items()}
    with ThreadPoolExecutor(max_workers=2) as workers:
        results = list(workers.map(lambda item: prepare(folder, *item), pairs.items()))
    (folder / "validation.json").write_text(json.dumps({"settings": settings, "clips": results}, indent=2), encoding="utf-8")
    cards = []
    for result in results:
        slug = result["clip"]
        title = html.escape(result["left"] + " / " + result["right"])
        size = result["master_bytes"] / 1024**3
        cards.append(f'''<article><h2>{title}</h2><div class="viewport"><video controls loop playsinline preload="none"
          poster="{slug}.poster.png" src="{slug}.mp4"></video></div><p><a href="{slug}.mp4" download>MP4 preview</a>
          <a href="{slug}.lossless.mkv" download>Lossless RGB master ({size:.2f} GiB)</a>
          <a href="{slug}.lossless.mkv.frames.csv">Frame log</a></p></article>''')
    page = '''<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Live AA comparison videos</title><style>
:root{color-scheme:dark;font:16px/1.55 system-ui;background:#0c1018;color:#e6edf7}body{margin:0 auto;max-width:1500px;padding:32px}
h1{font-size:32px;margin:0}h2{font-size:21px}p{color:#b7c5d9}a{color:#8dd4ff;margin-right:24px}button,select{font:inherit;background:#233044;color:white;border:1px solid #5a7090;border-radius:6px;padding:6px 12px;margin-right:12px}
.toolbar{position:sticky;top:0;z-index:1;background:#0c1018ee;padding:14px 0;display:flex;gap:6px;flex-wrap:wrap;align-items:center}
article{margin:24px 0 44px}.viewport{overflow:auto;border:1px solid #36475d;border-radius:8px;background:black}
video{display:block;width:100%;height:auto}body.native video{width:2560px;max-width:none}small{color:#b7c5d9}
</style><h1>Live AA comparison</h1>
<p>Four matched camera passes. Each clip: 14 seconds, 840 consecutive frames at 60 fps.<br>
Two seconds still &rarr; five-second pan &rarr; five-second reverse &rarr; two-second settle.</p>
<p>Native scene: 2560 &times; 1440. Supersampling: 200% per axis (5120 &times; 2880, 4&times; pixels).
DLAA preset PRESET; sharpness SHARPNESS; TAA stability STABILITY. The divider marks the left/right modes.</p>
<div class="toolbar"><button id="play">Play all from start</button><button id="pause">Pause all</button>
<label>Speed <select id="speed"><option value="1">1&times;</option><option value="0.5">0.5&times;</option><option value="0.25">0.25&times;</option></select></label>
<label><input type="checkbox" id="native"> Native pixel size (scroll to inspect)</label></div>
<p>MP4s retain the scene resolution and add a label band above it. For pixel-level judgment, use the
lossless RGB masters in a compatible player; MP4 compression and browser scaling can soften fine detail.</p>
CARDS
<small>Physics paused; camera motion rendered live at a fixed 1/60-second step. Capture can run slower than playback;
these clips do not measure performance. Stock cloud/exposure effects can share state between comparison passes.
<a href="validation.json">Capture validation</a></small>
<script>const videos=[...document.querySelectorAll('video')];
document.querySelector('#play').onclick=async()=>{videos.forEach(v=>{v.pause();v.currentTime=0;v.load()});await Promise.all(videos.map(v=>new Promise(r=>{if(v.readyState>=3)r();else v.addEventListener('canplay',r,{once:true})})));videos.forEach(v=>{v.playbackRate=+document.querySelector('#speed').value;v.play().catch(()=>{})})};
document.querySelector('#pause').onclick=()=>videos.forEach(v=>v.pause());
document.querySelector('#speed').onchange=e=>videos.forEach(v=>v.playbackRate=+e.target.value);
document.querySelector('#native').onchange=e=>document.body.classList.toggle('native',e.target.checked);
</script></html>'''
    page = page.replace("PRESET", html.escape(preset)).replace("SHARPNESS", f"{sharpness:.2f}")
    page = page.replace("STABILITY", f"{stability:.2f}").replace("CARDS", "\n".join(cards))
    (folder / "index.html").write_text(page, encoding="utf-8")
    print(f"Review: {folder / 'index.html'}", flush=True)


if __name__ == "__main__":
    main()
