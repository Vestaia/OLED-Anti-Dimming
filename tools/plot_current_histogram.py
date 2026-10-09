# SPDX-License-Identifier: GPL-3.0-only
"""Detailed predictions from a saved version-12 model; no app/camera changes."""
import argparse, csv, json, math, time
from pathlib import Path
import numpy as np
from threadpoolctl import threadpool_limits
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

parser=argparse.ArgumentParser()
parser.add_argument("run",type=Path)
parser.add_argument("--output",type=Path,required=True)
parser.add_argument("--shape",nargs=3,type=int)
parser.add_argument("--value-smoothing",type=float)
parser.add_argument("--smoothing",nargs=3,type=float)
parser.add_argument("--bandwidth",type=float)
parser.add_argument("--spike-amplitude",type=float)
parser.add_argument("--spike-scale",type=float)
parser.add_argument("--spike-kind",choices=["exponential","gaussian"],default="exponential")
parser.add_argument("--weight-kind",choices=["combined","exponential","sqrt-exponential","cubic-exponential"],default="combined")
args=parser.parse_args()
threadpool_limits(4)
start=time.perf_counter()
model=json.loads((args.run/"runtime-model.json").read_text(encoding="utf-8"))
meta=json.loads((args.run/"metadata.json").read_text(encoding="utf-8"))
assert model["version"] in (12,13)
model.setdefault("spike_scale",.00088)
model.setdefault("spike_amplitude",10)
if args.bandwidth is not None:
    assert args.bandwidth>0
    model["bandwidth"]=args.bandwidth
if args.spike_amplitude is not None:
    assert args.spike_amplitude>0
    model["spike_amplitude"]=args.spike_amplitude
if args.spike_scale is not None:
    assert args.spike_scale>0
    model["spike_scale"]=args.spike_scale
centers=np.asarray(model["centers"],dtype=np.float64)
gains=np.asarray(model["coefficients"],dtype=np.float64)
peak=model["peak_content_nits"]
old_shape=tuple(model["histogram_shape"])
H,S,V=tuple(args.shape or old_shape)
assert H>=3 and S>=2 and V>=2
smoothing=list(args.smoothing or model["smoothing"])
if args.shape and not args.smoothing:
    smoothing=[smoothing[0]*H/old_shape[0],smoothing[1]*(S-1)/(old_shape[1]-1),smoothing[2]*(V-1)/(old_shape[2]-1)]
if args.value_smoothing is not None:smoothing[2]=args.value_smoothing
assert all(b>0 for b in smoothing)
# Keep the L2 distance convention of the 9,216-bin reference across resolutions.
distance_scale=math.sqrt(H*S*V/9216)
matrices=[];transforms=[]
for axis,(old_n,n,old_width,new_width) in enumerate(zip(old_shape,(H,S,V),model["smoothing"],smoothing)):
    old_d=np.abs(np.arange(old_n)[:,None]-np.arange(old_n)[None,:])
    if axis==0:old_d=np.minimum(old_d,old_n-old_d)
    old=np.exp(-old_d/old_width);old/=old.sum(0)
    d=np.abs(np.arange(n)[:,None]-np.arange(n)[None,:])
    if axis==0:d=np.minimum(d,n-d)
    new=np.exp(-d/new_width);new/=new.sum(0);matrices.append(new)
    rebin=np.zeros((n,old_n))
    for source in range(old_n):
        coordinate=source*n/old_n if axis==0 else source*(n-1)/(old_n-1)
        lo=int(math.floor(coordinate));fraction=coordinate-lo
        rebin[lo%n,source]+=1-fraction
        rebin[(lo+1)%n if axis==0 else min(lo+1,n-1),source]+=fraction
    transforms.append((axis,np.linalg.solve(old.T,(new@rebin).T).T))
def resmooth(features):
    for axis,transform in transforms:
        dimension=features.ndim-3+axis
        features=np.moveaxis(np.moveaxis(features,dimension,-1)@transform.T,-1,dimension)
    return features
