#!/usr/bin/env python3
import json
import shutil
import sys
from pathlib import Path

candidate_dir = Path(sys.argv[1])
stable_dir = Path(sys.argv[2])
output_dir = Path(sys.argv[3])

with (candidate_dir / 'cohort-capture.json').open('r', encoding='utf-8') as stream:
    candidate = json.load(stream)
with (stable_dir / 'cohort-capture.json').open('r', encoding='utf-8') as stream:
    stable = json.load(stream)

if candidate['schema_version'] != 1 or stable['schema_version'] != 1:
    raise SystemExit('expected cohort schema_version=1')
if candidate['profile'] != stable['profile']:
    raise SystemExit('candidate/stable profile mismatch')
if candidate['load_shape'] != stable['load_shape']:
    raise SystemExit('candidate/stable load shape mismatch')
if candidate['observation']['requested_window_minutes'] != stable['observation']['requested_window_minutes']:
    raise SystemExit('candidate/stable requested window mismatch')
if candidate['cohort']['role'] != 'candidate' or stable['cohort']['role'] != 'stable':
    raise SystemExit('cohort roles are not candidate/stable')
if candidate.get('restoration') is None:
    raise SystemExit('candidate cohort is missing restoration evidence')
if stable.get('restoration') is not None:
    raise SystemExit('stable cohort must not publish restoration evidence')
if stable['cohort']['runtime_identity_digest'] != candidate['restoration']['runtime_identity_digest']:
    raise SystemExit('stable prior runtime identity does not match candidate restoration runtime identity')
if stable['cohort']['runtime_digest'] != candidate['restoration']['runtime_digest']:
    raise SystemExit('stable prior runtime digest does not match candidate restoration runtime digest')

candidate_metrics = {metric['id']: metric for metric in candidate['metrics']}
stable_metrics = {metric['id']: metric for metric in stable['metrics']}
if set(candidate_metrics) != set(stable_metrics):
    raise SystemExit('candidate/stable metrics differ')

combined_metrics = []
for metric_id, candidate_metric in sorted(candidate_metrics.items()):
    stable_metric = stable_metrics[metric_id]
    if candidate_metric['unit'] != stable_metric['unit']:
        raise SystemExit(f'metric unit mismatch for {metric_id}')
    combined_metrics.append({
        'id': metric_id,
        'unit': candidate_metric['unit'],
        'candidate_value': candidate_metric['value'],
        'stable_value': stable_metric['value'],
        'candidate_samples': candidate_metric['samples'],
        'stable_samples': stable_metric['samples'],
        'captured_at_utc': max(
            candidate_metric['captured_at_utc'],
            stable_metric['captured_at_utc'],
        ),
    })

output_dir.mkdir(parents=True, exist_ok=True)
for source in candidate_dir.iterdir():
    if source.name == 'cohort-capture.json':
        continue
    target = output_dir / source.name
    if source.is_dir():
        if target.exists():
            shutil.rmtree(target)
        shutil.copytree(source, target)
    else:
        shutil.copy2(source, target)

combined = {
    'schema_version': 1,
    'profile': candidate['profile'],
    'azure': candidate['azure'],
    'observation': {
        'started_at_utc': min(
            candidate['observation']['started_at_utc'],
            stable['observation']['started_at_utc'],
        ),
        'measurement_ended_at_utc': max(
            candidate['observation']['measurement_ended_at_utc'],
            stable['observation']['measurement_ended_at_utc'],
        ),
        'ended_at_utc': max(
            candidate['observation']['ended_at_utc'],
            stable['observation']['ended_at_utc'],
        ),
        'requested_window_minutes': candidate['observation']['requested_window_minutes'],
    },
    'load_shape': candidate['load_shape'],
    'cohorts': [candidate['cohort'], stable['cohort']],
    'metrics': combined_metrics,
    'restoration': candidate['restoration'],
}
with (output_dir / 'capture.json').open('w', encoding='utf-8') as stream:
    json.dump(combined, stream, indent=2, sort_keys=True)
    stream.write('\n')
