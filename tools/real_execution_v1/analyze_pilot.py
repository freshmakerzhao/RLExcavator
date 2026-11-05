"""Compare two fixed 16-case evaluations from the same split; never launch simulation.

Example: python -B analyze_pilot.py --baseline original3m_dev_final \
    --pilot pilot50k_dev --freeze training_freeze.json --output new_comparison
"""
from __future__ import annotations

import argparse
import csv
from datetime import datetime, timezone
import hashlib
import json
import math
from pathlib import Path
import re
import statistics


MODES = ("MoveToDig", "CarryMaterial")
AXES = ("boom", "stick", "bucket", "swing")
SCALARS = ("endpoint_error_cm", "elapsed_s", "final_progress_pct", "longest_limit_s",
           "longest_neutral_s", "longest_zero_proxy_s", "longest_ordered_stall_s")


def split_label(split):
    return {"dev": "development", "holdout": "holdout"}[split]


def scope_for_split(split):
    return (f"16 fixed {split_label(split)}-case pairs (8 per mode), not independent terrains. "
            "Conditional empty-bucket simulation execution proxy; no claim about measured hydraulic "
            "response or real-machine performance. Failures and censored cases are retained.")


def fingerprint(path):
    content = path.read_bytes()
    return {"path": str(path.resolve()), "bytes": len(content),
            "sha256": hashlib.sha256(content).hexdigest()}


def canonical(value):
    return json.dumps(value, sort_keys=True, ensure_ascii=False, allow_nan=False, separators=(",", ":"))


def number(value):
    return value if isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value) else None


def safe_id(value):
    if not isinstance(value, str) or not re.fullmatch(r"[A-Za-z0-9_-]+", value):
        raise ValueError("Non-path-safe model/case identifier")
    return value


def validate_run(run):
    manifest, rows = run["manifest"], run["results"]
    if manifest.get("schema") != "real_execution_benchmark.v1" or manifest.get("split") not in ("dev", "holdout"):
        raise ValueError("Expected a development or holdout benchmark manifest")
    if manifest.get("requested_controller") != "policy":
        raise ValueError("Joint witnesses cannot be compared as learned policies")
    identifiers = manifest.get("selected_case_ids", [])
    actual = [row.get("case_id") for row in rows]
    cases = manifest.get("suite", {}).get("cases", [])
    if len(identifiers) != 16 or len(set(identifiers)) != 16 or len(actual) != 16 or set(actual) != set(identifiers):
        raise ValueError("Comparison requires all 16 fixed case records exactly once; an unfinished batch is not a failure")
    if len(cases) != 16 or {case["id"] for case in cases} != set(identifiers):
        raise ValueError("Manifest suite differs from the selected 16 cases")
    if any(case.get("split") != manifest["split"] for case in cases):
        raise ValueError("Case split differs from benchmark manifest split")
    lookup = {case["id"]: case for case in cases}
    if any(sum(case.get("taskMode") == mode for case in cases) != 8 for mode in MODES):
        raise ValueError("Expected 8 MoveToDig and 8 CarryMaterial cases")
    safe_id(manifest["model"]["name"])
    for row in rows:
        safe_id(row["case_id"])
        if canonical(row.get("case")) != canonical(lookup[row["case_id"]]):
            raise ValueError(f"Result case differs from frozen manifest: {row['case_id']}")
        if row.get("success") is True and (row.get("controller") != "policy" or row.get("actor_actions_applied") is not True):
            raise ValueError("A success without verified policy execution cannot count as policy success")
    return run


def load_run(directory):
    files = {name: directory / f"benchmark_{name}.json" for name in ("manifest", "results")}
    run = {name: json.loads(path.read_text()) for name, path in files.items()}
    run = {**run, "directory": directory.resolve(), "sources": {name: fingerprint(path) for name, path in files.items()}}
    source = run["manifest"]["sources"]["cases"]
    if not re.fullmatch("[a-f0-9]{64}", source.get("sha256", "")):
        raise ValueError("Missing frozen case source hash")
    case_path = Path(source["path"])
    if case_path.exists() and fingerprint(case_path)["sha256"] != source["sha256"]:
        raise ValueError("The recorded case source has changed since evaluation")
    return validate_run(run)


