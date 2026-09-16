#!/usr/bin/env python3
"""Prepare matched reward ablations from one PPO YAML. Never launches training."""

import argparse
import copy
import json
from pathlib import Path

import yaml

VARIANTS = {
    'full': {'reward_deadzone_scale': 1.0, 'reward_pitch_scale': 1.0},
    'no_deadzone_reward': {'reward_deadzone_scale': 0.0, 'reward_pitch_scale': 1.0},
    'no_pitch_reward': {'reward_deadzone_scale': 1.0, 'reward_pitch_scale': 0.0},
}


def prepare(source, output, seeds, dry_run=False):
    """Write fresh configs and a run manifest, preserving all shared settings."""
    if len(seeds) < 3 or len(set(seeds)) != len(seeds) or any(
        type(seed) is not int or seed < 0 for seed in seeds
    ):
        raise ValueError('Provide at least three distinct nonnegative integer seeds.')
    source, output = Path(source).resolve(), Path(output).resolve()
    base = yaml.safe_load(source.read_text(encoding='utf-8'))
    if not isinstance(base, dict) or not isinstance(base.get('behaviors'), dict):
        raise ValueError('Base YAML must define behaviors.')
    behaviors = base['behaviors']
    if set(behaviors) != {'ExcavatorTrajectory'}:
        raise ValueError('Base YAML must contain only ExcavatorTrajectory.')
    behavior = behaviors['ExcavatorTrajectory']
    if not isinstance(behavior, dict) or behavior.get('trainer_type') != 'ppo':
        raise ValueError('ExcavatorTrajectory must use PPO.')
    budget = behavior.get('max_steps')
    if type(budget) is not int or budget <= 0:
        raise ValueError('max_steps must be a positive integer.')
    checkpoint = base.get('checkpoint_settings', {})
    if not isinstance(checkpoint, dict):
        raise ValueError('checkpoint_settings must be a mapping.')
    if behavior.get('init_path') or any(checkpoint.get(key) for key in ('initialize_from', 'resume', 'load_model', 'inference', 'force')):
        raise ValueError('Ablations must train from scratch; remove checkpoint initialization.')
    if not isinstance(base.get('environment_parameters', {}), dict):
        raise ValueError('environment_parameters must be a mapping.')
    env_settings = base.get('env_settings', {})
    if not isinstance(env_settings, dict):
        raise ValueError('env_settings must be a mapping.')
    # Commands own player/results selection; discard inherited locations in every arm.
    training_base = {
        **base,
        **({'env_settings': {key: value for key, value in env_settings.items() if key != 'env_path'}}
           if 'env_settings' in base else {}),
        **({'checkpoint_settings': {key: value for key, value in checkpoint.items() if key != 'results_dir'}}
           if 'checkpoint_settings' in base else {}),
    }
    configs = {
        name: {**copy.deepcopy(training_base), 'environment_parameters': {
            **copy.deepcopy(base.get('environment_parameters', {})), **scales}}
        for name, scales in VARIANTS.items()
    }
    runs = [
        {'variant': name, 'seed': seed, 'max_steps': budget,
         'command': ['python', 'tools/mlagents_train_no_onnx.py', str(output / f'{name}.yaml'),
                     f'--run-id={name}_seed{seed}', f'--seed={seed}',
                     f'--results-dir={output / "results"}',
                     '--env=<rebuilt-training-player>']}
        for name in VARIANTS for seed in seeds
    ]
    manifest = {
        'status': 'prepared_not_trained', 'base_config': str(source),
        'requires_rebuilt_player': True, 'train_from_scratch': True,
        'minimum_seeds_per_variant': 3, 'shared_max_steps': budget, 'results_dir': str(output / 'results'),
        'variants': VARIANTS, 'runs': runs,
        'notes': [
            'Rebuild the Unity training player with the reward scale implementation; old players ignore these parameters.',
            'Train every arm from scratch with the listed matched seeds and budget; do not resume or initialize from an existing policy.',
            'Only deadzone bump and step pitch penalties are ablated. Existing waypoint rewards have no pitch modulation.',
            'Task modes, pitch goals, 38D observations, termination, deadzone randomization and energy semantics remain shared.',
            'This is reward ablation, not an independent no-task-mode study.',
            'Verify Trajectory/RewardDeadzoneScale and Trajectory/RewardPitchScale in training evidence.',
            'Inherited env_settings.env_path and checkpoint_settings.results_dir are removed; commands select the rebuilt player and this preparation directory/results.',
            'Commands are preparation only. Replace the player placeholder before running from the RLExcavator repository.',
        ],
    }
    if dry_run:
        return manifest
    output.mkdir(parents=True, exist_ok=False)
    (output / 'base_config.yaml').write_text(yaml.safe_dump(base, sort_keys=False), encoding='utf-8')
    for name, config in configs.items():
        (output / f'{name}.yaml').write_text(yaml.safe_dump(config, sort_keys=False), encoding='utf-8')
    (output / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base-config', type=Path, required=True)
    parser.add_argument('--output-dir', type=Path, required=True)
    parser.add_argument('--seeds', type=int, nargs='+', default=[0, 1, 2])
    parser.add_argument('--dry-run', action='store_true', help='Validate and print manifest without creating files.')
    args = parser.parse_args()
    manifest = prepare(args.base_config, args.output_dir, args.seeds, args.dry_run)
    print(json.dumps(manifest, indent=2))


if __name__ == '__main__':
    main()
