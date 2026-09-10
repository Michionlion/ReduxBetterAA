"""Validate pre-UI RGBA16F captures and compare pixels without inventing ground truth.

Requires numpy and Pillow. Temporal variation is meaningful only for consecutive
stationary frames; sampled pans are matched poses, not motion-compensated video.
"""
import argparse
import hashlib
import itertools
import json
from pathlib import Path
import numpy as np
from PIL import Image


def load(root, frame, kind):
    path = root / frame[kind]
    expected = frame['width'] * frame['height'] * 4
    data = np.fromfile(path, dtype='<f2')
    if data.size != expected:
        raise ValueError(f'Wrong byte count: {path}')
    if not np.isfinite(data).all():
        raise ValueError(f'Nonfinite pixels: {path}')
    data = data.reshape(frame['height'], frame['width'], 4)[::-1, :, :3].astype(np.float32)
    return data


def metrics(a, b):
    delta = np.abs(a - b)
    mse = float(np.mean((a - b) ** 2, dtype=np.float64))
    return dict(mae=float(delta.mean(dtype=np.float64)), rmse=mse ** .5,
                p99=float(np.percentile(delta, 99)), maximum=float(delta.max()),
                changed_fraction=float(np.mean(np.max(delta, axis=2) > 1 / 1024)),
                psnr_peak1_db=None if mse == 0 else float(-10 * np.log10(mse)))


def preview(path, rgb):
    # Linear-to-sRGB for viewable PNGs; metrics always use unclipped linear data.
    x = np.clip(rgb, 0, 1)
    x = np.where(x <= .0031308, 12.92 * x, 1.055 * x ** (1 / 2.4) - .055)
    Image.fromarray(np.uint8(np.clip(x * 255 + .5, 0, 255))).save(path)