def check_start(start, case):
    route = start["route_ros_m"]
    expected = case["route_ros_m"]
    vectors = [(start.get("actual_joint_pose"), 4), (start.get("tip_ros_m"), 3), *[(point, 3) for point in route]]
    if any(not isinstance(value, list) or len(value) != size or any(number(component) is None for component in value)
           for value, size in vectors):
        raise ValueError(f"Invalid actual start/reference coordinates: {case['id']}")
    if start.get("case_id") != case["id"] or number(start.get("target_pitch_deg")) is None:
        raise ValueError(f"Actual start identity/pitch is missing: {case['id']}")
    if len(route) != len(expected) or math.dist(route[0], expected[0]) > .02:
        raise ValueError(f"Invalid actual reference anchoring: {case['id']}")
    if any(math.dist(a, b) > 1e-6 for a, b in zip(route[1:], expected[1:])):
        raise ValueError(f"Reference vertices changed: {case['id']}")
    if start.get("controller") != "policy" or start.get("response_model") != "CONDITIONAL_LINEAR_PROXY":
        raise ValueError(f"Unexpected actual controller/response: {case['id']}")
    if start.get("task_mode") != case["taskMode"] or abs(start["target_pitch_deg"] - case["targetPitchDeg"]) > 1e-4:
        raise ValueError(f"Actual task or pitch requirement changed: {case['id']}")


def verify_pairing(baseline, pilot):
    for run in (baseline, pilot):
        validate_run(run)
    left, right = baseline["manifest"], pilot["manifest"]
    if left["sources"]["cases"]["sha256"] != right["sources"]["cases"]["sha256"]:
        raise ValueError("Baseline and pilot case-file hashes differ")
    for source in ("legacy_metrics", "config", "player"):
        if left["sources"][source]["sha256"] != right["sources"][source]["sha256"]:
            raise ValueError(f"Metric/config source changed: {source}")
    for field in ("split", "selected_case_ids", "suite", "seed", "time_scale", "max_ticks", "seconds_per_case", "original_terminal_retained"):
        if canonical(left.get(field)) != canonical(right.get(field)):
            raise ValueError(f"Baseline/pilot protocol mismatch: {field}")
    lookup = [{row["case_id"]: row for row in run["results"]} for run in (baseline, pilot)]
    pairs, mappings = [], set()
    for case_id in left["selected_case_ids"]:
        rows = [records[case_id] for records in lookup]
        starts = [row.get("actual_start") for row in rows]
        pair = {"case_id": case_id, "mode": rows[0]["case"]["taskMode"], "verified": False}
        for start, row in zip(starts, rows):
            if start:
                check_start(start, row["case"])
                mappings.add(start.get("mapping"))
        if all(starts):
            a, b = starts
            joint = max(abs(x - y) for x, y in zip(a["actual_joint_pose"], b["actual_joint_pose"]))
            tip = math.dist(a["tip_ros_m"], b["tip_ros_m"])
            route = max(math.dist(x, y) for x, y in zip(a["route_ros_m"], b["route_ros_m"]))
            if joint > 1e-4 or tip > .001 or route > .001:
                raise ValueError(f"Measured starts/routes are not paired within the existing benchmark tolerances: {case_id}")
            pair = {**pair, "verified": True, "max_joint_difference_m_or_rad": joint,
                    "tip_difference_m": tip, "max_reference_vertex_difference_m": route}
        pairs.append(pair)
    if not mappings or None in mappings or len(mappings) != 1:
        raise ValueError("Execution mappings differ or are missing")
    return pairs


