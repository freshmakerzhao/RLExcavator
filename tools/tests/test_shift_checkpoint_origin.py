"""原点平移只改新副本；用当前实际 checkpoint 验证推理等价。"""

import hashlib
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import torch

MODULE = Path(__file__).parents[1] / 'shift_checkpoint_origin.py'
spec = importlib.util.spec_from_file_location('origin_shift', MODULE)
origin_shift = importlib.util.module_from_spec(spec)
spec.loader.exec_module(origin_shift)
BASELINE = Path(__file__).parents[2] / (
    'results/traj_full_follow_3cm_20260929/ExcavatorTrajectory/checkpoint.pt')


@unittest.skipUnless(BASELINE.is_file(), 'Frozen local checkpoint not installed')
class ActualCheckpointTests(unittest.TestCase):
    def test_new_checkpoint_preserves_actions_values_and_source(self):
        before_hash = hashlib.sha256(BASELINE.read_bytes()).hexdigest()
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / 'shifted.pt'
            report = origin_shift.shift_checkpoint(
                BASELINE, output, (0.01, -0.5, -0.058), 0.5)
            self.assertLess(report['verification']['action_max_abs_error'], 1e-5)
            self.assertLess(report['verification']['value_max_abs_error'], 1e-4)
            old = torch.load(BASELINE, map_location='cpu', weights_only=True)
            new = torch.load(output, map_location='cpu', weights_only=True)
            delta = torch.tensor([0.01, -0.5, -0.058]) / 0.5
            changed = []
            for section in ('Policy', 'Optimizer:critic'):
                for key, value in old[section].items():
                    if key.endswith('normalizer.running_mean'):
                        changed.append(f'{section}/{key}')
                        self.assertTrue(torch.equal(new[section][key][:9], value[:9]))
                        self.assertTrue(torch.equal(new[section][key][12:], value[12:]))
                        torch.testing.assert_close(new[section][key][9:12], value[9:12] - delta)
                    else:
                        self.assertTrue(torch.equal(new[section][key], value), key)
            self.assertEqual(len(changed), 4)
            self.assertEqual(report['changed_keys'], changed)
            assert_same(self, old['global_step'], new['global_step'])
            assert_same(self, old['Optimizer:value_optimizer'], new['Optimizer:value_optimizer'])
        self.assertEqual(hashlib.sha256(BASELINE.read_bytes()).hexdigest(), before_hash)

    def test_wrong_translation_fails_equivalence(self):
        old = torch.load(BASELINE, map_location='cpu', weights_only=True)
        shift = origin_shift.observation_shift((0, -0.5, 0), 0.5)
        with self.assertRaises(AssertionError):
            origin_shift.verify_equivalence(old, old, shift)

    def test_disagreeing_normalizer_aliases_leave_no_output(self):
        old = torch.load(BASELINE, map_location='cpu', weights_only=True)
        section = dict(old['Policy'])
        key = origin_shift.PREFIXES[0] + 'running_mean'
        section[key] = section[key] + 0.1
        invalid = {**old, 'Policy': section}
        with tempfile.TemporaryDirectory() as directory:
            source, output = Path(directory) / 'source.pt', Path(directory) / 'output.pt'
            torch.save(invalid, source)
            with self.assertRaisesRegex(ValueError, 'aliases disagree'):
                origin_shift.shift_checkpoint(source, output, (0, -0.5, 0), 0.5)
            self.assertFalse(output.exists())


def assert_same(test, before, after):
    if isinstance(before, torch.Tensor):
        test.assertTrue(torch.equal(before, after))
    elif isinstance(before, dict):
        test.assertEqual(before.keys(), after.keys())
        for key in before:
            assert_same(test, before[key], after[key])
    elif isinstance(before, (list, tuple)):
        test.assertEqual(len(before), len(after))
        for a, b in zip(before, after):
            assert_same(test, a, b)
    else:
        test.assertEqual(before, after)


class NoOverwriteTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.source = self.root / 'source.pt'
        self.source.write_bytes(b'source must stay untouched')

    def test_source_existing_output_and_symlink_refused_before_loading(self):
        existing = self.root / 'existing.pt'
        existing.write_bytes(b'previous output')
        linked = self.root / 'linked.pt'
        linked.symlink_to(self.source)
        dangling = self.root / 'dangling.pt'
        dangling.symlink_to(self.root / 'absent.pt')
        for output in (self.source, existing, linked, dangling):
            with self.subTest(output=output), patch.object(torch, 'load') as load:
                with self.assertRaisesRegex(ValueError, 'Refusing'):
                    origin_shift.shift_checkpoint(self.source, output, (0, -0.5, 0), 0.5)
                load.assert_not_called()
        self.assertEqual(self.source.read_bytes(), b'source must stay untouched')
        self.assertEqual(existing.read_bytes(), b'previous output')

    def test_invalid_shift_and_normalizer_leave_no_output(self):
        output = self.root / 'output.pt'
        for shift, scale in (((0, 0), 0.5), ((0, float('nan'), 0), 0.5),
                             ((0, 0, float('inf')), 0.5), ((0, 0, 0), 0),
                             ((0, 0, 0), -0.5), ((0, 0, 0), float('nan'))):
            with self.subTest(shift=shift, scale=scale), patch.object(torch, 'load') as load:
                with self.assertRaises(ValueError):
                    origin_shift.shift_checkpoint(self.source, output, shift, scale)
                load.assert_not_called()
                self.assertFalse(output.exists())


if __name__ == '__main__':
    unittest.main()
