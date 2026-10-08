"""Short slope-guided HSV experiment. Window fraction is not photometric APL."""
import argparse
import colorsys
import csv
from pathlib import Path


def make_plan(source, output):
    with source.open() as stream:
        rows = list(csv.DictReader(stream))
    primary = {}
    for c in ('red', 'green', 'blue'):
        primary[c] = sorted((float(r['area']), float(r['camera_code'])) for r in rows
                            if r['color'] == c and r['other'] == 'none' and r['kind'] != 'final_reference')
    colors = [('red', 0., 1.), ('green', 1/3, 1.), ('blue', 2/3, 1.),
              ('yellow', 1/6, 1.), ('cyan', .5, 1.), ('magenta', 5/6, 1.),
              ('warm_partial', .08, .45), ('teal_partial', .42, .65), ('violet_partial', .78, .35),
              ('red_half', 0., .5), ('green_half', 1/3, .5), ('blue_half', 2/3, .5), ('neutral', 0., 0.)]
    plan = []
    for name, h, s in colors:
        contribution = colorsys.hsv_to_rgb(h, s, 1)
        # RGB weights guide sample priority only, not the learned power model.
        candidates = {(.4, 0.)}
        for index, c in enumerate(('red', 'green', 'blue')):
            points = primary[c]
            for (a, y), (b, z) in zip(points, points[1:]):
                if b > a:
                    candidates.add(((a+b)/2, contribution[index]*abs(z-y)/(b-a)))
        scores = {}
        for area, score in candidates:
            bucket = round(area, 2)
            scores[bucket] = scores.get(bucket, 0)+score
        ranked = sorted(scores, key=lambda a: scores[a], reverse=True)
        areas = {.4, 1.}
        for a in ranked:
            if all(abs(a-b) >= .08 for b in areas):
                areas.add(a)
            if len(areas) == 3:
                break
        if name in ('red', 'green', 'blue', 'warm_partial', 'teal_partial', 'violet_partial'):
            areas.add(.15)
        plan.append((name, h, s, .01, 'reference'))
        for a in sorted(areas):
            plan.extend([(name, h, s, a, 'raw'), (name, h, s, a, 'match')])
        plan.append((name, h, s, .01, 'end_reference'))
    with output.open('w', newline='') as stream:
        writer = csv.writer(stream)
        writer.writerow(('name', 'hue', 'saturation', 'area', 'role'))
        writer.writerows(plan)
    print(f'{len(colors)} sparse HSV anchors; {len(plan)} planned states; high-slope areas prioritized')


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('source', type=Path)
    p.add_argument('output', type=Path)
    args = p.parse_args()
    make_plan(args.source, args.output)

