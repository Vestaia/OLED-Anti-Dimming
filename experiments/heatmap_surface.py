"""Heat maps distinguish fitted surfaces from measured prediction validation."""
import argparse,csv,json
from pathlib import Path
import numpy as np
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
from agnostic_surface import Surface,load

def plot(model_path,observations,output):
 out=Path(output);out.mkdir(parents=True,exist_ok=True);model=Surface.read(model_path)
 h=np.linspace(0,1,181);a=np.linspace(.01,1,100)
 fig,axes=plt.subplots(1,4,figsize=(15,4),constrained_layout=True)
 data=[]
 for s in (0,.35,.7,1):
  xx=np.array([[aa,hh,s] for aa in a for hh in h]);data.append(100*(np.exp(model.predict(xx)).reshape(len(a),len(h))-1))
 limit=max(5,max(np.max(np.abs(d)) for d in data))
 for ax,s,d in zip(axes,(0,.35,.7,1),data):
  im=ax.imshow(d,origin='lower',aspect='auto',extent=(0,360,1,100),cmap='coolwarm',vmin=-limit,vmax=limit)
  ax.set(title=f'Saturation {s:g}',xlabel='Hue (degrees)',ylabel='Window area (%)')
 fig.colorbar(im,ax=axes,label='Required input signal change (%)')
 fig.suptitle('Fitted correction surface at 100 nominal signal; interpolated, not dense measurements')
 fig.savefig(out/'surface-heatmap.png',dpi=160);plt.close(fig)
 rows=list(csv.DictReader(Path(observations).open()));valid={r['name'] for r in rows if r['role']=='end_reference' and abs(float(r['camera_code'])-float(r['reference_code']))<=2}
 rows=[r for r in rows if r['name'] in valid and r['role'] in ('raw','predicted')]
 if not any(r['role']=='predicted' for r in rows):return
 sats=sorted({float(r['saturation']) for r in rows});hs=sorted({float(r['hue']) for r in rows});areas=sorted({float(r['area']) for r in rows})
 errors={role:[] for role in ('raw','predicted')};fig,axes=plt.subplots(2,len(sats),figsize=(7*len(sats),7),squeeze=False,constrained_layout=True)
 limit=max(1,max(abs(float(r['camera_code'])-float(r['reference_code'])) for r in rows))
 for j,s in enumerate(sats):
  for i,role in enumerate(('raw','predicted')):
   z=np.full((len(areas),len(hs)),np.nan)
   for r in rows:
    if float(r['saturation'])==s and r['role']==role:
     e=float(r['camera_code'])-float(r['reference_code']);z[areas.index(float(r['area'])),hs.index(float(r['hue']))]=e;errors[role].append(e)
   ax=axes[i,j];im=ax.imshow(z,origin='lower',aspect='auto',cmap='coolwarm',vmin=-limit,vmax=limit)
   ax.set_xticks(range(len(hs)),[f'{v*360:.0f}' for v in hs]);ax.set_yticks(range(len(areas)),[f'{v*100:g}' for v in areas]);ax.set(title=f'{role}, saturation {s:g}',xlabel='Hue (degrees)',ylabel='Window area (%)')
 fig.colorbar(im,ax=axes.ravel().tolist(),label='Camera code minus own small-window reference')
 reused=sum(r.get('measurement_source')=='raw_100_reused_for_boost_floor' for r in rows)
 fig.suptitle('Measured held-out colors: predicted corrections without feedback; shared color scale'+(f'\n{reused} boost-floor cells reuse measured nominal-signal observations' if reused else ''))
 fig.savefig(out/'validation-heatmap.png',dpi=160);plt.close(fig)
 summary={k:dict(count=len(v),camera_code_rmse=float(np.sqrt(np.mean(np.square(v)))),max_absolute_camera_error=float(np.max(np.abs(v)))) for k,v in errors.items()}
 summary['boost_floor_reused_observations']=reused
 summary['invalid_groups']=sorted({r['name'] for r in csv.DictReader(Path(observations).open())}-valid)
 (out/'validation-summary.json').write_text(json.dumps(summary,indent=2),encoding='utf-8');print(json.dumps(summary,indent=2))

if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('model');p.add_argument('observations');p.add_argument('output');args=p.parse_args();plot(args.model,args.observations,args.output)
