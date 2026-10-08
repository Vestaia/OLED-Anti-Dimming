"""Prototype effective load -> white-probe camera response, never physical luminance.

Calibration is a 100-signal-nit BT.2020 RGB cube with a fixed 1% white probe.
Interpolated colors and all mixtures remain hypotheses, checked using holdouts.
"""
import argparse
import bisect
import csv
import json
import math
from pathlib import Path

RGB = {'red': (1, 0, 0), 'green': (0, 1, 0), 'blue': (0, 0, 1),
       'cyan': (0, 1, 1), 'magenta': (1, 0, 1), 'yellow': (1, 1, 0),
       'white': (1, 1, 1), 'black': (0, 0, 0)}


def interpolate(knots, x):
    if knots[0][0]-1e-9 <= x <= knots[-1][0]+1e-9:
        x = min(knots[-1][0], max(knots[0][0], x))
    if not knots[0][0] <= x <= knots[-1][0]:
        return None
    i = bisect.bisect_left([p[0] for p in knots], x)
    if i == 0:
        return knots[0][1]
    a, b = knots[i-1], knots[i]
    return a[1] + (b[1] - a[1]) * (x-a[0]) / (b[0]-a[0])


def decreasing_fit(values):
    """Equal-weight PAVA. Noise reduction prior, with fit residual reported."""
    blocks = []
    for y in values:
        blocks.append([y, 1])
        while len(blocks) > 1 and blocks[-2][0]/blocks[-2][1] < blocks[-1][0]/blocks[-1][1]:
            a, b = blocks[-2:]
            blocks[-2:] = [[a[0]+b[0], a[1]+b[1]]]
    return [total/n for total, n in blocks for _ in range(n)]


class PanelModel:
    def __init__(self, data):
        self.data = data

    def q(self, rgb_signal_nits):
        """Joint-color tetrahedral interpolation; not additive channel power."""
        rgb = [float(v)/100 for v in rgb_signal_nits]
        if len(rgb) != 3 or any(not math.isfinite(v) or v < 0 or v > 1 for v in rgb):
            raise ValueError('This prototype only covers RGB components 0..100 signal nits')
        order = sorted(range(3), key=lambda i: rgb[i], reverse=True)
        x, y, z = [rgb[i] for i in order]
        primary = ['red', 'green', 'blue'][order[0]]
        secondary = {frozenset((0, 1)): 'yellow', frozenset((0, 2)): 'magenta',
                     frozenset((1, 2)): 'cyan'}[frozenset(order[:2])]
        q = self.data['q_anchors']
        return (x-y)*q[primary] + (y-z)*q[secondary] + z*q['white']

    def F(self, load):
        """Return predicted camera code; None outside measured load range."""
        return interpolate(self.data['response_knots'], load)

    def predict(self, histogram):
        """histogram = [(RGB signal-nit triplet, total-frame fraction), ...].

        Include the white probe and black area. Total fractions must equal one.
        Result is experimental even inside the domain; consult validation fields.
        """
        if any(fraction < 0 for _, fraction in histogram) or abs(sum(f for _, f in histogram)-1) > 1e-6:
            raise ValueError('Nonnegative frame fractions must sum to one')
        # Subtract the fixed probe contribution already absorbed into F's origin.
        return self.F(sum(self.q(rgb)*fraction for rgb, fraction in histogram)-.01*self.data['q_anchors']['white'])


