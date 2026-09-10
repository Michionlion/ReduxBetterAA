"""Compare two validated native-AA runs against one fixed spatial reference."""
import argparse
import importlib.util
import json
from pathlib import Path
import numpy as np

spec = importlib.util.spec_from_file_location('aa', Path(__file__).with_name('analyze-aa-quality.py'))
aa = importlib.util.module_from_spec(spec)
spec.loader.exec_module(aa)


def mean_output(run, label):
    frames = [json.loads(p.read_text()) for p in sorted((run / 'pixels').glob(label + '-[0-9][0-9][0-9].json'))]
    if not frames:
        raise ValueError(f'Missing {label} in {run}')
    total = None
    for f in frames:
        x = aa.load(run / 'pixels', f, 'output')
        if total is None:
            total = np.zeros_like(x, dtype=np.float64)
        total += x
    return (total / len(frames)).astype(np.float32)


def compare(baseline, candidate, output):
    analyses = [json.loads((r / 'analysis.json').read_text()) for r in (baseline, candidate)]
    manifests = [json.loads((r / 'run.json').read_text(encoding='utf-8-sig')) for r in (baseline, candidate)]
    if manifests[0]['scriptSha256'] != manifests[1]['scriptSha256']:
        raise ValueError('Capture scripts differ; these are not controlled runs')
    for key in ('unityVersion', 'reduxVersion', 'graphicsDevice'):
        if analyses[0]['environment'][key] != analyses[1]['environment'][key]:
            raise ValueError(f'Environment mismatch: {key}')
    output.mkdir(parents=True, exist_ok=True)
    reference = mean_output(baseline, 'reference-stationary')
    h, w = reference.shape[0] // 2, reference.shape[1] // 2
    reference = reference.reshape(h, 2, w, 2, 3).mean(axis=(1, 3))
    roi = (slice(int(h * .15), int(h * .75)), slice(int(w * .35), int(w * .65)))
    aa.preview(output / 'reference-crop.png', reference[roi])
    results = dict(baseline=str(baseline), candidate=str(candidate), manifests=manifests,
        note='Both runs use the BASELINE supersampled spatial reference. This prevents reference drift from '
             'masquerading as a regression. The reference remains an approximation, not motion ground truth.',
        stationary={}, output_drift={}, sequences=[a['sequences'] for a in analyses], profiles=[a['profiles'] for a in analyses])
    for backend in ('custom', 'fsr', 'dlaa'):
        outputs = [mean_output(r, backend + '-stationary') for r in (baseline, candidate)]
        results['output_drift'][backend] = aa.metrics(*outputs)
        for name, x in zip(('baseline', 'candidate'), outputs):
            key = name + '-' + backend
            results['stationary'][key] = aa.metrics(x, reference)
            results['stationary'][key]['structure_roi'] = aa.metrics(x[roi], reference[roi])
            aa.preview(output / (key + '-crop.png'), x[roi])
        aa.preview(output / (backend + '-change-x16.png'), np.abs(outputs[0][roi] - outputs[1][roi]) * 16)
    (output / 'comparison.json').write_text(json.dumps(results, indent=2, allow_nan=False))
    rows = []
    for key, x in results['stationary'].items():
        rows.append(f"<tr><td>{key}</td><td>{x['rmse']:.6f}</td><td>{x['psnr_peak1_db']:.3f}</td><td>{x['structure_roi']['rmse']:.6f}</td></tr>")
    html = '<!doctype html><meta charset="utf-8"><title>TAA comparison</title><style>body{font:16px system-ui;background:#15191f;color:#eee;margin:2em}td,th{padding:.5em;text-align:left}.grid{display:grid;grid-template-columns:repeat(3,1fr);gap:1em}img{width:100%}a{color:#9cf}</style>'
    html += '<h1>Custom TAA quality comparison</h1><p>' + results['note'] + '</p><p><a href="comparison.json">All metrics, timing windows and manifests</a></p>'
    html += '<table><tr><th>Output</th><th>Linear RMSE</th><th>PSNR (peak 1)</th><th>Structure RMSE</th></tr>' + ''.join(rows) + '</table><div class="grid">'
    for p in sorted(output.glob('*.png')):
        html += f'<figure><figcaption>{p.stem}</figcaption><a href="{p.name}"><img src="{p.name}"></a></figure>'
    (output / 'index.html').write_text(html + '</div>', encoding='utf-8')
    print(json.dumps(results['stationary'], indent=2))


if __name__ == '__main__':
    p = argparse.ArgumentParser()
    p.add_argument('baseline', type=Path); p.add_argument('candidate', type=Path); p.add_argument('output', type=Path)
    args = p.parse_args()
    compare(args.baseline.resolve(), args.candidate.resolve(), args.output.resolve())
