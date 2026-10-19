# ML-Agents 训练环境说明

## 目标

训练环境只负责 PPO 训练和 PyTorch checkpoint 保存，不在训练过程中导出 ONNX。

原因是当前 RTX 5070 Ti 环境使用的 PyTorch 版本较新，`torch.onnx.export`
默认走 dynamo 导出路径，会依赖 `onnxscript`。而当前 ML-Agents 训练环境固定使用
`onnx==1.15.0` 和 `protobuf==3.20.3`，直接安装 `onnxscript` 会引入依赖冲突。

## 当前阶段：支撑面原点重训（2026-10-13，训练中）

用户已确认机器原点为回转轴线与履带支撑平面的交点，并要求重新训练 RL。坐标及旧模型过渡定义见 [ADR-0023](../../docs/adr/0023-swing-axis-runtime-origin.md)。两个训练场景的各四个 MachineRoot 已迁移；可达工作空间作反向平移，抽样世界位置最大差异约 `9.61e-7 m`，物理实体保持原位。

- 新播放器：`Builds/ExcavatorGroundOrigin/ExcavatorGroundOrigin.x86_64`，须由迁移后的完整 Follow 场景构建，不使用历史播放器启动本次训练。
- 新 run：`traj_ground_origin_20261013`，沿用 `config/trajectory_ppo.yaml` 的 20,000,000 步预算；单独保存产物，不覆盖或续写旧 run。
- 初始化：`tools/shift_checkpoint_origin.py` 将旧 checkpoint 的绝对铲尖观测归一化均值平移，先验证 actor/critic 等价，再生成新的 warm-start 副本。`--origin-shift-unity-m` 是**新原点减旧训练原点**，本次为 `0 -0.5 -0.058464295`；不是 ROS 轴序，也不是给观测直接加的偏移。`--position-normalizer` 须取共享契约值，本次为 `1.13`。已生成副本 `results/traj_ground_origin_init_20261013/ExcavatorTrajectory/checkpoint.pt`。
- 训练使用 `--initialize-from` 新副本；只有本次同一 run 中断后才使用 `--resume`。当前已从第 0 步启动，尚未完成新模型验收，实际命令和进度以[本次记录](../../EvaluationReport/2026-10-13_ground_origin_retrain/README.md)为准。
- 训练完成后单独导出、做固定种子与主项目 Follow/循环验收，成功后才替换活动 ONNX、模型元数据，并把共享契约中的运行到训练原点偏移切为零。此前旧基线继续经平移适配运行。

以下为本次已启动的实际命令；不要同时启动同一 run 的第二个训练进程：

```bash
conda activate ml_agents
cd /home/zhaoshuai/workspace_uinty/RL_prj/RLExcavator

python tools/mlagents_train_no_onnx.py config/trajectory_ppo.yaml \
  --env=Builds/ExcavatorGroundOrigin/ExcavatorGroundOrigin.x86_64 \
  --run-id=traj_ground_origin_20261013 \
  --initialize-from=traj_ground_origin_init_20261013 \
  --seed=20261013 --time-scale=20 --num-envs=2 --no-graphics
```

## 历史基线训练命令（2026-09-30 已完成）

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

修正后的播放器由 `RLExcavator.Editor.TrajectoryTrainingBuild.BuildLinux64()` 从 `Assets/Scenes/Training/ExcavatorRLTrain_Scale.unity` 构建；当时输出路径为上面的 `ExcavatorTrajectoryLimitsV2`（当前默认已改为 `ExcavatorGroundOrigin`）。使用 `--env` 后无需在编辑器按 Play。四个环境均为 `Default`，不加载旧推理模型。场景仍用五路点 `FullTraining` 课程和原奖励；训练 prefab 不再把可视化目标球半径加进到点容差，按 `shared/machine_profile.jsonc` 的 0.03 m 判断。三缸物理方向修正在 `ExcavatorScaleDown.prefab`，活动限位由同一个 shared profile 在启动时写入 AGX 约束。

新构建将机器参数原样复制到播放器的 `StreamingAssets/machine_profile.jsonc`。`tools/run_checkpoint_screen.py` 优先记录该文件的字节哈希，并继续识别已有播放器的 `machine_profile.json`；历史构建和冻结基线无需改写。

`traj_full_follow_positive_20260929` 在旧限位和约 0.0678 m 的有效到点容差下训练，已于 4,517,139 步暂停并保留。其权重可以作为修正阶段的初始化，但不能以 `--resume` 继续旧 run，因为新阶段改变了物理限位、观测归一化和到点判据。2026-09-29 的 10 万步短探针显示，用旧权重初始化的策略可在新标准下继续提高成绩；详见工作区 `EvaluationReport/2026-09-29_rl_limits_repair_and_training_decision.md`。旧方向语义的模型仍不可使用。

训练场景已移除旧的逐物理步示教记录器、重复键盘测试器和 HUD；保留 Agent 自带的 Heuristic 键盘输入。当前运行使用两个并行 Unity 播放器，每个播放器包含四个训练环境；`--time-scale=20` 只改变仿真时间推进速度。两项都不改变 PPO 的观测、动作和网络结构。长训每 25 万步保存一次 checkpoint。

