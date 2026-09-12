"""Validate and measure render-boundary motion captures. No video clock assumptions."""
import argparse
import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw


def matrix(record, key):
    m=record[key]
    return np.array([[m[f"e{r}{c}"] if f"e{r}{c}" in m else m[f"m{r}{c}"] for c in range(4)] for r in range(4)],np.float64)


def correlation(a,b):
    return float(np.corrcoef(a,b)[0,1]) if np.std(a)>1e-8 and np.std(b)>1e-8 else None


def validate_frames(metadata, frames):
    if len(frames)!=metadata["count"] or len(frames)<8: raise ValueError("Missing/insufficient frame metadata")
    if not np.all(np.diff([r["frame"] for r in frames])==1): raise ValueError("Frame gap or duplicate")
    if not np.all(np.diff([r["realtime"] for r in frames])>0): raise ValueError("Nonmonotonic timestamp")
    if not all(r["backend"]==metadata["backend"] for r in frames): raise ValueError("Unexpected backend fallback")
    if metadata.get("error") or metadata["pending"]!=0: raise ValueError("Incomplete/failed readback")


def require_finite(*arrays):
    if not all(np.isfinite(a).all() for a in arrays):
        raise ValueError("Nonfinite GPU sample")


def ema_error(raw, consumed, sign, pair_valid, region):
    """Verify the candidate really executed, including sign and texture orientation."""
    predicted=.5*(raw[1:]*sign+consumed[:-1])
    return float(np.mean(np.linalg.norm(consumed[1:]-predicted,axis=-1)[pair_valid][...,region]))


def classify_cadence(mean, step, reprojection_p95):
    """Cadence is an observation, not automatically a bad-vector regression."""
    active=step[1:]; values=mean[1:]
    if np.mean(values)<.001:
        return "stationary_or_insufficient_motion" if reprojection_p95<.05 else "missing_motion_check_vector_coherence"
    if active.mean()<.1 or active.mean()>.9: return "insufficient_inter_tick_samples"
    corr=correlation(values,active)
    ratio=float(np.mean(values[active])/max(1e-5,np.mean(values[~active])))
    if corr is not None and corr>.8 and ratio>10:
        return "physics_stepped_valid_camera_motion" if reprojection_p95<.05 else "physics_stepped_check_vector_coherence"
    return "motion_not_dominated_by_physics_steps"


