"""Small pattern training set; eight untouched photographic holdouts at 250 peak."""
import colorsys,csv,json,struct,urllib.request
from pathlib import Path
import numpy as np
from PIL import Image,ImageOps
from agnostic_surface import Surface

out=Path('build/mixed250-assets');out.mkdir(exist_ok=True)
sources=json.loads(Path('assets/benchmarks/manifest.json').read_text(encoding='utf-8'))
scenes={};records=[]
matrix=np.array([[.627404,.329283,.043313],[.069097,.919540,.011362],[.016391,.088013,.895595]])
for record in sources:
 name=record['name'];path=Path('assets/benchmarks')/record['file']
 import hashlib
 if hashlib.sha256(path.read_bytes()).hexdigest()!=record['sha256']:raise RuntimeError('Benchmark checksum failed')
 im=ImageOps.fit(Image.open(path).convert('RGB'),(1280,720));im.save(out/(name+'-preview.jpg'))
 s=np.asarray(im,dtype=float)/255;linear=np.where(s<=.04045,s/12.92,((s+.055)/1.055)**2.4)
 scenes[name]=linear@matrix.T;records.append(record)
for n in range(2,6):
 for variant in range(3):
  hs=[(i/n+variant*.093)%1 for i in range(n)]
  ss=[1 if variant==0 else .25+.65*(i%2) if variant==1 else .7 for i in range(n)]
  vs=[1 if variant<2 else .12+.88*i/(n-1) for i in range(n)]
  palette=np.array([colorsys.hsv_to_rgb(h,s,v) for h,s,v in zip(hs,ss,vs)])
  yy,xx=np.indices((720,1280));scenes[f'checker{n}_v{variant}']=palette[(xx//40+yy//40)%n]
# Neutral/low-value fields resolve a value dimension absent from the 100-peak HSV fit.
for v in (.15,.4,.7,1.):scenes[f'neutral{int(v*100)}']=np.full((720,1280,3),v)

def moments(image):
 sample=image[::10,::10].reshape(-1,3);lo=sample.min(axis=1);hi=sample.max(axis=1)
 return np.r_[sample.mean(axis=0),(sample**2).mean(axis=0),lo.mean(),hi.mean(),(lo**2).mean(),(hi**2).mean(),np.mean(hi-lo),np.mean(hi>.25),np.mean(hi>.5),np.mean(hi>.75)].tolist()

model=Surface.read('reports/surface-170952609/model.json');metadata=[];plan=[]
for name,image in scenes.items():
 rgba=np.concatenate((image,np.ones((*image.shape[:2],1))),axis=2).astype('<f4');path=out/(name+'.bin');path.write_bytes(struct.pack('<II',image.shape[1],image.shape[0])+rgba.tobytes())
 if name.startswith(('checker','neutral')):Image.fromarray(np.uint8(np.clip(image,0,1)**(1/2.2)*255)).save(out/(name+'-preview.jpg'))
 sample=image[::10,::10].reshape(-1,3);hsv=np.array([colorsys.rgb_to_hsv(*v) for v in sample]);hs=np.round(hsv[:,:2]*24)/24;unique,inv=np.unique(hs,axis=0,return_inverse=True);value=hsv[:,2]
 for area in (.2,.4,.7,1.):
  effective=max(.01,area*float(value.mean()));gain=np.average(model.predict(np.column_stack((np.full(len(unique),effective),unique)))[inv],weights=np.maximum(value,.0001));signal=250*np.exp(gain)
  group=f'{name}_a{int(area*100)}';asset=path.resolve().as_posix();training=name.startswith(('checker','neutral'))
  metadata.append(dict(name=group,scene=name,area=area,asset=asset,training=training,moments=moments(image),density_signal=float(signal)))
  if training:
   plan.extend([(group,0,0,.01,'reference',250,''),(group,0,0,area,'raw',250,asset),(group,0,0,area,'predicted_density',signal,asset),(group,0,0,area,'match',signal,asset),(group,0,0,.01,'end_reference',250,'')])
with Path('build/mixed250-training-plan.csv').open('w',newline='') as f:
 w=csv.writer(f);w.writerow(('name','hue','saturation','area','role','signal','asset','probe_base'));w.writerows([(*row,100) for row in plan])
(out/'metadata.json').write_text(json.dumps(dict(peak=250,sources=records,scenes=metadata),indent=2),encoding='utf-8')
print('16 training patterns,',sum(v['training'] for v in metadata),'training states; 8 photographs withheld')
