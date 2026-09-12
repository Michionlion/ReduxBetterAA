"""Analyze supplied debug recordings without interpreting encoded colors as raw vectors."""
import argparse
import hashlib
import json
import subprocess
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw


def analyze(path, output):
    metadata = json.loads(subprocess.check_output([
        "ffprobe", "-v", "error", "-select_streams", "v:0", "-show_streams", "-of", "json", str(path)
    ]))["streams"][0]
    fps = float(__import__("fractions").Fraction(metadata["avg_frame_rate"]))
    # Decode every displayed frame. Spatial subsampling keeps working memory small;
    # fixed terrain patches exclude the diagnostic panel, vessel, plume and HUD.
    raw = subprocess.check_output(["ffmpeg", "-v", "error", "-i", str(path),
        "-vf", "scale=320:180:flags=neighbor", "-fps_mode", "passthrough", "-f", "rawvideo", "-pix_fmt", "rgb24", "-"])
    frames = np.frombuffer(raw, np.uint8).reshape(-1, 180, 320, 3)
    timestamp_data=json.loads(subprocess.check_output([
        "ffprobe","-v","error","-select_streams","v:0","-show_entries",
        "frame=best_effort_timestamp_time","-of","json",str(path)]))["frames"]
    timestamps=np.array([float(f["best_effort_timestamp_time"]) for f in timestamp_data])
    if len(timestamps)!=len(frames) or len(frames)<24 or np.any(np.diff(timestamps)<=0):
        raise ValueError("Missing, insufficient or nonmonotonic video frames")
    timing_error=float(np.max(np.abs(np.diff(timestamps)-1/fps)))
    if timing_error>1e-4: raise ValueError("Variable frame spacing: resample by timestamps before estimating frequency")
    rois = {"terrain_left": (80, 90, 130, 125), "terrain_right": (210, 65, 285, 120),
            "terrain_center": (155, 125, 220, 160)}
    signals = {"timestamps":timestamps}
    result = dict(file=path.name, sha256=hashlib.sha256(path.read_bytes()).hexdigest(),
                  width=metadata["width"], height=metadata["height"], fps=fps, frames=len(frames),
                  maximum_timestamp_spacing_error_seconds=timing_error,rois={})
    plot = Image.new("RGB", (1200, 220 * len(rois) + 60), "white")
    draw = ImageDraw.Draw(plot)
    draw.text((15, 10), path.name + " | encoded RGB changes; not raw motion / physics frequency", fill="black")
    for j, (name, (x0, y0, x1, y1)) in enumerate(rois.items()):
        region = frames[:, y0:y1, x0:x1].astype(np.float32)
        signal = region.mean(axis=(1, 2))
        signals[name] = signal
        change = np.abs(np.diff(region, axis=0)).mean(axis=(1, 2, 3)) / 255
        # Remove slowly changing direction/background; report display-space energy.
        pad = np.pad(signal, ((6, 6), (0, 0)), mode="edge")
        trend = np.stack([np.convolve(pad[:, c], np.ones(13)/13, "valid") for c in range(3)], axis=1)
        high = signal - trend
        window = np.hanning(len(high))
        spectrum = (np.abs(np.fft.rfft(high * window[:, None], axis=0)) ** 2).sum(axis=1)
        freqs = np.fft.rfftfreq(len(high), 1/fps)
        spectrum[freqs < 5] = 0
        peaks = np.argsort(spectrum)[-3:][::-1]
        result["rois"][name] = dict(bounds_fullres=[int(x0*metadata["width"]/320),int(y0*metadata["height"]/180),int(x1*metadata["width"]/320),int(y1*metadata["height"]/180)],
            mean_frame_rgb_change=float(change.mean()), p95_frame_rgb_change=float(np.quantile(change,.95)),
            mean_rgb_highpass_rms=float(np.sqrt(np.mean(high**2))/255),
            strongest_encoded_frequencies_hz=freqs[peaks].tolist())
        top = 60+j*220
        draw.text((15, top), name+" | mean RGB (0..255), each encoded frame", fill="black")
        for c, color in enumerate(("red", "green", "blue")):
            pts = [(20+int(i*1160/max(1,len(signal)-1)), top+195-int(float(v)*.65)) for i,v in enumerate(signal[:,c])]
            draw.line(pts, fill=color, width=1)
    plot.save(output/(path.stem+"-timeseries.png"))
    np.savez_compressed(output/(path.stem+"-signals.npz"), **signals)
    # Contact sheet at 120 Hz makes alternating frames directly inspectable.
    start = min(60, len(frames)-24)
    sheet = Image.new("RGB", (6*320, 4*202), "#202020")
    d = ImageDraw.Draw(sheet)
    for i in range(24):
        x, y = (i%6)*320, (i//6)*202
        sheet.paste(Image.fromarray(frames[start+i]), (x,y))
        d.text((x+5,y+182), f"frame {start+i}, {(start+i)/fps:.4f} s", fill="white")
    sheet.save(output/(path.stem+"-consecutive.png"))
    return result


if __name__ == "__main__":
    p=argparse.ArgumentParser(); p.add_argument("input",type=Path); p.add_argument("output",type=Path)
    a=p.parse_args(); a.output.mkdir(parents=True,exist_ok=True)
    results=[analyze(path,a.output) for path in sorted(a.input.glob("motion-*.mp4"))]
    (a.output/"video-analysis.json").write_text(json.dumps(results,indent=2))
    print(json.dumps(results,indent=2))
