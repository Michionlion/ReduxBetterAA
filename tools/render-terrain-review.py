"""Derived review media only; measurements always use original float captures."""
import argparse
import json
from pathlib import Path
import subprocess
import numpy as np
from PIL import Image,ImageDraw,ImageFont
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt


def srgb(x):
    x=np.clip(x.astype(np.float32),0,1)
    return np.where(x<=.0031308,12.92*x,1.055*x**(1/2.4)-.055)


def load(root,name):
    p=root/'samples'/name
    frames=[json.loads(s) for s in (p/'frames.jsonl').read_text().splitlines()]
    a=np.memmap(p/'output.rgba16f',dtype='<f2',mode='r',shape=(len(frames),384,768,4))
    return a,np.array([f['realtime'] for f in frames])-frames[0]['realtime']


def main(root):
    out=root/'review';out.mkdir(exist_ok=True)
    names=[('NVIDIADLAA','DLAA L'),('TAA','TAA'),('FSR2NativeAA','FSR2 Native AA')]
    fig,axes=plt.subplots(3,1,figsize=(10,8),layout='constrained')
    quality={}
    for ax,(name,label) in zip(axes,names):
        means={}
        for suffix,color in [('before','#c65242'),('after','#187d9c')]:
            p=root/'samples'/(name+'-'+suffix)
            b=np.load(p/'review'/'output-hill-block16.npy')
            delta=np.abs(np.diff(b,axis=0)).mean(axis=(1,2,3))
            ax.plot(np.arange(1,len(b)),delta*1000,color=color,label=suffix,lw=1.3)
            data,_=load(root,name+'-'+suffix)
            mean=srgb(data[:,:,:,:3]).mean(axis=0)
            means[suffix]=mean
            Image.fromarray((np.flipud(mean)*255+.5).astype('uint8')).save(out/(name+'-'+suffix+'-mean.png'))
        ax.set_title(label,loc='left');ax.set_ylabel('Change × 1000')
        ax.axhline(.25,color='#777777',ls=':',lw=.8,label='acceptance limit')
        ax.set_ylim(bottom=0);ax.grid(alpha=.15);ax.legend(loc='upper right',ncol=3,frameon=False)
        # Descriptive detail check on the temporal mean, not an SSAA quality
        # reference: changing shadows/coverage can also change this number.
        def gradient(a):
            a=a[140:300,:512,:]
            return float((np.abs(np.diff(a,axis=0)).mean()+np.abs(np.diff(a,axis=1)).mean())*.5)
        quality[name]={'before_mean_gradient':gradient(means['before']),'after_mean_gradient':gradient(means['after'])}
    axes[-1].set_xlabel('Consecutive rendered frame')
    fig.suptitle('Northwest hillside flicker — saved Test view\n16×16 block means; same DLL and quality settings; correction bypassed / enabled')
    fig.savefig(out/'terrain-flicker.png',dpi=150);plt.close(fig)
    (out/'detail-proxy.json').write_text(json.dumps(quality,indent=2))
    font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',19)
    # Display at 120 Hz using previous-frame hold from actual render timestamps.
    # This preserves the measured cadence; it does not invent 120 unique frames.
    for name,label in names:
        before,tb=load(root,name+'-before');after,ta=load(root,name+'-after')
        duration=min(tb[-1],ta[-1]);path=out/(name+'-before-after.mp4')
        command=['ffmpeg','-y','-loglevel','error','-f','rawvideo','-pixel_format','rgb24','-video_size','1536x426','-framerate','120','-i','-',
                 '-an','-c:v','libx264','-preset','fast','-crf','14','-pix_fmt','yuv420p','-movflags','+faststart',str(path)]
        proc=subprocess.Popen(command,stdin=subprocess.PIPE)
        for t in np.arange(0,duration,1/120):
            canvas=Image.new('RGB',(1536,426),(22,26,32));draw=ImageDraw.Draw(canvas)
            for x,data,times,text in [(0,before,tb,'Depth correction bypassed'),(768,after,ta,'Depth correction enabled')]:
                i=max(0,np.searchsorted(times,t,side='right')-1)
                canvas.paste(Image.fromarray((np.flipud(srgb(data[i,:,:,:3]))*255+.5).astype('uint8')),(x,42))
                draw.text((x+12,9),label+' | '+text,font=font,fill='white')
            proc.stdin.write(canvas.tobytes())
        proc.stdin.close()
        if proc.wait()!=0:raise RuntimeError('Video encoding failed')
    summary=json.loads((root/'terrain-verification.json').read_text())
    rows=''.join('<tr><td>'+label+'</td><td>'+format(summary['comparisons'][name]['reduction_percent'],'.2f')+'%</td></tr>' for name,label in names)
    videos=''.join('<h2>'+label+'</h2><video controls loop muted preload="metadata" src="'+name+'-before-after.mp4"></video>' for name,label in names)
    html='''<!doctype html><meta charset="utf-8"><title>Northwest hills — depth jitter correction</title>
<style>body{background:#151b22;color:#e8eef5;font:17px system-ui;max-width:1200px;margin:36px auto;padding:0 24px}h1{font-size:32px}p{max-width:900px;line-height:1.5}img,video{width:100%;border-radius:8px}td{padding:9px 32px 9px 0}h2{margin-top:36px}a{color:#7bd7ee}</style>
<h1>Northwest hills: terrain depth correction</h1><p>Test save, launchpad 4, original camera. These pairs use the same production DLL and saved quality settings. The only difference is whether terrain depth receives the current camera jitter. Physics and AA filtering are unchanged.</p>
<table><tr><th>Mode</th><th>Measured flicker reduction</th></tr>'''+rows+'''</table><p>128 consecutive rendered frames per case. The metric measures coherent hillside changes after averaging 16×16 pixel blocks. It suppresses unrelated per-pixel dither. The paired CPU check added 0.002–0.003 ms per terrain-depth draw with zero managed allocation.</p><img src="terrain-flicker.png" alt="Before and after coherent hillside variation across frames">'''+videos+'''<p>Clips are native 768×384 crops. Playback holds recorded frames according to their actual timestamps on a 120 Hz timeline; it does not manufacture additional render samples. The original float captures, not these encoded previews, determine the scores. Loop boundaries are discontinuities.</p><p><a href="../terrain-verification.json">49 acceptance checks and timing distributions</a></p>'''
    (out/'index.html').write_text(html,encoding='utf-8')
    print(out.resolve())


if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('run',type=Path)
    main(parser.parse_args().run)
