# ML-Agents 训练环境说明

## 目标

训练环境只负责 PPO 训练和 PyTorch checkpoint 保存，不在训练过程中导出 ONNX。

原因是当前 RTX 5070 Ti 环境使用的 PyTorch 版本较新，`torch.onnx.export`
默认走 dynamo 导出路径，会依赖 `onnxscript`。而当前 ML-Agents 训练环境固定使用
`onnx==1.15.0` 和 `protobuf==3.20.3`，直接安装 `onnxscript` 会引入依赖冲突。

## 训练命令

不要直接使用 `mlagents-learn` 进行长训。使用项目内 wrapper：

```bash
conda activate ml_agents
cd /home/zhaoshuai/workspace_uinty/RL_prj/RLExcavator

python tools/mlagents_train_no_onnx.py config/trajectory_ppo.yaml \
  --run-id=traj_attitude_4env_0615_1356 \
  --resume
```

该脚本不会修改第三方包，也不会修改 `site-packages`。它只在当前 Python 进程中关闭
ML-Agents 的 ONNX 导出调用。

训练过程中仍会保存：

```text
results/<run_id>/<behavior>-<step>.pt
results/<run_id>/checkpoint.pt
```

训练过程中不会生成：

```text
results/<run_id>/<behavior>-<step>.onnx
```

## 不要做的事情

- 不要手动修改 conda 环境中的 `site-packages/mlagents/...`。
- 不要手动修改 `/home/zhaoshuai/workspace_uinty/ml-agents` 中的第三方源码作为常规训练方案。
- 不要在训练环境中直接安装 `onnxscript`，否则容易升级 `onnx/protobuf` 并破坏 ML-Agents 依赖。

## ONNX 导出

ONNX 是部署到 Unity `Inference Only` 时才需要的产物。当前策略仍在训练和调参阶段，
优先保证训练环境稳定。

后续需要 ONNX 时，使用项目内 legacy exporter wrapper，不要污染训练环境，也不要
修改第三方包：

```bash
conda activate ml_agents
cd /home/zhaoshuai/workspace_uinty/RL_prj/RLExcavator

python tools/mlagents_export_legacy_onnx.py config/trajectory_ppo.yaml \
  --run-id=<run_id> \
  --resume
```

这个 wrapper 只在当前 Python 进程中把 ML-Agents 的策略导出函数改为调用
`torch.onnx.export(..., dynamo=False)`，并把 ML-Agents 训练循环替换成
“连接 Unity 后立即保存/导出并退出”。它不会修改 `site-packages`，也不会继续训练。

如果仍然希望导出与训练使用不同 conda 环境，可以建立一个旧 protobuf 的导出环境。
注意它不是新版 ONNX 工具链环境：

```bash
conda create -n ml_agents_export python=3.10.12 pip -y
conda activate ml_agents_export
pip install -r requirements-export.txt
```

不要使用 `python=3.10` 的模糊写法。Conda 可能会安装更新的 3.10.x，例如
3.10.20，而当前 ML-Agents Python 包要求 `>=3.10.1, <=3.10.12`。

不要在这个环境中安装 `onnxscript`。`onnxscript` 需要更新的 `protobuf`，会与
`mlagents-envs 1.2.0.dev0` 的 `protobuf<3.21` 要求冲突。
