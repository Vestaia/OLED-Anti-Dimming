# SPDX-License-Identifier: GPL-3.0-only
"""Offline heatmap comparison with fixed measured labels and interpolation."""
import sys
from pathlib import Path
sys.argv=[sys.argv[0],"--grid","12","12","128"]
# Reuse mapping definitions without repeating the dataset fitting/study.
exec(Path("tools/study_smoothed_hsv.py").read_text().split("names=[];states=[]")[0])
folder=OUT/"query-colors";axes=json.loads((folder/"axes.json").read_text());labels=axes["labels"];areas=axes["areas"]
query=[]
for row in range(len(labels)):
 for col in range(len(areas)):
  population=np.fromfile(folder/f"{row}-{col}.bin",dtype="<f8",offset=4).reshape(-1,4)
  query.append(splat(population))
query=np.array(query)
# Order rows by channel, then ascending brightness. Unqualified rows are 250 nits.
def row_key(label):
 if label=="White":return (0,250)
 if label in ("R","G","B"):return ({"R":1,"G":2,"B":3}[label],250)
 tokens=label.split()
 if tokens[-1]=="W":return (0,float(tokens[0][:-1]))
 return ({"W":0,"R":1,"G":2,"B":3}[tokens[0]],float(tokens[1][:-1]))
order=sorted(range(len(labels)),key=lambda i:row_key(labels[i]))
query=query.reshape(len(labels),len(areas),H,S,V)[order].reshape(-1,H,S,V)
labels=[("W" if row_key(labels[i])[0]==0 else ["W","R","G","B"][row_key(labels[i])[0]])+f" {row_key(labels[i])[1]:g}n" for i in order]
np.savez_compressed(OUT/"heatmap-query-states.npz",features=query,labels=labels,areas=areas)
for name,widths in [("unsmoothed",None),("narrow",(.75,.75,2)),("medium",(1.25,1.25,4)),("wide",(2,2,8))]:
 data=np.load(OUT/(name+"-states.npz"));features=data["features"];gains=data["log_gains"]
 q=query.reshape(len(query),-1) if widths is None else smooth(query,widths)
 # Pairwise Euclidean distances, no whitening or rescaling. Stable normalized
 # inverse-distance seventh-power weights; camera labels are log multipliers.
 distance=np.sqrt(np.maximum((q*q).sum(1)[:,None]+(features*features).sum(1)[None,:]-2*q@features.T,0))
 weights=(distance+.0001)**-7;weights/=weights.sum(1)[:,None]
 boost=100*np.expm1(weights@gains);values=boost.reshape(len(labels),len(areas))
 with (OUT/(name+"-corrections.csv")).open("w",newline="") as output:
  writer=csv.writer(output);writer.writerow(["color"]+[f"{100*a:g}%" for a in areas]);writer.writerows([[label]+list(row) for label,row in zip(labels,values)])
 fig,ax=plt.subplots(figsize=(14,12));im=ax.imshow(values,cmap="Blues",vmin=0,vmax=120,aspect="auto")
 ax.set_xticks(range(len(areas)),[f"{100*a:g}%" for a in areas]);ax.xaxis.tick_top();ax.set_yticks(range(len(labels)),labels)
 ax.set_title(f"12 x 12 x 128 HSV: {name} smoothing\nPredicted brightness correction (%)",pad=35)
 for row in range(len(labels)):
  for col in range(len(areas)):
   value=values[row,col];ax.text(col,row,f"+{value:.1f}",ha="center",va="center",fontsize=8,color="white" if value>75 else "#172554")
 ax.set_xticks(np.arange(-.5,len(areas),1),minor=True);ax.set_yticks(np.arange(-.5,len(labels),1),minor=True);ax.grid(which="minor",color="white",linewidth=2);ax.tick_params(which="minor",bottom=False,left=False)
 fig.text(.08,.025,"Existing measurements; exact reference mosaic included. Same inverse-distance power 7.\nV histogram capped at dataset peak 1000 nits; 2500-nit rows are outside sampled range.",fontsize=10)
 fig.tight_layout(rect=[.01,.065,.99,.99]);fig.savefig(OUT/(name+"-corrections-heatmap.png"),dpi=150);plt.close(fig)
 print("Updated",name,"heatmap",flush=True)
with (OUT/"README.md").open("a") as output:output.write("\nCorrection heatmaps use exact pattern populations including the reference mosaic,\nexisting measured/inferred gains, and normalized 1/(Euclidean distance+0.0001)^7\nweights on log gains. No neighborhood cutoff or PCA. Fixed epsilon is identical\nacross variants, whose distance scales differ. Query statistics cap V at1000nits,\nso2500nit queries collide with1000nit states; those rows are extrapolation limits,\nnot physical clipping. No new measured validation or active app change.\n")