def verify_build_snapshot(baseline, pilot, snapshot):
    snapshot = snapshot.resolve(strict=True)
    frozen = json.loads(snapshot.read_text())
    records = {str(Path(item["path"]).resolve()): item for item in frozen["files"]}
    required = []
    build_sets = []
    for run in (baseline, pilot):
        player = Path(run["manifest"]["sources"]["player"]["path"]).resolve()
        data = player.parent / (player.stem + "_Data")
        paths = [data / name for name in ("Managed/Assembly-CSharp.dll", "globalgamemanagers", "level0", "StreamingAssets/machine_profile.jsonc")]
        if any(str(path) not in records for path in paths):
            return {"status": "unverified", "reason": "Player data paths are absent from pre-learning snapshot",
                    "snapshot": fingerprint(snapshot)}
        required.extend(paths)
        build_sets.append([records[str(path)]["sha256"] for path in paths])
    if build_sets[0] != build_sets[1]:
        raise ValueError("Frozen evaluated assembly/scene/profile identities differ between runs")
    required.extend(Path(path) for path in records if Path(path).name == "RealExecutionProfileV1.json")
    verified = []
    for path in dict.fromkeys(required):
        current = fingerprint(path)
        if current["sha256"] != records[str(path)]["sha256"]:
            raise ValueError(f"Build/profile changed from the pre-learning freeze: {path}")
        verified.append(current)
    return {"status": "selected_files_match_snapshot", "snapshot": fingerprint(snapshot),
            "snapshot_at": frozen.get("at"), "verified_current_files": verified,
            "scope": "Selected assembly, serialized scene and profiles match the pre-learning snapshot. "
                     "This is not an exhaustive dependency or process-time attestation."}


def stall_from_trace(path, row):
    """Existing ordered-stall criterion, integrated over observed sample intervals."""
    if not path.exists() or not row.get("actual_start"):
        return {"longest_ordered_stall_s": None, "trace_status": "missing"}
    samples, start = [], None
    with path.open() as stream:
        for line in stream:
            event = json.loads(line)
            if event.get("kind") == "start":
                start = event
                samples = [{**event, "progress": 0.}]
            elif event.get("kind") == "sample" and start:
                samples.append(event)
            elif event.get("kind") == "end" and start:
                break
    if canonical(start) != canonical(row["actual_start"]) or len(samples) != row.get("physics_samples"):
        raise ValueError(f"Raw trace does not match recorded episode: {row['case_id']}")
    duration = total = longest = current = 0.
    for a, b in zip(samples, samples[1:]):
        dt = b["elapsed_s"] - a["elapsed_s"]
        if dt < -1e-7:
            raise ValueError("Physical sample clock regressed")
        if dt <= 0:
            continue
        away = math.dist(b["tip_ros_m"], start["route_ros_m"][-1]) > .03
        stalled = away and (b.get("progress", 0.) - a.get("progress", 0.)) / dt < .005
        duration += dt if away else 0.
        total += dt if stalled else 0.
        current = current + dt if stalled else 0.
        longest = max(longest, current)
    fraction = total / duration if duration else None
    recorded = row.get("ordered_stall_fraction_nonterminal")
    if recorded is not None and (fraction is None or abs(recorded - fraction) > 1e-5):
        raise ValueError(f"Ordered-stall recomputation disagrees: {row['case_id']}")
    return {"longest_ordered_stall_s": longest, "ordered_stall_duration_s": total,
            "ordered_stall_fraction_nonterminal": fraction, "trace_status": "verified",
            "trace_source": fingerprint(path)}


def axis_max(axes, key):
    values = [number(axes.get(axis, {}).get(key)) for axis in AXES]
    return max(values) if all(value is not None for value in values) else None