centers=resmooth(centers.reshape(-1,*old_shape)).reshape(-1,H*S*V)
assert centers.min()>-1e-10 and np.max(np.abs(centers.sum(1)-1))<1e-9
print("Mapped anchors to",(H,S,V),"smoothing",smoothing,"distance scale",distance_scale,flush=True)

def solid(rgb):
    value=max(rgb);delta=value-min(rgb);sat=delta/value if value else 0;hue=0
    if delta:
        hue=((rgb[1]-rgb[2])/delta if value==rgb[0] else (rgb[2]-rgb[0])/delta+2 if value==rgb[1] else (rgb[0]-rgb[1])/delta+4)/6%1
    coords=np.array([hue*H,sat*sat*(S-1),math.sqrt(min(value/peak,1))*(V-1)])
    lo=np.floor(coords).astype(int);f=coords-lo;raw=np.zeros((H,S,V))
    for ds in range(2):
        for dv in range(2):
            weight=(f[1] if ds else 1-f[1])*(f[2] if dv else 1-f[2])
            si=min(lo[1]+ds,S-1);vi=min(lo[2]+dv,V-1)
            raw[:,si,vi]+=weight*(1-sat*sat)/H
            raw[lo[0],si,vi]+=weight*sat*sat*(1-f[0])
            raw[(lo[0]+1)%H,si,vi]+=weight*sat*sat*f[0]
    raw=np.einsum("ah,hsv->asv",matrices[0],raw)
    raw=np.einsum("bs,asv->abv",matrices[1],raw)
    return np.einsum("cv,abv->abc",matrices[2],raw).ravel()

# Reuse the exact black-plus-center-mosaic state created by the application.
with (args.run/"initial-plan.csv").open() as f:
    zero=next(row for row in csv.DictReader(f) if row["role"]=="match" and "-0_" in row["name"])
with (args.run/"initial-plan.csv.states.bin").open("rb") as f:
    f.seek(int(zero["state_index"])*math.prod(old_shape)*8)
    base=np.frombuffer(f.read(math.prod(old_shape)*8),dtype="<f8").copy()
