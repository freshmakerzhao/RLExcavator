import importlib.util
import hashlib
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

MODULE = Path(__file__).parents[1] / 'run_checkpoint_screen.py'
spec = importlib.util.spec_from_file_location('screen', MODULE)
screen = importlib.util.module_from_spec(spec)
spec.loader.exec_module(screen)


class ScreenTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        for name in ('tools/mlagents_train_no_onnx.py', 'config/train.yaml',
                     'player', 'source/ExcavatorTrajectory/checkpoint.pt'):
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text('fixture')
        (self.root / 'player').chmod(0o755)
        self.argv = ['--project', str(self.root), '--python', sys.executable,
                     '--config', 'config/train.yaml', '--player', 'player',
                     '--checkpoint', 'source', '--output', str(self.root / 'output'),
                     '--seeds', '1', '2', '--wall-seconds', '10']

    def test_dry_run_is_default_and_has_absolute_source_and_separate_output(self):
        args = screen.parse_args(self.argv)
        plan = screen.build_plan(args)
        self.assertFalse(args.execute)
        self.assertFalse((self.root / 'output').exists())
        cmd = plan['runs'][0]['command']
        self.assertIn('--deterministic', cmd)
        self.assertIn('--inference', cmd)
        self.assertIn('--initialize-from=' + str(self.root / 'source'), cmd)
        self.assertIn('--results-dir=' + str(self.root / 'output/results'), cmd)
        self.assertFalse(plan['formal_ready'])
        self.assertIn('checkpoint', plan['sources'])

    def test_invalid_seeds_budget_and_existing_output_rejected(self):
        for extra in (['--seeds', '1', '1'], ['--wall-seconds', '0'],
                      ['--wall-seconds', '3601'], ['--seeds', '-1']):
            with self.subTest(extra=extra), self.assertRaises(ValueError):
                screen.build_plan(screen.parse_args(self.argv + extra))
        (self.root / 'output').mkdir()
        with self.assertRaises(ValueError):
            screen.build_plan(screen.parse_args(self.argv))

    def test_plan_hashes_commented_jsonc_profile_before_legacy_json(self):
        assets = self.root / 'player_Data/StreamingAssets'
        assets.mkdir(parents=True)
        profile = assets / 'machine_profile.jsonc'
        profile.write_text('// current profile\n{"machine_id": "fixture"}\n')
        (assets / 'machine_profile.json').write_text('{"machine_id": "legacy"}')

        plan = screen.build_plan(screen.parse_args(self.argv))

        record = plan['sources']['player_machine_profile']
        self.assertEqual(record['path'], str(profile))
        self.assertEqual(record['sha256'], hashlib.sha256(profile.read_bytes()).hexdigest())

    def test_plan_still_hashes_profile_in_existing_json_player_build(self):
        profile = self.root / 'player_Data/StreamingAssets/machine_profile.json'
        profile.parent.mkdir(parents=True)
        profile.write_text('{"machine_id": "existing-build"}')

        plan = screen.build_plan(screen.parse_args(self.argv))

        self.assertEqual(plan['sources']['player_machine_profile']['path'], str(profile))

    def test_execute_never_overwrites_existing_output(self):
        plan = screen.build_plan(screen.parse_args(self.argv))
        (self.root / 'output').mkdir()
        sentinel = self.root / 'output/keep'
        sentinel.write_text('unchanged')
        with self.assertRaises(FileExistsError):
            screen.execute_plan(plan)
        self.assertEqual(sentinel.read_text(), 'unchanged')

    def test_timeout_terminates_only_started_process_group(self):
        result = screen.run_process(
            [sys.executable, '-c', 'import time; time.sleep(30)'],
            self.root, self.root / 'child.log', 0.1)
        self.assertEqual(result['status'], 'window_elapsed')
        self.assertTrue(result['window_censored'])
        self.assertIsNotNone(result['returncode'])

    def test_process_failure_is_preserved(self):
        result = screen.run_process(
            [sys.executable, '-c', 'raise SystemExit(7)'],
            self.root, self.root / 'failed.log', 5)
        self.assertEqual(result['status'], 'failed')
        self.assertEqual(result['returncode'], 7)

    def test_group_cleanup_uses_only_child_pid(self):
        with patch.object(screen.os, 'killpg') as kill:
            screen.signal_group(12345, signal.SIGTERM)
        kill.assert_called_once_with(12345, signal.SIGTERM)

    def test_timeout_leaves_unrelated_process_alive(self):
        other = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(30)'],
                                 start_new_session=True)
        try:
            screen.run_process([sys.executable, '-c', 'import time; time.sleep(30)'],
                               self.root, self.root / 'isolated.log', 0.1)
            self.assertIsNone(other.poll())
        finally:
            other.terminate()
            other.wait(timeout=5)

    def test_changed_input_refused_and_recorded_before_launch(self):
        plan = screen.build_plan(screen.parse_args(self.argv))
        (self.root / 'config/train.yaml').write_text('changed')
        with patch.object(screen, 'runtime_versions', return_value={'python': 'fixture'}), \
             patch.object(screen, 'run_process') as launch:
            report = screen.execute_plan(plan)
        launch.assert_not_called()
        self.assertIn('Input changed', report['failure'])
        self.assertTrue((self.root / 'output/screen.json').is_file())


if __name__ == '__main__':
    unittest.main()
