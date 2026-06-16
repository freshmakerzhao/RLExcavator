#!/usr/bin/env python3
"""Run mlagents-learn while disabling ONNX export.

This wrapper keeps the Python environment and third-party ML-Agents package
read-only. It only changes ML-Agents behavior inside this Python process:

- PyTorch checkpoints are still written.
- Intermediate and final ONNX export calls are skipped.

Use this for training. Export ONNX separately only when a trained policy needs
to be loaded by Unity inference.
"""

from __future__ import annotations

import logging
import sys


def disable_onnx_export() -> None:
    """Disable ML-Agents ONNX export for the current process only."""
    from mlagents.trainers.model_saver.torch_model_saver import TorchModelSaver
    from mlagents.trainers.settings import SerializationSettings
    from mlagents.trainers.torch_entities.model_serialization import ModelSerializer

    logger = logging.getLogger("rl_excavator.mlagents_train_no_onnx")

    SerializationSettings.convert_to_onnx = False

    def skip_policy_export(self: ModelSerializer, output_filepath: str) -> None:
        logger.info("Skipping ONNX export for %s. PyTorch checkpoint is kept.", output_filepath)

    def skip_model_export(
        self: TorchModelSaver, output_filepath: str, behavior_name: str
    ) -> None:
        logger.info(
            "Skipping ONNX export for behavior %s at %s. PyTorch checkpoint is kept.",
            behavior_name,
            output_filepath,
        )

    ModelSerializer.export_policy_model = skip_policy_export
    TorchModelSaver.export = skip_model_export


def main() -> None:
    if len(sys.argv) == 1:
        print(
            "Usage: python tools/mlagents_train_no_onnx.py "
            "config/trajectory_ppo.yaml --run-id=<run_id> [mlagents-learn args...]",
            file=sys.stderr,
        )
        raise SystemExit(2)

    disable_onnx_export()

    from mlagents.trainers.learn import main as mlagents_main

    mlagents_main()


if __name__ == "__main__":
    main()
