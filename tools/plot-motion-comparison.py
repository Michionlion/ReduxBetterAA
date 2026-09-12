"""Plot measured raw/consumed motion and the camera experiment's vessel judder."""
import argparse
import json
from pathlib import Path

import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
import numpy as np

p=argparse.ArgumentParser();p.add_argument('run',type=Path);args=p.parse_args()
fig,axes=plt.subplots(4,1,figsize=(12,9),layout='constrained')
region=np.zeros((36,64),bool);region[8:28,5:25]=True;region[8:28,39:59]=True
names=[('baseline-dlaa','Baseline DLAA'),('motion-ema-dlaa','50% motion EMA'),('camera-interpolate-dlaa','Camera-only render interpolation')]
maximum=0
for ax,(name,title) in zip(axes,names):
    path=args.run/'samples'/name
    m=json.loads((path/'capture.json').read_text());r=[json.loads(s) for s in (path/'frames.jsonl').read_text().splitlines()]
    times=np.array([f['realtime'] for f in r]);times-=times[0];window=times<=.6
    dims=np.array([r[0]['width'],r[0]['height']])
    for kind,color in [('raw','#2762a5'),('consumed','#d07717')]:
        v=np.fromfile(path/(kind+'.rgba32f'),np.float32).reshape(m['count'],36,64,4)[...,:2]*dims
        mean=np.linalg.norm(v,axis=-1)[:,region].mean(axis=1)
        maximum=max(maximum,float(mean[window].max()))
        ax.plot(times[window],mean[window],'.-',color=color,label=kind,linewidth=1.2,markersize=3)
    ax.set(title=title,ylabel='Terrain motion (pixels)',xlim=(0,.6))
    ax.grid(alpha=.2);ax.legend(loc='upper right',ncol=2)
    viewport=np.array([f['vesselViewport']['y'] for f in r])*dims[1]
    axes[3].plot(times[window],viewport[window]-np.median(viewport[window]),'.-',label=title,markersize=3,linewidth=1.2)
for ax in axes[:3]: ax.set_ylim(0,maximum*1.05)
axes[3].set(title='Focused vessel: vertical movement on screen',ylabel='Pixels from median',xlabel='Elapsed capture time (seconds)',xlim=(0,.6))
axes[3].grid(alpha=.2);axes[3].legend(loc='upper right',fontsize=8)
fig.suptitle('Measured motion cadence: quieter vectors alone do not establish a better image',fontsize=14)
fig.savefig(args.run/'motion-comparison.png',dpi=150)
fig.savefig(args.run/'motion-comparison.svg')
print(args.run/'motion-comparison.png')
