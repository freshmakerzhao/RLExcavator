"""Local JSON-line client for the ExcavatorRuntime Unity scene.

Run ``python3 tools/sim_bridge_client.py --demo`` after pressing Play in Unity
to see the upper carriage rotate. Use ``--axis boom|stick|bucket`` to test
each cylinder visually. ``--smoke`` keeps the small single-axis check.
Without an option, enter one JSON request per line; each line receives one reply.
"""

import argparse
import json
import math
import socket
import sys
import time


def exchange(reader, writer, request, verbose=True):
    writer.write(json.dumps(request, allow_nan=False) + "\n")
    writer.flush()
    line = reader.readline()
    if not line:
        raise ConnectionError("Unity closed the simulation connection")
    reply = json.loads(line)
    if verbose:
        print(json.dumps(reply, ensure_ascii=False))
    return reply


def smoke(reader, writer):
    reset = exchange(reader, writer, {"kind": "reset"})
    if not reset.get("accepted"):
        raise RuntimeError("reset rejected: " + str(reset))

    epoch = reset["epoch"]
    before = exchange(reader, writer, {"kind": "step", "epoch": epoch, "steps": 1})
    if not before.get("state_valid"):
        raise RuntimeError("initial state invalid: " + str(before))

    command = {
        "kind": "velocity_reference",
        "epoch": epoch,
        "sequence": 1,
        "issued_sim_time_s": before["sim_time_s"],
        "expires_sim_time_s": before["sim_time_s"] + 0.25,
        "axis_order": ["boom", "stick", "bucket", "swing"],
        "values": [0.01, 0.0, 0.0, 0.0],
    }
    accepted = exchange(reader, writer, command)
    if not accepted.get("accepted"):
        raise RuntimeError("velocity rejected: " + str(accepted))

    after = exchange(reader, writer, {"kind": "step", "epoch": epoch, "steps": 10})
    if not after.get("state_valid"):
        raise RuntimeError("final state invalid: " + str(after))
    delta = (
        after["actuator_state"]["boom"]["position_m"]
        - before["actuator_state"]["boom"]["position_m"]
    )
    if delta <= 0:
        raise RuntimeError(f"boom did not move in the positive direction: {delta}")
    print(f"PASS: boom moved {delta:.6f} m in 10 physics steps")


def demo(reader, writer):
    reset = exchange(reader, writer, {"kind": "reset"}, verbose=False)
    if not reset.get("accepted") or not reset.get("state_valid"):
        raise RuntimeError("reset failed: " + str(reset))

    epoch = reset["epoch"]
    before = exchange(reader, writer, {"kind": "step", "epoch": epoch, "steps": 1}, verbose=False)
    if not before.get("state_valid"):
        raise RuntimeError("initial state invalid: " + str(before))

    issued_at = before["sim_time_s"]
    command = exchange(
        reader,
        writer,
        {
            "kind": "velocity_reference",
            "epoch": epoch,
            "sequence": 1,
            "issued_sim_time_s": issued_at,
            "expires_sim_time_s": issued_at + 2.6,
            "axis_order": ["boom", "stick", "bucket", "swing"],
            "values": [0.0, 0.0, 0.0, 0.3],
        },
        verbose=False,
    )
    if not command.get("accepted"):
        raise RuntimeError("swing command rejected: " + str(command))

    print("回转命令已接收。请看 Unity Game 窗口，上车将在 3 秒后开始旋转。", flush=True)
    time.sleep(3)
    after = exchange(reader, writer, {"kind": "step", "epoch": epoch, "steps": 120}, verbose=False)
    if not after.get("state_valid"):
        raise RuntimeError("final state invalid: " + str(after))
    angle_deg = math.degrees(
        after["actuator_state"]["swing"]["position_rad"]
        - before["actuator_state"]["swing"]["position_rad"]
    )
    if angle_deg < 20:
        raise RuntimeError(f"回转角度不足，实际只有 {angle_deg:.1f}°")
    print(f"PASS: 上车回转 {angle_deg:.1f}°（120 个物理步）；保持 Play 可查看最终姿态。")


def axis_demo(reader, writer, axis, speed, steps):
    reset = exchange(reader, writer, {"kind": "reset"}, verbose=False)
    if not reset.get("accepted") or not reset.get("state_valid"):
        raise RuntimeError("reset failed: " + str(reset))
    epoch = reset["epoch"]
    before = exchange(reader, writer, {"kind": "step", "epoch": epoch, "steps": 1}, verbose=False)
    if not before.get("state_valid"):
        raise RuntimeError("initial state invalid: " + str(before))

    axes = ["boom", "stick", "bucket", "swing"]
    values = [0.0] * 4
    values[axes.index(axis)] = speed
    issued_at = before["sim_time_s"]
    step_duration = issued_at - reset["sim_time_s"]
    command = exchange(
        reader,
        writer,
        {
            "kind": "velocity_reference",
            "epoch": epoch,
            "sequence": 1,
            "issued_sim_time_s": issued_at,
            "expires_sim_time_s": issued_at + steps * step_duration + 0.2,
            "axis_order": axes,
            "values": values,
        },
        verbose=False,
    )
    if not command.get("accepted"):
        raise RuntimeError(f"{axis} command rejected: {command}")

    print(f"{axis} 速度 {speed:+.3f} m/s 已接收；请切到 Unity Game 窗口，3 秒后开始运动。", flush=True)
    time.sleep(3)
    after = exchange(reader, writer, {"kind": "step", "epoch": epoch, "steps": steps}, verbose=False)
    if not after.get("state_valid"):
        raise RuntimeError("final state invalid: " + str(after))
    delta = (
        after["actuator_state"][axis]["position_m"]
        - before["actuator_state"][axis]["position_m"]
    )
    if delta * speed <= 0:
        raise RuntimeError(f"{axis} did not move as commanded: {delta:.6f} m")
    print(f"PASS: {axis} 油缸位移 {delta:+.4f} m（{steps} 个物理步）；保持 Play 可查看最终姿态。")


def connect_when_ready(port):
    deadline = time.monotonic() + 15
    while True:
        try:
            return socket.create_connection(("127.0.0.1", port), timeout=1)
        except ConnectionRefusedError:
            if time.monotonic() >= deadline:
                raise
            time.sleep(0.1)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=18790)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--smoke", action="store_true")
    mode.add_argument("--demo", action="store_true")
    mode.add_argument("--axis", choices=("boom", "stick", "bucket"))
    parser.add_argument("--speed", type=float, default=0.01, help="--axis 的目标油缸速度，m/s")
    parser.add_argument("--steps", type=int, default=80, help="--axis 的物理步数")
    args = parser.parse_args()
    if args.axis and (not math.isfinite(args.speed) or args.speed == 0 or not 1 <= args.steps <= 1000):
        parser.error("--axis 要求非零有限速度，步数为 1 到 1000")
    with connect_when_ready(args.port) as connection:
        connection.settimeout(90)
        with connection.makefile("r", encoding="utf-8") as reader, connection.makefile(
            "w", encoding="utf-8"
        ) as writer:
            if args.smoke:
                smoke(reader, writer)
            elif args.demo:
                demo(reader, writer)
            elif args.axis:
                axis_demo(reader, writer, args.axis, args.speed, args.steps)
            else:
                for line in sys.stdin:
                    if not line.strip():
                        continue
                    try:
                        exchange(reader, writer, json.loads(line))
                    except (ValueError, ConnectionError) as error:
                        print(error, file=sys.stderr)
                        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
