# ML-Agents 训练、推理与 ONNX 导出笔记

## 1. 背景

当前项目使用：

```text
Unity 6
com.unity.ml-agents 4.0.3
Python mlagents / mlagents-envs 1.2.0.dev0
PyTorch 2.12.0
ONNX 1.15.0
protobuf 3.20.3
```

Unity 端 ML-Agents package 和 Python 端 `mlagents-learn` 是两套东西：

```text
Unity package:
  负责 Agent、Behavior Parameters、DecisionRequester、Unity 通信。

Python mlagents-learn:
  负责 PPO 训练、checkpoint 保存、ONNX 导出。
```

所以 Unity 端 package 是最新版本，并不代表 Python 端训练/导出依赖一定兼容。

## 2. 遇到的问题

直接运行：

```bash
mlagents-learn config/trajectory_ppo.yaml --run-id=<run_id> --resume
```

训练到 checkpoint 或训练结束时，ML-Agents 会自动尝试导出 ONNX。

当前环境中，PyTorch 2.12 的 `torch.onnx.export` 默认使用新版 dynamo 导出路径：

```python
torch.onnx.export(..., dynamo=True)
```

该路径需要 `onnxscript`。

但是 `onnxscript` 需要较新的 `onnx / protobuf`，而当前 `mlagents-envs 1.2.0.dev0`
要求：

```text
protobuf >= 3.6, < 3.21
```

因此如果在训练环境中安装 `onnxscript`，会导致依赖冲突。

典型错误包括：

```text
ModuleNotFoundError: No module named 'onnxscript'
```

或者：

```text
Cannot install protobuf==7.35.0 because mlagents-envs depends on protobuf<3.21
```

## 3. 不采用的方案

以下方案不再使用：

```text
1. 修改 conda 环境中的 site-packages
2. 修改 /home/zhaoshuai/workspace_uinty/ml-agents 第三方源码
3. 在训练环境中强行安装 onnxscript
4. 把 protobuf 升级到 7.x
```

原因：

```text
这些做法会破坏环境可复现性，也容易影响训练依赖。
```

## 4. 最终方案

使用项目内 wrapper 脚本控制训练和导出行为。

这些脚本只影响当前 Python 进程，不修改第三方包，不修改 `site-packages`。

### 4.1 训练时跳过 ONNX 导出

脚本：

```text
tools/mlagents_train_no_onnx.py
```

作用：

```text
训练正常进行
.pt checkpoint 正常保存
ONNX 导出被跳过
不会触发 onnxscript 报错
```

训练时不要直接运行 `mlagents-learn`，而是运行：

```bash
conda activate ml_agents
cd /home/zhaoshuai/workspace_uinty/RL_prj/RLExcavator

python tools/mlagents_train_no_onnx.py config/trajectory_ppo.yaml \
  --run-id=<run_id>
```

续训：

```bash
python tools/mlagents_train_no_onnx.py config/trajectory_ppo.yaml \
  --run-id=<run_id> \
  --resume
```

训练过程中会保存：

```text
results/<run_id>/checkpoint.pt
results/<run_id>/ExcavatorTrajectory/ExcavatorTrajectory-<step>.pt
```

训练过程中不会保存：

```text
*.onnx
```

### 4.2 导出 ONNX 时使用 legacy exporter

脚本：

```text
tools/mlagents_export_legacy_onnx.py
```

作用：

```text
使用 ML-Agents 官方 trainer 流程加载 checkpoint
但在当前 Python 进程中强制 torch.onnx.export(..., dynamo=False)
避免 onnxscript/protobuf 冲突
```

导出命令：

```bash
conda activate ml_agents
cd /home/zhaoshuai/workspace_uinty/RL_prj/RLExcavator

python tools/mlagents_export_legacy_onnx.py config/trajectory_ppo.yaml \
  --run-id=<run_id> \
  --resume
```

示例：

```bash
python tools/mlagents_export_legacy_onnx.py config/trajectory_ppo.yaml \
  --run-id=traj_attitude_4env_0615_1356 \
  --resume
```

成功后会生成类似：

```text
results/traj_attitude_4env_0615_1356/ExcavatorTrajectory/ExcavatorTrajectory-2000035.onnx
```

该脚本是 export-only：

```text
连接 Unity
获取 BehaviorSpec
加载 checkpoint
立即保存/导出 ONNX
退出
```

它不会继续训练，也不会为了等待 checkpoint interval 而推进环境 step。

## 5. 为什么导出 ONNX 需要启动 Unity

当前 `tools/mlagents_export_legacy_onnx.py` 本质上仍然走 ML-Agents trainer 流程。

Python trainer 启动后需要 Unity 连接，原因是它要从 Unity 获取 BehaviorSpec：

```text
Behavior name
Observation shape
Action space
Sensor 信息
```

然后它才能：

```text
构建 policy 网络
加载 checkpoint.pt
导出 ONNX
```

所以导出时终端会显示：

```text
Listening on port xxxx. Start training by pressing the Play button in the Unity Editor.
```