def analyze(run):
    root = run / 'pixels'
    groups, hashes = {}, {}
    for path in sorted(root.glob('*.json')):
        f = json.loads(path.read_text())
        expected = {'custom': 'Custom TAA', 'fsr': 'FSR2 Native AA', 'dlaa': 'NVIDIA DLAA', 'reference': 'Off'}[f['label'].split('-')[0]]
        requested = {'Custom TAA': 'CustomTaa', 'FSR2 Native AA': 'AmdFsr2', 'NVIDIA DLAA': 'NvidiaDlaa', 'Off': 'Off'}[expected]
        if (expected != 'Off' and not f['active']) or f['selected'] != expected or f['requested'] != requested or f['sharpness'] != 0:
            raise ValueError(f'Invalid backend/settings: {path}')
        groups.setdefault(f['label'], []).append(f)
    if not groups:
        raise ValueError('No pixel captures')
    if len({(f['width'], f['height']) for label, fs in groups.items() if not label.startswith('reference') for f in fs}) != 1:
        raise ValueError('Mismatched dimensions')
    result = dict(note='Linear RGB differences, not ground-truth quality rankings. Lower temporal variation can also mean blur. '
                  'Clouds/lighting and jitter phase can differ between modes. Pan captures are sampled poses.',
                  sequences={}, pairwise={}, hashes=hashes, profiles={}, reference_errors={})
    means = {}
    for label, frames in groups.items():
        frames.sort(key=lambda f: f['index'])
        if len(frames) > 1 and any(b['frame'] != a['frame'] + 1 for a, b in zip(frames, frames[1:])):
            raise ValueError(f'Nonconsecutive rendered frames: {label}')
        total = square = previous = None
        diffs, effects = [], []
        for f in frames:
            a, b = load(root, f, 'input'), load(root, f, 'output')
            effects.append(metrics(a, b)['mae'])
            if previous is not None:
                diffs.append(float(np.abs(b - previous).mean(dtype=np.float64)))
            if total is None:
                total = np.zeros_like(b, dtype=np.float64); square = total.copy()
            total += b; square += b.astype(np.float64) ** 2; previous = b
            for kind in ('input', 'output'):
                with (root / f[kind]).open('rb') as stream:
                    hashes[f[kind]] = hashlib.file_digest(stream, 'sha256').hexdigest()
        mean = (total / len(frames)).astype(np.float32)
        means[label] = mean
        variance = np.maximum(square / len(frames) - (total / len(frames)) ** 2, 0)
        result['sequences'][label] = dict(frames=len(frames), width=mean.shape[1], height=mean.shape[0],
            temporal_std_mean=float(np.sqrt(variance).mean()), adjacent_mae=None if not diffs else float(np.mean(diffs)),
            input_output_mae=float(np.mean(effects)), first_frame=frames[0]['frame'], last_frame=frames[-1]['frame'])
        if len(frames) > 1:
            preview(run / (label + '.png'), mean)
            preview(run / (label + '-variation-x16.png'), np.sqrt(variance) * 16)
    if 'reference-stationary' in means:
        ref = means['reference-stationary']
        h, w = means['custom-stationary'].shape[:2]
        if ref.shape[:2] != (h * 2, w * 2):
            raise ValueError('Spatial reference must have exactly twice the native width and height')
        ref = ref.reshape(h, 2, w, 2, 3).mean(axis=(1, 3))
        preview(run / 'reference-downsampled.png', ref)
        result['reference_note'] = 'Off at 200% width/height, 4 settled frames averaged, 2x2 linear box downsample. '
        result['reference_note'] += 'Spatial approximation, not exact ground truth: render-scale LOD/postprocessing and time can differ.'
        for backend in ('custom', 'fsr', 'dlaa'):
            value = means[backend + '-stationary']
            result['reference_errors'][backend] = metrics(value, ref)
            preview(run / (backend + '-reference-error-x8.png'), np.abs(value - ref) * 8)
            # Fixed geometric ROI shared by every mode, independent of candidate values.
            roi = (slice(int(h * .15), int(h * .75)), slice(int(w * .35), int(w * .65)))
            result['reference_errors'][backend]['structure_roi'] = metrics(value[roi], ref[roi])
            preview(run / (backend + '-structure-crop.png'), value[roi])
        preview(run / 'reference-structure-crop.png', ref[roi])
    for scene in ('stationary', 'settle'):
        for a, b in itertools.combinations(('custom', 'fsr', 'dlaa'), 2):
            key = f'{a}-vs-{b}-{scene}'
            result['pairwise'][key] = metrics(means[f'{a}-{scene}'], means[f'{b}-{scene}'])
            preview(run / (key + '-difference-x8.png'), np.abs(means[f'{a}-{scene}'] - means[f'{b}-{scene}']) * 8)
    reports = list((run / 'harness').rglob('report.json'))
    for path in reports:
        report = json.loads(path.read_text(encoding='utf-8-sig'))
        result['environment'] = report.get('environment')
        result['harness_values'] = report.get('values', report.get('artifacts'))
        for backend in ('custom', 'fsr', 'dlaa'):
            profiles = [v for k, v in report.get('values', {}).items() if k.startswith(backend + '-profile-')]
            if len(profiles) != 3 or any(p['State'] != 'Complete' or p['Samples'] != 240 for p in profiles):
                raise ValueError(f'Missing/invalid timing windows: {backend}')
            result['profiles'][backend] = dict(
                frame_ms_runs=[p['AverageCpuFrameMilliseconds'] for p in profiles],
                frame_ms_median=float(np.median([p['AverageCpuFrameMilliseconds'] for p in profiles])),
                resolve_cpu_ms_median=float(np.median([p['AverageResolveCpuMilliseconds'] for p in profiles])),
                gpu_ms_median=None if any(p['GpuSamples'] == 0 for p in profiles) else
                    float(np.median([p['AverageGpuFrameMilliseconds'] for p in profiles])))
    (run / 'analysis.json').write_text(json.dumps(result, indent=2, allow_nan=False))
    pngs = sorted(run.glob('*.png'))
    html = '<!doctype html><meta charset="utf-8"><title>Native AA pixel comparison</title>'
    html += '<style>body{background:#15191f;color:#eee;font:16px system-ui;margin:2em}img{max-width:100%}figure{margin:1em 0} .grid{display:grid;grid-template-columns:1fr 1fr;gap:20px}</style>'
    html += '<h1>Native AA pixel comparison</h1><p>' + result['note'] + '</p><p><a href="analysis.json">Metrics and SHA-256 manifest</a></p><div class="grid">'
    for path in pngs:
        html += f'<figure><figcaption>{path.stem}</figcaption><a href="{path.name}"><img loading="lazy" src="{path.name}"></a></figure>'
    html += '</div>'
    (run / 'index.html').write_text(html, encoding='utf-8')
    print(json.dumps({k: v for k, v in result['sequences'].items() if not '-pan-' in k}, indent=2))
    print(run / 'index.html')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('run', type=Path)
    analyze(parser.parse_args().run.resolve())