base=resmooth(base.reshape(old_shape)).ravel()
black=solid([0,0,0])
width,height=meta["display_width"],meta["display_height"]
side=max(2,min(min(width,height)//2*2,int(math.sqrt(width*height*.01)/2)*2))
areas=np.linspace(0,1,401)
fractions=[]
for area in areas:
    half=np.float32(math.sqrt(np.float32(area)))*np.float32(.5)
    axis_counts=[]
    for length in (width,height):
        uv=(np.arange(length,dtype=np.float32)+np.float32(.5))/length
        inside=np.abs(uv-np.float32(.5))<=half
        origin=(length-side)//2
        probe=(np.arange(length)>=origin)&(np.arange(length)<origin+side)
        axis_counts.append((inside.sum(),(inside&probe).sum()))
    fractions.append((axis_counts[0][0]*axis_counts[1][0]-axis_counts[0][1]*axis_counts[1][1])/(width*height))
fractions=np.array(fractions)
sqrt_values=np.linspace(0,math.sqrt(peak),321)
brightness=sqrt_values**2
norm_base=base@base
center_constant=norm_base+(centers*centers).sum(1)-2*(centers@base)
channels={"White":np.ones(3),"Red":np.array([1,0,0]),"Green":np.array([0,1,0]),"Blue":np.array([0,0,1])}
maps={}
args.output.mkdir(parents=True,exist_ok=True)
for label,color in channels.items():
    deltas=np.array([solid(color*nits)-black for nits in brightness])
    dot=centers@deltas.T
    base_dot=deltas@base
    delta_norm=(deltas*deltas).sum(1)
    values=[]
    for row in range(len(brightness)):
        d2=center_constant[None,:]+2*fractions[:,None]*(base_dot[row]-dot[:,row])[None,:]+fractions[:,None]**2*delta_norm[row]
        d=np.sqrt(np.maximum(d2,0))*distance_scale
        spike_exponent=-d*d/(2*model["spike_scale"]**2) if args.spike_kind=="gaussian" else -d/model["spike_scale"]
        log_weights=-(np.abs(d)/model["bandwidth"])**3 if args.weight_kind=="cubic-exponential" else -np.sqrt(d/model["bandwidth"]) if args.weight_kind=="sqrt-exponential" else -d/model["bandwidth"] if args.weight_kind=="exponential" else np.logaddexp(-d*d/(2*model["bandwidth"]**2),math.log(model["spike_amplitude"])+spike_exponent)
        weights=np.exp(log_weights-log_weights.max(1)[:,None]);weights/=weights.sum(1)[:,None]
        values.append(100*np.expm1(weights@gains))
    maps[label]=np.array(values)
    print(label,"complete",round(time.perf_counter()-start,1),"s",flush=True)

fig,axes=plt.subplots(2,2,figsize=(17,12),layout="constrained")
vmax=float(max(np.max(v) for v in maps.values()))
for ax,(label,values) in zip(axes.ravel(),maps.items()):
    image=ax.imshow(values,origin="lower",aspect="auto",extent=[0,100,0,math.sqrt(peak)],cmap="magma",vmin=0,vmax=vmax,interpolation="nearest")
    ax.set_title(label,fontsize=15)
    ticks=np.array([0,25,50,100,200,350,500,750,1000,1500,2000,2500]);ticks=ticks[ticks<=peak]
    ax.set_yticks(np.sqrt(ticks),[f"{n:g}" for n in ticks]);ax.set_xticks(np.arange(0,101,10))
    ax.set_xlabel("Window area (%)");ax.set_ylabel("HSV V (nit-scaled channel amplitude; sqrt axis)")
    grid=[scene for scene in meta["scenes"] if scene.get("grid_brightness",0)>0 and ((label=="White" and scene.get("grid_saturation")==0) or (label!="White" and scene.get("grid_saturation")==1 and scene.get("grid_hue")=={"Red":0,"Green":120,"Blue":240}[label]))]
    ax.scatter([s["area"]*100 for s in grid],[math.sqrt(s["grid_brightness"]) for s in grid],s=7,facecolors="none",edgecolors="white",alpha=.5,linewidths=.5)
fig.colorbar(image,ax=axes.ravel().tolist(),label="Predicted uniform brightness correction (%)",shrink=.85)
weight_label=f"cubic exponential; h={model['bandwidth']:g}" if args.weight_kind=="cubic-exponential" else f"sqrt exponential; h={model['bandwidth']:g}" if args.weight_kind=="sqrt-exponential" else f"exponential only; h={model['bandwidth']:g}" if args.weight_kind=="exponential" else f"{args.spike_kind} spike; h={model['bandwidth']:g}, A={model['spike_amplitude']:g}, s={model['spike_scale']:g}"
fig.suptitle(f"Current calibration: {args.run.name}\nHistogram {H} x {S} x {V}; Laplace {tuple(round(x,3) for x in smoothing)}; {weight_label}",fontsize=17)
path=args.output/"current-calibration-detailed.png";fig.savefig(path,dpi=190);plt.close(fig)
np.savez_compressed(args.output/"predictions.npz",areas=areas,brightness=brightness,**maps)
summary={"source":str(args.run),"model_version":model["version"],"smoothing":smoothing,"bandwidth":model["bandwidth"],"spike_kind":args.spike_kind,"spike_amplitude":model["spike_amplitude"],"spike_scale":model["spike_scale"],"anchors":len(gains),"zero_anchors":int((gains==0).sum()),"grid":[401,321],"peak":peak,"ranges":{k:[float(v.min()),float(v.max())] for k,v in maps.items()}}
summary["weight_kind"]=args.weight_kind
summary["histogram_shape"]=[H,S,V]
summary["distance_scale"]=distance_scale
summary["migration"]="Unsmooth saved populations, linearly rebin, then apply target smoothing; measured gains unchanged"
(args.output/"summary.json").write_text(json.dumps(summary,indent=2),encoding="utf-8")
print(path,flush=True)
