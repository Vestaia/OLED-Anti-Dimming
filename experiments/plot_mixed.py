"""Measured mixed-scene heat maps, with reference drift rejection."""
import argparse,csv,json
from pathlib import Path
import numpy as np
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
from PIL import Image

p=argparse.ArgumentParser();p.add_argument('observations');p.add_argument('output');args=p.parse_args()
out=Path(args.output);out.mkdir(parents=True,exist_ok=True)
rows=list(csv.DictReader(Path(args.observations).open()))
metadata=json.loads(Path('build/mixed-assets/metadata.json').read_text(encoding='utf-8'))
groups={}
for r in rows:groups.setdefault(r['name'],[]).append(r)
valid=[];invalid=[]
for name,records in groups.items():
 closing=next((r for r in records if r['role']=='end_reference'),None)
 if closing and abs(float(closing['camera_code'])-float(closing['reference_code']))<=.5:valid.append(name)
 else:invalid.append(name)
scenes=list(dict.fromkeys(s['scene'] for s in metadata['scenes']));areas=(.2,.4,1.)
roles=('raw','predicted_mean','predicted_average','predicted_density','calibrated')
titles=('Raw','Mean color correction','Average RGB color','Brightness density','Feedback-matched reference')
maps=[];summary={}
for role in roles:
 z=np.full((len(scenes),len(areas)),np.nan);err=[];checker=[];photo=[]
 for record in rows:
  if record['name'] in valid and record['role']==role:
   scene=record['name'].rsplit('_a',1)[0];area=float(record['area']);e=float(record['camera_code'])-float(record['reference_code'])
   z[scenes.index(scene),areas.index(area)]=e;err.append(e)
   (checker if scene.startswith('checker') else photo).append(e)
 def stats(v):return dict(count=len(v),rmse=float(np.sqrt(np.mean(np.square(v)))),maximum=float(np.max(np.abs(v)))) if v else None
 summary[role]=dict(all=stats(err),checkerboard=stats(checker),photographs=stats(photo));maps.append(z)
limit=max(1,max(np.nanmax(np.abs(z)) for z in maps))
fig,axes=plt.subplots(1,5,figsize=(18,8),constrained_layout=True)
for ax,title,z in zip(axes,titles,maps):
 im=ax.imshow(z,aspect='auto',cmap='coolwarm',vmin=-limit,vmax=limit)
 ax.set_xticks(range(3),['20','40','100']);ax.set_yticks(range(len(scenes)),scenes if ax==axes[0] else ['']*len(scenes));ax.set(title=title,xlabel='Window area (%)')
fig.colorbar(im,ax=axes,label='Camera code minus fixed white probe reference')
fig.suptitle('Measured mixed scenes: fixed 1% white center; three fresh frames per observation\nGlobal input scaling affects both surround and probe; candidates receive no feedback; blanks fail reference-drift checks')
fig.savefig(out/'mixed-error-heatmap.png',dpi=160);plt.close(fig)
fig,axes=plt.subplots(1,2,figsize=(12,4),constrained_layout=True)
for ax,name in zip(axes,('lake','night')):
 ax.imshow(Image.open('build/mixed-assets/'+name+'-preview.jpg'));ax.set_title(name+' benchmark (SDR source)');ax.axis('off')
fig.savefig(out/'image-benchmarks.png',dpi=150);plt.close(fig)
fig,axes=plt.subplots(3,4,figsize=(12,6),constrained_layout=True)
for ax,name in zip(axes.flat,sorted([s for s in scenes if s.startswith('checker')],key=lambda s:(int(s[-1]),int(s[7])))):
 ax.imshow(Image.open('build/mixed-assets/'+name+'-preview.jpg'));ax.set_title(name,fontsize=9);ax.axis('off')
fig.savefig(out/'checkerboard-benchmarks.png',dpi=150);plt.close(fig)
summary['rejected_groups']=invalid;summary['accepted_groups']=len(valid)
summary['reference_drift_threshold_codes']=.5;summary['sources']=metadata['sources']
duplicates=[]
for name,records in groups.items():
 if name not in valid:continue
 a=next((r for r in records if r['role']=='predicted_mean'),None)
 b=next((r for r in records if r['role']=='predicted_density'),None)
 if a and b and abs(float(a['signal_nits'])-float(b['signal_nits']))<.001:
  duplicates.append(float(a['camera_code'])-float(b['camera_code']))
summary['equal_signal_repeatability']=dict(count=len(duplicates),rms_difference=float(np.sqrt(np.mean(np.square(duplicates)))),maximum_difference=float(np.max(np.abs(duplicates)))) if duplicates else None
(out/'summary.json').write_text(json.dumps(summary,indent=2),encoding='utf-8')
print(json.dumps({k:v for k,v in summary.items() if k!='sources'},indent=2))
