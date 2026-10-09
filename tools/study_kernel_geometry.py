# SPDX-License-Identifier: GPL-3.0-only
"""Offline normalized geometry experiment. Does not modify runtime models."""
import csv
import json
import math
from pathlib import Path
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from scipy.optimize import root

OUT=Path("build/kernel-geometry-shape")
OUT.mkdir(parents=True,exist_ok=True)
TRANSITION_NITS=5.0
# Extra design choice: 25-nit window distance is 99% of its 250-nit value.
def equations(parameters):
    b,offset,normalization=parameters
    z=lambda v:b*math.sqrt(v)+offset*(-math.expm1(-v/TRANSITION_NITS))
    separation=lambda delta:-math.expm1(-delta*delta/2)
    return [2*separation(z(350)-z(250))-normalization,
            .04*2*separation(z(250))-normalization,
            separation(z(25))/.99**2-separation(z(250))]
fit=root(equations,[.0986,2.312,.079955])
assert fit.success and max(abs(v) for v in equations(fit.x))<1e-10
b,offset,normalization=map(float,fit.x)
color_scale=math.sqrt(-2*math.log1p(-normalization/2))
z=lambda v:b*math.sqrt(v)+offset*(-math.expm1(-v/TRANSITION_NITS))
point_distance=lambda delta:math.sqrt(2*(-math.expm1(-delta*delta/2))/normalization)
window_distance=lambda v,delta_population:abs(delta_population)*point_distance(z(v))
brightness_distance=lambda a,v1,v2:a*point_distance(z(v2)-z(v1))
references={"250_to_350_full":brightness_distance(1,250,350),
            "twenty_point_window_250":window_distance(250,.2),
            "120_degree_hue_full_saturation":point_distance(color_scale),
            "zero_to_one_saturation":point_distance(color_scale)}
assert all(abs(v-1)<1e-10 for v in references.values())
# Circular hue and saturation use the previous 3-coordinate geometry multiplied
# by color_scale. No change to hue wrapping or squared saturation.
parameters={"brightness_sqrt_coefficient":b,"brightness_offset":offset,
            "transition_nits":TRANSITION_NITS,"normalization":normalization,
            "color_coordinate_scale":color_scale,"bandwidth":1,"references":references}
(OUT/"parameters.json").write_text(json.dumps(parameters,indent=2))
first=[[v,window_distance(v,.4),window_distance(v,.9)] for v in [.1,1,5,10,25,50,75,100,250,500,1000]]
second=[[f"{a} -> {c} nits",brightness_distance(.1,a,c),brightness_distance(1,a,c)] for a,c in [(250,300),(250,350),(1000,1050),(1000,1500)]]

def render(filename,title,headers,rows,note):
    fig,ax=plt.subplots(figsize=(9,max(3.6,1.7+.4*len(rows))))
    fig.patch.set_facecolor("#f8fafc");ax.axis("off")
    ax.set_title(title,fontsize=16,fontweight="bold",loc="left",pad=22,color="#172554")
    display=[[str(row[0])+ (" nits" if isinstance(row[0],(int,float)) else ""),f"{row[1]:.5f}",f"{row[2]:.5f}"] for row in rows]
    table=ax.table(cellText=display,colLabels=headers,cellLoc="center",loc="center")
    table.auto_set_font_size(False);table.set_fontsize(11);table.scale(1,1.75)
    for (row,col),cell in table.get_celld().items():
        cell.set_edgecolor("#e2e8f0")
        cell.set_facecolor("#dbeafe" if row==0 else "#ffffff" if row%2 else "#eff6ff")
        if row==0:cell.set_text_props(fontweight="bold",color="#172554")
    fig.text(.08,.035,note,fontsize=9,color="#475569")
    fig.tight_layout(rect=[.02,.08,.98,.98]);fig.savefig(OUT/filename,dpi=180);plt.close(fig)
render("chart-1-window-distances.png","Chart 1: window population changes",
       ["White brightness","Distance: 10% -> 50%","Distance: 10% -> 100%"],first,
       "White on black; no reference mosaic. Gaussian bandwidth 1; distances are sqrt(D).")
render("chart-2-brightness-distances.png","Chart 2: brightness changes",
       ["Brightness transition","Distance: 10% coverage","Distance: 100% coverage"],second,
       "Identical black remainder; no reference mosaic. Current Gaussian MMD population scaling.")
for filename,rows in [("window-distances.csv",first),("brightness-distances.csv",second)]:
    with (OUT/filename).open("w",newline="") as output:csv.writer(output).writerows(rows)
(OUT/"README.md").write_text(f"""# Kernel geometry shape experiment

Offline only; application and selected calibration remain unchanged.
Bandwidth stays 1. The reference distances are full-screen250->350nits,
20-percentage-point white-window change at250nits, full-saturation120degree hue
change, and saturation0->1 at fixed brightness. All equal1.

Brightness coordinate: z(V) = {b:.12g} sqrt(V) + {offset:.12g}(1-exp(-V/5)).
V is max-channel linear brightness in nits. Scale previous three circular
hue/saturation coordinates by {color_scale:.12g}.
Normalize squared MMD by {normalization:.12g}.

Additional non-unique design choice:25nit window distances are99% of250nit;
5nit near-black transition constant. Population and kernel saturation behavior
remain unchanged. Gaussian MMD distance scales linearly with changed foreground
population when the remainder is identical; squared cost scales quadratically.
These charts are analytic point mixtures, not new camera measurements. General
clustered scenes require recomputing pixel coordinates and k-means statistics.
""")
print(json.dumps(parameters,indent=2))
print("Chart1",first)
print("Chart2",second)