def analyze(path):
    m=json.loads((path/"capture.json").read_text())
    frames=[json.loads(line) for line in (path/"frames.jsonl").read_text().splitlines()]
    n=m["count"]; validate_frames(m,frames)
    w,h=m["vectorWidth"],m["vectorHeight"]
    raw=np.fromfile(path/"raw.rgba32f",np.float32).reshape(n,h,w,4)[...,:2].astype(np.float64)
    consumed=np.fromfile(path/"consumed.rgba32f",np.float32).reshape(n,h,w,4)[...,:2].astype(np.float64)
    depth=np.fromfile(path/"depth.rgba32f",np.float32).reshape(n,h,w,4)[...,0].astype(np.float64)
    colors={k:np.memmap(path/(k+".rgba16f"),np.float16,"r",shape=(n,m["colorHeight"],m["colorWidth"],4)) for k in (("input","output") if m.get("colorCapture",True) else ())}
    require_finite(raw,consumed,depth,*colors.values())
    times=np.array([r["realtime"] for r in frames]); fixed=np.array([r["fixedTime"] for r in frames])
    step=np.r_[False,np.diff(fixed)>1e-5]; dt=np.diff(times)
    valid_frame=np.array([r["frame"]-r["resetFrame"]>2 for r in frames]);valid_frame[0]=False
    pair_valid=valid_frame[1:]&valid_frame[:-1]
    pixels=np.array([frames[0]["width"],frames[0]["height"]]); raw*=pixels; consumed*=pixels
    # Exclude vessel centre, sky and viewport borders. Same spatial mask in every arm.
    region=np.zeros((h,w),bool); region[8:28,5:25]=True; region[8:28,39:59]=True
    mask=(depth>1e-9)&region[None,:,:]
    magnitude=np.linalg.norm(raw,axis=-1)
    mean=np.array([np.mean(magnitude[i][mask[i]]) for i in range(n)])
    position=np.array([[r["position"][k] for k in "xyz"] for r in frames])
    movement=np.r_[0,np.linalg.norm(np.diff(position,axis=0),axis=-1)]
    output=dict(label=m["label"],backend=m["backend"],frames=n,
        seconds=float(times[-1]-times[0]),render_fps=float(1/np.mean(dt)),frame_ms_p50=float(np.median(dt)*1000),
        frame_ms_p95=float(np.quantile(dt,.95)*1000),physics_hz=float(1/frames[0]["fixedDelta"]),
        physics_step_fraction=float(step[valid_frame].mean()),raw_motion_mean_px=float(np.mean(mean[valid_frame])),
        raw_motion_step_px=float(np.mean(mean[step&valid_frame])) if np.any(step&valid_frame) else None,
        raw_motion_no_step_px=float(np.mean(mean[~step&valid_frame])),
        motion_vs_physics_step_correlation=correlation(mean[valid_frame],step[valid_frame]),
        motion_vs_camera_translation_correlation=correlation(mean[valid_frame],movement[valid_frame]),
        camera_translation_step_m=float(np.mean(movement[step&valid_frame])) if np.any(step&valid_frame) else None,
        camera_translation_no_step_m=float(np.mean(movement[~step&valid_frame])),
        reset_guard_excluded_frames=int(np.sum(~valid_frame)),
        reset_frames=[r["frame"] for r in frames if r["frame"]==r["resetFrame"]])
    output["candidate"]=m.get("candidate","none")
    if "surfaceSpeed" in frames[0]:
        output["surface_speed_range_mps"]=[min(r["surfaceSpeed"] for r in frames),max(r["surfaceSpeed"] for r in frames)]
        output["altitude_range_m"]=[min(r["altitude"] for r in frames),max(r["altitude"] for r in frames)]
    if "vesselViewport" in frames[0]:
        center=np.array([[r["vesselViewport"][k] for k in "xy"] for r in frames])*pixels
        # High-pass only: avoid counting a deliberate pan as judder.
        pad=np.pad(center,((2,2),(0,0)),mode="edge")
        trend=np.stack([np.convolve(pad[:,i],np.ones(5)/5,"valid") for i in range(2)],axis=-1)
        output["vessel_screen_highpass_rms_px"]=float(np.sqrt(np.mean((center-trend)[valid_frame]**2)))
    # Reconstruct static-world camera motion in double precision using the actual
    # unjittered camera matrices and sampled current raw depth. Audit both texture
    # Y conventions rather than silently fitting a per-frame sign.
    u,v=np.meshgrid((np.arange(w)+.5)/w,(np.arange(h)+.5)/h)
    vp=np.stack([matrix(r,"gpuProjection")@matrix(r,"view") for r in frames])
    expected={}
    for flip in (False,True):
        predictions=np.zeros_like(raw)
        for i in range(1,n):
            j=frames[i]["jitter"]
            uv=np.stack((u+j["x"],v+j["y"]),axis=-1)
            py=1-uv[...,1] if flip else uv[...,1]
            clip=np.stack((uv[...,0]*2-1,py*2-1,depth[i],np.ones_like(u)),axis=-1)
            prev=np.einsum("ij,hwj->hwi",vp[i-1]@np.linalg.inv(vp[i]),clip)
            prevuv=prev[...,:2]/prev[...,3,None]*.5+.5
            if flip: prevuv[...,1]=1-prevuv[...,1]
            predictions[i]=(uv-prevuv)*pixels
        good=mask&valid_frame[:,None,None]
        error=np.linalg.norm(predictions-raw,axis=-1)
        expected[str(flip)]=dict(median_error_px=float(np.median(error[good])),mean_error_px=float(np.mean(error[good])),
            p95_error_px=float(np.quantile(error[good],.95)),prediction_mean_px=float(np.mean(np.linalg.norm(predictions,axis=-1)[good])))
        np.save(path/("camera-motion-flipy-"+str(flip)+".npy"),predictions.astype(np.float32))
    output["camera_reprojection_audit"]=expected
    output["cadence_classification"]=classify_cadence(mean[valid_frame],step[valid_frame],expected["False"]["p95_error_px"])
    sign_errors={}
    for x in (-1,1):
        for y in (-1,1): sign_errors[f"{x},{y}"]=float(np.mean(np.linalg.norm(consumed-raw*[x,y],axis=-1)[mask&valid_frame[:,None,None]]))
    output["consumed_vs_raw_sign_audit_px"]=sign_errors
    best_sign=min(sign_errors,key=sign_errors.get)
    sign=np.array([int(x) for x in best_sign.split(",")])
    output["best_motion_sign"]=best_sign
    if m.get("candidate")=="motion-ema":
        output["ema_recurrence_mean_error_px"]=ema_error(raw,consumed,sign,pair_valid,region)
    magnitudes=np.linalg.norm(consumed,axis=-1)
    output["consumed_motion_highpass_rms_px"]=float(np.sqrt(np.mean((np.diff(magnitudes,axis=0)[pair_valid][...,region])**2)))
    output["raw_motion_highpass_rms_px"]=float(np.sqrt(np.mean((np.diff(magnitude,axis=0)[pair_valid][...,region])**2)))
    for k,c in colors.items():
        means=[]; differences=[]
        prev=None
        for image in c:
            img=np.asarray(image[40:140,40:280,:3],np.float32)
            means.append(float(np.mean(img)))
            if prev is not None: differences.append(float(np.mean(np.abs(img-prev))))
            prev=img
        differences=np.array(differences)
        output[k+"_mean_abs_frame_change"]=float(np.mean(differences[pair_valid]))
        output[k+"_frame_change_vs_physics_step_correlation"]=correlation(differences[pair_valid],step[1:][pair_valid])
        output[k+"_mean_luminance"]=float(np.mean(means))
    plot=Image.new("RGB",(1400,520),"white"); draw=ImageDraw.Draw(plot)
    draw.text((10,10),m["label"]+" | blue: mean terrain raw motion (px), orange: physics steps, red: camera translation (m)",fill="black")
    for j,(signal,col) in enumerate(((mean,"blue"),(step.astype(float),"orange"),(movement,"red"))):
        top=50+j*150; mx=max(1e-6,float(np.quantile(signal,.99)))
        draw.text((10,top),f"scale {mx:.6f}",fill=col)
        draw.line([(15+int((times[i]-times[0])/(times[-1]-times[0])*1360),top+130-int(min(1,float(value)/mx)*110)) for i,value in enumerate(signal)],fill=col,width=1)
    plot.save(path/"motion-timeseries.png")
    if colors:
        # Sparse context, uniformly tone mapped; not a fine-detail quality score.
        sheet=Image.new("RGB",(4*320,3*202),"#202020"); d=ImageDraw.Draw(sheet)
        for i,idx in enumerate(np.linspace(0,n-1,12).astype(int)):
            a=np.asarray(colors["output"][idx,:,:,:3],np.float32); a=np.clip(a,0,1)**(1/2.2)
            x,y=(i%4)*320,(i//4)*202; sheet.paste(Image.fromarray((a*255).astype(np.uint8)).transpose(Image.Transpose.FLIP_TOP_BOTTOM),(x,y))
            d.text((x+3,y+182),f"{idx}: {times[idx]-times[0]:.3f}s",fill="white")
        sheet.save(path/"scene-contact.png")
    (path/"analysis.json").write_text(json.dumps(output,indent=2,allow_nan=False))
    return output


if __name__=="__main__":
    parser=argparse.ArgumentParser(); parser.add_argument("run",type=Path)
    parser.add_argument("--require-smooth",action="store_true",help="Fail if stepped cadence or insufficient moving samples remain; use only when testing an intended smoothing fix")
    a=parser.parse_args()
    paths=sorted(a.run.rglob("frames.jsonl"))
    if not paths: raise SystemExit("No frame metadata: incomplete/invalid capture")
    results=[analyze(p.parent) for p in paths]
    (a.run/"motion-analysis.json").write_text(json.dumps(results,indent=2,allow_nan=False))
    print(json.dumps(results,indent=2,allow_nan=False))
    if a.require_smooth and any(r["cadence_classification"]!="motion_not_dominated_by_physics_steps" for r in results):
        raise SystemExit("Cadence gate failed or lacked sufficient moving samples; inspect motion-analysis.json")
