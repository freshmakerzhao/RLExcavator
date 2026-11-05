"""Fixed-case runtime and metrics retained from the validated V5 evaluator.

Repository-local dependency of run_experiment.py; importing this file launches
nothing. Only run_one connects a fresh Unity player for an explicitly selected case.
"""
from __future__ import annotations

import importlib.util
import json
import math
import sys
from pathlib import Path
import time

HERE = Path(__file__).resolve().parent
BASE = HERE / "policy_loader.py"


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False) + "\n")


def distance(a, b):
    return math.dist(a, b)


def percentile(values, fraction):
    ordered = sorted(values)
    if not ordered:
        return None
    index = (len(ordered) - 1) * fraction
    lo, hi = math.floor(index), math.ceil(index)
    return ordered[lo] * (hi - index) + ordered[hi] * (index - lo) if lo != hi else ordered[lo]


def geometric_distance(point, path):
    """Unconstrained nearest distance to the exact saved reference polyline."""
    best = math.inf
    for a, b in zip(path, path[1:]):
        ab = [y - x for x, y in zip(a, b)]
        square = sum(x * x for x in ab)
        fraction = min(1., max(0., sum((x - y) * z for x, y, z in zip(point, a, ab)) / square)) if square else 0.
        best = min(best, distance(point, [x + fraction * y for x, y in zip(a, ab)]))
    return best


def describe(values):
    return {"mean": sum(values) / len(values), "maximum": max(values), "p95": percentile(values, .95)} if values else None


def summarize_samples(case, samples, decisions, terminal):
    """Physical samples define geometry and time; decision samples define action changes."""
    route = case["route_ros_m"]
    if not samples:
        return {"success": False, "status": "missing_physics_samples", "physics_samples": 0}
    errors = [geometric_distance(row["tip_ros_m"], route) for row in samples]
    pitch_errors = [abs(math.remainder(row["pitch_deg"] - case["target_pitch_deg"], 360.)) for row in samples]
    intervals = [(a, b, b["elapsed_s"] - a["elapsed_s"]) for a, b in zip(samples, samples[1:])]
    intervals = [(a, b, dt) for a, b, dt in intervals if dt > 0]
    duration = sum(dt for _, _, dt in intervals)
    moving = [(a, b, dt) for a, b, dt in intervals if distance(b["tip_ros_m"], route[-1]) > .03]
    interior_duration = sum(dt for _, _, dt in moving)
    pause_duration = sum(dt for a, b, dt in moving if distance(a["tip_ros_m"], b["tip_ros_m"]) / dt < .02)
    ordered_stall_duration = sum(dt for a, b, dt in moving if (b.get("progress", 0.) - a.get("progress", 0.)) / dt < .005)
    actions = [row["action"] for row in decisions]
    delta = [sum((x - y) ** 2 for x, y in zip(a, b)) / 4 for a, b in zip(actions, actions[1:])]
    final = samples[-1]
    endpoint_error = distance(final["tip_ros_m"], route[-1])
    final_speed = distance(intervals[-1][0]["tip_ros_m"], intervals[-1][1]["tip_ros_m"]) / intervals[-1][2] if intervals else None
    return {"success": bool(terminal and terminal.get("success")),
            "status": terminal.get("reason", "unknown") if terminal else "censored",
            "physics_samples": len(samples), "decisions": len(decisions),
            "elapsed_s": final["elapsed_s"], "geometric_cross_track_m": describe(errors),
            "pitch_error_deg": describe(pitch_errors), "final_pitch_error_deg": pitch_errors[-1],
            "endpoint_error_m": endpoint_error, "final_speed_mps": final_speed,
            "final_progress": final.get("progress"), "action_delta_squared_per_axis": describe(delta),
            "sample_interval_s": describe([dt for _, _, dt in intervals]),
            "nonterminal_duration_s": interior_duration,
            "pause_fraction_nonterminal": pause_duration / interior_duration if interior_duration else None,
            "ordered_stall_fraction_nonterminal": ordered_stall_duration / interior_duration if interior_duration else None,
            "time_weighted_geometric_mean_m": sum(geometric_distance(b["tip_ros_m"], route) * dt for _, b, dt in intervals) / duration if duration else None,
            "metric_scope": "Physics samples, with actual sample cadence reported; pause/stall are descriptive, not new success gates."}


def load_base():
    spec = importlib.util.spec_from_file_location("v5_candidate_loader", BASE)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def consume_event(capture, event):
    """Keep the newest reset until its first terminal; never mix two episodes."""
    if capture.get("terminal"):
        return capture
    if event["kind"] == "start":
        return {"start": event, "samples": [{**event, "elapsed_s": 0., "progress": 0.}],
                "decisions": [], "terminal": None, "observation_contract": None}
    if event["kind"] == "sample":
        return {**capture, "samples": [*capture.get("samples", []), event]}
    if event["kind"] == "end":
        return {**capture, "terminal": event}
    return capture


