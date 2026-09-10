"""Compare captured-input shader experiments on the valid interior of native crops."""
import argparse
import json
from pathlib import Path
import numpy as np


def sequence(root, region, size):
    values = np.stack([np.fromfile(root / f'{i:03}-output-{region}.rgba16f', dtype='<f2')
                       .reshape(size, size, 4)[32:-32, 32:-32, :3].astype(np.float32) for i in range(128)])
    assert np.isfinite(values).all(), root
    return values


def analyze(replay, capture):
    result = {}
    for region, size in [('structure', 512), ('terrain', 256)]:
        reference = sequence(capture / 'pixels/reference', region, size)
        actual = sequence(capture / 'pixels/custom', region, size)
        gx = np.abs(np.diff(reference, axis=2, append=reference[:, :, -1:])).mean(3)
        gy = np.abs(np.diff(reference, axis=1, append=reference[:, -1:])).mean(3)
        gradient = gx + gy
        edges = gradient > np.percentile(gradient, 85, axis=(1, 2), keepdims=True)
        result[region] = {}
        for arm in sorted(p for p in replay.iterdir() if p.is_dir()):
            v = sequence(arm, region, size)
            stats = {}
            for label, window in [('still', slice(8, 32)), ('pan', slice(32, 96)), ('settle', slice(112, 128))]:
                residual = v[window] - reference[window]
                stats[label] = {
                    'reference_rmse': float(np.sqrt(np.mean(residual**2))),
                    'edge_rmse': float(np.sqrt(np.mean(residual[edges[window]]**2))),
                    'reference_error_change': float(np.abs(np.diff(residual, axis=0)).mean()),
                    'std': float(v[window].std(0).mean())}
            stats['player_match_rmse'] = float(np.sqrt(np.mean((v-actual)**2)))
            stats['early_settle_rmse'] = float(np.sqrt(np.mean((v[96:104]-reference[96:104])**2)))
            if region == 'structure':
                # Fixed baseline-selected grating ROI, bottom-origin native pixels:
                # x=80..431, y=40..229. Keep this constant for every candidate.
                residual = (v-reference)[32:96, 8:198, 48:400]
                stats['grating_pan'] = {
                    'reference_rmse': float(np.sqrt(np.mean(residual**2))),
                    'reference_error_change': float(np.abs(np.diff(residual, axis=0)).mean())}
            result[region][arm.name] = stats
            print(region, arm.name, json.dumps(stats))
    (replay/'metrics.json').write_text(json.dumps(result, indent=2))


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('replay', type=Path)
    p.add_argument('capture', type=Path)
    args = p.parse_args()
    analyze(args.replay, args.capture)