def flatten_run(run, label):
    rows, axes_rows = [], []
    records = {row["case_id"]: row for row in run["results"]}
    for case_id in run["manifest"]["selected_case_ids"]:
        row = records[case_id]
        axes = row.get("execution", {}).get("axes", {})
        path = run["directory"] / f"benchmark_{run['manifest']['model']['name']}_{case_id}" / "benchmark_physics.jsonl"
        stall = stall_from_trace(path, row)
        endpoint, progress = number(row.get("endpoint_error_m")), number(row.get("final_progress"))
        rows.append({"variant": label, "case_id": case_id, "mode": row["case"]["taskMode"],
            "split": row["case"]["split"],
            "checkpoint_step": row.get("checkpoint_step"),
            "success": row.get("success") is True, "status": row.get("status", "unknown"),
            "endpoint_error_cm": endpoint * 100 if endpoint is not None else None,
            "elapsed_s": number(row.get("elapsed_s")),
            "final_progress_pct": progress * 100 if progress is not None else None,
            "longest_limit_s": axis_max(axes, "longest_limit_s"),
            "longest_neutral_s": axis_max(axes, "longest_neutral_s"),
            "longest_zero_proxy_s": axis_max(axes, "longest_nonzero_ref_zero_proxy_s"), **stall})
        axes_rows.extend({"variant": label, "case_id": case_id, "axis": axis, **axes.get(axis, {})} for axis in AXES)
    return rows, axes_rows


def descriptive(rows, key):
    values = [value for row in rows if (value := number(row.get(key))) is not None]
    return {"observed_cases": len(values), "missing_cases": len(rows) - len(values),
            "mean": statistics.mean(values) if values else None,
            "median": statistics.median(values) if values else None,
            "minimum": min(values) if values else None, "maximum": max(values) if values else None}


def summarize(rows, pairs):
    splits = {row.get("split", "dev") for row in rows}
    if len(splits) != 1 or not splits.issubset({"dev", "holdout"}):
        raise ValueError("Summary cannot combine different dataset splits")
    groups = []
    for label in ("baseline", "pilot"):
        for mode in ("all", *MODES):
            selected = [row for row in rows if row["variant"] == label and (mode == "all" or row["mode"] == mode)]
            statuses = sorted({row["status"] for row in selected})
            groups.append({"variant": label, "mode": mode, "cases": len(selected),
                "successes": sum(row["success"] for row in selected),
                "status_counts": {status: sum(row["status"] == status for row in selected) for status in statuses},
                "metrics": {key: descriptive(selected, key) for key in SCALARS}})
    lookup = {(row["variant"], row["case_id"]): row for row in rows}
    differences = [{"case_id": pair["case_id"], "mode": pair["mode"],
        **{key: (lookup[("pilot", pair["case_id"])][key] - lookup[("baseline", pair["case_id"])][key]
                 if pair["verified"] and all(lookup[(label, pair["case_id"])].get(key) is not None for label in ("baseline", "pilot")) else None)
           for key in SCALARS}} for pair in pairs]
    return {"split": next(iter(splits)), "groups": groups, "paired_differences_pilot_minus_baseline": differences,
            "paired_difference_summary": {key: descriptive(differences, key) for key in SCALARS},
            "all_measured_starts_paired": all(pair["verified"] for pair in pairs),
            "paired_comparison_permitted": all(pair["verified"] for pair in pairs),
            "pairing": pairs}


def write_csv(path, rows):
    keys = list(dict.fromkeys(key for row in rows for key in row if key != "trace_source"))
    with path.open("w", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=keys, extrasaction="ignore")
        writer.writeheader()
        writer.writerows(rows)


