"""Generate stimuli, not images. Values are requested HDR signal nits, not measured output."""
import argparse
import csv
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    fields = ['id', 'role', 'probe_signal_nits', 'probe_area_fraction',
              'background_signal_nits', 'background_bright_fraction']
    rows = []
    # Bright fraction is a fraction of the non-probe area. Presenter must preserve probe geometry.
    for probe in (50, 100, 200):
        for background in (100, 400, 1000):
            for fraction in (0.1, 0.5, 1.0):
                for role, level, area in (
                    ('reference_before', 0, 0),
                    ('measurement', background, fraction),
                    ('reference_after', 0, 0),
                ):
                    rows.append(dict(zip(fields, (len(rows), role, probe, 0.01, level, area))))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open('w', newline='', encoding='utf-8') as stream:
        writer = csv.DictWriter(stream, fieldnames=fields)
        writer.writeheader()
        writer.writerows(rows)
    print(f'Wrote {len(rows)} stimulus steps to {args.output}')


if __name__ == '__main__':
    main()
