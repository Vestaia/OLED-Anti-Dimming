# SPDX-License-Identifier: GPL-3.0-only
"""Offline distribution mapping and sparsity study; no runtime/model mutation."""
import csv,json,math,time,argparse
from pathlib import Path
import numpy as np
from scipy.spatial.distance import pdist,squareform
from threadpoolctl import threadpool_limits
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
threadpool_limits(1)
SRC=Path("build/kernel-embedding")
parser=argparse.ArgumentParser();parser.add_argument("--grid",nargs=3,type=int,default=[9,8,64]);args=parser.parse_args()
H,S,V=args.grid
OUT=Path("build/smoothed-hsv-study" if args.grid==[9,8,64] else "build/smoothed-hsv-study-"+"x".join(map(str,args.grid)));OUT.mkdir(parents=True,exist_ok=True)
meta=json.loads((SRC/"source-metadata.json").read_text(encoding="utf8"))
scenes={s["name"]:s for s in meta["scenes"]}
rows=list(csv.DictReader((SRC/"source-training.csv").open()))
closing={r["name"]:abs(float(r["camera_code"])-float(r["reference_code"]))/max(1e-12,float(r["reference_code"])) for r in rows if r["role"]=="end_reference"}
good={}
for r in rows:
 if r["role"] in ("matched","skipped_flat") and r["name"] in scenes and closing.get(r["name"],closing.get(scenes[r["name"]]["scene"],999))<=.05:
  if r["role"]=="matched" or r["name"] not in good or good[r["name"]]["role"]!="matched":good[r["name"]]=r
PEAK=meta["peak_content_nits"]
assert H%3==0 and min(H,S,V)>1

def splat(a):
 rgb=np.maximum(a[:,:3],0);weight=a[:,3]/a[:,3].sum()
 value=rgb.max(1);delta=value-rgb.min(1);sat=np.divide(delta,value,out=np.zeros_like(value),where=value>0)
 hue=np.zeros_like(value)
 for k in (2,1,0):
  mask=(value==rgb[:,k])&(delta>0);i,j=((1,2),(2,0),(0,1))[k]
  hue[mask]=(2*k+(rgb[mask,i]-rgb[mask,j])/delta[mask])/6
 hue%=1
 coords=[hue*H,sat**2*(S-1),np.sqrt(np.minimum(value*250/PEAK,1))*(V-1)]
 lower=[np.floor(c).astype(int) for c in coords];fractions=[c-l for c,l in zip(coords,lower)]
 out=np.zeros((H,S,V))
 # At the neutral axis hue is undefined. Fade smoothly to uniform hue weights
 # as saturation approaches zero; saturated pixels retain circular hue locality.
 chromatic=sat**2
 for hi in range(2):
  hw=fractions[0] if hi else 1-fractions[0];hidx=(lower[0]+hi)%H
  for si in range(2):
   sw=fractions[1] if si else 1-fractions[1];sidx=np.minimum(lower[1]+si,S-1)
   for vi in range(2):
    vw=fractions[2] if vi else 1-fractions[2];vidx=np.minimum(lower[2]+vi,V-1)
    w=weight*sw*vw
    np.add.at(out,(hidx,sidx,vidx),w*hw*chromatic)
    neutral=np.zeros((S,V));np.add.at(neutral,(sidx,vidx),w*hw*(1-chromatic)/H)
    out+=neutral[None,:,:]
 assert abs(out.sum()-1)<1e-9
 return out

def matrix(n,sigma,circular=False):
 d=np.abs(np.arange(n)[:,None]-np.arange(n)[None,:]);d=np.minimum(d,n-d) if circular else d
 a=np.exp(-.5*(d/sigma)**2);return a/a.sum(0)

def smooth(raw,widths):
 mh,ms,mv=[matrix(n,w,i==0) for i,(n,w) in enumerate(zip((H,S,V),widths))]
 x=np.einsum("ah,nhsv->nasv",mh,raw,optimize=True)
 x=np.einsum("bs,nasv->nabv",ms,x,optimize=True)
 return np.einsum("cv,nabv->nabc",mv,x,optimize=True).reshape(len(raw),-1)

def load(name):return np.fromfile(SRC/"colors"/(name+".bin"),dtype="<f8",offset=4).reshape(-1,4)

names=[];states=[];gains=[];roles=[];group={}
t0=time.perf_counter()
for name,r in good.items():
 state=splat(load(name));key=state.round(12).tobytes();gain=0 if r["role"]=="skipped_flat" else max(0,math.log(float(r["signal_nits"])/250))
 if key in group:gains[group[key]].append(gain)
 else:group[key]=len(states);names.append(name);states.append(state);gains.append([gain]);roles.append(r["role"])
names.append("black-anchor");states.append(splat(np.array([[0,0,0,1.]])));gains.append([0]);roles.append("anchor")
raw=np.array(states);gains=np.array([np.mean(g) for g in gains]);names=np.array(names)
np.savez_compressed(OUT/"unsmoothed-states.npz",features=raw,names=names,log_gains=gains)
print("Mapped",len(raw),"unique measured/inferred states in",round(time.perf_counter()-t0,2),"seconds",flush=True)

# Two exactly specified grayscale queries used in the prior investigation.
queries=[]
for area in (.34,.38):
 # Use identical sampled center mosaic from exported query-W populations.
 a=load(f"query-W-{area:g}").copy();mask=np.all(a[:,:3]==1,axis=1);assert mask.any();a[mask,:3]=.3
 queries.append(splat(a))
