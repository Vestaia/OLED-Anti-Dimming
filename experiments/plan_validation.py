"""Withheld hue/saturation combinations, evaluated without observation matching."""
import argparse,csv
from pathlib import Path
from agnostic_surface import Surface
p=argparse.ArgumentParser();p.add_argument('model');p.add_argument('output');args=p.parse_args();m=Surface.read(args.model)
rows=[]
for s in (.5,.85):
 for i in range(8):
  h=(i+.5)/8;name=f'held_h{i}_s{int(s*100)}'
  rows.append((name,h,s,.01,'reference',100))
  for a in (.2,.4,.75,1.):rows.extend([(name,h,s,a,'raw',100),(name,h,s,a,'predicted',m.signal(h,s,a))])
  rows.append((name,h,s,.01,'end_reference',100))
for h,name in ((1/3,'check_green'),(2/3,'check_blue')):
 rows.append((name,h,1.,.01,'reference',100))
 for a in (.2,.4,.75,1.):rows.extend([(name,h,1.,a,'raw',100),(name,h,1.,a,'predicted',m.signal(h,1.,a))])
 rows.append((name,h,1.,.01,'end_reference',100))
with Path(args.output).open('w',newline='') as f:
 w=csv.writer(f);w.writerow(('name','hue','saturation','area','role','signal'));w.writerows(rows)
print(len(rows),'validation states')
