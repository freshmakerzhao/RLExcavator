#!/usr/bin/env python3
"""Prepare or execute bounded deterministic ML-Agents checkpoint screens.

Default: print a dry-run plan without creating output or launching Unity.
Example from RLExcavator (use a NEW output path):
  python tools/run_checkpoint_screen.py --python /path/to/ml_agents/bin/python \
    --checkpoint results/traj_full_follow_3cm_20260929 \
    --output /path/to/new-screen --seeds 20260904 20260905 --wall-seconds 120
Append --execute to run. Training parameters remain in --config's authoritative
YAML. This is a random training-environment screening window, not a frozen
catalog benchmark, ONNX evaluation, reward ablation, or formal experiment.
Raw logs and TensorBoard events are retained; no episode statistics are inferred
from a wall-clock stop. Timeout is reported as censored, never as task success.
"""

import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import time


SCOPE = ('Random training-environment checkpoint inference screening only; '
         'not a frozen catalog benchmark, ONNX parity test, or reward ablation. '
         'A wall-clock window can end before any complete summary or episode.')


def parse_args(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--project', type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument('--python', type=Path, default=Path(sys.executable))
    parser.add_argument('--config', type=Path,
                        default=Path('config/trajectory_ppo.yaml'))
    parser.add_argument('--player', type=Path, default=Path(
        'Builds/ExcavatorTrajectoryLimitsV2/ExcavatorTrajectoryLimitsV2.x86_64'))
    parser.add_argument('--checkpoint', type=Path, required=True,
                        help='Existing ML-Agents run directory containing ExcavatorTrajectory/checkpoint.pt')
    parser.add_argument('--output', type=Path, required=True, help='New output directory; never overwritten')
    parser.add_argument('--seeds', nargs='+', type=int, default=[20260904])
    parser.add_argument('--wall-seconds', type=int, default=120,
                        help='Per-seed wall budget, including startup (1..3600)')
    parser.add_argument('--execute', action='store_true')
    return parser.parse_args(argv)


def source_record(path):
    if not path.is_file():
        raise ValueError(f'Required file missing: {path}')
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(chunk)
    return {'path': str(path), 'sha256': digest.hexdigest(), 'bytes': path.stat().st_size}


def build_plan(args):
    root = args.project.resolve(strict=True)
    source = (root / args.checkpoint).resolve(strict=True)
    output = args.output.resolve()
    if output.exists():
        raise ValueError(f'Output already exists; choose a new directory: {output}')
    if output == source or source in output.parents:
        raise ValueError('Output must be outside the source checkpoint directory')
    if not 1 <= args.wall_seconds <= 3600:
        raise ValueError('wall-seconds must be in 1..3600')
    seeds = args.seeds
    if not 1 <= len(seeds) <= 8 or len(set(seeds)) != len(seeds):
        raise ValueError('Provide 1..8 distinct seeds')
    if any(not 0 <= seed <= 2147483647 for seed in seeds):
        raise ValueError('Seeds must be in 0..2147483647')
    python = args.python.resolve(strict=True)
    player = (root / args.player).resolve(strict=True)
    for binary in (python, player):
        if not binary.is_file() or not os.access(binary, os.X_OK):
            raise ValueError(f'Not an executable file: {binary}')
    files = {'runner': Path(__file__).resolve(),
             'wrapper': root / 'tools/mlagents_train_no_onnx.py',
             'config': (root / args.config).resolve(strict=True), 'player': player,
             'checkpoint': source / 'ExcavatorTrajectory/checkpoint.pt'}
    sources = {name: source_record(path) for name, path in files.items()}
    assets = player.parent / (player.stem + '_Data/StreamingAssets')
    for name in ('machine_profile.jsonc', 'machine_profile.json'):
        profile = assets / name
        if profile.is_file():
            sources = {**sources, 'player_machine_profile': source_record(profile)}
            break
    command = [str(python), str(files['wrapper']), str(files['config']),
               '--env=' + str(player), '--inference', '--deterministic',
               '--initialize-from=' + str(source), '--results-dir=' + str(output / 'results'),
               '--time-scale=20', '--no-graphics']
    return {'schema': 'rl_checkpoint_screen.v1', 'formal_ready': False, 'scope': SCOPE,
            'project': str(root), 'python': str(python), 'output': str(output),
            'wall_seconds': args.wall_seconds, 'sources': sources,
            'hash_scope': 'Named inputs only; player data/native libraries are not fully hashed.',
            'runs': [{'seed': seed, 'command': command + [f'--run-id=screen_seed_{seed}',
                      f'--seed={seed}']} for seed in seeds]}


def signal_group(pid, sig):
    try:
        os.killpg(pid, sig)
    except ProcessLookupError:
        pass


def stop_process(process):
    # start_new_session binds this group solely to our child and descendants.
    signal_group(process.pid, signal.SIGTERM)
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        pass
    finally:
        signal_group(process.pid, signal.SIGKILL)
        process.wait()


def run_process(command, cwd, log_path, wall_seconds):
    start = time.monotonic()
    with log_path.open('x') as log:
        process = subprocess.Popen(command, cwd=cwd, stdout=log, stderr=subprocess.STDOUT,
                                   start_new_session=True)
        status = 'failed'
        try:
            code = process.wait(timeout=wall_seconds)
            status = 'exited' if code == 0 else 'failed'
        except subprocess.TimeoutExpired:
            status = 'window_elapsed'
        except KeyboardInterrupt:
            status = 'interrupted'
        finally:
            stop_process(process)
    return {'status': status, 'returncode': process.returncode,
            'window_censored': status in ('window_elapsed', 'interrupted'),
            'elapsed_seconds': time.monotonic() - start, 'log': str(log_path),
            'task_success': None}


def runtime_versions(python):
    code = ('import sys,json,importlib.metadata as m; '
            'print(json.dumps({"python":sys.version,"packages":'
            '{k:m.version(k) for k in ("mlagents","torch","tensorboard","PyYAML")}}))')
    result = subprocess.run([python, '-c', code], capture_output=True, text=True, timeout=20)
    if result.returncode:
        raise ValueError('Runtime version probe failed: ' + result.stderr.strip())
    return json.loads(result.stdout)


def execute_plan(plan):
    output = Path(plan['output'])
    output.mkdir(parents=True, exist_ok=False)
    manifest = {**plan, 'started_utc': datetime.now(timezone.utc).isoformat(),
                'execution': [], 'runtime_versions': None, 'failure': None}
    path = output / 'screen.json'
    try:
        manifest['runtime_versions'] = runtime_versions(plan['python'])
        # Detect changed named inputs between preparation and execution.
        for record in plan['sources'].values():
            if source_record(Path(record['path'])) != record:
                raise ValueError('Input changed since plan: ' + record['path'])
        path.write_text(json.dumps(manifest, indent=2) + '\n')
        for run in plan['runs']:
            result = run_process(run['command'], plan['project'],
                                 output / f"seed_{run['seed']}.log", plan['wall_seconds'])
            manifest['execution'] = [*manifest['execution'], {**run, **result}]
            path.write_text(json.dumps(manifest, indent=2) + '\n')
            if result['status'] in ('failed', 'interrupted'):
                break
    except (OSError, ValueError, subprocess.SubprocessError, KeyboardInterrupt) as exc:
        manifest['failure'] = f'{type(exc).__name__}: {exc}'
    finally:
        path.write_text(json.dumps(manifest, indent=2) + '\n')
    return manifest


def main(argv=None):
    try:
        args = parse_args(argv)
        plan = build_plan(args)
        report = execute_plan(plan) if args.execute else {**plan, 'dry_run': True}
        print(json.dumps(report, indent=2))
        failed = report.get('failure') or any(
            run['status'] in ('failed', 'interrupted') for run in report.get('execution', []))
        return 1 if failed else 0
    except (OSError, ValueError) as exc:
        print(f'Checkpoint screen refused: {exc}', file=sys.stderr)
        return 2


if __name__ == '__main__':
    raise SystemExit(main())
