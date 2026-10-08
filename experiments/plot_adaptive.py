import csv,sys,json
from pathlib import Path
import numpy as np
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
out=Path(sys.argv[1]);files=list(out.glob('round-*/validation.csv'))
if (out/'validation.csv').exists():files.append(out/'validation.csv')
if (out/'initial-validation.csv').exists():files.append(out/'initial-validation.csv')
if not files:sys.exit(0)
path=max(files,key=lambda p:p.stat().st_mtime);rows=list(csv.DictReader(path.open()));names=list(dict.fromkeys(r['name'] for r in rows));roles=['raw','predicted_adaptive','calibrated']
closing={r['name']:abs(float(r['camera_code'])-float(r['reference_code'])) for r in rows if r['role']=='end_reference'}
meta=json.loads((out/'metadata.json').read_text(encoding='utf-8'));lookup={r['name']:r for r in meta['scenes']};scenes=list(dict.fromkeys(lookup[n]['scene'] for n in names));areas=sorted(set(lookup[n]['area'] for n in names));data=np.full((3,len(scenes),len(areas)),np.nan)
for r in rows:
 if r['role'] in roles and closing.get(r['name'],999)<=.5:
  scene=lookup[r['name']];data[roles.index(r['role']),scenes.index(scene['scene']),areas.index(scene['area'])]=float(r['camera_code'])-float(r['reference_code'])
fig,axes=plt.subplots(1,3,figsize=(12,max(4,len(scenes)*.48)),sharey=True);limit=max(1,np.nanmax(abs(data)))
for k,ax in enumerate(axes):
 im=ax.imshow(data[k],cmap='RdBu_r',vmin=-limit,vmax=limit,aspect='auto');ax.set_xticks(range(len(areas)),[f'{a*100:g}%' for a in areas]);ax.set_yticks(range(len(scenes)),scenes);ax.set_title(['Raw','Adaptive model','Feedback match'][k]);ax.set_xlabel('Window area')
 for i in range(len(scenes)):
  for j in range(len(areas)):
   if np.isfinite(data[k,i,j]):ax.text(j,i,f'{data[k,i,j]:+.2f}',ha='center',va='center',fontsize=9)
   else:ax.text(j,i,'×',ha='center',va='center',color='gray')
fig.suptitle('Relative camera-code error (not luminance %) · drift-rejected cases marked ×');fig.tight_layout(rect=(0,0,.93,.94));fig.colorbar(im,cax=fig.add_axes((.94,.15,.015,.7)));fig.savefig(out/'validation-heatmap.png',dpi=140);plt.close(fig)
summary={role:dict(samples=int(np.isfinite(data[k]).sum()),rms=float(np.sqrt(np.nanmean(data[k]**2))),max_abs=float(np.nanmax(abs(data[k])))) for k,role in enumerate(roles)};(out/'validation-summary.json').write_text(json.dumps(summary,indent=2),encoding='utf-8');print(json.dumps(summary))