def run_one(args, model, case_index, case, suite, checkpoint):
    """Unity's benchmark component writes per-physics JSONL, outside policy inputs."""
    import torch
    from mlagents_envs.side_channel.engine_configuration_channel import EngineConfigurationChannel
    from mlagents_envs.side_channel.environment_parameters_channel import EnvironmentParametersChannel
    from mlagents_envs.side_channel.stats_side_channel import StatsSideChannel
    from mlagents_envs.environment import UnityEnvironment

    base = load_base()
    checkpoint_path = Path(model["checkpoint"])
    settings = base.network_settings(Path(model["config"]))
    output = args.output / f"benchmark_{model['name']}_{case['id']}"
    output.mkdir(parents=True, exist_ok=False)
    engine, parameters, stats = EngineConfigurationChannel(), EnvironmentParametersChannel(), StatsSideChannel()
    parameters.set_float_parameter("benchmark_case", case_index)
    engine.set_configuration_parameters(time_scale=args.time_scale, target_frame_rate=-1, capture_frame_rate=60)
    physics_path = output / "benchmark_physics.jsonl"
    events, samples, decisions, terminal, start, env, reader = [], [], [], None, None, None, None
    capture = {"start": None, "samples": [], "decisions": [], "terminal": None, "observation_contract": None}
    actual_case = case
    wall_start = time.monotonic()
    try:
        env = UnityEnvironment(file_name=str(args.player.resolve()), base_port=args.base_port,
            seed=args.seed, no_graphics=True, side_channels=[engine, parameters, stats], timeout_wait=60,
            additional_args=["--continuous-path-benchmark", str(args.cases.resolve()),
                             "--benchmark-output", str(physics_path.resolve())], log_folder=str(output))
        env.reset()
        matches = [name for name in env.behavior_specs if name.split("?")[0] == base.BEHAVIOR]
        if len(matches) != 1:
            raise ValueError(f"Expected one fixed-case behavior: {list(env.behavior_specs)}")
        behavior = matches[0]
        actor = base.make_actor(env.behavior_specs[behavior], settings, checkpoint)
        for tick in range(args.max_ticks + 1):
            decision, finished = env.get_steps(behavior)
            if reader is None and physics_path.exists():
                reader = physics_path.open()
            fresh = []
            if reader is not None:
                while True:
                    offset, line = reader.tell(), reader.readline()
                    if not line or not line.endswith("\n"):
                        reader.seek(offset)
                        break
                    fresh.append(json.loads(line))
            for event in fresh:
                events.append(event)
                kind = event["kind"]
                if kind == "start":
                    if event["case_id"] != case["id"] or event["case_index"] != case_index:
                        raise ValueError("Unity case differs from requested fixed case")
                    start = event
                    route = event["route_ros_m"]
                    if len(route) != len(case["route_ros_m"]):
                        raise ValueError("Unity altered reference vertex count")
                    if distance(route[0], case["route_ros_m"][0]) > .02 or max(distance(a, b) for a, b in zip(route[1:], case["route_ros_m"][1:])) > 1e-6:
                        raise ValueError("Unity altered the fixed route beyond permitted actual-tip anchoring")
                    if abs(event["target_pitch_deg"] - case["target_pitch_deg"]) > 1e-3:
                        raise ValueError("Unity pitch requirement differs from requested case")
                    if event["task_mode"] != case["task_mode"]:
                        raise ValueError("Unity task mode differs from requested case")
                    actual_case = {**case, "route_ros_m": route}
                capture = consume_event(capture, event)
                start, samples, decisions, terminal = (capture[key] for key in ("start", "samples", "decisions", "terminal"))
                if terminal:
                    break
            if terminal:
                break
            if len(finished):
                raise ValueError("Terminal without benchmark end telemetry")
            if tick == args.max_ticks:
                break
            if time.monotonic() - wall_start > args.seconds:
                break
            if len(decision) > 1:
                raise ValueError("Fixed benchmark requires exactly one enabled agent")
            if len(decision):
                if start:
                    observed = decision.obs[0][0]
                    mode = [1., 0.] if case["task_mode"] == "MoveToDig" else [0., 1.]
                    contract = {"expected_mode": mode, "observed_mode": observed[36:38].tolist(),
                                "expected_target_pitch_deg": case["target_pitch_deg"],
                                "observed_target_pitch_deg": float(observed[44]) * 180.}
                    if max(abs(float(a) - b) for a, b in zip(observed[36:38], mode)) > 1e-5 or abs(contract["observed_target_pitch_deg"] - case["target_pitch_deg"]) > 1e-3:
                        raise ValueError("Policy observation mode/pitch requirement differs from fixed case")
                    if capture["observation_contract"] is None:
                        capture = {**capture, "observation_contract": contract}
                with torch.no_grad():
                    _, outputs, _ = actor.get_action_and_stats([torch.as_tensor(decision.obs[0], device="cpu")])
                action = outputs["env_action"]
                if start:
                    decisions.append({"kind": "decision", "tick": tick, "elapsed_s": samples[-1]["elapsed_s"],
                                      "obs": decision.obs[0][0].tolist(),
                                      "action": action.continuous[0].tolist()})
                env.set_actions(behavior, action)
            env.step()
    finally:
        if env is not None:
            env.close()
        if reader is not None:
            reader.close()
        with (output / "benchmark_trace.jsonl").open("w") as stream:
            for value in [*events, *decisions]:
                stream.write(json.dumps(value, allow_nan=False) + "\n")
    result = {"model": model["name"], "checkpoint": str(checkpoint_path.resolve()),
              "checkpoint_step": int(checkpoint["global_step"]["_GlobalSteps__global_step"].item()),
              "case_id": case["id"], "case": case, "actual_start": start,
              "observation_contract": capture["observation_contract"],
              "wall_elapsed_s": time.monotonic() - wall_start,
              **summarize_samples(actual_case, samples, decisions, terminal)}
    write_json(output / "benchmark_result.json", result)
    print(json.dumps({key: result[key] for key in ("model", "case_id", "success", "status", "elapsed_s") if key in result}), flush=True)
    return result
