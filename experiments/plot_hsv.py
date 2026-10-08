"""Plot measured comparisons and save a sparse prototype correction surface."""
import argparse
import colorsys
import csv
import json
from pathlib import Path
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('observations', type=Path)
    p.add_argument('--output-dir', type=Path, required=True)
    args = p.parse_args()
    args.output_dir.mkdir(parents=True, exist_ok=True)
    with args.observations.open() as stream:
        rows = list(csv.DictReader(stream))
    groups = {}
    for r in rows:
        groups.setdefault(r['name'], []).append(r)
    selected = ['red', 'green', 'blue', 'warm_partial', 'teal_partial', 'violet_partial']
    fig, axes = plt.subplots(2, 3, figsize=(12, 7), constrained_layout=True)
    for ax, name in zip(axes.flat, selected):
        records = groups.get(name, [])
        for role, label, color in [('raw', 'Uncalibrated', '#d87813'), ('calibrated', 'Calibrated (remeasured)', '#18824b')]:
            pts = sorted((float(r['area'])*100, float(r['camera_code'])) for r in records if r['role'] == role)
            if pts:
                ax.plot(*zip(*pts), 'o-', label=label, color=color)
        ref = next((r for r in records if r['role'] == 'reference'), None)
        if ref:
            target = float(ref['reference_code'])
            ax.axhline(target, color='gray', linestyle=':', label='Small-window reference')
            ax.set_title(f"{name}: H={float(ref['hue'])*360:.0f}°, S={float(ref['saturation']):.2f}")
            end = next((r for r in records if r['role'] == 'end_reference'), None)
            if end and abs(float(end['camera_code'])-target) > 2:
                ax.text(.03, .04, 'Reference drift: tentative', transform=ax.transAxes, color='#a03020', fontsize=8)
            elif not end:
                ax.text(.03, .04, 'Closing reference not acquired', transform=ax.transAxes, color='#a03020', fontsize=8)
        else:
            ax.set_title(name+' (not acquired)')
        ax.set_xlabel('Illuminated window (%)')
        ax.set_ylabel('Center ROI camera code')
        ax.grid(alpha=.2)
    axes.flat[0].legend(fontsize=8)
    fig.suptitle('Measured window sweeps — each color uses its own fixed exposure/reference\nLines connect sparse measurements; they are not dense sweep validation')
    fig.savefig(args.output_dir/'window-sweeps.png', dpi=150)
    plt.close(fig)
    fig, ax = plt.subplots(figsize=(9, 4.5), constrained_layout=True)
    for role, color in [('raw', '#d87813'), ('calibrated', '#18824b')]:
        pts = sorted((float(r['hue'])*360, float(r['camera_code'])-float(r['reference_code'])) for r in rows
                     if r['role'] == role and abs(float(r['area'])-.4) < 1e-5 and float(r['saturation']) > .999)
        if pts:
            ax.plot(*zip(*pts), 'o-', color=color, label='Uncalibrated' if role == 'raw' else 'Calibrated (remeasured)')
    ax.axhline(0, color='gray', linestyle=':')
    ax.set(xlabel='Hue (degrees), saturation 1', ylabel='Camera code minus own small-window reference', title='40% window: sparse hue sweep')
    ax.grid(alpha=.2)
    ax.legend()
    fig.savefig(args.output_dir/'hue-sweep-40.png', dpi=150)
    plt.close(fig)
    fig, axes = plt.subplots(1, 3, figsize=(11, 4), constrained_layout=True)
    for ax, hue in zip(axes, (0., 1/3, 2/3)):
        for role, color in [('raw', '#d87813'), ('calibrated', '#18824b')]:
            pts = sorted((float(r['saturation']), float(r['camera_code'])-float(r['reference_code'])) for r in rows
                         if r['role'] == role and abs(float(r['area'])-.4) < 1e-5
                         and (abs(float(r['hue'])-hue) < .001 or float(r['saturation']) == 0))
            if pts:
                ax.plot(*zip(*pts), 'o-', color=color, label=role)
        ax.axhline(0, color='gray', linestyle=':')
        ax.set(title=f'Hue {hue*360:.0f}°', xlabel='Saturation', ylabel='Reference error (camera codes)')
        ax.grid(alpha=.2)
    axes[0].legend()
    fig.suptitle('40% window: sparse saturation sweeps')
    fig.savefig(args.output_dir/'saturation-sweeps-40.png', dpi=150)
    plt.close(fig)
    anchors = []
    summary = []
    for name, records in groups.items():
        refs = [r for r in records if r['role'] == 'reference']
        ends = [r for r in records if r['role'] == 'end_reference']
        if not refs:
            continue
        ref = refs[0]
        corrected = [r for r in records if r['role'] == 'calibrated']
        drift = float(ends[-1]['camera_code'])-float(ref['reference_code']) if ends else None
        knots = [[.01, 100.]] + sorted([float(r['area']), float(r['signal_nits'])] for r in corrected)
        max_error = max((abs(float(r['camera_code'])-float(r['reference_code'])) for r in corrected), default=None)
        valid = drift is not None and abs(drift) <= 2 and max_error is not None and max_error <= 2
        summary.append({'name': name, 'hue': float(ref['hue']), 'saturation': float(ref['saturation']),
                        'reference_drift_codes': drift, 'max_remeasured_error_codes': max_error, 'completed_and_verified': valid})
        if valid:
            anchors.append({'name': name, 'hue': float(ref['hue']), 'saturation': float(ref['saturation']),
                            'rgb_direction': colorsys.hsv_to_rgb(float(ref['hue']), float(ref['saturation']), 1), 'knots': knots})
    model = {'kind': 'sparse_hsv_signal_correction', 'baseline_signal_nits': 100,
             'interior_interpolation': 'tentative inverse-distance interpolation in linear RGB direction; log signal interpolation over window area',
             'camera_values_are_not_luminance': True, 'anchors': anchors, 'validation': summary}
    (args.output_dir/'hsv-model.json').write_text(json.dumps(model, indent=2))
    (args.output_dir/'summary.json').write_text(json.dumps(summary, indent=2))
    print(json.dumps(summary, indent=2))


if __name__ == '__main__':
    main()