query_raw=np.array(queries)

summary=[]
for label,widths in [("unsmoothed",None),("narrow",(.75,.75,2)),("medium",(1.25,1.25,4)),("wide",(2,2,8))]:
 features=raw.reshape(len(raw),-1) if widths is None else smooth(raw,widths);query=query_raw.reshape(len(query_raw),-1) if widths is None else smooth(query_raw,widths)
 distances=squareform(pdist(features));np.fill_diagonal(distances,np.inf);nearest=distances.min(1);pairs=pdist(features)
 centered=features-features.mean(0);eigen=np.linalg.eigvalsh(centered@centered.T);eigen=np.maximum(eigen,0)[::-1];eigen/=eigen.sum()
 participation=1/(eigen@eigen);cumulative=np.cumsum(eigen)
 # Covariance effective rank is diagnostic only. No PCA projection is applied.
 record=dict(label=label,sigma_bins=widths,states=len(features),features=features.shape[1],pair_mean=float(pairs.mean()),pair_std=float(pairs.std()),pair_cv=float(pairs.std()/pairs.mean()),pair_p05=float(np.quantile(pairs,.05)),pair_p50=float(np.median(pairs)),pair_p95=float(np.quantile(pairs,.95)),diameter=float(pairs.max()),nearest_mean=float(nearest.mean()),nearest_median=float(np.median(nearest)),nearest_mean_over_diameter=float(nearest.mean()/pairs.max()),participation_dimension=float(participation),variance90_components=int(np.searchsorted(cumulative,.9)+1),variance99_components=int(np.searchsorted(cumulative,.99)+1))
 summary.append(record);np.savez_compressed(OUT/(label+"-states.npz"),features=features,names=names,log_gains=gains)
 neighbors=[]
 for area,q in zip((.34,.38),query):
  d=np.linalg.norm(features-q,axis=1);order=np.argsort(d)
  entries=[dict(name=str(names[i]),distance=float(d[i]),correction_percent=float(100*np.expm1(gains[i])),area=scenes.get(str(names[i]),{}).get("area"),role=roles[i]) for i in order[:15]]
  neighbors.append(dict(query_area=area,white_nits=75,nearest=entries))
 (OUT/(label+"-neighbors.json")).write_text(json.dumps(neighbors,indent=2))
 fig,ax=plt.subplots(1,2,figsize=(11,4));ax[0].hist(pairs/pairs.max(),bins=60,color="#2563eb",alpha=.8,label="All pairs");ax[0].hist(nearest/pairs.max(),bins=30,color="#ea580c",alpha=.8,label="Nearest neighbor");ax[0].set(xlabel="Euclidean distance / dataset diameter",ylabel="Count",title=f"{label}: distance distribution");ax[0].legend();ax[1].plot(np.arange(1,len(eigen)+1),cumulative);ax[1].set(xlim=(1,100),ylim=(0,1.01),xlabel="Covariance components (diagnostic only)",ylabel="Cumulative variance",title=f"Participation dimension: {participation:.2f}");fig.tight_layout();fig.savefig(OUT/(label+"-sparsity.png"),dpi=160);plt.close(fig)
 print(json.dumps(record),flush=True)
 print(label,"75nit white nearest",[(n["query_area"],[(x["name"],round(x["distance"],5)) for x in n["nearest"][:3]]) for n in neighbors],flush=True)
(OUT/"summary.json").write_text(json.dumps(summary,indent=2))
with (OUT/"summary.csv").open("w",newline="") as f:w=csv.DictWriter(f,fieldnames=summary[0]);w.writeheader();w.writerows(summary)
(OUT/"README.md").write_text(f"""# Smoothed HSV distribution study

Offline existing dataset: managed-20261008-185625-856. Exact raster color
populations previously exported including the 1% center mosaic. Stable matched
and inferred-zero labels only, duplicates merged, plus black anchor. No camera
acquisition; active model and app untouched.

{H} hue centers at multiples of {360/H:g} degrees; primaries aligned.
{S} centers uniformly spaced in S?; {V} centers uniformly spaced in sqrt(V/{PEAK:g}).
V=max linear BT.2020 channel, capped at {PEAK:g} nits for statistics only.
Trilinear mass assignment followed by separable Gaussian smoothing, column-
normalized at saturation/value boundaries to preserve mass, circular hue.
Hue-local mass is weighted by S?; remaining mass is uniform in hue so neutral
colors have no arbitrary hue. This design choice can be changed independently.

Widths (H,S,V), Gaussian standard deviations in grid intervals:
narrow (.75,.75,2), medium (1.25,1.25,4), wide (2,2,8).
Each normalized state has {H*S*V} features, no PCA projection/no whitening.
Distances are raw Euclidean distances; their global magnitude shrinks with
smoothing, so normalized distance distributions are reported as well.
Effective dimension uses covariance participation ratio (sum eigenvalues)? /
sum eigenvalues?. It describes this dataset, not guaranteed intrinsic dimension.
90/99% variance counts are diagnostics, not compression used in inference.
Zero anchors and sweep families influence sparsity; this is not a uniformly
random sample of all possible distributions. Low effective dimension does not
prove adequate calibration coverage or guarantee correct prediction distances.
""")
