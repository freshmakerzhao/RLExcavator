"""Strict 47D/four-action deterministic PT loader; no environment or process launch."""
from __future__ import annotations

import yaml
from mlagents.trainers.settings import NetworkSettings
from mlagents.trainers.torch_entities.networks import SimpleActor

BEHAVIOR = "ExcavatorContinuousPath"
OBSERVATIONS = 47


def network_settings(path):
    settings = yaml.safe_load(path.read_text())["behaviors"][BEHAVIOR]["network_settings"]
    if settings.get("memory"):
        raise ValueError("This screen supports the current feed-forward policy only")
    return NetworkSettings(
        normalize=settings.get("normalize", False),
        hidden_units=settings.get("hidden_units", 128),
        num_layers=settings.get("num_layers", 2),
        deterministic=True,
    )


def validate_spec(spec):
    if len(spec.observation_specs) != 1 or spec.observation_specs[0].shape != (OBSERVATIONS,):
        raise ValueError(f"Expected one {OBSERVATIONS}D vector observation: {spec.observation_specs}")
    if spec.action_spec.continuous_size != 4 or spec.action_spec.discrete_size != 0:
        raise ValueError(f"Expected four continuous actuator commands: {spec.action_spec}")


def make_actor(spec, settings, checkpoint):
    validate_spec(spec)
    actor = SimpleActor(spec.observation_specs, settings, spec.action_spec)
    actor.load_state_dict(checkpoint["Policy"], strict=True)
    actor.to("cpu")
    actor.eval()
    return actor
