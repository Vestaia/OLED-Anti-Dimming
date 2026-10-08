"""Empirical scene-moment correction, selected without photographic labels."""
import argparse,csv,json
from pathlib import Path
import numpy as np
from scipy.interpolate import RBFInterpolator

PROBE=np.array([.4,.4,.4,.16,.16,.16,.4,.4,.16,.16,0.,1.,0.,0.])

def state(record):
 a=record['area'];m=np.asarray(record['moments'])
 # RGB first/second moments, minimum/maximum RGB means and squares, occupancy bins.
 # These are descriptive statistics; no claim about physical subpixel power.
 return np.r_[a,(a-.01)*m+.01*PROBE]

def polynomial(x):
 a=x[:,0]-.01;m=x[:,1:]-.01*PROBE
 return np.column_stack((a,a*a,a**3,m,a[:,None]*m))

class SceneSurface:
 def __init__(self,x,y,kind='ridge',smooth=.01):
  self.x=np.asarray(x);self.y=np.asarray(y);self.kind=kind;self.smooth=smooth
  if kind=='ridge':
   b=polynomial(self.x);self.scale=np.maximum(np.std(b,axis=0),.02);b=b/self.scale
   self.coef=np.linalg.solve(b.T@b+smooth*np.eye(b.shape[1]),b.T@self.y)
  else:
   self.scale=np.maximum(np.std(self.x,axis=0),.05)
   self.rbf=RBFInterpolator(self.x/self.scale,self.y,kernel='gaussian',epsilon=.3,smoothing=smooth,degree=-1)
 def predict(self,x):
  x=np.atleast_2d(x)
  if self.kind=='ridge':y=polynomial(x)/self.scale@self.coef
  else:
   base=np.tile(np.r_[.01,.01*PROBE],(len(x),1))
   y=self.rbf(x/self.scale)-self.rbf(base/self.scale)
  return np.maximum(y,0)
 def signal(self,record):return float(250*np.exp(self.predict([state(record)])[0]))
 def save(self,path):Path(path).write_text(json.dumps(dict(kind=self.kind,smooth=self.smooth,x=self.x.tolist(),y=self.y.tolist(),peak=250),indent=2),encoding='utf-8')
 @classmethod
 def read(cls,path):
  d=json.loads(Path(path).read_text(encoding='utf-8'));return cls(d['x'],d['y'],d['kind'],d['smooth'])

def fit(observations,output):
 out=Path(output);out.mkdir(parents=True,exist_ok=True)
 meta=json.loads(Path('build/mixed250-assets/metadata.json').read_text(encoding='utf-8'));lookup={r['name']:r for r in meta['scenes']}
 rows=list(csv.DictReader(Path(observations).open()));closing={r['name']:abs(float(r['camera_code'])-float(r['reference_code'])) for r in rows if r['role']=='end_reference'}
 rows=[r for r in rows if r['role']=='matched' and closing.get(r['name'],999)<=.5]
 x=np.array([state(lookup[r['name']]) for r in rows]);y=np.log([float(r['signal_nits'])/250 for r in rows]);groups=np.array([lookup[r['name']]['scene'] for r in rows]);scores=[]
 for kind in ('ridge','rbf'):
  for smooth in (.001,.01,.1,1.):
   residual=[]
   for i in range(4):
    hold=np.isin(groups,np.unique(groups)[i::4]);model=SceneSurface(x[~hold],y[~hold],kind,smooth);residual.extend(model.predict(x[hold])-y[hold])
   scores.append(dict(kind=kind,smooth=smooth,log_signal_rmse=float(np.sqrt(np.mean(np.square(residual))))))
 scores.sort(key=lambda r:r['log_signal_rmse']);(out/'comparison.json').write_text(json.dumps(scores,indent=2),encoding='utf-8')
 models={}
 for kind in ('ridge','rbf'):
  best=next(v for v in scores if v['kind']==kind);models[kind]=SceneSurface(x,y,kind,best['smooth']);models[kind].save(out/(kind+'-model.json'))
 plan=[]
 for r in meta['scenes']:
  if r['training']:continue
  name=r['name'];a=r['area'];asset=r['asset']
  plan.append((name,0,0,.01,'reference',250,''))
  for role,signal in [('raw',250),('predicted_density',r['density_signal']),('predicted_ridge',models['ridge'].signal(r)),('predicted_rbf',models['rbf'].signal(r)),('match',models[scores[0]['kind']].signal(r))]:plan.append((name,0,0,a,role,signal,asset))
  plan.append((name,0,0,.01,'end_reference',250,''))
 with Path('build/mixed250-validation-plan.csv').open('w',newline='') as f:
  w=csv.writer(f);w.writerow(('name','hue','saturation','area','role','signal','asset','probe_base'));w.writerows([(*row,100) for row in plan])
 print(json.dumps(scores,indent=2));print(len(rows),'stable training targets;',len(plan),'photographic validation states')

if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('observations');p.add_argument('output');args=p.parse_args();fit(args.observations,args.output)
