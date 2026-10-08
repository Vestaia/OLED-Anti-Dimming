"""Replay settled-response traces to compare acquisition window sizes."""
import argparse,csv,json
from pathlib import Path
import numpy as np
p=argparse.ArgumentParser();p.add_argument('traces');p.add_argument('output');args=p.parse_args()
groups={}
for r in csv.DictReader(Path(args.traces).open()):
 if r['role']=='response_trace':groups.setdefault((r['name'],r['stimulus_ms']),[]).append(r)
results=[]
for n in (3,4,5,6):
 errors=[];times=[]
 for (name,start),r in groups.items():
  t=np.array([int(v['host_ms'])-int(start) for v in r]);y=np.array([float(v['camera_code']) for v in r]);late=y[t>=1500]
  noise=late.std()/np.sqrt(3);plateau=late.mean();q=[];floor=max(.08,6*noise);slope_floor=max(.012,3*noise/np.sqrt(2))
  for ti,yi in zip(t,y):
   if ti<85:continue
   q.append(yi)
   if len(q)<n:continue
   v=np.array(q[-n:]);diff=np.diff(v);slope=np.polyfit(np.arange(n),v,1)[0]
   stopped=abs(diff[-1])>=.7*abs(diff[-2]) or max(abs(diff[-1]),abs(diff[-2]))<floor/3
   if np.sqrt(np.mean(diff**2))<=floor and abs(slope)<=slope_floor and stopped:
    errors.append(abs(v[-3:].mean()-plateau));times.append(int(ti));break
 results.append(dict(history_frames=n,accepted_traces=len(times),maximum_bias_codes=max(errors),median_time_ms=float(np.median(times))))
Path(args.output).write_text(json.dumps(results,indent=2),encoding='utf-8');print(json.dumps(results,indent=2))
