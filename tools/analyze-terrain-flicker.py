"""Per-render native terrain pixels, with same-frame source and output evidence.

Stationary controls isolate changes without confusing scene movement with flicker.
Moving windows are aligned using captured depth and actual raster/view matrices;
depth discontinuities, off-screen samples and reset frames are excluded.
"""
import argparse
import hashlib
import json
from pathlib import Path

import numpy as np
from PIL import Image


def matrix(frame, key):
    m = frame[key]
    return np.array([[m.get(f"e{r}{c}", m.get(f"m{r}{c}")) for c in range(4)] for r in range(4)], dtype=np.float64)


def srgb(x):
    x = np.clip(x.astype(np.float32), 0, 1)
    return np.where(x <= .0031308, 12.92*x, 1.055*x**(1/2.4)-.055)


def bilinear(a, x, y):
    h, w = a.shape[:2]
    x0 = np.clip(np.floor(x).astype(int), 0, w-2)
    y0 = np.clip(np.floor(y).astype(int), 0, h-2)
    fx = np.clip(x-x0, 0, 1)
    fy = np.clip(y-y0, 0, 1)
    if a.ndim == 3:
        fx, fy = fx[..., None], fy[..., None]
    return (a[y0,x0]*(1-fx)+a[y0,x0+1]*fx)*(1-fy)+(a[y0+1,x0]*(1-fx)+a[y0+1,x0+1]*fx)*fy


