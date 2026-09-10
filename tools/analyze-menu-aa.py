"""Analyze menu captures from Run-QualityTests.ps1 -Menu or -MenuIsolation.

Requires numpy and Pillow. Reference/error metrics are diagnostics, not ground truth.
"""
import argparse
import hashlib
import html
import json
from pathlib import Path

import numpy as np
from PIL import Image


def srgb(x):
    x = np.maximum(x, 0)
    return np.clip(np.where(x <= .0031308, 12.92*x, 1.055*x**(1/2.4)-.055), 0, 1)


def load_frame(path, metadata):
    image = np.fromfile(path, dtype='<f2').reshape(metadata['height'], metadata['width'], 4).astype(np.float32)
    if not np.isfinite(image).all():
        raise ValueError(f'Nonfinite pixels: {path}')
    return image[::-1, :, :3]


def analyze(root):
    pixels = root / 'pixels'
    review = root / 'menu-review'
    review.mkdir(exist_ok=True)
    groups = {}
    for path in sorted(pixels.glob('*.json')):
        metadata = json.loads(path.read_text(encoding='utf-8-sig'))
        if 'input' in metadata and 'output' in metadata:
            groups.setdefault(metadata['label'], []).append(metadata)
    if not groups:
        raise ValueError('No captured frames')
    reference = None
    if 'reference' in groups:
        m = groups['reference'][0]
        reference = load_frame(pixels/m['output'], m)
        reference = srgb(reference.reshape(m['height']//2, 2, m['width']//2, 2, 3).mean(axis=(1, 3)))
    results, manifest, sections = {}, [], []
    for label, frames in groups.items():
        previous = None
        changes, input_changes, differences, jitter = [], [], [], []
        previous_input = None
        diff_sum = None
        for index, m in enumerate(frames):
            inp, output = (load_frame(pixels/m[k], m) for k in ('input', 'output'))
            differences.append(float(np.abs(inp-output).mean()))
            a, b = srgb(inp), srgb(output)
            jitter.append([m['jitterX'], m['jitterY']])
            if previous is not None:
                diff = np.abs(b-previous).mean(axis=2)
                diff_sum = diff if diff_sum is None else diff_sum+diff
                changes.append(float(diff.mean()))
                input_changes.append(float(np.abs(a-previous_input).mean()))
            previous, previous_input = b, a
            for k in ('input','output'):
                file = pixels/m[k]
                manifest.append({'path': str(file.relative_to(root)), 'bytes':file.stat().st_size,
                                 'sha256':hashlib.file_digest(file.open('rb'), 'sha256').hexdigest()})
            if index == 0:
                Image.fromarray((b*255).astype('uint8')).resize((1280,720)).save(review/(label+'.png'))
                if b.shape[0] == 1440:
                    crop = b[220:580,1550:2110]
                    Image.fromarray((crop*255).astype('uint8')).resize((1120,720),Image.Resampling.NEAREST).save(review/(label+'-detail.png'))
                reference_error = None
                if reference is not None and b.shape == reference.shape and label in {
                    'Off', 'FXAAHigh', 'SMAA', 'TAA', 'NVIDIADLAA', 'FSR2NativeAA'
                }:
                    # Fixed vessel ROI in the recorded 1440p menu. Excludes the HDR sun.
                    sl = (slice(200,1120),slice(950,2150))
                    reference_error = float(np.sqrt(np.mean((b[sl]-reference[sl])**2)))
        results[label] = {'frames':len(frames), 'selected':frames[0]['selected'],
            'dimensions':[frames[0]['width'],frames[0]['height']], 'finite':True,
            'input_output_linear_mae':float(np.mean(differences)), 'jitter_normalized':jitter,
            'mean_output_frame_change_srgb':float(np.mean(changes)) if changes else None,
            'mean_input_frame_change_srgb':float(np.mean(input_changes)) if input_changes else None,
            'vessel_reference_rmse_srgb':reference_error}
        if diff_sum is not None:
            diff_sum /= len(changes)
            Image.fromarray((np.clip(diff_sum*10,0,1)*255).astype('uint8')).resize((1280,720)).save(review/(label+'-change.png'))
            if diff_sum.shape == (1440,2560):
                results[label]['planet_frame_change_srgb'] = float(diff_sum[1000:1400,1700:2500].mean())
        name=html.escape(label)
        sections.append(f'<section><h2>{name}</h2><img src="{name}.png"><p>{html.escape(json.dumps(results[label],indent=2))}</p></section>')
    (review/'metrics.json').write_text(json.dumps(results,indent=2))
    (review/'manifest.json').write_text(json.dumps(manifest,indent=2))
    (review/'index.html').write_text('<!doctype html><meta charset="utf-8"><title>Main menu AA review</title><style>body{background:#15181e;color:#e6e7eb;font:16px system-ui;max-width:1300px;margin:30px auto}img{max-width:100%}p{white-space:pre-wrap}section{margin:40px 0}</style><h1>Main menu AA review</h1><p>Same-frame pre-UI inputs/outputs. Previews use clipped sRGB conversion of linear HDR. Change images amplify consecutive-frame differences 10x. The 2x spatial reference and error metrics are diagnostic, not a ground-truth quality ranking; lighting, sampling and filtering can differ. Render timings exclude readback. Raw files are hashed in manifest.json.</p>'+''.join(sections))
    print(review)
    for label,r in results.items():
        print(label, 'frames',r['frames'],'change',r['mean_output_frame_change_srgb'],'reference',r['vessel_reference_rmse_srgb'])


if __name__ == '__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('run',type=Path)
    analyze(parser.parse_args().run.resolve())
