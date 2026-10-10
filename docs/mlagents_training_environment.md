# ML-Agents 训练环境说明

## 目标

训练环境只负责 PPO 训练和 PyTorch checkpoint 保存，不在训练过程中导出 ONNX。

原因是当前 RTX 5070 Ti 环境使用的 PyTorch 版本较新，`torch.onnx.export`
默认走 dynamo 导出路径，会依赖 `onnxscript`。而当前 ML-Agents 训练环境固定使用
`onnx==1.15.0` 和 `protobuf==3.20.3`，直接安装 `onnxscript` 会引入依赖冲突。

## 本阶段训练命令（已完成）

不要直接使用 `mlagents-learn` 进行长训。使用项目内 wrapper：

```bash
conda activate ml_agents
cd /home/zhaoshuai/workspace_uinty/RL_prj/RLExcavator

python tools/mlagents_train_no_onnx.py config/trajectory_ppo.yaml \
  --env=Builds/ExcavatorTrajectoryLimitsV2/ExcavatorTrajectoryLimitsV2.x86_64 \
  --run-id=traj_full_follow_3cm_20260929 \
  --initialize-from=traj_full_follow_positive_20260929 \
  --seed=20260929 --time-scale=20 --num-envs=2 --no-graphics
```

修正后的播放器由 `RLExcavator.Editor.TrajectoryTrainingBuild.BuildLinux64()` 从 `Assets/Scenes/Training/ExcavatorRLTrain_Scale.unity` 构建；默认输出路径已改为上面的 `ExcavatorTrajectoryLimitsV2`。使用 `--env` 后无需在编辑器按 Play。四个环境均为 `Default`，不加载旧推理模型。场景仍用五路点 `FullTraining` 课程和原奖励；训练 prefab 不再把可视化目标球半径加进到点容差，按 `shared/machine_profile.jsonc` 的 0.03 m 判断。三缸物理方向修正在 `ExcavatorScaleDown.prefab`，活动限位由同一个 shared profile 在启动时写入 AGX 约束。

新构建将机器参数原样复制到播放器的 `StreamingAssets/machine_profile.jsonc`。`tools/run_checkpoint_screen.py` 优先记录该文件的字节哈希，并继续识别已有播放器的 `machine_profile.json`；历史构建和冻结基线无需改写。

`traj_full_follow_positive_20260929` 在旧限位和约 0.0678 m 的有效到点容差下训练，已于 4,517,139 步暂停并保留。其权重可以作为修正阶段的初始化，但不能以 `--resume` 继续旧 run，因为新阶段改变了物理限位、观测归一化和到点判据。2026-09-29 的 10 万步短探针显示，用旧权重初始化的策略可在新标准下继续提高成绩；详见工作区 `EvaluationReport/2026-09-29_rl_limits_repair_and_training_decision.md`。旧方向语义的模型仍不可使用。

训练场景已移除旧的逐物理步示教记录器、重复键盘测试器和 HUD；保留 Agent 自带的 Heuristic 键盘输入。当前运行使用两个并行 Unity 播放器，每个播放器包含四个训练环境；`--time-scale=20` 只改变仿真时间推进速度。两项都不改变 PPO 的观测、动作和网络结构。长训每 25 万步保存一次 checkpoint。

## 分段训练

`traj_extension_positive_fast2_20260928` 从零开始直接训练完整难度，在约 1290 万步中断。`traj_tracking_easy_positive_20260929` 从零开始完成短路径预训练，在 1,150,322 步中断。`traj_full_follow_positive_20260929` 从短路径 checkpoint 初始化，停在 4,517,139 步。修正后的 3 cm Follow 训练应另开 run，使用相同轴方向的已训练权重初始化；短路径模型不作为基线。

1. **先验物理可达性**：用当前 `machine_profile.jsonc` 的正反向速度和固定死区，分别检查 MoveToDig、CarryMaterial 的采样目标能否在 `maxStepsPerWaypoint=1200` 内到达。不可达时先调整路点分布或训练时间预算，不修改权威机器速度来迁就指标。
2. **短路径预训练（已完成）**：关闭附加奖励并在短路径学习位置跟踪；其 checkpoint 仅是初始化资产。
3. **完整 Follow（2026-09-30 训练完成，2026-10-01 到点验收通过）**：新 run 在修正限位和 3 cm 到点判据下完成 20,000,037 步。两个新种子的确定性推理共完成 1047/1047 回合；主项目 MoveToDig、CarryMaterial 五路点和默认两路点均完成，终点停止后误差仍在容差内。正式导出文件为 `Assets/AIModels/ScaleFollow3cm_20260929.onnx`，主项目默认配置已引用。完整证据见工作区 `EvaluationReport/2026-10-01_rl_follow_acceptance.md`；该验收范围为尖端到点和停止。
4. **后续优化**：仿真中从选点到挖掘、倾倒的完整流程跑通后，再针对真实失败模式逐项研究死区、终点制动等改进；不把当前短路径成功率当成系统完成率。

阶段切换使用新 run ID 和 `--initialize-from`，重新开始优化器计划；只有同一阶段中断后接着训练才使用 `--resume`。每阶段保存其场景/播放器版本、profile、run ID 和固定评估结果，避免不同物理设置下的成功率被直接比较。

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

ONNX 用于主项目的策略推理，也可用于 Unity `Inference Only`。当前 Follow 基线已导出并冻结；其身份和使用限制见 [RL 基线说明](rl_baseline_v1.md)。

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

## 目录整理后的来源记录

2026-10-05 将两个训练场景移至 `Assets/Scenes/Training`，训练脚本归入 `Assets/Scripts/Training`，预制体目录改为 `Assets/Prefabs`，模型源文件归入 `Assets/Models`。资源 GUID 保持不变。基线 `.baseline.json` 中的旧场景路径和 prefab 哈希仍表示冻结当日的历史来源，不改写它们来冒充新目录的哈希。当前训练场景、物理参数、采样和奖励的清理回归见 [清理记录](../../EvaluationReport/2026-10-05_unity_cleanup.md)。
