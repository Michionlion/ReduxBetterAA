"""Create native-pixel, synchronized review clips from measured shimmer crops."""
import argparse
import json
import html
import subprocess
from pathlib import Path
import numpy as np
from PIL import Image, ImageDraw, ImageFont


def frame(path, size):
    rgb=np.fromfile(path,dtype='<f2').reshape(size,size,4)[::-1,:,:3].astype(np.float32)
    assert np.isfinite(rgb).all()
    rgb=np.clip(rgb,0,1)
    rgb=np.where(rgb<=.0031308,rgb*12.92,1.055*rgb**(1/2.4)-.055)
    return Image.fromarray(np.uint8(np.clip(rgb*255+.5,0,255)))


def build(baseline, candidate, baseline_label='Original TAA', candidate_label='Improved TAA'):
    # Analysis must have succeeded first, using this exact shared reference.
    metrics=json.loads((candidate/'shimmer.json').read_text())
    assert Path(metrics['reference']).resolve()==baseline.resolve()
    out=candidate/'review';out.mkdir(exist_ok=True)
    arms=[(baseline_label,baseline,'custom'),(candidate_label,candidate,'custom'),
          ('DLAA M',candidate,'dlaa'),('FSR2 Native AA',candidate,'fsr'),('SSAA 200%',baseline,'reference')]
    font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',16)
    for roi,size in [('structure',512),('terrain',256)]:
        master=out/f'{roi}-lossless.mkv';width=size*len(arms);height=size+32
        command=['ffmpeg','-y','-v','error','-f','rawvideo','-pixel_format','rgb24',
                 '-video_size',f'{width}x{height}','-framerate','60','-i','pipe:0',
                 '-an','-c:v','libx264rgb','-crf','0','-preset','fast','-threads','8',str(master)]
        process=subprocess.Popen(command,stdin=subprocess.PIPE)
        try:
            for i in range(128):
                canvas=Image.new('RGB',(width,height),'#111820');draw=ImageDraw.Draw(canvas)
                for column,(label,run,arm) in enumerate(arms):
                    draw.text((column*size+8,5),label,font=font,fill='white')
                    canvas.paste(frame(run/'pixels'/arm/f'{i:03}-output-{roi}.rgba16f',size),(column*size,32))
                process.stdin.write(canvas.tobytes())
                if i in (0,63,127): canvas.save(out/f'{roi}-{i:03}.png')
        finally:
            process.stdin.close()
        if process.wait(): raise RuntimeError('Lossless encoding failed')
        subprocess.run(['ffmpeg','-y','-v','error','-threads','8','-i',str(master),
                        '-an','-c:v','libx264','-crf','10','-preset','medium','-threads','8',
                        '-pix_fmt','yuv420p','-movflags','+faststart',str(out/f'{roi}.mp4')],check=True)
        # Verify one decoded RGB frame against its uncompressed source, and all frame counts.
        decoded=subprocess.check_output(['ffmpeg','-v','error','-threads','8','-i',str(master),
            '-vf','select=eq(n\\,63)','-frames:v','1','-f','rawvideo','-pix_fmt','rgb24','pipe:1'])
        assert decoded==Image.open(out/f'{roi}-063.png').tobytes()
        for clip in (master,out/f'{roi}.mp4'):
            info=json.loads(subprocess.check_output(['ffprobe','-v','error','-count_frames',
                '-select_streams','v:0','-show_entries','stream=nb_read_frames,r_frame_rate',
                '-of','json',str(clip)]))['streams'][0]
            assert info['nb_read_frames']=='128' and info['r_frame_rate']=='60/1'
    path=json.loads((candidate/'run.json').read_text(encoding='utf-8-sig')).get('shimmerPath','pan')
    motion='panning with a diagonal direction reversal' if path=='reverse-diagonal' else 'panning'
    page='''<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Custom TAA temporal stability</title><style>body{background:#111820;color:#e9edf2;font:16px system-ui;margin:24px}p{max-width:850px;line-height:1.5}video{width:100%;max-width:2560px}a{color:#8bc6ff}.native video{width:auto;max-width:none}.clip{overflow:auto}label{display:block;margin:20px 0}</style>
<h1>Custom TAA temporal stability</h1><p>BASELINE_LABEL / CANDIDATE_LABEL / DLAA M / FSR2 Native AA / supersampling at 200% per axis. Matching camera poses, 128 consecutive frames at 60 FPS: 0.53 seconds still, 1.07 seconds MOTION_LABEL, 0.53 seconds settling. Physics is paused. These clips measure visual quality, not performance.</p>
<p>MP4 previews and browser scaling can hide fine shimmer. Use native size or the lossless RGB masters for close inspection. The supersampled image is a spatial reference approximation.</p>
<label><input type="checkbox" onchange="document.body.classList.toggle('native',this.checked)"> Native pixel size (scroll horizontally)</label>
<label>Playback speed <select onchange="document.querySelectorAll('video').forEach(v=>v.playbackRate=Number(this.value))"><option value="1">1x</option><option value="0.5">0.5x</option><option value="0.25">0.25x</option></select></label>
<h2>Vessel and launch structure</h2><div class="clip"><video controls loop muted playsinline preload="metadata" src="structure.mp4"></video></div>
<p><a href="structure-lossless.mkv">Lossless master</a> · <a href="structure-000.png">Still frame</a> · <a href="structure-063.png">Pan frame</a> · <a href="structure-127.png">Settled frame</a></p>
<h2>Terrain</h2><div class="clip"><video controls loop muted playsinline preload="metadata" src="terrain.mp4"></video></div>
<p><a href="terrain-lossless.mkv">Lossless master</a> · <a href="terrain-063.png">Pan frame</a> · <a href="../shimmer.json">Measured results</a></p>'''
    (out/'index.html').write_text(page.replace('BASELINE_LABEL',html.escape(baseline_label))
        .replace('CANDIDATE_LABEL',html.escape(candidate_label)).replace('MOTION_LABEL',motion),encoding='utf-8')
    print(out/'index.html')


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('baseline',type=Path);parser.add_argument('candidate',type=Path)
    parser.add_argument('--baseline-label',default='Original TAA');parser.add_argument('--candidate-label',default='Improved TAA')
    args=parser.parse_args();build(args.baseline,args.candidate,args.baseline_label,args.candidate_label)
