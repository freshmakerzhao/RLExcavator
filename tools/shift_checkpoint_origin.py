#!/usr/bin/env python3
"""为已确认的纯原点平移生成新的 ML-Agents Follow warm-start checkpoint。

在 ml_agents 环境运行，--origin-shift-unity-m 是新原点减旧训练原点，
以旧训练坐标系的 Unity XYZ 米表示；不是 ROS XYZ，也不是观测增加量。
仅适用于当前 38 维、256×2、4 连续动作的 PPO 网络。不移动实体、改变轴向、
尺度、观测顺序或动力学。调用者必须先在 Unity 确认这些前提。
只调整绝对 tip 观测 9:12 的均值，保留方差、计数及所有学习参数。
随后用安装的 ML-Agents 网络验证确定动作和 critic，再排他创建新文件。
输出仅用于 --initialize-from 新 run；本工具不启动训练，也不覆盖源模型。
"""

import argparse
import hashlib
import io
import json
import math
from pathlib import Path
import sys

import torch


SECTIONS = ('Policy', 'Optimizer:critic')
PREFIXES = ('network_body.observation_encoder.processors.0.normalizer.',
            'network_body.processors.0.normalizer.')


def observation_shift(origin_shift_unity_m, position_normalizer):
    if (len(origin_shift_unity_m) != 3
            or not all(math.isfinite(x) for x in origin_shift_unity_m)
            or not math.isfinite(position_normalizer) or position_normalizer <= 0):
        raise ValueError('Require three finite Unity metres and a positive position normalizer')
    # 新原点向 +Y 移动，物体的新 Y 坐标就向 -Y 移动，均值也必须同向移动。
    shift = -torch.tensor(origin_shift_unity_m, dtype=torch.float32,
                          device='cpu') / position_normalizer
    return torch.cat((torch.zeros(9, device='cpu'), shift, torch.zeros(26, device='cpu')))


def shifted_state(checkpoint, shift):
    changed = []
    result = dict(checkpoint)
    for section in SECTIONS:
        state = checkpoint[section]
        for suffix in ('running_mean', 'running_variance', 'normalization_steps'):
            first, second = (state[prefix + suffix] for prefix in PREFIXES)
            if not torch.equal(first, second):
                raise ValueError(f'Normalizer aliases disagree: {section}/{suffix}')
            expected_shape = () if suffix == 'normalization_steps' else (38,)
            if tuple(first.shape) != expected_shape or not torch.isfinite(first).all():
                raise ValueError(f'Invalid normalizer: {section}/{suffix}')
            if suffix != 'running_mean' and not (first > 0).all():
                raise ValueError(f'Nonpositive normalizer: {section}/{suffix}')
        keys = [prefix + 'running_mean' for prefix in PREFIXES]
        # 不对共享 storage 做原地加减：state_dict 中两条路径可能指向同一缓冲区。
        result[section] = type(state)((key, value + shift if key in keys else value)
                                      for key, value in state.items())
        changed.extend(f'{section}/{key}' for key in state if key in keys)
    return result, changed


def load_networks(checkpoint):
    from mlagents.trainers.settings import NetworkSettings
    from mlagents.trainers.torch_entities.networks import SimpleActor, ValueNetwork
    from mlagents_envs.base_env import (ActionSpec, DimensionProperty, ObservationSpec,
                                      ObservationType)

    observation = [ObservationSpec((38,), (DimensionProperty.NONE,),
                                   ObservationType.DEFAULT, 'VectorSensor')]
    settings = NetworkSettings(normalize=True, hidden_units=256, num_layers=2,
                               deterministic=True)
    # ML-Agents 导入可能将默认设备设为 CUDA；离线等价验证固定在 CPU。
    with torch.device('cpu'):
        actor = SimpleActor(observation, settings, ActionSpec.create_continuous(4)).eval()
        critic = ValueNetwork(['extrinsic'], observation, settings).eval()
    # 严格加载拒绝网络架构漂移，避免对未知 checkpoint 默默做“近似”适配。
    actor.load_state_dict(checkpoint['Policy'], strict=True)
    critic.load_state_dict(checkpoint['Optimizer:critic'], strict=True)
    return actor, critic