def plot_comparison(rows, summary, output):
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    from matplotlib.lines import Line2D
    plt.rcParams.update({"font.family": "DejaVu Sans", "font.size": 10,
        "axes.spines.top": False, "axes.spines.right": False, "axes.titleweight": "bold"})
    colors = {"baseline": "#577DA8", "pilot": "#17958F"}
    labels = {"baseline": "Baseline", "pilot": "Pilot"}
    split = split_label(summary.get("split", "dev"))
    fig, panels = plt.subplots(2, 2, figsize=(13, 8.7), constrained_layout=True)
    ax = panels[0, 0]
    for index, label in enumerate(("baseline", "pilot")):
        groups = [next(group for group in summary["groups"] if group["variant"] == label and group["mode"] == mode) for mode in MODES]
        bars = ax.bar([i + (index - .5) * .32 for i in range(2)], [group["successes"] for group in groups],
                      width=.3, color=colors[label], label=labels[label])
        for bar, group in zip(bars, groups):
            ax.text(bar.get_x() + bar.get_width()/2, bar.get_height()+.14, f"{group['successes']}/{group['cases']}", ha="center", weight="bold")
    ax.set(xticks=[0, 1], xticklabels=["Move-to-dig", "Transport mode"], ylim=(0, 9.1),
           yticks=[0, 2, 4, 6, 8], ylabel="Completed cases", title="A  Original completion criteria")
    ax.legend(frameon=False, loc="lower left")
    for ax, key, title, ylabel in ((panels[0, 1], "endpoint_error_cm", "B  Final endpoint error", "Endpoint error (cm)"),
        (panels[1, 0], "elapsed_s", "C  Observed episode duration", "Observed time (s)"),
        (panels[1, 1], "longest_ordered_stall_s", "D  Longest interval without path progress", "Longest interval (s)")):
        for case_index, pair in enumerate(summary["pairing"], 1):
            chosen = [next(row for row in rows if row["case_id"] == pair["case_id"] and row["variant"] == label) for label in ("baseline", "pilot")]
            if pair["verified"] and all(row.get(key) is not None for row in chosen):
                ax.plot([case_index-.12, case_index+.12], [row[key] for row in chosen], color="#B8C4CB", lw=1, zorder=1)
            for side, row in enumerate(chosen):
                value = row.get(key)
                if value is None:
                    continue
                marker = "o" if row["success"] else ("^" if row["status"] == "censored" else "X")
                ax.scatter(case_index + (side-.5)*.24, value, color=colors[row["variant"]],
                           marker=marker, s=42 if row["success"] else 65, edgecolor="white", linewidth=.5, zorder=3)
        ax.set(title=title, ylabel=ylabel, xlabel=f"Fixed {split} case", xticks=range(1, 17), xlim=(.5, 16.5))
        if [pair["mode"] for pair in summary["pairing"]] == [MODES[0]] * 8 + [MODES[1]] * 8:
            ax.axvline(8.5, color="#D3DBE0", lw=.8, ls=":")
            ax.set_xlabel("Case 1–8: approach | Case 9–16: transport")
        ax.set_ylim(bottom=0)
        ax.grid(axis="y", color="#E6EBEF", zorder=0)
        missing = sum(row.get(key) is None for row in rows)
        if missing:
            ax.text(.02, .96, f"Missing: {missing}/32 observations", transform=ax.transAxes, va="top", fontsize=8)
    panels[0, 1].axhline(3., color="#9BA5AD", ls="--", lw=1, label="Original 3 cm threshold")
    panels[0, 1].legend(frameon=False, fontsize=8)
    panels[1, 0].legend(handles=[Line2D([], [], marker="X", color="#647785", ls="", label="Failed / timeout"),
                               Line2D([], [], marker="^", color="#647785", ls="", label="Censored")], frameon=False, fontsize=8)
    fig.suptitle(f"Fixed {split} policy comparison | conditional execution proxy", fontsize=17, weight="bold")
    fig.supxlabel(f"16 paired {split} cases; failures retained. Time is observation duration, not time-to-success.\n"
                  "Empty-bucket proxy. No-progress interval: endpoint >3 cm and ordered progress gain <0.005/s. No real-machine claim.", fontsize=9)
    fig.savefig(output / "pilot_comparison.png", dpi=190, facecolor="white")
    plt.close(fig)


def fmt(value):
    return "missing" if value is None else f"{value:.3f}"


