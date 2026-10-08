"""Coverage plus disagreement/curvature weighted sparse acquisition."""
import csv
import numpy as np
from pathlib import Path
from agnostic_surface import Surface,load
x,y,g,_=load('reports/hsv-169782187/observations.csv')
a=Surface(x,y,'rbf',.01);b=Surface(x,y,'spline',.1)
rows=[]
for s in (0.,.35,.7,1.):
 for i in range(1 if s==0 else 12):
  h=i/12;name=f'h{i:02}_s{int(s*100):02}'
  grid=np.linspace(.1,.9,17);xx=np.array([[v,h,s] for v in grid]);v=a.predict(xx)
  score=np.abs(v-b.predict(xx))+.3*np.abs(np.gradient(v,grid))+.01*np.abs(np.gradient(np.gradient(v,grid),grid))
  areas={.4,1.}
  for j in np.argsort(score)[::-1]:
   if all(abs(grid[j]-v)>.09 for v in areas):areas.add(round(float(grid[j]),3));break
  if s in (0.,1.) and i%4==0:areas.add(.15)
  rows.append((name,h,s,.01,'reference',100))
  for area in sorted(areas):
   rows.extend([(name,h,s,area,'raw',100),(name,h,s,area,'match',a.signal(h,s,area))])
  rows.append((name,h,s,.01,'end_reference',100))
with Path('build/dense-plan.csv').open('w',newline='') as f:
 w=csv.writer(f);w.writerow(('name','hue','saturation','area','role','signal'));w.writerows(rows)
print(len(rows),'states;',sum(r[4]=='match' for r in rows),'feedback matches')