def coherent_blocks(series, validpairs):
    n,h,w,c=series.shape
    metrics={}
    for block in (1,4,16):
        b=series.reshape(n,h//block,block,w//block,block,c).mean(axis=(2,4))
        delta=np.abs(np.diff(b,axis=0))[validpairs]
        metrics[str(block)]={'mae':float(delta.mean()),'p95':float(np.percentile(delta,95))}
    return metrics


def classify_flicker(score, applicable):
    # Calibrated for Test's saved northwest-hills view. Validate production on a
    # fresh run; this is not a universal score for arbitrary landscapes/motion.
    if not applicable: return 'not_applicable_to_this_view_or_motion'
    return 'coherent_hillside_flicker' if score>.00025 else 'within_terrain_flicker_limit'


def matches_fixture_view(meta, frames):
    # Fixed screen region only has meaning at Test's saved orientation and
    # distance. Small origin/float differences across reloads are permitted.
    rotation=np.array([[.17250216,.98415828,.04093605],
                       [-.13047659,.06402302,-.98938215],
                       [.97632939,-.16532943,-.13945365]])
    if (meta['x'],meta['y'],meta['width'],meta['height'])!=(768,896,768,384): return False
    for f in frames:
        if (f['width'],f['height'])!=(2560,1440): return False
        view=matrix(f,'view'); projection=matrix(f,'gpuProjection')
        if not np.allclose(view[:3,:3],rotation,atol=2e-4,rtol=0): return False
        if abs(view[2,3]+728.0894)>.03: return False
        if not np.allclose([projection[0,0],projection[1,1]],[.97427857,-1.73205078],atol=1e-6,rtol=0): return False
    return True


def correspondence(cur, prev, depth, meta, projection):
    """D3D [0,1] depth, bottom-first texture. Camera-relative double CPU math."""
    h,w = depth.shape
    yy,xx = np.mgrid[:h,:w]
    ndc = np.stack(((xx+meta['x']+.5)/cur['width']*2-1,
                    1-(yy+meta['y']+.5)/cur['height']*2,depth,np.ones_like(depth)),axis=-1)
    # Depth was rasterized with the jittered projection. Output pixels use the
    # unjittered projection; sample corresponding depth at its raster offset.
    cp = matrix(cur, projection)
    transform = matrix(prev, projection) @ matrix(prev,'view') @ np.linalg.inv(matrix(cur,'view')) @ np.linalg.inv(cp)
    clip = ndc @ transform.T
    clip = clip[...,:3]/clip[...,3:4]
    x = (clip[...,0]+1)*.5*prev['width']-.5-meta['x']
    y = (1-clip[...,1])*.5*prev['height']-.5-meta['y']
    valid=(x>=1)&(x<w-2)&(y>=1)&(y<h-2)&(depth>1e-9)&np.isfinite(x)&np.isfinite(y)
    return x,y,clip[...,2],valid


def analyze(folder):
    m=json.loads((folder/'terrain.json').read_text())
    fs=[json.loads(s) for s in (folder/'frames.jsonl').read_text().splitlines()]
    n,h,w=m['count'],m['height'],m['width']
    assert len(fs)==n and n>=8 and m['pending']==0 and not m.get('error'), 'Incomplete capture'
    assert all(b['frame']==a['frame']+1 for a,b in zip(fs,fs[1:])), 'Frame gap'
    assert all(f['frame']==f['projectionFrame'] for f in fs), 'Stale projection'
    assert all(f['backend']==m['backend'] for f in fs), 'Backend changed'
    times=np.array([f['realtime'] for f in fs]);dt=np.diff(times)
    assert np.all(dt>0), 'Invalid clock'
    images={}
    for k in ['input','output','overview-input','overview-output']:
        shape=(n,h,w,4) if not k.startswith('overview') else (n,m['overviewHeight'],m['overviewWidth'],4)
        p=folder/(k+'.rgba16f');assert p.stat().st_size==int(np.prod(shape))*2, 'Wrong color byte count'
        images[k]=np.memmap(p,dtype=np.float16,mode='r',shape=shape)
        assert np.isfinite(images[k]).all(), 'Nonfinite color'
    dp=folder/'depth.r32f';assert dp.stat().st_size==n*h*w*4,'Wrong depth byte count'
    depth=np.memmap(dp,dtype=np.float32,mode='r',shape=(n,h,w));assert np.isfinite(depth).all(),'Nonfinite depth'
    out=folder/'review';out.mkdir(exist_ok=True)
    metrics={}
    validframes=np.array([f['frame']-f['resetFrame']>3 for f in fs])
    stationary=all(np.array_equal(matrix(f,'view'),matrix(fs[0],'view')) and np.array_equal(matrix(f,'gpuProjection'),matrix(fs[0],'gpuProjection')) for f in fs)
    for kind in ['input','output']:
        values=[];fractions=[];maps=np.zeros((h,w),np.float64);counts=np.zeros((h,w),int)
        rawvalues=[];brightness=[]
        prev=srgb(images[kind][0,:,:,:3])
        for i in range(1,n):
            cur=srgb(images[kind][i,:,:,:3])
            if not (validframes[i] and validframes[i-1]):prev=cur;continue
            if stationary:
                # Stationary acceptance uses native output and coherent blocks.
                # Source jitter differences remain explicitly unaligned.
                valid=np.ones((h,w),bool)
                difference=np.mean(np.abs(cur-prev),axis=-1)
            else:
                projection='rasterGpuProjection' if kind=='input' else 'gpuProjection'
                x,y,predz,valid=correspondence(fs[i],fs[i-1],depth[i],m,projection)
                prevz=bilinear(depth[i-1],x,y)
                valid &= np.abs(prevz-predz)<np.maximum(2e-7,np.abs(predz)*.003)
                difference=np.mean(np.abs(cur-bilinear(prev,x,y)),axis=-1)
            if np.count_nonzero(valid)<100:raise ValueError('Insufficient valid terrain correspondence')
            values.append(float(difference[valid].mean()));fractions.append(float(np.mean(difference[valid]>.02)))
            rawvalues.append(float(np.mean(np.abs(cur-prev))));brightness.append(float(cur.mean()))
            maps+=np.where(valid,difference,0);counts+=valid;prev=cur
        assert values,'No reset-free pairs'
        resultmap=maps/np.maximum(counts,1)
        np.save(out/(kind+'-residual-map.npy'),resultmap)
        Image.fromarray((np.flipud(np.clip(resultmap*20,0,1))*255).astype(np.uint8)).save(out/(kind+'-residual-x20.png'))
        metrics[kind]={'mean_aligned_delta':float(np.mean(values)),'p95_frame_aligned_delta':float(np.percentile(values,95)),
            'mean_unaligned_delta':float(np.mean(rawvalues)),'changed_2pct_fraction':float(np.mean(fractions)),
            'valid_fraction':float(np.mean(counts>0)),'frame_aligned_delta':values}
        # Fixed native hillside interior: excludes sky, horizon, river and most
        # trees. Block means suppress PPv2's unrelated per-pixel dither. This is
        # the acceptance score only for the immutable Test view/ROI and a still
        # camera; never interpret ordinary camera motion as a flicker failure.
        s=srgb(images[kind][:,140:300,0:512,:3])
        coherent=coherent_blocks(s,validframes[1:]&validframes[:-1])
        b=s.reshape(n,10,16,32,16,3).mean(axis=(2,4))
        np.save(out/(kind+'-hill-block16.npy'),b)
        metrics[kind]['hill_blocks']=coherent
        for idx in [0,n//2,n-1]:
            Image.fromarray((np.flipud(srgb(images[kind][idx,:,:,:3]))*255+.5).astype(np.uint8)).save(out/f'{kind}-{idx:03}.png')
    for kind in ['overview-input','overview-output']:
        Image.fromarray((np.flipud(srgb(images[kind][n//2,:,:,:3]))*255+.5).astype(np.uint8)).save(out/(kind+'.png'))
        series=srgb(images[kind][...,:3])
        change=np.abs(np.diff(series,axis=0)).mean(axis=-1)
        validpairs=validframes[1:]&validframes[:-1]
        flicker=change[validpairs].mean(axis=0)
        np.save(out/(kind+'-delta-map.npy'),flicker)
        Image.fromarray((np.flipud(np.clip(flicker*20,0,1))*255+.5).astype(np.uint8)).save(out/(kind+'-delta-x20.png'))
        metrics[kind]={'mean_unaligned_delta':float(flicker.mean()),'hill_unaligned_delta':float(flicker[185:250,100:400].mean())}
    result={'label':m['label'],'candidate':m['candidate'],'backend':m['backend'],'count':n,
        'fps':float(1/np.median(dt)),'frame_ms_p95':float(np.percentile(dt,95)*1000),
        'altitude_range':[min(f['altitude'] for f in fs),max(f['altitude'] for f in fs)],
        'speed_range':[min(f['speed'] for f in fs),max(f['speed'] for f in fs)],
        'paused':all(f['timeScale']==0 for f in fs),'stationary':stationary,'transparent_jitter':all(f['transparentJitter'] for f in fs),
        'metric_schema':3,'stationary_score_applicable':stationary and matches_fixture_view(m,fs),
        'depth_range':[float(depth.min()),float(depth.max())], 'metrics':metrics,
        'input_output_mae':float(np.mean(np.abs(srgb(images['input'][n//2,:,:,:3])-srgb(images['output'][n//2,:,:,:3]))))}
    if 'pqsFrame' in fs[0]:
        result['pqs_current_frame_fraction']=float(np.mean([f['frame']==f['pqsFrame'] for f in fs]))
        result['pqs_projection_max_difference']=float(max(np.max(np.abs(matrix(f,'pqsRaster')-matrix(f,'rasterGpuProjection'))) for f in fs))
    if 'pqsDepthFrame' in fs[0]:
        result['pqs_depth_current_frame_fraction']=float(np.mean([f['frame']==f['pqsDepthFrame'] for f in fs]))
        result['pqs_depth_projection_max_difference']=float(max(np.max(np.abs(matrix(f,'pqsDepthRaster')-matrix(f,'rasterGpuProjection'))) for f in fs))
    result['flicker_limit']=.00025
    result['classification']=classify_flicker(metrics['output']['hill_blocks']['16']['mae'],result['stationary_score_applicable'] and n>=96)
    (out/'result.json').write_text(json.dumps(result,indent=2))
    return result


def main():
    parser=argparse.ArgumentParser();parser.add_argument('run',type=Path);args=parser.parse_args()
    paths=sorted(args.run.rglob('terrain.json'))
    if not paths:raise ValueError('No terrain captures')
    results=[]
    for p in paths:
        r=analyze(p.parent);results.append(r)
        print(r['label'],r['backend'],round(r['fps'],1),'hill16',r['metrics']['output']['hill_blocks']['16']['mae'],
              'PQS depth matrix error',r.get('pqs_depth_projection_max_difference'),r['classification'],flush=True)
    (args.run/'terrain-analysis.json').write_text(json.dumps(results,indent=2))


if __name__=='__main__':main()
