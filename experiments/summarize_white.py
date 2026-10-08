"""Summarize observed camera codes; do not infer luminance ratios."""
import argparse
import csv
import json
from pathlib import Path
from statistics import mean


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('observations', type=Path)
    parser.add_argument('curve', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    with args.observations.open(encoding='utf-8-sig') as stream:
        observations = list(csv.DictReader(stream))
    with args.curve.open(encoding='utf-8-sig') as stream:
        curve = list(csv.DictReader(stream))
    reference = [float(r['score']) for r in observations if r['stage'] == '4']
    final = [float(r['score']) for r in observations if r['stage'] == '6']
    result = {
        'measurement_domain': 'camera code values, not physical luminance',
        'initial_reference_code': mean(reference) if reference else None,
        'final_reference_code': mean(final) if final else None,
        'reference_drift_codes': mean(final) - mean(reference) if reference and final else None,
        'camera_frames_recorded': len(observations),
        'sampled_match_or_check_states': len(curve),
        'max_recorded_match_error_codes': max(
            (abs(float(r['camera_observed']) - float(r['camera_target'])) for r in curve), default=None),
        'elapsed_recorded_seconds': (
            (int(observations[-1]['host_ms']) - int(observations[0]['host_ms'])) / 1000
            if observations else None),
    }
    args.output.write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
