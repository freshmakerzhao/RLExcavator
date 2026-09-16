import importlib.util
import json
from pathlib import Path

import pytest
import yaml


def load_tool():
    spec = importlib.util.spec_from_file_location('prepare_reward_ablation', Path(__file__).parents[1] / 'prepare_reward_ablation.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def test_prepares_three_matched_arms_without_training(tmp_path):
    source = tmp_path / 'base.yaml'
    base = {'behaviors': {'ExcavatorTrajectory': {'trainer_type': 'ppo', 'max_steps': 5000000}},
            'environment_parameters': {'unrelated': 0.7}}
    source.write_text(yaml.safe_dump(base))
    output = tmp_path / 'prepared'
    load_tool().prepare(source, output, [11, 22, 33])
    manifest = json.loads((output / 'manifest.json').read_text())
    assert manifest['status'] == 'prepared_not_trained'
    assert manifest['requires_rebuilt_player'] is True
    assert len(manifest['runs']) == 9
    assert all('--initialize-from' not in run['command'] and '--resume' not in run['command'] for run in manifest['runs'])
    assert {(run['variant'], run['seed']) for run in manifest['runs']} == {
        (name, seed) for name in ('full', 'no_deadzone_reward', 'no_pitch_reward') for seed in (11, 22, 33)}
    for name, scales in [('full', (1, 1)), ('no_deadzone_reward', (0, 1)), ('no_pitch_reward', (1, 0))]:
        config = yaml.safe_load((output / f'{name}.yaml').read_text())
        params = config['environment_parameters']
        assert (params.pop('reward_deadzone_scale'), params.pop('reward_pitch_scale')) == scales
        assert config == base
    assert yaml.safe_load(source.read_text()) == base


@pytest.mark.parametrize('seeds', [[1], [1, 1, 2], [-1, 2, 3]])
def test_requires_three_distinct_nonnegative_seeds(tmp_path, seeds):
    with pytest.raises(ValueError, match='seed'):
        load_tool().prepare(tmp_path / 'unused', tmp_path / 'output', seeds)


def base_file(tmp_path, extra=None):
    source = tmp_path / 'base.yaml'
    config = {'behaviors': {'ExcavatorTrajectory': {'trainer_type': 'ppo', 'max_steps': 1000}}}
    config.update(extra or {})
    source.write_text(yaml.safe_dump(config))
    return source


def test_dry_run_does_not_create_output(tmp_path):
    output = tmp_path / 'output'
    result = load_tool().prepare(base_file(tmp_path), output, [0, 1, 2], dry_run=True)
    assert result['shared_max_steps'] == 1000
    assert not output.exists()


def test_existing_output_is_never_overwritten(tmp_path):
    output = tmp_path / 'output'
    output.mkdir()
    sentinel = output / 'full.yaml'
    sentinel.write_text('existing evidence')
    with pytest.raises(FileExistsError):
        load_tool().prepare(base_file(tmp_path), output, [0, 1, 2])
    assert sentinel.read_text() == 'existing evidence'


@pytest.mark.parametrize('checkpoint', [{'resume': True}, {'initialize_from': 'old'}, {'load_model': True}])
def test_rejects_checkpoint_reuse(tmp_path, checkpoint):
    with pytest.raises(ValueError, match='scratch'):
        load_tool().prepare(base_file(tmp_path, {'checkpoint_settings': checkpoint}), tmp_path / 'output', [0, 1, 2])


@pytest.mark.parametrize('checkpoint', [{'inference': True}, {'force': True}])
def test_rejects_inference_or_overwrite_runs(tmp_path, checkpoint):
    with pytest.raises(ValueError):
        load_tool().prepare(base_file(tmp_path, {'checkpoint_settings': checkpoint}), tmp_path / 'output', [0, 1, 2])


def test_commands_use_isolated_results_and_rebuilt_player(tmp_path):
    source = base_file(tmp_path, {'env_settings': {'env_path': '/old/player'},
                                  'checkpoint_settings': {'results_dir': '/old/results'}})
    output = tmp_path / 'prepared'
    manifest = load_tool().prepare(source, output, [0, 1, 2])
    for run in manifest['runs']:
        assert f'--results-dir={output / "results"}' in run['command']
        assert '--env=<rebuilt-training-player>' in run['command']
    full = yaml.safe_load((output / 'full.yaml').read_text())
    assert 'env_path' not in full.get('env_settings', {})
    assert 'results_dir' not in full.get('checkpoint_settings', {})
