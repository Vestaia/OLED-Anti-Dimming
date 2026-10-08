"""Fill dark and sparse-highlight coverage gaps without photographic labels."""
import colorsys,csv,json,struct
from pathlib import Path
import numpy as np
from PIL import Image
out=Path('build/mixed250-assets')
meta=json.loads((out/'metadata.json').read_text(encoding='utf-8'))
meta['scenes']=[r for r in meta['scenes'] if not r['scene'].startswith('low_')]
scenes={}
for v in (0,.01,.03,.07,.1):scenes[f'low_gray{int(v*100):02}']=np.full((720,1280,3),v)
for h,name in ((0,'red'),(1/3,'green'),(2/3,'blue'),(.08,'warm')):
 scenes['low_'+name]=np.broadcast_to(np.array(colorsys.hsv_to_rgb(h,1 if name!='warm' else .5,.05)),(720,1280,3)).copy()
yy,xx=np.indices((720,1280));slot=(xx//24+13*(yy//24))%100
for density in (.03,.1,.25):
 for kind in ('white','mixed'):
  colors=np.array([[1,1,1]] if kind=='white' else [[1,1,1],[.1,1,.3],[1,.35,.05]])
  image=np.full((720,1280,3),.01);active=slot<int(density*100)
  image[active]=colors[(slot[active]%len(colors))]
  scenes[f'low_sparse{int(density*100):02}_{kind}']=image
plan=[]
for name,image in scenes.items():
 rgba=np.concatenate((image,np.ones((*image.shape[:2],1))),axis=2).astype('<f4');path=out/(name+'.bin');path.write_bytes(struct.pack('<II',image.shape[1],image.shape[0])+rgba.tobytes())
 Image.fromarray(np.uint8(np.clip(image,0,1)**(1/2.2)*255)).save(out/(name+'-preview.jpg'))
 sample=image[::10,::10].reshape(-1,3);lo=sample.min(axis=1);hi=sample.max(axis=1)
 moments=np.r_[sample.mean(axis=0),(sample**2).mean(axis=0),lo.mean(),hi.mean(),(lo**2).mean(),(hi**2).mean(),np.mean(hi-lo),np.mean(hi>.25),np.mean(hi>.5),np.mean(hi>.75)].tolist()
 areas=(.4,.7,1.) if name.startswith('low_sparse') and '03_' not in name else (.4,1.)
 for a in areas:
  group=f'{name}_a{int(a*100)}';asset=path.resolve().as_posix()
  meta['scenes'].append(dict(name=group,scene=name,area=a,asset=asset,training=True,moments=moments,density_signal=250))
  plan.extend([(group,0,0,.01,'reference',250,''),(group,0,0,a,'raw',250,asset),(group,0,0,a,'match',250,asset),(group,0,0,.01,'end_reference',250,'')])
(out/'metadata.json').write_text(json.dumps(meta,indent=2),encoding='utf-8')
with Path('build/low-load-plan.csv').open('w',newline='') as f:
 w=csv.writer(f);w.writerow(('name','hue','saturation','area','role','signal','asset','probe_base'));w.writerows([(*r,100) for r in plan])
print(len(scenes),'additional low-load patterns;',len(plan)//4,'measured states')
