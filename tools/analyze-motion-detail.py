"""Native-pixel crop inspection; motion-compensated change is not ground truth."""
import argparse
import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw


def sample(image, x, y):
    x=np.clip(x,0,image.shape[1]-1.00001);y=np.clip(y,0,image.shape[0]-1.00001)
    ix=x.astype(int);iy=y.astype(int);fx=(x-ix)[...,None];fy=(y-iy)[...,None]
    return ((image[iy,ix]*(1-fx)+image[iy,ix+1]*fx)*(1-fy)
            +(image[iy+1,ix]*(1-fx)+image[iy+1,ix+1]*fx)*fy)


def analyze(path):
    m=json.loads((path/'capture.json').read_text())
    if not m.get('detailCapture'): raise ValueError('Native detail was not captured')
    frames=[json.loads(s) for s in (path/'frames.jsonl').read_text().splitlines()]
    n=m['count'];w=m['detailWidth'];h=m['detailHeight']
    colors={k:np.memmap(path/(k+'-detail.rgba16f'),np.float16,'r',shape=(n,h,w,4)) for k in ('input','output')}
    if not all(np.isfinite(c).all() for c in colors.values()): raise ValueError('Nonfinite native detail')
    raw=np.fromfile(path/'raw.rgba32f',np.float32).reshape(n,m['vectorHeight'],m['vectorWidth'],4)[...,:2]
    width,height=frames[0]['width'],frames[0]['height']
    x,y=np.meshgrid(np.arange(w),np.arange(h))
    sx=(x+m['detailX']+.5)/width*m['vectorWidth']-.5
    sy=(y+m['detailY']+.5)/height*m['vectorHeight']-.5
    # Ground/water around the focused vessel. Exclude crop borders and vessel.
    region=(x>16)&(x<w-16)&(y>16)&(y<h-16)&((x<w*.38)|(x>w*.62))
    valid=np.array([r['frame']-r['resetFrame']>2 for r in frames]);valid[:1]=False
    results={}
    for kind,images in colors.items():
        changes=[];gradients=[];per_frame=[]
        for i in range(1,n):
            if not (valid[i] and valid[i-1]): continue
            mv=sample(raw[i],sx,sy)*[width,height]
            cur=np.asarray(images[i,:,:,:3],np.float32)
            prev=np.asarray(images[i-1,:,:,:3],np.float32)
            if kind=='input':
                # Raster UV = unjittered UV - the recorded jitter.
                now=frames[i]['jitter'];old=frames[i-1]['jitter']
                cur=sample(cur,x-now['x']*width,y-now['y']*height)
                prev=sample(prev,x-mv[...,0]-old['x']*width,y-mv[...,1]-old['y']*height)
            else:
                prev=sample(prev,x-mv[...,0],y-mv[...,1])
            residual=np.mean(np.abs(cur-prev),axis=-1)[region]
            per_frame.append(float(residual.mean()));changes.append(residual[::16])
            gradients.append(float(np.mean(np.abs(np.diff(cur,axis=1))[:,16:-16])))
        values=np.concatenate(changes)
        results[kind]={'reprojected_mean_abs_change':float(np.mean(per_frame)),
            'reprojected_abs_change_p95':float(np.quantile(values,.95)),
            'mean_horizontal_gradient':float(np.mean(gradients)),
            'per_frame_reprojected_change':per_frame}
    results['label']=m['label'];results['backend']=m['backend']
    results['limits']='Approximate ground motion from a sparse vector grid; one fixture, no supersampled ground truth. Do not rank quality from a single scalar.'
    # Consecutive central vessel crops preserve pixels at 2x nearest display scale.
    start=60;panel=Image.new('RGB',(8*192,2*278),'#202020');draw=ImageDraw.Draw(panel)
    for row,kind in enumerate(('input','output')):
        for column in range(8):
            a=np.asarray(colors[kind][start+column,64:192,w//2-48:w//2+48,:3],np.float32)
            im=Image.fromarray((np.clip(a,0,1)**(1/2.2)*255).astype(np.uint8)).transpose(Image.Transpose.FLIP_TOP_BOTTOM)
            panel.paste(im.resize((192,256),Image.Resampling.NEAREST),(column*192,row*278))
            draw.text((column*192+3,row*278+258),f'{kind} frame {start+column}',fill='white')
    panel.save(path/'native-vessel-contact.png')
    (path/'detail-analysis.json').write_text(json.dumps(results,indent=2,allow_nan=False))
    return results


if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('run',type=Path);args=parser.parse_args()
    rows=[analyze(p.parent) for p in sorted(args.run.rglob('capture.json')) if json.loads(p.read_text()).get('detailCapture')]
    if not rows: raise SystemExit('No native detail captures')
    (args.run/'detail-analysis.json').write_text(json.dumps(rows,indent=2,allow_nan=False))
    for r in rows: print(r['label'],{k:{key:v for key,v in r[k].items() if not isinstance(v,list)} for k in ('input','output')})
