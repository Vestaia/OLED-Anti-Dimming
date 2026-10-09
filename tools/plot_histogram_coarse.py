# SPDX-License-Identifier: GPL-3.0-only
"""Coarse table from a saved detailed histogram prediction grid."""
import argparse,csv,json
from pathlib import Path
import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
parser=argparse.ArgumentParser();parser.add_argument("folder",type=Path);args=parser.parse_args()
folder=args.folder;data=np.load(folder/"predictions.npz")
summary=json.loads((folder/"summary.json").read_text())
areas=data["areas"];brightness=data["brightness"]
target_areas=np.array([.05,.1,.2,.25,.4,.5,.6,.75,.8,1.])
levels=np.array([10,25,40,75,100,160,200,250,350,500,750,1000.])
names=[];rows=[]
for channel,short in [("White","W"),("Red","R"),("Green","G"),("Blue","B")]:
    values=data[channel]
    for nits in levels:
        vertical=np.array([np.interp(np.sqrt(nits),np.sqrt(brightness),values[:,j]) for j in range(len(areas))])
        names.append(f"{short} {nits:g}n");rows.append(np.interp(target_areas,areas,vertical))
matrix=np.array(rows)
with (folder/"coarse-table.csv").open("w",newline="") as f:
    writer=csv.writer(f);writer.writerow(["Color / HSV V"]+[f"{x*100:g}%" for x in target_areas]);writer.writerows([[name]+[f"{v:.2f}" for v in row] for name,row in zip(names,matrix)])
fig,ax=plt.subplots(figsize=(12,17));im=ax.imshow(matrix,cmap="magma",vmin=0,vmax=95,aspect="auto")
ax.set_xticks(range(len(target_areas)),[f"{100*a:g}%" for a in target_areas]);ax.xaxis.tick_top();ax.set_yticks(range(len(names)),names);ax.tick_params(axis="y",labelsize=9)
weight_label=f"cubic exponential; h={summary['bandwidth']:g}" if summary.get("weight_kind")=="cubic-exponential" else f"sqrt exponential; h={summary['bandwidth']:g}" if summary.get("weight_kind")=="sqrt-exponential" else f"exponential only; h={summary['bandwidth']:g}" if summary.get("weight_kind")=="exponential" else f"{summary.get('spike_kind','exponential')} spike; h={summary['bandwidth']:g}, A={summary.get('spike_amplitude',10):g}, s={summary['spike_scale']:g}"
ax.set_title(f"Histogram {tuple(summary.get('histogram_shape', [12,12,64]))}; Laplace {tuple(round(x,3) for x in summary['smoothing'])}; {weight_label}\nPredicted correction (%) by window area",pad=35)
for i,row in enumerate(matrix):
    for j,v in enumerate(row):ax.text(j,i,f"{v:.1f}",ha="center",va="center",fontsize=8,color="white" if v<60 else "#222222")
for y in [11.5,23.5,35.5]:ax.axhline(y,color="white",linewidth=2)
fig.colorbar(im,ax=ax,label="Correction (%)",fraction=.035,pad=.02)
fig.text(.05,.012,"Same saved calibration; values interpolated from the 401 x 321 prediction grid.\nBrightness labels are HSV V, not equal-luminance colors. Center probe and zero anchors included.",fontsize=9)
fig.tight_layout(rect=[0,.04,1,1]);fig.savefig(folder/"coarse-table.png",dpi=170);plt.close(fig)
print(folder/"coarse-table.png")
