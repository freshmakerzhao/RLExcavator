"""Isolated real-execution-proxy preparation and fixed-case policy evaluation.

Only ``benchmark`` launches a Unity player. ``train-command`` prints argv; it
does not launch learning. No command deploys a policy or accesses real hardware.
"""
from __future__ import annotations

import argparse
import copy
from contextlib import contextmanager
from datetime import datetime, timezone
import hashlib
import importlib.util
import io
import json
import math
import os
from pathlib import Path
import re
import shlex
import sys


HERE = Path(__file__).resolve().parent
UNITY = HERE.parents[1]
PYTHON = Path(sys.executable)
CONFIG = UNITY / "config/real_execution_v1_ppo.yaml"
ORIGINAL = UNITY / "results/continuous_path_v5_20261021_train/ExcavatorContinuousPath/ExcavatorContinuousPath-3000018.pt"
RUN_ID = "continuous_path_real_v1_20261105"
EXPECTED_COUNTS = {"train": 32, "dev": 16, "holdout": 16}
BENCHMARK = HERE / "fixed_case_benchmark.py"
SCOPE = "Conditional simulation execution proxy; not a validated hydraulic model or real-machine result."


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False) + "\n")


def identity(path):
    path = Path(path).resolve(strict=True)
    return {"path": str(path), "bytes": path.stat().st_size,
            "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}


def finite(value, label):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        raise ValueError(f"{label} must be a finite number")
    return float(value)


def vector(value, keys, label):
    if not isinstance(value, dict) or any(key not in value for key in keys):
        raise ValueError(f"{label} must contain {keys}")
    return [finite(value[key], f"{label}.{key}") for key in keys]


def benchmark_case(case):
    """Keep Unity's resolved case untouched, and append legacy evaluator aliases."""
    if not re.fullmatch(r"[A-Za-z0-9_-]+", case.get("id", "")):
        raise ValueError("Case id must be a nonempty path-safe identifier")
    if case.get("split") not in EXPECTED_COUNTS:
        raise ValueError("Unknown case split")
    task = case.get("taskMode")
    if task not in ("MoveToDig", "CarryMaterial"):
        raise ValueError("Unknown task mode")
    pose = vector(case.get("startJointPose"), "xyzw", "startJointPose")
    vector(case.get("goalJointPose"), "xyzw", "goalJointPose")
    points = case.get("referenceUnityLocal", [])
    samples = case.get("jointSamples", [])
    pitches, clearances = case.get("sampledPitchDeg", []), case.get("sampledClearance", [])
    if len(points) < 2 or not all(len(value) == len(points) for value in (samples, pitches, clearances)):
        raise ValueError("Case must contain equal-length resolved tip/joint/pitch/clearance samples")
    if not all(value is True for value in clearances) or not case.get("validationSource", "").strip():
        raise ValueError("Case lacks recorded sampled clearance/source")
    joints = [vector(value, "xyzw", "jointSamples") for value in samples]
    if any(abs(a - b) > 1e-6 for a, b in zip(joints[0], pose)):
        raise ValueError("Start joint pose differs from first joint sample")
    points = [vector(value, "xyz", "referenceUnityLocal") for value in points]
    pitches = [finite(value, "sampledPitchDeg") for value in pitches]
    target = finite(case.get("targetPitchDeg"), "targetPitchDeg")
    if abs(target - (70. if task == "MoveToDig" else 180.)) > 1e-4:
        raise ValueError("Target pitch must retain original mode semantics")
    if abs(math.remainder(pitches[-1] - target, 360.)) > 20.:
        raise ValueError("Sampled goal cannot meet the original terminal pitch tolerance")
    length = sum(math.dist(a, b) for a, b in zip(points, points[1:]))
    if length < .06 or abs(length - finite(case.get("pathLengthM"), "pathLengthM")) > .002:
        raise ValueError("Invalid or inconsistent sampled path length")
    return {**copy.deepcopy(case), "start_joint_pose": pose,
            "route_ros_m": [[p[2], -p[0], p[1]] for p in points],
            "task_mode": task, "target_pitch_deg": target}


def freeze_cases(source, output, expected_counts=None):
    expected = EXPECTED_COUNTS if expected_counts is None else expected_counts
    dataset = json.loads(source.read_text())
    if dataset.get("schema") != "real_execution_cases_v1":
        raise ValueError("Expected resolved real_execution_cases_v1")
    cases = [benchmark_case(case) for case in dataset.get("cases", [])]
    identifiers = [case["id"] for case in cases]
    if len(set(identifiers)) != len(identifiers):
        raise ValueError("Duplicate case ids")
    splits = {split: [case for case in cases if case["split"] == split] for split in expected}
    counts = {key: len(value) for key, value in splits.items()}
    if counts != expected:
        raise ValueError(f"Resolved coverage mismatch: {counts}; expected {expected}")
    # All validation finishes before creating the immutable output directory.
    output.mkdir(parents=True, exist_ok=False)
    for split, selected in splits.items():
        write_json(output / f"{split}.json", {"schema": "real_execution_cases_v1", "scope": SCOPE,
                   "split": split, "cases": selected})
    write_json(output / "all.json", {"schema": "real_execution_cases_v1", "scope": SCOPE, "cases": cases})
    write_json(output / "manifest.json", {"schema": "real_execution_case_freeze.v1",
        "created_utc": datetime.now(timezone.utc).isoformat(), "source": identity(source),
        "counts": counts, "files": [identity(output / f"{split}.json") for split in splits],
        "scope": SCOPE, "heldout_policy": "holdout is excluded from default development evaluation and training"})
    return counts


def training_command(player, base_port, resume=False, run_id=RUN_ID):
    if not re.fullmatch(r"[A-Za-z0-9_-]+", run_id) or not 1024 <= base_port <= 65534:
        raise ValueError("Invalid run id or base port")
    argv = [str(PYTHON), str(UNITY / "tools/mlagents_train_no_onnx.py"), str(CONFIG),
            f"--env={player}", f"--run-id={run_id}", "--seed=20261105", "--time-scale=20",
            "--num-envs=1", "--no-graphics", f"--base-port={base_port}"]
    return argv + (["--resume"] if resume else ["--initialize-from=continuous_path_v5_20261021_train"])


def select_cases(suite, split, selected_ids):
    rows = list(enumerate(suite.get("cases", [])))
    if not rows or any(case.get("split") != split for _, case in rows):
        raise ValueError("Benchmark file must contain only the explicitly selected split")
    identifiers = [case["id"] for _, case in rows]
    if len(set(identifiers)) != len(identifiers):
        raise ValueError("Duplicate benchmark case ids would reuse an output directory")
    if len(set(selected_ids)) != len(selected_ids):
        raise ValueError("Duplicate requested case id")
    unknown = set(selected_ids) - {case["id"] for _, case in rows}
    if unknown:
        raise ValueError(f"Unknown requested cases: {sorted(unknown)}")
    return [(index, case) for index, case in rows if not selected_ids or case["id"] in selected_ids]


def load_benchmark():
    spec = importlib.util.spec_from_file_location("fixed_case_benchmark", BENCHMARK)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def execution_episode(events):
    """Match evaluator semantics: restart at each start, stop at first end."""
    rows, start, last, terminal = [], None, None, False
    for event in events:
        kind = event.get("kind")
        if kind not in ("start", "sample", "execution", "end"):
            continue
        at = finite(event.get("elapsed_s"), "elapsed_s")
        if kind == "start":
            rows, start, last = [], at, at
            continue
        if start is None:
            continue
        if at < last - 1e-7:
            raise ValueError("Execution event clock regressed without a start")
        last = max(last, at)
        if kind == "execution":
            for key in ("requested_velocity", "proxy_velocity"):
                value = event.get(key)
                if not isinstance(value, list) or len(value) != 4:
                    raise ValueError(f"{key} must contain four axis values")
                for component in value:
                    finite(component, key)
            for key in ("limit_mask", "neutral_mask"):
                value = event.get(key)
                if isinstance(value, bool) or not isinstance(value, int) or not 0 <= value <= 15:
                    raise ValueError(f"{key} must be a four-axis bit mask")
            if finite(event.get("control_dt_s"), "control_dt_s") <= 0:
                raise ValueError("control_dt_s must be positive")
            rows.append(event)
        if kind == "end":
            terminal = True
            break
    return rows, start, last, terminal


def active_duration(flags, durations):
    total = longest = current = 0.
    for active, duration in zip(flags, durations):
        current = current + duration if active else 0.
        total += duration if active else 0.
        longest = max(longest, current)
    return total, longest


def direction_reversals(values):
    signs = [1 if value > 0 else -1 for value in values if abs(value) > 1e-9]
    return sum(left != right for left, right in zip(signs, signs[1:]))


def execution_metrics(events):
    """Measure forward-held commands, never backdate a newly computed mask."""
    rows, start, last, terminal = execution_episode(events)
    times = [row["elapsed_s"] for row in rows]
    durations = [max(0., right - left) for left, right in zip(times, times[1:] + [last])]
    observed = sum(durations)
    axes = {}
    for index, axis in enumerate(("boom", "stick", "bucket", "swing")):
        requested = [row["requested_velocity"][index] for row in rows]
        proxy = [row["proxy_velocity"][index] for row in rows]
        flags = {"limit": [bool(row["limit_mask"] & (1 << index)) for row in rows],
                 "neutral": [bool(row["neutral_mask"] & (1 << index)) for row in rows],
                 "nonzero_ref_zero_proxy": [abs(ref) > 1e-9 and abs(speed) <= 1e-9
                                            for ref, speed in zip(requested, proxy)]}
        metrics = {}
        for label, active in flags.items():
            total, longest = active_duration(active, durations)
            metrics = {**metrics, f"{label}_duration_s": total if rows else None,
                       f"longest_{label}_s": longest if rows else None,
                       f"{label}_fraction": total / observed if observed > 0 else None}
        axes[axis] = {**metrics,
            "requested_direction_reversals": direction_reversals([v for v, dt in zip(requested, durations) if dt > 0]),
            "executed_direction_reversals": direction_reversals([v for v, dt in zip(proxy, durations) if dt > 0])}
    cadence = [row["control_dt_s"] for row in rows]
    return {"available": bool(rows), "execution_samples": len(rows), "terminal_observed": terminal,
            "observed_case_duration_s": last - start if start is not None else None,
            "observed_hold_duration_s": observed,
            "unclassified_initial_s": times[0] - start if rows else None,
            "control_dt_mean_s": sum(cadence) / len(cadence) if cadence else None,
            "control_dt_min_s": min(cadence) if cadence else None,
            "control_dt_max_s": max(cadence) if cadence else None,
            "axes": axes,
            "definitions": "Each execution row applies forward until the next execution or final observed sample/end. Initial time is unclassified; censored tails are not extrapolated. Limit and neutral masks are distinct and can overlap across axes. Zero-reference/zero-proxy samples are not failures. Reversals ignore zero commands. Proxy velocity is commanded simulation speed, not measured hydraulic response."}


def read_execution_metrics(directory, controller):
    with (directory / "benchmark_physics.jsonl").open() as stream:
        events = [json.loads(line) for line in stream if line.strip()]
    rows, _, _, _ = execution_episode(events)
    observed_controller = None
    for event in events:
        if event.get("kind") == "start":
            observed_controller = event.get("controller")
        elif event.get("kind") == "end":
            break
    if observed_controller != controller:
        raise ValueError(f"Unity controller {observed_controller!r} differs from requested {controller!r}")
    if any(row.get("policy_action_used") is not (controller == "policy") for row in rows):
        raise ValueError("Unity execution policy_action_used differs from the selected controller")
    result = {**execution_metrics(events), "controller": observed_controller}
    if not result["available"]:
        raise ValueError("No execution telemetry in the selected benchmark episode")
    return result


@contextmanager
def controller_environment(controller):
    """Set only this evaluation's child process source; restore caller state."""
    if controller not in ("policy", "joint-witness"):
        raise ValueError("Unknown evaluation controller")
    flag = "REAL_EXECUTION_WITNESS"
    previous = os.environ.get(flag)
    if controller == "joint-witness":
        os.environ[flag] = "1"
    else:
        os.environ.pop(flag, None)
    try:
        yield
    finally:
        if previous is None:
            os.environ.pop(flag, None)
        else:
            os.environ[flag] = previous


def annotate_controller_trace(directory, controller, *, verified):
    """Annotate the evaluator trace; Unity's original physics JSONL is retained."""
    path = directory / "benchmark_trace.jsonl"
    if not path.exists():
        return
    rows = [json.loads(line) for line in path.read_text().splitlines() if line.strip()]
    action_role = ("policy_output" if controller == "policy" else "ignored_policy_output") if verified else "unverified_policy_output"
    annotated = [{**row, "requested_controller": controller,
                  "evaluation_controller": controller if verified else None,
                  **({"actor_action_applied": (controller == "policy") if verified else None,
                      "action_role": action_role}
                     if row.get("kind") == "decision" else {})} for row in rows]
    path.write_text("".join(json.dumps(row, allow_nan=False) + "\n" for row in annotated))


def run_benchmark(args):
    with controller_environment(args.controller):
        return _run_benchmark(args)


def _run_benchmark(args):
    import torch
    torch.set_default_device("cpu")
    torch.set_num_threads(1)
    torch.manual_seed(args.seed)
    suite = json.loads(args.cases.read_text())
    if suite.get("schema") != "real_execution_cases_v1":
        raise ValueError("Benchmark requires resolved real_execution_cases_v1")
    cases = [benchmark_case(case) for case in suite.get("cases", [])]
    suite = {**suite, "cases": cases}
    selected = select_cases(suite, args.split, args.case_id)
    model = {"name": args.model_name, "checkpoint": str(args.checkpoint.resolve()),
             "config": str(CONFIG), "controller": args.controller}
    if not re.fullmatch(r"[A-Za-z0-9_-]+", args.model_name):
        raise ValueError("Model name must be path-safe")
    checkpoint_bytes = args.checkpoint.read_bytes()
    checkpoint = torch.load(io.BytesIO(checkpoint_bytes), map_location="cpu", weights_only=False)
    sources = {key: identity(path) for key, path in {
        "checkpoint": args.checkpoint, "cases": args.cases, "player": args.player,
        "runner": Path(__file__), "legacy_metrics": BENCHMARK,
        "policy_loader": HERE / "policy_loader.py", "config": CONFIG}.items()}
    args.output.mkdir(parents=True, exist_ok=False)
    write_json(args.output / "benchmark_manifest.json", {"schema": "real_execution_benchmark.v1",
        "scope": SCOPE, "split": args.split, "sources": sources, "model": model,
        "requested_controller": args.controller, "requested_actor_actions_applied": args.controller == "policy",
        "suite": suite, "selected_case_ids": [case["id"] for _, case in selected],
        "seed": args.seed, "time_scale": args.time_scale, "max_ticks": args.max_ticks,
        "seconds_per_case": args.seconds, "original_terminal_retained": True,
        "started_utc": datetime.now(timezone.utc).isoformat()})
    benchmark, results = load_benchmark(), []
    for index, case in selected:
        case_directory = args.output / f"benchmark_{args.model_name}_{case['id']}"
        task_result = None
        execution = None
        try:
            try:
                task_result = benchmark.run_one(args, model, index, case, suite, checkpoint)
                execution = read_execution_metrics(case_directory, args.controller)
            finally:
                annotate_controller_trace(case_directory, args.controller, verified=execution is not None)
            result = {**task_result, "execution": execution, "requested_controller": args.controller,
                      "controller": args.controller, "actor_actions_applied": args.controller == "policy"}
            write_json(case_directory / "benchmark_result.json", result)
        except Exception as error:
            result = {"model": args.model_name, "case_id": case["id"], "case": case,
                "success": False, "status": "infrastructure_or_contract_error",
                "error": f"{type(error).__name__}: {error}", "physics_samples": 0,
                "recorded_task_result": task_result, "requested_controller": args.controller,
                "controller": args.controller if execution is not None else None,
                "actor_actions_applied": (args.controller == "policy") if execution is not None else None}
            case_directory.mkdir(exist_ok=True)
            write_json(case_directory / "benchmark_result.json", result)
            # Preserve the result and stop: another case must not mask a broken player/contract.
            results.append(result)
            write_json(args.output / "benchmark_results.json", results)
            break
        results.append(result)
        write_json(args.output / "benchmark_results.json", results)
    measured = [row for row in results if row.get("physics_samples", 0) > 1]
    controller_verified = bool(results) and all(row.get("controller") == args.controller for row in results)
    summary = {"schema": "real_execution_benchmark_summary.v1", "scope": SCOPE,
        "requested_controller": args.controller,
        "controller": args.controller if controller_verified else None,
        "actor_actions_applied": (args.controller == "policy") if controller_verified else None,
        "action_delta_scope": "actor output; diagnostic only and not executed in joint-witness mode",
        "expected_cases": len(selected), "recorded_cases": len(results),
        "successes": sum(row["success"] for row in results),
        "status_counts": {status: sum(row["status"] == status for row in results)
                          for status in sorted({row["status"] for row in results})},
        "measured_cases": len(measured), "all_terminal": len(results) == len(selected)
            and all(row["status"] not in ("censored", "missing_physics_samples", "infrastructure_or_contract_error") for row in results),
        "case_mean_endpoint_error_m": sum(row["endpoint_error_m"] for row in measured) / len(measured) if measured else None,
        "case_mean_progress": sum(row["final_progress"] for row in measured) / len(measured) if measured else None,
        "failures": [row["case_id"] for row in results if not row["success"]],
        "execution_metrics": {row["case_id"]: row["execution"] for row in results if "execution" in row}}
    write_json(args.output / "benchmark_summary.json", summary)
    print(json.dumps(summary, ensure_ascii=False), flush=True)
    return 0 if summary["all_terminal"] else 1


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    prepare = sub.add_parser("prepare", help="Validate and freeze resolved train/dev/holdout files; no process launch")
    prepare.add_argument("--source", type=Path, required=True)
    prepare.add_argument("--output", type=Path, required=True)
    train = sub.add_parser("train-command", help="Print a command only; 50k pilot is included in the 500k ceiling")
    train.add_argument("--player", type=Path, required=True)
    train.add_argument("--base-port", type=int, required=True)
    train.add_argument("--run-id", default=RUN_ID)
    train.add_argument("--resume", action="store_true")
    bench = sub.add_parser("benchmark", help="Run fixed cases in a new player, without optimizer updates")
    bench.add_argument("--cases", type=Path, required=True)
    bench.add_argument("--player", type=Path, required=True)
    bench.add_argument("--output", type=Path, required=True)
    bench.add_argument("--checkpoint", type=Path, default=ORIGINAL)
    bench.add_argument("--model-name", default="original3m")
    bench.add_argument("--controller", choices=("policy", "joint-witness"), default="policy",
                       help="Policy evaluation or separate joint-space dynamic witness; no learning")
    bench.add_argument("--split", choices=("dev", "holdout"), default="dev")
    bench.add_argument("--case-id", action="append", default=[])
    bench.add_argument("--seed", type=int, default=20261105)
    bench.add_argument("--base-port", type=int, required=True)
    bench.add_argument("--time-scale", type=float, default=20.)
    bench.add_argument("--seconds", type=float, default=120.)
    bench.add_argument("--max-ticks", type=int, default=1200)
    args = parser.parse_args(argv)
    if args.command == "prepare":
        print(json.dumps(freeze_cases(args.source, args.output)))
        return 0
    if args.command == "train-command":
        args.player.resolve(strict=True)
        command = training_command(args.player.resolve(), args.base_port, args.resume, args.run_id)
        print(json.dumps({"cwd": str(UNITY), "argv": command, "shell_command": shlex.join(command),
                          "launch_performed": False, "pilot_pause": "For a new supervised batch, pause after the first stable nonempty positive-numbered periodic checkpoint (possibly 49999); inspect before resume."}, indent=2))
        return 0
    if not (1024 <= args.base_port <= 65534 and args.seconds > 0 and args.max_ticks > 0
            and math.isfinite(args.time_scale) and args.time_scale > 0):
        parser.error("Invalid port or runtime budgets")
    return run_benchmark(args)


if __name__ == "__main__":
    sys.exit(main())
