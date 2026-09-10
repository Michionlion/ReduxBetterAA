"""Measure native, consecutive still/pan/settle crops against one fixed SSAA run."""
import argparse
import csv
import json
from pathlib import Path
import numpy as np
from PIL import Image


def read(path, size):
    data=np.fromfile(path,dtype='<f2')
    if data.size != size*size*4 or not np.isfinite(data).all():
        raise ValueError(f'Invalid pixels: {path}')
    return data.reshape(size,size,4)[::-1,:,:3].astype(np.float32)


def preview(path, rgb):
    x=np.clip(rgb,0,1)
    x=np.where(x<=.0031308,12.92*x,1.055*x**(1/2.4)-.055)
    Image.fromarray(np.uint8(np.clip(x*255+.5,0,255))).save(path)


def analyze(root, reference):
    path=json.loads((root/'run.json').read_text(encoding='utf-8-sig')).get('shimmerPath','pan')
    assert path==json.loads((reference/'run.json').read_text(encoding='utf-8-sig')).get('shimmerPath','pan'), 'Mismatched camera paths'
    reports=[json.loads(next(p.glob('harness/*/shimmer/report.json')).read_text(encoding='utf-8-sig')) for p in (root,reference)]
    for report in reports:
        assert report['status']=='passed', 'Capture test failed'
    for key in ('kspVersion','reduxVersion','unityVersion','graphicsDevice'):
        assert reports[0]['environment'][key]==reports[1]['environment'][key], f'Mismatched {key}'
    result={}
    for roi,size in [('structure',512),('terrain',256)]:
        ref=np.stack([read(reference/'pixels/reference'/f'{i:03}-output-{roi}.rgba16f',size) for i in range(128)])
        ref_mean=ref[:32].mean(0)
        gradient=np.abs(np.diff(ref_mean,axis=0,append=ref_mean[-1:])).mean(2)+np.abs(np.diff(ref_mean,axis=1,append=ref_mean[:,-1:])).mean(2)
        edge=gradient>np.percentile(gradient,85)
        result[roi]={}
        for arm in sorted((root/'pixels').iterdir()):
            if not arm.is_dir(): continue
            with (arm/'frames.csv').open() as f: frames=list(csv.DictReader(f))
            assert len(frames)==128
            assert [int(f['index']) for f in frames]==list(range(128))
            ids=[int(f['unity_frame']) for f in frames]
            assert ids==list(range(ids[0],ids[0]+128))
            for i,f in enumerate(frames):
                travel=0 if i<32 else i-31 if i<64 else 95-i if i<96 else 0
                yaw=travel*.2 if path=='reverse-diagonal' else (0 if i<32 else min(i-31,64)*.1)
                pitch=30+travel*.1 if path=='reverse-diagonal' else 30
                assert abs(float(f['yaw'])-yaw)<2e-6 and abs(float(f.get('pitch',30))-pitch)<5e-6
            selected={'reference':'Off','fsr':'FSR2 Native AA','dlaa':'NVIDIA DLAA'}.get(arm.name,'Custom TAA')
            assert all(f['selected']==selected for f in frames), 'Backend fallback during capture'
            if selected=='Custom TAA': assert all(f['history_used']=='True' for f in frames), 'History reset during capture'
            assert np.isfinite([[float(f['jitter_x']),float(f['jitter_y'])] for f in frames]).all()
            values=np.stack([read(arm/f'{i:03}-output-{roi}.rgba16f',size) for i in range(128)])
            stats={}
            for name,sl in [('still',slice(0,32)),('pan',slice(32,96)),('settle',slice(112,128))]:
                v=values[sl]; delta=v-ref[sl]
                stats[name]={'std':float(v.std(0).mean()),'edge_std':float(v.std(0)[edge].mean()),
                    'adjacent_mae':float(np.abs(np.diff(v,axis=0)).mean()),
                    'reference_rmse':float(np.sqrt(np.mean(delta**2))),
                    'reference_error_change':float(np.abs(np.diff(delta,axis=0)).mean())}
            stats['early_settle_rmse']=float(np.sqrt(np.mean((values[96:104]-ref[96:104])**2)))
            if roi=='structure':
                # Baseline-selected fixed grating crop; these arrays are top-origin.
                residual=(values-ref)[32:96,282:472,80:432]
                stats['grating_pan']={'reference_rmse':float(np.sqrt(np.mean(residual**2))),
                    'reference_error_change':float(np.abs(np.diff(residual,axis=0)).mean())}
            for mode in (4,5,6,9):
                samples=[read(p,size)[:,:,0] for p in sorted(arm.glob(f'*-debug{mode}-{roi}.rgba16f'))]
                if samples:
                    stats['debug'+str(mode)]=[float(s.mean()) for s in samples]
            result[roi][arm.name]=stats
            for i in (0,63,127): preview(root/f'{arm.name}-{roi}-{i:03}.png',values[i])
            if roi=='structure': print(arm.name,json.dumps(stats),flush=True)
    (root/'shimmer.json').write_text(json.dumps({'reference':str(reference.resolve()),
        'note':'Linear RGB. Still std measures stationary variation. During pan, reference_error_change measures adjacent changes in the residual against matching SSAA frames; raw temporal std includes real movement. SSAA is an approximate spatial reference, not motion ground truth. Fixed initial edge mask is meaningful only during the initial still phase.',
        'metrics':result},indent=2))
    return result


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('run',type=Path);parser.add_argument('--reference',type=Path)
    args=parser.parse_args();analyze(args.run,args.reference or args.run)
