"""Offline checks; no Unity/player/training or hardware process is launched."""
import copy
from contextlib import redirect_stdout
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch


REPO = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("real_v1_runner", REPO / "tools/real_execution_v1/run_experiment.py")
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


def case(case_id="dev_MoveToDig_left_short_0", split="dev"):
    return {
        "id": case_id, "split": split, "taskMode": "MoveToDig", "direction": -1,
        "longerPath": False, "startJointPose": dict(x=-.04, y=-.05, z=.04, w=0.),
        "goalJointPose": dict(x=-.03, y=-.04, z=.05, w=-.2),
        "jointSamples": [dict(x=-.04, y=-.05, z=.04, w=0.),
                         dict(x=-.03, y=-.04, z=.05, w=-.2)],
        "referenceUnityLocal": [dict(x=.1, y=.5, z=.8), dict(x=.2, y=.5, z=.9)],
        "sampledPitchDeg": [75., 72.], "sampledClearance": [True, True],
        "validationSource": "AGX home-lock sampled tip clearance", "targetPitchDeg": 70.,
        "pathLengthM": 2 ** .5 / 10,
    }


class RunnerTests(unittest.TestCase):
    def test_conversion_preserves_resolved_case_and_applies_unity_ros_axes(self):
        original = case()
        before = copy.deepcopy(original)
        result = runner.benchmark_case(original)
        self.assertEqual(original, before)
        self.assertEqual(result["route_ros_m"], [[.8, -.1, .5], [.9, -.2, .5]])
        self.assertEqual(result["start_joint_pose"], [-.04, -.05, .04, 0.])
        self.assertEqual(result["referenceUnityLocal"], original["referenceUnityLocal"])
        self.assertEqual(result["task_mode"], "MoveToDig")

    def test_unresolved_or_nonfinite_case_is_rejected(self):
        for change in ({"referenceUnityLocal": []}, {"sampledClearance": [True, False]},
                       {"targetPitchDeg": float("nan")}, {"validationSource": ""},
                       {"sampledPitchDeg": [75., 180.]}):
            with self.subTest(change=change), self.assertRaises(ValueError):
                runner.benchmark_case({**case(), **change})

    def test_case_id_cannot_escape_output_directory(self):
        with self.assertRaises(ValueError):
            runner.benchmark_case(case("../other"))

    def test_freeze_is_exclusive_and_preserves_split_counts_and_source_hash(self):
        cases = [case("t0", "train"), case("d0", "dev"), case("h0", "holdout")]
        with tempfile.TemporaryDirectory() as tmp:
            source, output = Path(tmp) / "source.json", Path(tmp) / "frozen"
            source.write_text(json.dumps({"schema": "real_execution_cases_v1", "cases": cases}))
            runner.freeze_cases(source, output, expected_counts={"train": 1, "dev": 1, "holdout": 1})
            self.assertEqual(len(json.loads((output / "dev.json").read_text())["cases"]), 1)
            manifest = json.loads((output / "manifest.json").read_text())
            self.assertEqual(manifest["source"]["sha256"], runner.identity(source)["sha256"])
            with self.assertRaises(FileExistsError):
                runner.freeze_cases(source, output, expected_counts={"train": 1, "dev": 1, "holdout": 1})

    def test_freeze_rejects_duplicates_and_missing_counts_before_writing(self):
        with tempfile.TemporaryDirectory() as tmp:
            source, output = Path(tmp) / "source.json", Path(tmp) / "frozen"
            source.write_text(json.dumps({"schema": "real_execution_cases_v1", "cases": [case(), case()]}))
            with self.assertRaises(ValueError):
                runner.freeze_cases(source, output)
            self.assertFalse(output.exists())

    def test_resume_never_retains_original_initialization(self):
        initial = runner.training_command(Path("/player"), 5815, resume=False)
        resume = runner.training_command(Path("/player"), 5815, resume=True)
        self.assertIn("--initialize-from=continuous_path_v5_20261021_train", initial)
        self.assertNotIn("--resume", initial)
        self.assertIn("--resume", resume)
        self.assertFalse(any("initialize-from" in part for part in resume))
        self.assertFalse(any("init_path" in part for part in initial + resume))

    def test_selected_dev_cases_keep_original_indices(self):
        suite = {"cases": [case("a"), case("b")]}
        self.assertEqual(runner.select_cases(suite, "dev", ["b"])[0][0], 1)
        with self.assertRaises(ValueError):
            runner.select_cases(suite, "dev", ["missing"])
        with self.assertRaises(ValueError):
            runner.select_cases({"cases": [case("h", "holdout")]}, "dev", [])

    def test_benchmark_rejects_duplicate_ids_before_case_directory_collision(self):
        duplicate = case()
        with self.assertRaises(ValueError):
            runner.select_cases({"cases": [duplicate, copy.deepcopy(duplicate)]}, "dev", [])

    def test_policy_clears_inherited_witness_and_restores_environment_on_error(self):
        with patch.dict(os.environ, {"REAL_EXECUTION_WITNESS": "1"}):
            with self.assertRaisesRegex(RuntimeError, "sentinel"):
                with runner.controller_environment("policy"):
                    self.assertNotIn("REAL_EXECUTION_WITNESS", os.environ)
                    raise RuntimeError("sentinel")
            self.assertEqual(os.environ["REAL_EXECUTION_WITNESS"], "1")

    def test_witness_is_explicit_and_environment_does_not_leak(self):
        with patch.dict(os.environ):
            os.environ.pop("REAL_EXECUTION_WITNESS", None)
            with runner.controller_environment("joint-witness"):
                self.assertEqual(os.environ["REAL_EXECUTION_WITNESS"], "1")
            self.assertNotIn("REAL_EXECUTION_WITNESS", os.environ)
            with self.assertRaises(ValueError):
                with runner.controller_environment("unknown"):
                    self.fail("Invalid controller entered")

    def test_benchmark_runs_under_selected_controller_without_launching_unity(self):
        with patch.dict(os.environ, {"REAL_EXECUTION_WITNESS": "inherited"}):
            for controller, expected in (("policy", None), ("joint-witness", "1")):
                def capture_environment(args):
                    self.assertEqual(os.environ.get("REAL_EXECUTION_WITNESS"), expected)
                    return 37
                with patch.object(runner, "_run_benchmark", side_effect=capture_environment):
                    self.assertEqual(runner.run_benchmark(SimpleNamespace(controller=controller)), 37)
                self.assertEqual(os.environ["REAL_EXECUTION_WITNESS"], "inherited")

    def test_witness_trace_marks_actor_output_unused_without_changing_actions(self):
        with tempfile.TemporaryDirectory() as tmp:
            directory = Path(tmp)
            trace = directory / "benchmark_trace.jsonl"
            rows = [{"kind": "decision", "action": [.1, .2, .3, .4]},
                    {"kind": "execution", "requested_velocity": [.01, 0., 0., 0.]}]
            trace.write_text("".join(json.dumps(row) + "\n" for row in rows))
            runner.annotate_controller_trace(directory, "joint-witness", verified=True)
            annotated = [json.loads(line) for line in trace.read_text().splitlines()]
            self.assertEqual(annotated[0]["action"], rows[0]["action"])
            self.assertEqual(annotated[0]["evaluation_controller"], "joint-witness")
            self.assertFalse(annotated[0]["actor_action_applied"])
            self.assertEqual(annotated[0]["action_role"], "ignored_policy_output")
            self.assertEqual(annotated[1]["requested_velocity"], rows[1]["requested_velocity"])

    def test_unverified_trace_does_not_claim_actor_was_ignored(self):
        with tempfile.TemporaryDirectory() as tmp:
            directory = Path(tmp)
            trace = directory / "benchmark_trace.jsonl"
            trace.write_text(json.dumps({"kind": "decision", "action": [.1, .2, .3, .4]}) + "\n")
            runner.annotate_controller_trace(directory, "joint-witness", verified=False)
            result = json.loads(trace.read_text())
            self.assertEqual(result["requested_controller"], "joint-witness")
            self.assertIsNone(result["evaluation_controller"])
            self.assertIsNone(result["actor_action_applied"])
            self.assertEqual(result["action_role"], "unverified_policy_output")

    def test_execution_source_must_match_cli_and_not_an_older_binary(self):
        execution = {"kind": "execution", "elapsed_s": .05, "control_dt_s": .05,
                     "limit_mask": 0, "neutral_mask": 0, "policy_action_used": False,
                     "requested_velocity": [.01, 0., 0., 0.], "proxy_velocity": [.01, 0., 0., 0.]}
        start = {"kind": "start", "elapsed_s": 0., "controller": "joint-witness"}
        end = {"kind": "end", "elapsed_s": .1}
        with tempfile.TemporaryDirectory() as tmp:
            directory = Path(tmp)
            path = directory / "benchmark_physics.jsonl"
            path.write_text("".join(json.dumps(row) + "\n" for row in (start, execution, end)))
            self.assertTrue(runner.read_execution_metrics(directory, "joint-witness")["available"])
            with self.assertRaises(ValueError):
                runner.read_execution_metrics(directory, "policy")
            for bad_execution in ({**execution, "policy_action_used": True},
                                  {key: value for key, value in execution.items() if key != "policy_action_used"}):
                path.write_text("".join(json.dumps(row) + "\n" for row in (start, bad_execution, end)))
                with self.assertRaises(ValueError):
                    runner.read_execution_metrics(directory, "joint-witness")

    def test_wrong_binary_preserves_task_result_without_asserting_actual_controller(self):
        torch = SimpleNamespace(set_default_device=lambda _: None, set_num_threads=lambda _: None,
                                manual_seed=lambda _: None, load=lambda *a, **kw: {})
        with tempfile.TemporaryDirectory() as tmp:
            directory = Path(tmp)
            suite, player, checkpoint = [directory / name for name in ("dev.json", "player", "model.pt")]
            suite.write_text(json.dumps({"schema": "real_execution_cases_v1", "cases": [case()]}))
            player.write_bytes(b"unused")
            checkpoint.write_bytes(b"unused")
            args = SimpleNamespace(controller="joint-witness", cases=suite, player=player,
                checkpoint=checkpoint, output=directory / "result", split="dev", case_id=[],
                model_name="witness", seed=1, time_scale=20., max_ticks=3, seconds=1.)
            task_result = {"success": True, "status": "success", "physics_samples": 2}
            def fake_case(*unused):
                output = args.output / f"benchmark_witness_{case()['id']}"
                output.mkdir()
                rows = [{"kind": "start", "elapsed_s": 0., "controller": "policy"},
                        {"kind": "end", "elapsed_s": .1}]
                (output / "benchmark_physics.jsonl").write_text("".join(json.dumps(row) + "\n" for row in rows))
                (output / "benchmark_trace.jsonl").write_text(json.dumps({"kind": "decision", "action": [0.] * 4}) + "\n")
                return task_result
            with patch.dict("sys.modules", {"torch": torch}), patch.object(runner, "load_benchmark", return_value=SimpleNamespace(run_one=fake_case)), redirect_stdout(io.StringIO()):
                self.assertEqual(runner.run_benchmark(args), 1)
            result = json.loads((args.output / "benchmark_results.json").read_text())[0]
            summary = json.loads((args.output / "benchmark_summary.json").read_text())
            manifest = json.loads((args.output / "benchmark_manifest.json").read_text())
            self.assertEqual(manifest["sources"]["policy_loader"],
                             runner.identity(runner.HERE / "policy_loader.py"))
            self.assertEqual(result["recorded_task_result"], task_result)
            self.assertEqual(result["requested_controller"], "joint-witness")
            self.assertIsNone(result["controller"])
            self.assertIsNone(result["actor_actions_applied"])
            self.assertIsNone(summary["controller"])
            self.assertIsNone(summary["actor_actions_applied"])

    def test_execution_durations_use_forward_hold_and_distinguish_limit_from_deadzone(self):
        def execution(at, limit, neutral, reference, proxy):
            return {"kind": "execution", "elapsed_s": at, "control_dt_s": .05,
                    "limit_mask": limit, "neutral_mask": neutral,
                    "requested_velocity": reference, "proxy_velocity": proxy}
        events = [{"kind": "start", "elapsed_s": 0.},
                  execution(.06, 1, 2, [.01, .01, 0., 0.], [0., 0., 0., 0.]),
                  execution(.10, 1, 0, [.01, .01, 0., 0.], [0., .01, 0., 0.]),
                  execution(.16, 0, 0, [-.01, -.01, 0., 0.], [-.01, -.01, 0., 0.]),
                  {"kind": "end", "elapsed_s": .20}]
        result = runner.execution_metrics(events)
        self.assertAlmostEqual(result["observed_hold_duration_s"], .14)
        self.assertAlmostEqual(result["unclassified_initial_s"], .06)
        self.assertAlmostEqual(result["axes"]["boom"]["limit_duration_s"], .10)
        self.assertAlmostEqual(result["axes"]["boom"]["longest_limit_s"], .10)
        self.assertAlmostEqual(result["axes"]["stick"]["neutral_duration_s"], .04)
        self.assertEqual(result["axes"]["stick"]["executed_direction_reversals"], 1)
        self.assertAlmostEqual(result["axes"]["boom"]["nonzero_ref_zero_proxy_duration_s"], .10)

    def test_execution_statistics_do_not_extend_censored_last_command_or_cross_reset(self):
        row = {"kind": "execution", "elapsed_s": .05, "control_dt_s": .05,
               "limit_mask": 1, "neutral_mask": 0,
               "requested_velocity": [.01, 0., 0., 0.], "proxy_velocity": [0., 0., 0., 0.]}
        events = [{"kind": "start", "elapsed_s": 0.}, row,
                  {"kind": "sample", "elapsed_s": .09},
                  {"kind": "start", "elapsed_s": 0.}, row,
                  {"kind": "sample", "elapsed_s": .07}]
        result = runner.execution_metrics(events)
        self.assertEqual(result["execution_samples"], 1)
        self.assertFalse(result["terminal_observed"])
        self.assertAlmostEqual(result["axes"]["boom"]["limit_duration_s"], .02)


if __name__ == "__main__":
    unittest.main()
