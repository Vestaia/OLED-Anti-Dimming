"""Mixed-scene benchmarks; aggregators are hypotheses, not power estimates."""
import colorsys,csv,json,struct,urllib.request
from pathlib import Path
import numpy as np
from PIL import Image
from agnostic_surface import Surface
out=Path('build/mixed-assets');out.mkdir(exist_ok=True)
m=Surface.read('reports/surface-170952609/model.json')
scenes={};sources=json.loads(Path('assets/benchmarks/manifest.json').read_text(encoding='utf-8'))
for record in sources:
 name=record['name'];path=Path('assets/benchmarks')/record['file']
 import hashlib
 if hashlib.sha256(path.read_bytes()).hexdigest()!=record['sha256']:raise RuntimeError('Benchmark checksum failed')
 im=Image.open(path).convert('RGB').resize((1280,720));im.save(out/(name+'-preview.jpg'))
 srgb=np.asarray(im,dtype=float)/255
 linear=np.where(srgb<=.04045,srgb/12.92,((srgb+.055)/1.055)**2.4)
 matrix=np.array([[.627404,.329283,.043313],[.069097,.919540,.011362],[.016391,.088013,.895595]])
 scenes[name]=linear@matrix.T
for n in range(2,6):
 for variant in range(3):
  hs=[(i/n+variant*.071)%1 for i in range(n)]
  ss=[1 if variant==0 else (.35+.55*(i%2)) if variant==1 else .65 for i in range(n)]
  vs=[1 if variant<2 else (.3+.7*i/(n-1)) for i in range(n)]
  palette=np.array([colorsys.hsv_to_rgb(h,s,v) for h,s,v in zip(hs,ss,vs)])
  yy,xx=np.indices((720,1280));index=(xx//40+yy//40)%n
  scenes[f'checker{n}_v{variant}']=palette[index]
metadata=[];plan=[]
for name,image in scenes.items():
 rgba=np.concatenate((image,np.ones((*image.shape[:2],1))),axis=2).astype('<f4')
 path=out/(name+'.bin');path.write_bytes(struct.pack('<II',image.shape[1],image.shape[0])+rgba.tobytes())
 if name.startswith('checker'):
  Image.fromarray(np.uint8(np.clip(image,0,1)**(1/2.2)*255)).save(out/(name+'-preview.jpg'))
 sample=image[::10,::10].reshape(-1,3);hsv=np.array([colorsys.rgb_to_hsv(*v) for v in sample]);average=sample.mean(axis=0);ah,asat,av=colorsys.rgb_to_hsv(*average)
 hs=np.round(hsv[:,:2]*24)/24;unique,inv=np.unique(hs,axis=0,return_inverse=True)
 for area in (.2,.4,1.):
  xx=np.column_stack((np.full(len(unique),area),unique));g=m.predict(xx)[inv];v=hsv[:,2]
  mean=100*np.exp(np.mean(g*(v>.01)))
  avg=m.signal(ah,asat,area)
  effective=max(.01,area*float(v.mean()))
  density=100*np.exp(float(np.average(m.predict(np.column_stack((np.full(len(unique),effective),unique)))[inv],weights=np.maximum(v,.0001))))
  group=f'{name}_a{int(area*100)}';asset=path.resolve().as_posix()
  plan.append((group,0,0,.01,'reference',100,''))
  for role,signal in [('raw',100),('predicted_mean',mean),('predicted_average',avg),('predicted_density',density),('match',mean)]:plan.append((group,0,0,area,role,signal,asset))
  plan.append((group,0,0,.01,'end_reference',100,''))
  metadata.append(dict(name=group,scene=name,area=area,mean_signal=mean,average_signal=avg,density_signal=density,mean_rgb=average.tolist(),mean_value=float(v.mean())))
with Path('build/mixed-plan.csv').open('w',newline='') as f:
 w=csv.writer(f);w.writerow(('name','hue','saturation','area','role','signal','asset'));w.writerows(plan)
(out/'metadata.json').write_text(json.dumps(dict(sources=sources,scenes=metadata),indent=2),encoding='utf-8')
print(len(scenes),'scenes;',len(metadata),'scene/window benchmarks;',len(plan),'planned states')