def write_report(output, analysis):
    summary = analysis["summary"]
    split = split_label(summary.get("split", "dev"))
    lines = [f"# Fixed {split} policy comparison", "", analysis["scope"], "",
        "All 16 allocated cases per model remain in the denominator. Censored duration is a lower bound on time-to-success; "
        "timeout and failure duration must not be read as successful completion time. Missing values remain missing.", "",
        "| Policy | Mode | Completed | Mean endpoint (cm) | Mean observed time (s) | Mean final progress (%) | Mean longest stall (s) |",
        "|---|---|---:|---:|---:|---:|---:|"]
    for group in summary["groups"]:
        metrics = group["metrics"]
        values = [fmt(metrics[key]["mean"]) + (f" (n={metrics[key]['observed_cases']})" if metrics[key]["missing_cases"] else "")
                  for key in ("endpoint_error_cm", "elapsed_s", "final_progress_pct", "longest_ordered_stall_s")]
        lines.append(f"| {group['variant']} | {group['mode']} | {group['successes']}/{group['cases']} | " + " | ".join(values) + " |")
    lines += ["", f"Measured start/reference pairing verified: {sum(pair['verified'] for pair in summary['pairing'])}/16.",
        "Existing pairing tolerances: joint difference ≤1e-4 m or rad, tip/reference difference ≤1 mm. "
        "Frozen case file hash, embedded case definitions, controller, recorded response/mapping identifiers and evaluation budgets are checked. "
        "This check does not by itself certify all player build dependencies or physical-model fidelity.", "",
        f"Selected build/profile snapshot check: `{analysis['build_snapshot']['status']}`; details and identities are in `comparison.json`.", "",
        "Limit, neutral and nonzero-reference/zero-proxy durations are separate. Per-case longest values are maxima across four axes; "
        "axis durations are not summed as independent observations. Stall uses observed physical samples and the original descriptive threshold; "
        "it is not a new success gate.", "", "![Fixed-case comparison](pilot_comparison.png)", "",
        "`paired_cases.csv` retains all case metrics/statuses; `axis_execution.csv` retains individual-axis execution metrics; "
        "`comparison.json` records pairing, missing counts, differences (pilot minus baseline) and source identities.", ""]
    (output / "report.md").write_text("\n".join(lines))


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--pilot", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--freeze", type=Path, required=True,
                        help="Explicit pre-learning identity JSON; existing source paths must remain accessible")
    args = parser.parse_args(argv)
    baseline, pilot = load_run(args.baseline), load_run(args.pilot)
    if baseline["manifest"]["sources"]["checkpoint"]["sha256"] == pilot["manifest"]["sources"]["checkpoint"]["sha256"]:
        raise ValueError("Baseline and pilot reference the same checkpoint; no learned-policy comparison can be labeled")
    pairs = verify_pairing(baseline, pilot)
    build_snapshot = verify_build_snapshot(baseline, pilot, args.freeze)
    baseline_rows, baseline_axes = flatten_run(baseline, "baseline")
    pilot_rows, pilot_axes = flatten_run(pilot, "pilot")
    rows = baseline_rows + pilot_rows
    analysis = {"schema": "real_execution_pilot_comparison.v1", "scope": scope_for_split(baseline["manifest"]["split"]),
        "created_utc": datetime.now(timezone.utc).isoformat(),
        "sources": {"baseline": baseline["sources"], "pilot": pilot["sources"], "analyzer": fingerprint(Path(__file__))},
        "checkpoints": {label: run["manifest"]["sources"]["checkpoint"] for label, run in (("baseline", baseline), ("pilot", pilot))},
        "build_snapshot": build_snapshot, "rows": rows, "summary": summarize(rows, pairs)}
    args.output.mkdir(parents=True, exist_ok=False)
    (args.output / "comparison.json").write_text(json.dumps(analysis, indent=2, ensure_ascii=False, allow_nan=False) + "\n")
    write_csv(args.output / "paired_cases.csv", rows)
    write_csv(args.output / "axis_execution.csv", baseline_axes + pilot_axes)
    plot_comparison(rows, analysis["summary"], args.output)
    write_report(args.output, analysis)
    print(json.dumps({"output": str(args.output.resolve()), "paired_cases": len(pairs),
        "successes": {label: sum(row["success"] for row in rows if row["variant"] == label) for label in ("baseline", "pilot")}}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
