"""Plot measured pattern colors from a completed calibration; never opens a camera."""
import argparse, csv, json, os, struct
from pathlib import Path
import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

parser=argparse.ArgumentParser()
parser.add_argument("calibration",type=Path)
parser.add_argument("--output",type=Path,default=Path("reports/gamut-sanity"))
a=parser.parse_args();a.output.mkdir(parents=True,exist_ok=True)
meta=json.loads((a.calibration/"metadata.json").read_text())
files=[a.calibration/"combined-training.csv"]
validations=sorted(a.calibration.rglob("*validation.csv"),key=lambda p:p.stat().st_mtime)
if validations: files.append(validations[-1])
roles={"raw","matched","predicted_adaptive","predicted_rbf","panel_plateau"}
measured=set()
for path in files:
    with path.open(newline="") as f:
        measured.update(r["name"] for r in csv.DictReader(f) if r["role"] in roles)
scenes=[s for s in meta["scenes"] if s["name"] in measured]
peak=meta["peak_content_nits"]
clouds={"Solid patterns":[],"Clustered / image patterns":[],"Reference mosaic":[]}
seen=set();missing=[]
for s in scenes:
    path=Path(s["asset"])
    if str(path) in seen:continue
    seen.add(str(path))
    if not path.exists():missing.append(str(path));continue
    data=path.read_bytes();w,h=struct.unpack_from("<II",data)
    colors=np.unique(np.frombuffer(data,dtype="<f4",offset=8).reshape(h*w,4)[:,:3],axis=0)*250
    group="Solid patterns" if len(colors)==1 else "Clustered / image patterns"
    clouds[group].append(colors)
if meta.get("mosaic_probe"):
    w,h=meta["display_width"],meta["display_height"]
    side=max(2,min(min(w,h)//2*2,int(np.sqrt(w*h*.01)/2)*2))
    yy,xx=np.indices((side,side),dtype=np.uint32)
    key=xx//2+yy*np.uint32(4099)
    def hashed(x):
        x=x.copy();x^=x>>16;x*=np.uint32(0x7feb352d);x^=x>>15;x*=np.uint32(0x846ca68b);x^=x>>16;return x
    probe=[]
    for c in range(3):
        values=(hashed(key+np.uint32((c*0x9e3779b9)&0xffffffff))&65535).astype(float)/65535*2
        probe.append(np.where(xx%2,2-values,values)*100)
    clouds["Reference mosaic"].append(np.stack(probe,axis=-1).reshape(-1,3))
clouds={k:np.concatenate(v) for k,v in clouds.items() if v}
fig=plt.figure(figsize=(16,8),layout="constrained")
for index,angle in enumerate([(25,35),(20,130)]):
    ax=fig.add_subplot(1,2,index+1,projection="3d")
    for label,rgb in clouds.items():
        coord=np.sqrt(np.maximum(rgb,0)/peak)
        # Display hues only; coordinate positions carry nominal signal brightness.
        display=rgb/np.maximum(rgb.max(axis=1,keepdims=True),1)
        colors=np.clip(display,0,1)**(1/2.2)
        size=32 if label=="Solid patterns" else 1 if label=="Reference mosaic" else 5
        ax.scatter(*coord.T,c=colors,s=size,alpha=.16 if label=="Reference mosaic" else .65,label=label,depthshade=False)
    ax.set(xlabel="sqrt(R / peak)",ylabel="sqrt(G / peak)",zlabel="sqrt(B / peak)",xlim=(0,1),ylim=(0,1),zlim=(0,1))
    ax.set_box_aspect((1,1,1));ax.view_init(*angle)
    if index==0:ax.legend(loc="upper left")
fig.suptitle(f"Last calibration: {len(scenes)} measured pattern/window states, {len(seen)} textures\nNominal unboosted BT.2020 color distributions; peak {peak:g} nits")
fig.savefig(a.output/"gamut-scatter.png",dpi=190)
fig.savefig(a.output/"gamut-scatter.svg")
summary={"calibration":str(a.calibration.resolve()),"measured_states":len(scenes),"textures":len(seen),"color_points":{k:len(v) for k,v in clouds.items()},"missing_assets":missing,"coordinates":"sqrt(linear BT.2020 channel nits / selected peak)","scope":"Measured nominal pattern colors, including shared reference; excludes skipped/unmeasured states and intermediate gain-search trials."}
(a.output/"samples.json").write_text(json.dumps(summary,indent=2))
(a.output/"README.md").write_text("# Last calibration gamut samples\n\n![](gamut-scatter.png)\n\nSource: "+str(a.calibration.resolve())+"\n\nAll measured nominal pattern colors are shown, including shared probe pixels.\nWindow sizes repeat colors and are counted as separate measured states, but\nidentical textures are drawn once. These are unboosted signals, not measured panel\nluminance or intermediate gain-search trials. Unmeasured/skipped states are excluded.\nSee samples.json for counts and provenance.\n")
print(json.dumps(summary))
