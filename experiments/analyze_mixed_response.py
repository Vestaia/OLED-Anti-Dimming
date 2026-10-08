"""Compare early three-frame observations against later held plateaus."""
import argparse,csv,json
from pathlib import Path
import numpy as np
p=argparse.ArgumentParser();p.add_argument('observations');p.add_argument('output');args=p.parse_args()
rows=list(csv.DictReader(Path(args.observations).open()));groups={}
for r in rows:
 if r['role']=='response_trace':groups.setdefault((r['name'],r['stimulus_ms']),[]).append(r)
results=[]
for (name,stimulus),records in groups.items():
 t=np.array([float(r['host_ms'])-float(stimulus) for r in records]);y=np.array([float(r['camera_code']) for r in records]);late=y[t>=1500]
 if len(late)<8:continue
 end=float(late.mean());early=y[(t>=78)&(t<=180)][:3];band=max(.35,3*float(late.std()))
 stable=next((float(t[i]) for i in range(len(t)) if t[i]>=20 and np.all(np.abs(y[i:]-end)<=band)),None)
 results.append(dict(name=name,signal=float(records[0]['signal_nits']),early_camera_code=float(early.mean()) if len(early) else None,plateau_camera_code=end,early_minus_plateau=float(early.mean()-end) if len(early) else None,stable_to_end_ms=stable,band_codes=band))
Path(args.output).write_text(json.dumps(results,indent=2),encoding='utf-8');print(json.dumps(results,indent=2))