def verify_equivalence(old, new, shift):
    old_actor, old_critic = load_networks(old)
    new_actor, new_critic = load_networks(new)
    normalizer = old_actor.network_body.observation_encoder.processors[0].normalizer
    mean = normalizer.running_mean
    std = (normalizer.running_variance / normalizer.normalization_steps).sqrt()
    generator = torch.Generator(device='cpu').manual_seed(20261013)
    # 测试 checkpoint 分布附近和裁剪边界；这是代数平移验证，不冒充 Unity 轨迹验收。
    probes = torch.cat((mean[None], mean[None] - 6 * std, mean[None] + 6 * std,
                        mean + torch.randn(512, 38, generator=generator,
                                           device='cpu') * std * 2))
    translated = probes + shift
    with torch.no_grad(), torch.device('cpu'):
        normal_new = new_actor.network_body.observation_encoder.processors[0].normalizer
        norm_old, norm_new = normalizer(probes), normal_new(translated)
        torch.testing.assert_close(norm_old, norm_new, rtol=0, atol=2e-6)
        old_action, old_stats, _ = old_actor.get_action_and_stats([probes])
        new_action, new_stats, _ = new_actor.get_action_and_stats([translated])
        pairs = {
            'action': (old_action.continuous_tensor, new_action.continuous_tensor),
            'env_action': (torch.from_numpy(old_stats['env_action'].continuous),
                           torch.from_numpy(new_stats['env_action'].continuous)),
            'value': (old_critic([probes])[0]['extrinsic'],
                      new_critic([translated])[0]['extrinsic']),
        }
        for name, (before, after) in pairs.items():
            # critic 输出可达数百；允许 float32 相对舍入误差，动作仍用绝对阈值。
            torch.testing.assert_close(before, after, rtol=1e-6 if name == 'value' else 0,
                                       atol=1e-5)
    return {'samples': len(probes), 'scope': 'checkpoint inference on deterministic synthetic probes',
            'normalized_max_abs_error': float((norm_old - norm_new).abs().max()),
            **{name + '_max_abs_error': float((a - b).abs().max())
               for name, (a, b) in pairs.items()}}


def shift_checkpoint(source, output, origin_shift_unity_m, position_normalizer):
    source, output = Path(source).resolve(strict=True), Path(output).absolute()
    if output.resolve() == source or output.exists() or output.is_symlink():
        raise ValueError('Refusing source overwrite or an existing output; choose a new .pt path')
    shift = observation_shift(origin_shift_unity_m, position_normalizer)
    # 只加载张量/基础容器，不允许反序列化任意自定义 Python 对象。
    source_bytes = source.read_bytes()
    original = torch.load(io.BytesIO(source_bytes), map_location='cpu', weights_only=True)
    adjusted, changed = shifted_state(original, shift)
    buffer = io.BytesIO()
    torch.save(adjusted, buffer)
    payload = buffer.getvalue()
    roundtrip = torch.load(io.BytesIO(payload), map_location='cpu', weights_only=True)
    verification = verify_equivalence(original, roundtrip, shift)
    # 等价验证完成后再创建；xb 同时阻止检查与写入之间出现的覆盖竞争。
    with output.open('xb') as stream:
        stream.write(payload)
    return {'schema': 'rl_origin_warmstart.v1', 'source': str(source), 'output': str(output),
            'source_sha256': hashlib.sha256(source_bytes).hexdigest(),
            'output_sha256': hashlib.sha256(payload).hexdigest(),
            'origin_shift_unity_m': list(origin_shift_unity_m),
            'position_normalizer': position_normalizer, 'changed_keys': changed,
            'verification': verification}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path)
    parser.add_argument('output', type=Path)
    parser.add_argument('--origin-shift-unity-m', type=float, nargs=3, required=True,
                        metavar=('DX', 'DY', 'DZ'))
    parser.add_argument('--position-normalizer', type=float, required=True)
    args = parser.parse_args(argv)
    try:
        print(json.dumps(shift_checkpoint(args.source, args.output, args.origin_shift_unity_m,
                                          args.position_normalizer), indent=2))
    except (OSError, ValueError, KeyError, RuntimeError, AssertionError) as exc:
        print(f'Origin checkpoint shift refused: {exc}', file=sys.stderr)
        return 2
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
