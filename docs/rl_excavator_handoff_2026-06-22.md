# RLExcavator 当前会话 Handoff

生成时间：2026-06-22

项目路径：

- 主要工作目录：`/home/zhaoshuai/workspace_uinty/RL_prj/RLExcavator`
- 当前系统上下文的 cwd 可能显示为 `/home/zhaoshuai/workspace_excavator/excavator_testbed`，但本轮实际代码修改都在 `RLExcavator`。

已有项目内交接/设计文档：

- `docs/HANDOFF.md`
- `docs/task_demo_system_design.md`
- `docs/real_machine_observation_requirements.md`
- `docs/mlagents_training_environment.md`

本文件只总结当前长对话中最重要的新增状态，避免重复项目内已有文档。

---

## 1. 当前主线

当前研究/工程主线已经收敛到：

> Unity + AGX 挖掘机仿真中，复现并扩展低层 waypoint-following / trajectory tracking controller，并构建一个可交互算法验证演示场景。

目前重点不是继续挖掘意图学习，而是：

1. 低层 RL 轨迹跟踪策略已经可用。
2. 演示场景已经有 UI、起终点编辑、轨迹生成、轨迹追踪、俯视小地图。
3. 正在补齐模块二“轨迹规划”能力，用可达空间约束和 RRT* 生成轨迹。
4. 后续要把感知/建图模块的输出接入轨迹规划，再把规划轨迹交给 RL tracking policy。

---

## 2. 当前 demo 场景

演示场景已经更名为：

- `ExcavatorAlgorithmValidationScene`

目标：

- 用于算法验证和领导演示。
- 不用于训练。
- 训练 UI/键盘说明 UI 不应自动耦合进 demo。

当前 demo UI 结构：

- 顶部信息栏：标题“算法验证”、单位/平台文字。
- 中间 tab：`感知建图 / 生成轨迹 / 轨迹追踪`。
- 左侧菜单：随 tab 切换。
- 右上角：俯视小地图/态势监控窗口。
- 左侧状态栏：显示状态、模式、控制输入、路径进度、编辑点、起终点、规划摘要、点选摘要、铲斗尖局部坐标。

相关脚本：

- `Assets/Scripts/UI/ExcavationDemoRuntimeUI.cs`
- `Assets/Scripts/Planning/ExcavationDemoTaskManager.cs`
- `Assets/Scripts/Planning/SimpleTrajectoryPlanner.cs`
- `Assets/Scripts/Planning/DemoPointPicker.cs`
- `Assets/Scripts/Planning/ReachableWorkspaceVisualizer.cs`

---

## 3. 本轮新增：Game 视图点选起点/终点

新增文件：

- `Assets/Scripts/Planning/DemoPointPicker.cs`
- `Assets/Scripts/Planning/DemoPointPicker.cs.meta`

用途：

- 在 Game 视图中直接用鼠标设置当前编辑的起点或终点。
- 替代只靠 Inspector 或六方向按钮移动点的低效方式。

使用方式：

1. 进入 `生成轨迹` tab。
2. 点击 `选起点后点画面` 或 `选终点后点画面`。
3. 在 Game 视图中左键点击平台/规划平面设置点。
4. 按住左键拖拽可连续移动当前点。
5. 鼠标滚轮调整当前点高度。
6. 点击 `生成轨迹` 或 `生成并追踪`。

实现细节：

- 优先通过 `Physics.Raycast` 命中可点选地面/平台。
- 未命中 Collider 时，退回到当前编辑点高度处的水平平面。
- `ExcavationDemoTaskManager` 新增 `pointPicker` 引用。
- 若缺少 `DemoPointPicker` 且 `createPointPickerIfMissing=true`，会自动在 `DemoSystem` 上创建一个并连接到 planner。

建议后续：

- 给 demo 地面/平台单独设 Layer。
- 将 `DemoPointPicker.pickableSurfaceMask` 只设置到该 Layer，减少误点。

---

## 4. 本轮新增：RRT* 改为可达空间参数域规划

用户指出旧实现问题：

- 旧逻辑像是先生成直线/折线，再找可达区域最近点。
- 这样 RRT 没有真正意义。
- 更合理的是把不可达区域作为状态空间边界或约束，让 RRT 直接在可达区域内生成轨迹。

已经修改：

- `Assets/Scripts/Planning/SimpleTrajectoryPlanner.cs`
- `Assets/Scripts/Planning/ReachableWorkspaceVisualizer.cs`
- `docs/task_demo_system_design.md`
- `docs/HANDOFF.md`