这不是异常，而是 ML-Agents 需要 Unity 提供当前 Agent 的规格。

## 6. 导出命令会不会继续训练

当前 `tools/mlagents_export_legacy_onnx.py` 已经被设计为 export-only。如果运行：

```bash
python tools/mlagents_export_legacy_onnx.py config/trajectory_ppo.yaml \
  --run-id=<run_id> \
  --resume
```

它会：

```text
连接 Unity
获取 BehaviorSpec
加载 checkpoint
立即导出 ONNX
退出
```

不需要等待 `max_steps`，也不需要等待 `checkpoint_interval`。

可以加 `--inference`，但不是必须：

```bash
python tools/mlagents_export_legacy_onnx.py config/trajectory_ppo.yaml \
  --run-id=<run_id> \
  --resume \
  --inference
```

该方式仍然需要 Unity 连接，因为仍然需要 BehaviorSpec。

如果未来需要完全离线导出，需要单独实现一个更底层的脚本，手动构造 policy 网络并加载
`checkpoint.pt`。当前阶段暂不优先做。

## 7. 推理方式

### 7.1 Python inference

如果只想用 Python 加载已有 run 做推理测试：

```bash
conda activate ml_agents
cd /home/zhaoshuai/workspace_uinty/RL_prj/RLExcavator

python tools/mlagents_train_no_onnx.py config/trajectory_ppo.yaml \
  --run-id=<run_id> \
  --resume \
  --inference
```

如果想用随机采样动作，不加 `--deterministic`。

如果想使用策略分布均值动作：

```bash
python tools/mlagents_train_no_onnx.py config/trajectory_ppo.yaml \
  --run-id=<run_id> \
  --resume \
  --inference \
  --deterministic
```

注意：之前实验中发现，某些策略加 `--deterministic` 后会卡在局部状态，不加时反而能继续推进。
这说明策略均值动作还不够稳定，不能仅凭 deterministic 推理判断策略完全失败。

### 7.2 Unity ONNX inference

导出 `.onnx` 后，在 Unity 中使用：

```text
Behavior Parameters
  Model: 选择导出的 .onnx
  Behavior Type: Inference Only
  Inference Device: Default 或 CPU
```

然后点击 Play。

需要保证：

```text
1. Unity 场景中的 Behavior Name 与训练时一致
2. Vector Observation Space Size 与训练时一致
3. Continuous Actions 数量与训练时一致
4. task mode / target pitch / trajectory 设置与训练时一致
```

当前 `ExcavatorTrajectory` 的关键规格：

```text
Behavior Name: ExcavatorTrajectory
Vector Observation Space Size: 37
Continuous Actions: 4
```

## 8. 当前推荐工作流

### 8.1 长时间训练

```bash
conda activate ml_agents
cd /home/zhaoshuai/workspace_uinty/RL_prj/RLExcavator

python tools/mlagents_train_no_onnx.py config/trajectory_ppo.yaml \
  --run-id=<run_id> \
  --resume
```

### 8.2 快速 smoke 测试

```bash
python tools/mlagents_train_no_onnx.py config/trajectory_smoke.yaml \
  --run-id=traj_smoke_$(date +%m%d_%H%M)
```

`trajectory_smoke.yaml` 的 checkpoint interval 较小，用来验证：

```text
Unity 能连接
episode 能运行
reward 能写入
.pt checkpoint 能保存
ONNX 能跳过
```

### 8.3 ONNX 导出

```bash
python tools/mlagents_export_legacy_onnx.py config/trajectory_ppo.yaml \
  --run-id=<run_id> \
  --resume
```

## 9. 环境分工

当前建议只保留一个主训练环境：

```text
ml_agents
```

它负责：

```text
训练
Python inference
legacy ONNX export
```

不要在该环境中安装：

```text
onnxscript
protobuf 7.x
```

如果未来确实需要单独导出环境，可以使用：

```bash
conda create -n ml_agents_export python=3.10.12 pip -y
conda activate ml_agents_export
pip install -r requirements-export.txt
```

但目前不是必须。

## 10. 常见检查命令

查看环境版本：

```bash
conda activate ml_agents
python -V
pip show mlagents mlagents-envs torch onnx protobuf onnxscript
```

确认没有安装 `onnxscript`：

```bash
pip show onnxscript
```

确认训练结果：

```bash
find results/<run_id> -name "*.pt"
find results/<run_id> -name "*.onnx"
```

查看 TensorBoard：

```bash
tensorboard --logdir results
```

## 11. 当前项目注意事项

当前轨迹跟踪策略仍在调试阶段。判断策略质量时不要只看：

```text
Environment/Cumulative Reward
```

还要看：

```text
Trajectory/SuccessRate
Trajectory/WaypointReachRate
Trajectory/PathProgress
Trajectory/BucketPitchErrorDeg
Trajectory/TimeoutRate
```

当前阶段建议：

```text
先固定 MoveToDig 单模式训练
不要过早打开 randomizeTaskModeOnEpisodeBegin
等 MoveToDig 稳定后，再训练 CarryMaterial
最后再考虑混合 task mode
```