def fit(rows):
    references = [r for r in rows if r['kind'] == 'final_reference']
    initial = next((r for r in rows if r['color'] == 'white' and float(r['area']) == .01), None)
    if not references or not initial:
        raise ValueError('Acquisition must include a final reference check')
    if abs(float(references[-1]['camera_code'])-float(initial['camera_code'])) > 2:
        raise ValueError('Reference drift invalidates this dataset')
    exposures = {r['exposure_log2_s'] for r in rows if 'exposure_log2_s' in r}
    if len(exposures) > 1:
        raise ValueError('Cannot mix camera exposure settings in one response model')
    pure = {c: sorted((float(r['area']), float(r['camera_code'])) for r in rows
                     if r['color'] == c and r['other'] == 'none' and r['kind'] != 'final_reference')
            for c in RGB if c != 'black'}
    if any(len(k) < 2 for k in pure.values()):
        raise ValueError('Missing complete primary/secondary/white sweeps')
    reference_color = min(pure, key=lambda c: pure[c][-1][1])
    reference = pure[reference_color]
    fitted = decreasing_fit([y for _, y in reference])
    F = list(zip([a-.01 for a, _ in reference], fitted))
    anchors = {reference_color: 1., 'black': 0.}
    diagnostics = {}
    # The reference F covers load <=1. Test that domain rather than extrapolate.
    for c, points in pure.items():
        if c == reference_color:
            continue
        candidates = []
        for i in range(1001):
            q = i/1000
            errors = [interpolate(F, (a-.01)*q)-y for a, y in points]
            candidates.append((sum(e*e for e in errors)/len(errors), q))
        loss, q = min(candidates)
        compatible = [v for mse, v in candidates if mse <= loss+1.]
        anchors[c] = q
        diagnostics[c] = {'rmse_camera_codes': math.sqrt(loss),
                          'compatible_q_range': [min(compatible), max(compatible)],
                          'identification': 'weak' if max(compatible)-min(compatible) > .15 else 'constrained',
                          'domain_warning': q == 1.,
                          'range_definition': 'MSE within one code squared of best fit; not a statistical confidence interval'}
    data = {'version': 1, 'model': 'scalar_joint_color_load_with_white_probe_response',
            'measurement_domain': 'camera code values, not physical luminance',
            'probe_signal_nits': 100, 'probe_area': .01, 'anchor_component_signal_nits': 100,
            'camera_exposure_log2_seconds': int(next(iter(exposures))) if exposures else None,
            'interior_rgb_interpolation': 'tentative tetrahedral interpolation; unmeasured brightness/saturation',
            'load_reference_color': reference_color, 'load_origin': 'fixed white probe; load describes surround contribution',
            'q_anchors': anchors, 'response_knots': F, 'raw_color_response_curves': pure,
            'q_diagnostics': diagnostics,
            'reference_monotonic_fit_rmse_codes': math.sqrt(sum((a-b)**2 for a, b in zip(fitted, [y for _, y in reference]))/len(reference))}
    model = PanelModel(data)
    holdouts = []
    for r in rows:
        if r['kind'] != 'mixture_holdout':
            continue
        a = float(r['area'])
        load = (a-.01)*(anchors[r['color']]+anchors[r['other']])/2
        predicted = model.F(load)
        observed = float(r['camera_code'])
        holdouts.append({'colors': [r['color'], r['other']], 'area': a, 'load': load,
                         'observed_camera_code': observed, 'predicted_camera_code': predicted,
                         'error_codes': None if predicted is None else predicted-observed})
    errors = [r['error_codes'] for r in holdouts if r['error_codes'] is not None]
    data['mixture_holdouts'] = holdouts
    data['mixture_rmse_codes'] = math.sqrt(sum(e*e for e in errors)/len(errors)) if errors else None
    data['mixture_max_error_codes'] = max(map(abs, errors), default=None)
    data['scalar_model_status'] = ('provisionally_supported_for_tested_palette'
        if len(errors) == len(holdouts) and errors and max(map(abs, errors)) <= 2
        and all(d['rmse_camera_codes'] <= 2 for d in diagnostics.values())
        else 'scalar_model_inadequate_or_unvalidated')
    return data


def self_test():
    anchors = {'black': 0, 'red': .4, 'green': .5, 'blue': .3,
               'white': 1, 'cyan': .7, 'magenta': .6, 'yellow': .8}
    rows = []
    response = lambda p: 80 - 50*max(0, p-.2)
    for c in RGB:
        if c == 'black':
            continue
        for a in (.01, .1, .2, .4, .6, .8, 1):
            rows.append(dict(color=c, other='none', area=a, camera_code=response(.01+(a-.01)*anchors[c]), kind='anchor'))
    for c, d in [('red', 'green'), ('white', 'cyan')]:
        rows.append(dict(color=c, other=d, area=.8, camera_code=response(.01+.79*(anchors[c]+anchors[d])/2), kind='mixture_holdout'))
    rows.append(dict(color='white', other='none', area=.01, camera_code=response(.01), kind='final_reference'))
    fitted = fit(rows)
    if fitted['mixture_max_error_codes'] > .1:
        raise AssertionError('Known effective-load model was not recovered')
    model = PanelModel(fitted)
    if abs(model.q((100, 100, 100))-1) > 1e-6 or model.F(2) is not None:
        raise AssertionError('Joint-color or domain check failed')
    rows[-2]['camera_code'] += 10
    if fit(rows)['scalar_model_status'] != 'scalar_model_inadequate_or_unvalidated':
        raise AssertionError('Mixture violation was accepted')
    print('PASS joint-color load recovery, unsupported load domain, mixture rejection')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('csv', type=Path, nargs='?')
    parser.add_argument('--output', type=Path)
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return
    if not args.csv or not args.output:
        parser.error('Provide a completed color CSV and --output')
    with args.csv.open(encoding='utf-8-sig') as stream:
        data = fit(list(csv.DictReader(stream)))
    args.output.write_text(json.dumps(data, indent=2), encoding='utf-8')
    print(json.dumps({k: data[k] for k in ('scalar_model_status', 'q_anchors', 'mixture_rmse_codes', 'mixture_max_error_codes')}, indent=2))


if __name__ == '__main__':
    main()