当前 RRT* 语义：

- RRT 状态不再是自由 `XYZ`。
- RRT 状态是可达空间二维参数：
  - `direction01 ∈ [0,1]`
  - `range01 ∈ [0,1]`
- 每个参数点通过 `ReachableWorkspaceVisualizer.SampleWorld(...)` 映射成真实铲斗尖世界坐标。
- RRT 的采样、扩展、重连、平滑、重采样都在参数域内完成。
- 可达空间外部不作为普通 collider 障碍，而是状态空间边界，RRT 不会采样到那里。
- `rrtObstacleMask` 留给可达空间内部障碍，例如地面、料堆、设备、禁入区。

RRT 当前输入可以理解为：

| 输入 | 当前实现 |
|---|---|
| 起点 | UI 起点世界坐标，转成最近可达空间参数 |
| 终点 | UI 终点世界坐标，转成最近可达空间参数 |
| 状态空间 | `[0,1] x [0,1]` 参数域 |
| 采样器 | 参数域随机采样，按 `rrtGoalBias` 概率采样目标 |
| steer | 参数域扩展，用世界距离限制 `rrtStepSize` |
| 点有效性 | 参数在范围内，映射世界点不低于 `rrtMinWorldY`，不与 `rrtObstacleMask` 碰撞 |
| 边有效性 | 参数域线段离散采样 `rrtWorkspaceEdgeSamples` 次，每个采样点和相邻小段都检查 |
| 距离/代价 | 使用参数点映射后的世界距离 |
| 输出 | 参数路径映射为 world waypoint，写入 `TrajectoryProvider` |

重要字段：

- `plannerAlgorithm = RrtStar`
- `reachableWorkspace`
- `rrtMaxIterations`
- `rrtStepSize`
- `rrtGoalBias`
- `rrtGoalTolerance`
- `rrtRewireRadius`
- `rrtWorkspaceEdgeSamples`
- `rrtObstacleMask`
- `fallbackToArcOnRrtFailure`

注意：

- 字段名 `fallbackToArcOnRrtFailure` 保留是为了兼容 Unity 序列化。
- 当前 RRT 失败 fallback 实际是“可达空间参数域插值”，不是自由空间圆弧。
- `clampGeneratedWaypointsToWorkspace` 现在只作为非参数域轨迹保险，例如手动切回 `ArcInterpolation`。

---

## 5. 可达空间

相关脚本：

- `ReachableWorkspaceVisualizer`

当前默认可达空间：

- 15 个手动标定的铲斗尖点。
- 5 个方向：左、左前、正前、右前、右。
- 3 个距离层：近、中、远。
- 坐标参考系是 `env01` / `TrajectoryEnvironment` 局部坐标。

用户提供过的标定点已经写入默认数组，项目文档里也有说明。

重要接口：

- `SampleWorld(float direction01, float range01)`
- `SampleWorld(Vector2 parameters)`
- `ClosestWorldPoint(Vector3 worldPoint)`
- `ClosestWorldParameters(Vector3 worldPoint)`
- `WorldPointToLocal(Vector3 worldPoint)`
- `LocalPointToWorld(Vector3 localPoint)`

后续注意：

- 如果可达空间视觉偏移，优先检查 `workspaceFrame` 是否绑定正确。
- 如果需要手动修改可达空间，可用右键菜单：
  - `Create Editable Anchor Objects`
  - 拖动 15 个锚点
  - `Bake Anchors From Editable Objects`

---

## 6. 当前训练/推理/ONNX 状态

训练环境问题已解决：

- 不要直接长训 `mlagents-learn`，因为 PyTorch ONNX 导出会因 `onnxscript` / `onnx` / `protobuf` 冲突中断。
- 用 wrapper 跳过训练过程中的 ONNX 导出：

```bash
python tools/mlagents_train_no_onnx.py config/trajectory_ppo.yaml \
  --run-id=<run_id> \
  [--resume 或 --initialize-from=<old_run_id>]
```

导出 ONNX：

```bash
python tools/mlagents_export_legacy_onnx.py config/trajectory_ppo.yaml \
  --run-id=<run_id> \
  --resume
```

注意：

- 导出脚本需要连接 Unity，因为它复用了 ML-Agents trainer 启动流程注册 brain。
- `--inference` 是让 Python trainer 端用已有 checkpoint 做推理，不等于 Unity 直接加载 ONNX。
- Unity 中使用 ONNX：把 `.onnx` 拖到 `Behavior Parameters -> Model`，Behavior Type 设置为 `Inference Only`。
- `Deterministic Inference` 勾选后使用均值动作，抖动更小，但如果策略均值动作学得不好可能卡住。

