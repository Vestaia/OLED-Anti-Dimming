"""Photographic holdout heat maps; training and matching remain separate."""
import argparse,csv,json
from pathlib import Path
import numpy as np
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
from PIL import Image
p=argparse.ArgumentParser();p.add_argument('observations');p.add_argument('output');args=p.parse_args()
out=Path(args.output);out.mkdir(exist_ok=True,parents=True)
meta=json.loads(Path('build/mixed250-assets/metadata.json').read_text(encoding='utf-8'));rows=list(csv.DictReader(Path(args.observations).open()))
scenes=[r['name'] for r in meta['sources']];areas=(.2,.4,.7,1.)
ends={r['name']:abs(float(r['camera_code'])-float(r['reference_code'])) for r in rows if r['role']=='end_reference'}
valid={n for n,e in ends.items() if e<=.5};roles=('raw','predicted_density','predicted_ridge','predicted_rbf','calibrated');titles=('Raw','Density heuristic','Pattern-trained ridge','Pattern-trained RBF','Scene feedback (reference)')
maps=[];summary={}
for role in roles:
 z=np.full((len(scenes),len(areas)),np.nan);e=[]
 for r in rows:
  if r['name'] not in valid or r['role']!=role:continue
  scene=r['name'].rsplit('_a',1)[0];error=float(r['camera_code'])-float(r['reference_code']);z[scenes.index(scene),areas.index(float(r['area']))]=error;e.append(error)
 summary[role]=dict(count=len(e),camera_code_rmse=float(np.sqrt(np.mean(np.square(e)))),maximum_absolute_camera_error=float(np.max(np.abs(e)))) if e else None;maps.append(z)
limit=max(1,max(np.nanmax(np.abs(z)) for z in maps if not np.all(np.isnan(z))))
fig,axes=plt.subplots(1,5,figsize=(18,6),constrained_layout=True)
for ax,title,z in zip(axes,titles,maps):
 im=ax.imshow(z,aspect='auto',cmap='coolwarm',vmin=-limit,vmax=limit)
 ax.set_xticks(range(4),['20','40','70','100']);ax.set_yticks(range(len(scenes)),scenes if ax==axes[0] else ['']*len(scenes));ax.set(title=title,xlabel='Window area (%)')
 for i in range(z.shape[0]):
  for j in range(z.shape[1]):
   if np.isfinite(z[i,j]):ax.text(j,i,f'{z[i,j]:+.1f}',ha='center',va='center',fontsize=7,color='white' if abs(z[i,j])>limit*.55 else 'black')
fig.colorbar(im,ax=axes,label='Camera code minus own low-load reference')
training_path=out/'combined-training.csv' if (out/'combined-training.csv').exists() else out/'training.csv'
if training_path.exists():
 training_names={r['name'].rsplit('_a',1)[0] for r in csv.DictReader(training_path.open()) if r['role']=='matched'}
else:training_names={r['scene'] for r in meta['scenes'] if r['training']}
fig.suptitle(f'Eight photographic benchmarks: 250-nit nominal scene peak, fixed 100-nit white probe\nLearned predictors use {len(training_names)} synthetic patterns; photographs excluded from fitting; settling measured per stimulus')
fig.savefig(out/'photographic-holdout-heatmap.png',dpi=160);plt.close(fig)
fig,axes=plt.subplots(2,4,figsize=(14,5),constrained_layout=True)
for ax,name in zip(axes.flat,scenes):
 ax.imshow(Image.open('build/mixed250-assets/'+name+'-preview.jpg'));ax.set_title(name);ax.axis('off')
fig.savefig(out/'photographs.png',dpi=150);plt.close(fig)
summary['rejected_groups']=sorted({r['name'] for r in rows}-valid);summary['monitor_limited']=[r for r in rows if r['role'] in ('panel_plateau','pq_domain_limit')]
changed=[float(r['stable_ms']) for r in rows if float(r['stable_ms'])>0 and r['role'] not in ('matched','plateau_detected')]
summary['changed_stimulus_settling_ms']=dict(median=float(np.median(changed)),p90=float(np.percentile(changed,90)),maximum=max(changed)) if changed else None
summary['sources']=meta['sources'];(out/'validation-summary.json').write_text(json.dumps(summary,indent=2),encoding='utf-8')
print(json.dumps({k:v for k,v in summary.items() if k not in ('sources','monitor_limited')},indent=2))
