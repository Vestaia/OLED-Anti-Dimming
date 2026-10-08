"""Bounded validation-driven acquisition. Camera codes are matching errors, not luminance."""
import argparse,csv,json,struct,subprocess,time,colorsys,os
from pathlib import Path
import numpy as np

ROOT=Path(__file__).resolve().parents[1]
PROBE=np.array([.4,.4,.4,.16,.16,.16,.4,.4,.16,.16,0,1,0,0])
def frame_state(record):return (record['area']-.01)*np.asarray(record['moments'])+.01*PROBE
class RuntimeModel:
 def __init__(self,x,y):
  self.x=np.vstack((x,np.zeros(14)));self.y=np.r_[y,0];self.scale=np.maximum(self.x.std(0),.05);self.z=self.x/self.scale
  kernel=np.exp(-.09*((self.z[:,None]-self.z[None,:])**2).sum(2));self.coef=np.linalg.solve(kernel+.01*np.eye(len(self.x)),self.y)
  self.baseline=float(np.exp(-.09*(self.z*self.z).sum(1))@self.coef)
 def predict(self,x):return np.maximum(np.exp(-.09*((np.atleast_2d(x)[:,None]/self.scale-self.z[None,:])**2).sum(2))@self.coef-self.baseline,0)
 def signal(self,r):return float(250*np.exp(self.predict(frame_state(r))[0]))
 def save(self,path):Path(path).write_text(json.dumps(dict(version=1,features=14,peak=250,epsilon=.3,baseline=self.baseline,scale=self.scale.tolist(),centers=self.z.tolist(),coefficients=self.coef.tolist(),samples=len(self.y)-1,domain='linear BT.2020 whole-frame moments / 250 signal nits'),indent=2),encoding='utf-8')
def moments(image):
 p=image.reshape(-1,3);lo=p.min(1);hi=p.max(1)
 return np.r_[p.mean(0),(p*p).mean(0),lo.mean(),hi.mean(),(lo*lo).mean(),(hi*hi).mean(),(hi-lo).mean(),[(hi>v).mean() for v in (.25,.5,.75)]]
def texture(path):
 data=Path(path).read_bytes();w,h=struct.unpack('<II',data[:8]);return np.frombuffer(data[8:],'<f4').reshape(h,w,4)[:,:,:3].copy()
def write_texture(path,image):
 image=np.concatenate([image,np.ones((*image.shape[:2],1))],2).astype('<f4');path.write_bytes(struct.pack('<II',image.shape[1],image.shape[0])+image.tobytes())
def write_plan(path,records,model=None):
 with path.open('w',newline='') as f:
  w=csv.writer(f);w.writerow(('name','hue','saturation','area','role','signal','asset','probe_base'))
  for r in records:
   n=r['name'];w.writerow((n,0,0,.01,'reference',250,'',100))
   if model is not None:w.writerow((n,0,0,r['area'],'raw',250,r['asset'],100))
   w.writerow((n,0,0,r['area'],'raw' if model is None else 'predicted_adaptive',250 if model is None else model.signal(r),r['asset'],100))
   w.writerow((n,0,0,r['area'],'match',250 if model is None else model.signal(r),r['asset'],100))
   w.writerow((n,0,0,.01,'end_reference',250,'',100))
def acquire(plan,out):
 before=set(ROOT.glob('hsv-observations-*.csv'))
 p=subprocess.Popen([str(ROOT/'build/hdr-probe.exe'),'--hsv-calibrate',str(plan),'--exit-after-calibration'],cwd=ROOT)
 if p.wait()!=0:raise RuntimeError('Camera acquisition failed')
 created=set(ROOT.glob('hsv-observations-*.csv'))-before
 if len(created)!=1:raise RuntimeError('Expected one observation file')
 path=created.pop();out.write_bytes(path.read_bytes());rows=list(csv.DictReader(out.open()))
 expected={r['name'] for r in csv.DictReader(plan.open()) if r['role']=='end_reference'};closed={r['name'] for r in rows if r['role']=='end_reference'}
 if not expected<=closed:raise RuntimeError('Measurement cancelled or incomplete; calibration model not replaced')
 return rows