## 分段训练

`traj_extension_positive_fast2_20260928` 从零开始直接训练完整难度，在约 1290 万步中断。`traj_tracking_easy_positive_20260929` 从零开始完成短路径预训练，在 1,150,322 步中断。`traj_full_follow_positive_20260929` 从短路径 checkpoint 初始化，停在 4,517,139 步。修正后的 3 cm Follow 训练应另开 run，使用相同轴方向的已训练权重初始化；短路径模型不作为基线。

1. **先验物理可达性**：用当前 `machine_profile.jsonc` 的正反向速度和固定死区，分别检查 MoveToDig、CarryMaterial 的采样目标能否在 `maxStepsPerWaypoint=1200` 内到达。不可达时先调整路点分布或训练时间预算，不修改权威机器速度来迁就指标。
2. **短路径预训练（已完成）**：关闭附加奖励并在短路径学习位置跟踪；其 checkpoint 仅是初始化资产。
3. **完整 Follow（2026-09-30 训练完成，2026-10-01 到点验收通过）**：新 run 在修正限位和 3 cm 到点判据下完成 20,000,037 步。两个新种子的确定性推理共完成 1047/1047 回合；主项目 MoveToDig、CarryMaterial 五路点和默认两路点均完成，终点停止后误差仍在容差内。导出文件 `Assets/AIModels/ScaleFollow3cm_20260929.onnx` 保留为旧原点冻结基线。完整证据见工作区 `EvaluationReport/2026-10-01_rl_follow_acceptance.md`；该验收范围为尖端到点和停止。
4. **支撑平面原点重训（2026-10-14 完成并切换）**：`traj_ground_origin_20261013` 完成 20,000,034 步。最终模型经两个新种子 324/324 已终止回合筛查、18,944 条观测 ONNX 数值一致性及主项目 Follow/多铲运行验证，导出为当前 `Assets/AIModels/ScaleFollowGroundOrigin_20261014.onnx`。模型和共享运行原点偏移零值成对启用；旧模型保留。采样分布、碰墙后路线修正及验收边界见[本轮验收](../../EvaluationReport/2026-10-14_rl_ai_acceptance/README.md)，不将这些样本解释为全域可达保证。
5. **后续优化**：针对真实失败模式逐项研究死区、终点制动等改进；不把训练环境跟踪成功率当成系统完成率。

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

## 连续路径候选训练

连续路径候选使用独立场景 `Assets/Scenes/Training/ExcavatorRLTrain_ContinuousPath.unity` 和 47 维观测，不加载现有 38 维 Follow 模型。需要同步当前训练设置时，在 Unity 菜单执行 `Tools/Excavator/Continuous Path/Update Training Scene`，再执行 `Build Linux Player`，然后运行：

```bash
conda activate ml_agents
cd /home/zhaoshuai/workspace_uinty/RL_prj/RLExcavator
python tools/mlagents_train_no_onnx.py config/continuous_path_ppo.yaml \
  --env=Builds/ExcavatorContinuousPathV3/ExcavatorContinuousPath.x86_64 \
  --run-id=<new_continuous_path_run> --num-envs=2 --no-graphics --time-scale=20
```

短训检查改用 `config/continuous_path_smoke.yaml`。当前 V3 从零训练 200 万步，修正姿态奖励并固定两种任务并行覆盖；仍是短程路径候选，不自动替换主线。设置、验证和启动记录见 [V3 实验记录](../../EvaluationReport/2026-10-19_continuous_path_rl_v3/README.md)。

扩大范围的 V4 单独使用 `ExcavatorRLTrain_ContinuousPathExpanded.unity`。首次通过 Unity 菜单 `Tools/Excavator/Continuous Path/Create Expanded Training Scene` 创建；后续更新用 `Update Expanded Training Scene`，再用 `Build Expanded Linux Player` 构建。它保留工作空间弧形路线，端点采样距离 0.35–1.0 m；真实路径长度和回转范围以采样/运行日志为准。

```bash
python tools/mlagents_train_no_onnx.py config/continuous_path_expanded_ppo.yaml \
  --env=Builds/ExcavatorContinuousPathV4/ExcavatorContinuousPath.x86_64 \
  --run-id=<new_expanded_path_run> --initialize-from=continuous_path_v3_20261019 \
  --num-envs=2 --no-graphics --time-scale=20
```

这是从 V3 权重热启动的新训练，默认预算 300 万新步，学习率 1e-4；不是恢复 V3 原运行，也不会替换 38D 主线模型。[V4 采样、验证与训练记录](../../EvaluationReport/2026-10-19_continuous_path_rl_v4/README.md)。

## ONNX 导出

ONNX 用于主项目的策略推理，也可用于 Unity `Inference Only`。当前新原点 Follow 模型的身份与导出证据见[本轮验收](../../EvaluationReport/2026-10-14_rl_ai_acceptance/README.md)；此前模型的冻结记录见 [RL 基线 v1](rl_baseline_v1.md)。

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
