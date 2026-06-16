#!/usr/bin/env python3
"""Run ML-Agents with the legacy TorchScript ONNX exporter.

PyTorch 2.12 defaults `torch.onnx.export(..., dynamo=True)`, which requires
`onnxscript`. ML-Agents 1.2.0.dev0 pins protobuf to the old 3.x range, while
recent onnxscript versions require newer protobuf. This wrapper avoids that
dependency conflict without modifying ML-Agents or site-packages.

Use this only when ONNX output is needed. It connects to Unity once to obtain
the BehaviorSpec, loads the requested checkpoint, exports ONNX, and exits
without advancing training.
"""

from __future__ import annotations

import logging
import sys


def force_legacy_onnx_export() -> None:
    """Force ML-Agents policy export to use torch.onnx dynamo=False."""
    from mlagents.trainers.trainer_controller import TrainerController
    from mlagents.trainers.settings import SerializationSettings
    from mlagents.trainers.torch_entities.model_serialization import (
        ModelSerializer,
        exporting_to_onnx,
    )
    from mlagents.torch_utils import torch

    logger = logging.getLogger("rl_excavator.mlagents_export_legacy_onnx")

    def export_policy_model(self: ModelSerializer, output_filepath: str) -> None:
        onnx_output_path = f"{output_filepath}.onnx"
        logger.info("Exporting %s with legacy torch.onnx exporter.", onnx_output_path)

        with exporting_to_onnx():
            torch.onnx.export(
                self.policy.actor,
                self.dummy_input,
                onnx_output_path,
                opset_version=SerializationSettings.onnx_opset,
                input_names=self.input_names,
                output_names=self.output_names,
                dynamic_axes=self.dynamic_axes,
                dynamo=False,
            )

        logger.info("Exported %s", onnx_output_path)

    ModelSerializer.export_policy_model = export_policy_model

    def export_once(self: TrainerController, env_manager) -> None:
        logger.info(
            "Export-only mode: connecting to Unity, loading checkpoint, exporting ONNX, then exiting."
        )
        self._create_output_path(self.output_path)
        self._reset_env(env_manager)
        self._save_models()
        self.join_threads()

    TrainerController.start_learning = export_once


def main() -> None:
    if len(sys.argv) == 1:
        print(
            "Usage: python tools/mlagents_export_legacy_onnx.py "
            "config/trajectory_ppo.yaml --run-id=<run_id> --resume [mlagents-learn args...]",
            file=sys.stderr,
        )
        raise SystemExit(2)

    force_legacy_onnx_export()

    from mlagents.trainers.learn import main as mlagents_main

    mlagents_main()


if __name__ == "__main__":
    main()