当前比较重要的模型：

- `traj_fan_reward_0618_01`：用户手动测试 ONNX 感觉效果短期够用。
- `traj_attitude_4env_0615_1356`：之前训练过的 mixed / attitude 相关 run。
- `traj_carry_from_scratch_4env_0616_01`：Carry 单模式从头训练。

---

## 7. 当前工作区状态

截至本 handoff 生成时，`git status --short` 显示：

```text
 M Assets/Scenes/ExcavatorAlgorithmValidationScene.unity
 M Assets/Scripts/Planning/ExcavationDemoTaskManager.cs
 M Assets/Scripts/Planning/ReachableWorkspaceVisualizer.cs
 M Assets/Scripts/Planning/SimpleTrajectoryPlanner.cs
 M Assets/Scripts/UI/ExcavationDemoRuntimeUI.cs
 M ProjectSettings/ProjectSettings.asset
 M docs/HANDOFF.md
 M docs/task_demo_system_design.md
?? Assets/Scripts/Planning/DemoPointPicker.cs
?? Assets/Scripts/Planning/DemoPointPicker.cs.meta
```

其中有些 scene / ProjectSettings / 可达空间相关改动是用户和此前步骤积累的，不要贸然 revert。

已执行检查：

- `git diff --check` 通过。
- Unity batchmode 编译无法执行，因为同一项目已经在 Unity Editor 中打开：

```text
Multiple Unity instances cannot open the same project.
```

本机没有可用的 `dotnet/csc/mcs` 命令，因此不能在 Unity 外做 C# 编译。

下一位接手者应在 Unity Editor Console 里确认是否有红色 C# 编译错误。

---

## 8. 下一步建议

优先级从高到低：

1. 在 Unity 中确认新 RRT 参数域实现能编译。
2. 在 `ExcavatorAlgorithmValidationScene` 中测试：
   - 点选起点/终点是否正常。
   - `plannerAlgorithm=RrtStar`。
   - `reachableWorkspace` 绑定正确。
   - 点击 `生成轨迹` 后 waypoint 是否自然分布在蓝色可达空间内，而不是直线后吸附。
3. 如果轨迹仍奇怪，检查：
   - `ReachableWorkspaceVisualizer.workspaceFrame`
   - `SimpleTrajectoryPlanner.reachableWorkspace`
   - `rrtWorkspaceEdgeSamples`
   - `rrtStepSize`
   - `rrtGoalBias`
   - `rrtSmoothingPasses`
4. 给 demo 地面设置 pickable Layer，并配置 `DemoPointPicker.pickableSurfaceMask`。
5. 如果要引入障碍，先用 Unity Collider + Layer 模拟，再把该 Layer 设置给 `rrtObstacleMask`。
6. 等 RRT 和 demo 稳定后再考虑提交 git。
7. 下一阶段可做：
   - RRT 障碍演示。
   - 感知建图模块 placeholder 输出占据/障碍。
   - UI 上提供“添加障碍 / 清除障碍 / 重新规划”。
   - 将 demo UI 从 runtime 代码生成逐步迁移到 Canvas Prefab 或 UI Toolkit。

---

## 9. 建议使用的 skills

- `diagnose`
  - Unity 编译错误、RRT 轨迹异常、UI 点击异常、ONNX/ML-Agents 训练中断时使用。
- `handoff`
  - 每次完成一个较大阶段后继续压缩上下文。
- `zoom-out`
  - 当需要重新评估“模块一感知、模块二规划、模块三追踪、模块四任务状态机”整体边界时使用。
- `academic-research-suite`
  - 当需要把当前系统整理成论文路线、baseline 对比、实验矩阵时使用。
- `grill-me`
  - 当需要审稿人/导师视角压力测试论文贡献或 demo 系统价值时使用。

---

## 10. 对下一位 agent 的提醒

- 默认用中文回复用户。
- 代码修改必须同步更新项目内相应文档，或在最终回复说明为什么无需更新。
- 不要修改第三方包或 `site-packages`。
- 不要随意 revert 用户在 Unity Scene / ProjectSettings 中的改动。
- Unity scene/prefab 可能很容易产生序列化 diff，改前先确认必要性。
- 当前 demo 是工程展示/算法验证，不要把训练逻辑和 demo 控制逻辑重新耦合。
- RRT 的“可达空间外部”应是状态空间边界，不应再退回“最后投影吸附”作为主要规划方式。