def seed_patterns(out):
 scenes=[];validation=[];yy,xx=np.indices((180,320));tiles=(xx//16+yy//16)
 palettes=[('white',[[1,1,1]]),('red',[[1,0,0]]),('green',[[0,1,0]]),('blue',[[0,0,1]])]
 for n in (2,3,4,5):
  for rotation in (0,.13):palettes.append((f'mix{n}_{rotation}',[colorsys.hsv_to_rgb((i/n+rotation)%1,.65 if i%2 else 1,1) for i in range(n)]))
 for v in (.01,.04,.1,.3):palettes.append((f'dark{v}',[[v,v,v]]))
 for name,palette in palettes:
  image=np.array(palette)[tiles%len(palette)];asset=out/(name+'.bin');write_texture(asset,image)
  for a in (.1,.25,.4,.7,1):scenes.append(dict(name=f'{name}_{a}',scene=name,area=a,asset=asset.resolve().as_posix(),moments=moments(image).tolist(),training=True,density_signal=250))
 for i in range(8):
  palette=[colorsys.hsv_to_rgb((i*.117+j*.27)%1,.25+i*.09,.1 if i<2 else .7 if i<5 else 1) for j in range(2+i%4)]
  image=np.array(palette)[tiles%len(palette)];asset=out/f'holdout{i}.bin';write_texture(asset,image)
  for a in (.4,1):validation.append(dict(name=f'holdout{i}_{a}',scene=f'holdout{i}',area=a,asset=asset.resolve().as_posix(),moments=moments(image).tolist(),training=False,density_signal=250))
 return dict(scenes=scenes+validation),scenes
def neighbors(validation,metadata,out,max_cases=4,threshold=.75):
 lookup={r['name']:r for r in metadata['scenes']};closing={r['name']:abs(float(r['camera_code'])-float(r['reference_code'])) for r in validation if r['role']=='end_reference'}
 # Rank one case per source to avoid spending the whole round on near duplicates.
 candidates=sorted((r for r in validation if r['role'] in ('predicted_adaptive','predicted_rbf') and closing.get(r['name'],999)<=.5),key=lambda r:abs(float(r['camera_code'])-float(r['reference_code'])),reverse=True)
 selected=[];seen=set()
 for row in candidates:
  r=lookup.get(row['name']);error=abs(float(row['camera_code'])-float(row['reference_code']))
  if r is None or error<threshold or r['scene'] in seen:continue
  seen.add(r['scene']);selected.append((r,error))
  if len(selected)>=max_cases:break
 generated=[]
 for i,(r,error) in enumerate(selected):
  image=texture(r['asset'])[::4,::4];a=r['area']
  # Local perturbations isolate geometry, brightness distribution, and saturation.
  variants=[('area',image,max(.02,a-.08) if a>.92 else min(1,a+.08)),('level',image*.85,a),('mix',.85*image+.15*image.mean(2,keepdims=True),a)]
  for label,img,area in variants:
   name=f'adapt_{out.name}_{i}_{label}';asset=out/(name+'.bin');write_texture(asset,img)
   generated.append(dict(name=name,scene=name,training=True,area=area,asset=asset.resolve().as_posix(),moments=moments(img).tolist(),density_signal=250,parent=r['name'],parent_error=error))
 return generated
def export_runtime(rows,metadata,path):
 lookup={r['name']:r for r in metadata['scenes']};closing={r['name']:abs(float(r['camera_code'])-float(r['reference_code'])) for r in rows if r['role']=='end_reference'}
 matched=[r for r in rows if r['role']=='matched' and closing.get(r['name'],999)<=.5 and r['name'] in lookup]
 # Deployment uses whole-screen moments, with no window-area input or fictitious probe.
 if not matched:raise RuntimeError('No stable camera matches to fit')
 model=RuntimeModel([frame_state(lookup[r['name']]) for r in matched],np.log([float(r['signal_nits'])/250 for r in matched]));model.save(path)
 data=json.loads(path.read_text(encoding='utf-8'));data['monitor_device']=metadata.get('monitor_device',os.environ.get('OLED_CALIBRATION_DISPLAY',''));path.write_text(json.dumps(data,indent=2),encoding='utf-8')
 return matched
def main():
 p=argparse.ArgumentParser();p.add_argument('--training',default='reports/low-load-refinement/combined-training.csv');p.add_argument('--validation',default='reports/low-load-refinement/validation.csv');p.add_argument('--metadata',default='build/mixed250-assets/metadata.json');p.add_argument('--output',default='reports/adaptive');p.add_argument('--rounds',type=int,default=1);p.add_argument('--cases',type=int,default=4);p.add_argument('--generate-only',action='store_true');p.add_argument('--export-only',action='store_true');p.add_argument('--fresh',action='store_true');p.add_argument('--validate-only',action='store_true');args=p.parse_args()
 out=ROOT/args.output;out.mkdir(parents=True,exist_ok=True)
 if not args.fresh:meta=json.loads((ROOT/args.metadata).read_text(encoding='utf-8'));rows=list(csv.DictReader((ROOT/args.training).open()));validation=list(csv.DictReader((ROOT/args.validation).open()))
 if args.validate_only:
  lookup={r['name']:r for r in meta['scenes']};closing={r['name']:abs(float(r['camera_code'])-float(r['reference_code'])) for r in rows if r['role']=='end_reference'};good=[r for r in rows if r['role']=='matched' and closing.get(r['name'],999)<=.5]
  model=RuntimeModel([frame_state(lookup[r['name']]) for r in good],np.log([float(r['signal_nits'])/250 for r in good]));write_plan(out/'validation-plan.csv',[r for r in meta['scenes'] if not r['training']],model);acquire(out/'validation-plan.csv',out/'validation.csv');return
 if args.fresh:
  meta,seeds=seed_patterns(out);meta['monitor_device']=os.environ.get('OLED_CALIBRATION_DISPLAY','');write_plan(out/'initial-plan.csv',seeds);rows=acquire(out/'initial-plan.csv',out/'initial.csv')
  lookup={r['name']:r for r in meta['scenes']};closing={r['name']:abs(float(r['camera_code'])-float(r['reference_code'])) for r in rows if r['role']=='end_reference'};good=[r for r in rows if r['role']=='matched' and closing.get(r['name'],999)<=.5]
  if len(good)<10:raise RuntimeError('Too few stable initial measurements')
  model=RuntimeModel([frame_state(lookup[r['name']]) for r in good],np.log([float(r['signal_nits'])/250 for r in good]));model.save(out/'runtime-model-pending.json')
  write_plan(out/'initial-validation-plan.csv',[r for r in meta['scenes'] if not r['training']],model);validation=acquire(out/'initial-validation-plan.csv',out/'initial-validation.csv')
 if not args.export_only:
  for iteration in range(args.rounds):
   folder=out/f'round-{iteration+1}';folder.mkdir(exist_ok=True);new=neighbors(validation,meta,folder,args.cases)
   if not new:break
   meta['scenes'].extend(new);write_plan(folder/'plan.csv',new)
   print(f'Adaptive round {iteration+1}: {len(new)} local patterns',flush=True)
   if args.generate_only:break
   rows.extend(acquire(folder/'plan.csv',folder/'observations.csv'))
   # Fit in memory; photographic labels enter training only when explicitly measured in this round.
   lookup={r['name']:r for r in meta['scenes']};closing={r['name']:abs(float(r['camera_code'])-float(r['reference_code'])) for r in rows if r['role']=='end_reference'}
   good=[r for r in rows if r['role']=='matched' and closing.get(r['name'],999)<=.5]
   model=RuntimeModel([frame_state(lookup[r['name']]) for r in good],np.log([float(r['signal_nits'])/250 for r in good]));model.save(out/'runtime-model-pending.json')
   hold=[r for r in meta['scenes'] if not r['training']];write_plan(folder/'validation-plan.csv',hold,model)
   validation=acquire(folder/'validation-plan.csv',folder/'validation.csv')
 (out/'metadata.json').write_text(json.dumps(meta,indent=2),encoding='utf-8')
 with (out/'combined-training.csv').open('w',newline='') as f:
  w=csv.DictWriter(f,fieldnames=rows[0].keys());w.writeheader();w.writerows(rows)
 good=export_runtime(rows,meta,out/'runtime-model.json');print(f'Exported GPU model from {len(good)} stable matches',flush=True)
if __name__=='__main__':main()
