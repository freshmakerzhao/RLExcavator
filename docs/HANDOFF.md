# RLExcavator 交接文档（Handoff）

> 目的：让接手者（人或 Codex 等 AI agent）无需追溯历史对话即可继续工作。
> 项目：用 Unity + AGX Dynamics + ML-Agents 复现挖掘机低层轨迹跟踪控制，并逐步扩展到自主挖装任务。
> 最后更新：2026-06-16。

---

## 0. 2026-06-15 快速接手快照

当前主线已经从早期 `ExcavatorDigAgent` 挖土 demo 转向 `ExcavatorTrajectoryAgent`
低层轨迹跟踪控制，目标是复现 Spinelli 等 2026 工作中的 waypoint-following controller，
后续把它作为论文/系统 baseline。

当前最重要的状态：

- 训练环境不要直接运行 `mlagents-learn` 长训。当前 PyTorch ONNX 导出默认会走
  dynamo 路径并依赖 `onnxscript`，与训练环境固定的 `onnx==1.15.0` /
  `protobuf==3.20.3` 冲突。使用项目内 wrapper：
  `python tools/mlagents_train_no_onnx.py config/trajectory_ppo.yaml --run-id=<id> ...`。
  该脚本只跳过训练过程中的 ONNX 导出，仍保存 `.pt` checkpoint，不修改第三方包或
  `site-packages`。详细说明见 `docs/mlagents_training_environment.md`。
  如果需要 ONNX，使用 `tools/mlagents_export_legacy_onnx.py`，它只在当前进程中
  强制 `torch.onnx.export(..., dynamo=False)`，并连接 Unity 后立即导出退出，
  同样不修改第三方包。

- `traj_4env_reward_movement_0614_0930` 是当前效果较好的 trajectory 模型。
  - Unity 中观察效果较好。
  - TensorBoard 中 `SuccessRate=1`、`WaypointReachRate=1`、`PathProgress=1`。
  - 不加 `--deterministic` 推理时 4 个环境都能动；加 `--deterministic` 会出现部分环境卡住，说明当前策略分布学到了可用动作，但 mean action 还不够稳定。
- 2026-06-14 起开始加入 bucket attitude conditioning：
  - `MoveToDig`：移动到挖掘点，要求铲斗接近入铲/垂直姿态。
  - `CarryMaterial`：带土运输，要求铲斗接近水平或略后仰姿态。
  - 两种模式本质上都是轨迹跟踪，差异主要是目标铲斗 pitch 不同。
- 当前姿态条件版本观测维度为 37，不能 resume 旧的 31 维 checkpoint。
- 姿态 smoke `traj_attitude_smoke_0614_1147` 已确认链路跑通、指标写入、无 NaN，但效果不适合长训：
  - `SuccessRate=0`
  - `WaypointReachRate=0`
  - `WaypointTimeoutRate=1`
  - `BucketPitchErrorDeg` 很大，说明 `bucketPitchAxisLocal` / 目标 pitch 还未完成标定。
- 真机部署观测需求已单独整理到 `docs/real_machine_observation_requirements.md`。

当前最该做的事情：

1. 在 Unity `Heuristic Only` 模式下手动操控铲斗，标定 bucket pitch。
2. 确认 `bucketAttitudeReference` 显式绑定到真正随铲斗旋转的对象，例如 `Traj_Bucket`。
3. 确认 `bucketPitchFrame` 显式绑定到随 swing 一起旋转的上车/驾驶室/回转平台节点，避免 swing 污染 bucket pitch。
4. 确认只按 `F/H` 控制 swing 时，UI 中 `Bucket pitch` 基本不变；只按 `J/L` 控制铲斗液压缸时，`Bucket pitch` 明显变化。
5. 如果上一步不成立，先不要训练，继续修 `bucketPitchFrame` / `bucketAttitudeReference` / `bucketPitchAxisLocal`。
6. 分别记录合理的 `MoveToDig` 和 `CarryMaterial` 目标 pitch，写入：
   - `moveToDigTargetBucketPitchDeg`
   - `carryMaterialTargetBucketPitchDeg`
7. 先固定单一模式跑 smoke，再混合两种模式训练。

建议 skills：

- `diagnose`：Unity 行为异常、训练卡死、reward 不符合预期时使用。
- `handoff`：继续阶段性压缩当前上下文，避免训练/实验细节遗忘。
- `academic-research-suite`：需要把当前路线整理成论文实验设计或对齐 ETH/Spinelli baseline 时使用。
- `grill-me`：在确定论文贡献、baseline 或实验设计前做审稿人式压力测试。

---

## 1. 一句话现状

低层轨迹跟踪主链路已跑通：Unity + AGX 挖掘机通过 `ExcavatorController`
接收 4 维归一化速度动作，`ExcavatorTrajectoryAgent` 根据 future waypoints、
执行器状态、铲斗尖状态和任务模式输出 boom / stick / bucket / swing。
下一步不是盲目长训，而是先把 bucket pitch 的参考轴和两种目标角度校准好。

---

## 2. 项目背景（必读）

- 路径下有两个 Unity 工程：
  - `Turtle Agent/`：参考用的 ML-Agents 入门 demo（离散动作，已跑通），可借鉴 Agent 写法。
  - `RLExcavator/`：本项目，挖掘机 RL。已装 ML-Agents 4.0.3 + AGXUnity。
  - `../GraphPerceptionPrj/`：另一个工程，含一套复杂的挖掘机控制系统（多层抽象 + TCP 模型推理桥）。
    我们刻意不复制它，改为在 RLExcavator 里写最小、可理解的实现。它仅作“参考/灵感来源”。
- 目标范围（第一版 demo）：固定底盘、回转锁定，只训练 大臂 boom + 小臂 stick + 铲斗 bucket 三关节，连续控制。
- 完整规划见 ../RL_DIG_DEMO_PLAN.md。

---

## 3. 已完成 / 文件清单

| 文件 | 作用 | 状态 |
|---|---|---|
| Assets/Scripts/Control/ExcavatorController.cs | 关节控制器：归一化动作→AGX 目标速度；读关节位置；home 复位；demo 命令输入闸门 | 写完，API 核对过 |
| Assets/Scripts/Agents/ExcavatorDigAgent.cs | ML-Agents Agent：观测/连续动作/奖励/episode/Heuristic | 写完，未在编辑器验证 |
| Assets/Scripts/Testing/ExcavatorKeyboardTester.cs | 键盘手动驱动，接 RL 前验证连线 | 写完，按键已按当前场景调过 |
| config/excavator_ppo.yaml | PPO 训练超参（behavior 名 ExcavatorDig） | 写完 |
| RL_DIG_DEMO_PLAN.md | 总体规划与里程碑 | 写完 |

所有自写代码命名空间统一为 RLExcavator。

### Unity 接线现状（2026-06-03）

场景：`Assets/Scenes/AGXUnity_Excavator_measurements.unity`。

已确认 `Excavator CAT 365 Tracked` 上官方 `Excavator` 组件的 `Controlled Constraints`
对应关系：

| RL 字段 | Hierarchy 中拖入的 Constraint GameObject | 官方语义 |
|---|---|---|
| boom[0] | BoomPrismatic1 | Left/Right Elevate Prismatic 之一（大臂一侧抬升液压缸） |
| boom[1] | BoomPrismatic2 | Left/Right Elevate Prismatic 之一（大臂另一侧抬升液压缸） |
| stick | ArmPrismatic | Stick Prismatic（小臂液压缸） |
| bucket | BucketPrismatic | Bucket Prismatic（铲斗液压缸） |
| swing | CabinHinge（可选） | Swing Hinge（上车回转） |

注意：Left/Right Elevate Prismatic 指的是大臂左右两侧液压缸，不是左右两根大臂。
第一版 RL 可固定回转，`swing` 可不接。

已验证 `ArmPrismatic` 与官方 `Stick Prismatic` 对应：二者的 Constraint 端点均为
`CylContainerMid` / `CylinderMid`，Type 为 `Prismatic`。

使用自写键盘测试时，需要禁用官方 `Excavator Input Controller`，避免官方输入和
`ExcavatorKeyboardTester` 同时写同一批 Constraint。

使用 `ExcavatorDigAgent` 的 Heuristic 验证时，建议临时禁用 `ExcavatorKeyboardTester`，
避免键盘测试脚本和 Agent 同时写 boom/stick/bucket 速度。

当前 `ExcavatorKeyboardTester` 按键对：

| 关节 | 按键对 |
|---|---|
| boom | I / K（代码中 K 为正向，I 为反向） |
| stick | T / G（代码中 G 为正向，T 为反向） |
| bucket | J / L（代码中 J 为正向，L 为反向） |
| swing | F / H（代码中 H 为正向，F 为反向） |

当前手感调校：

- `T/G`（stick）和 `F/H`（swing）保持直接速度控制，手感已确认可用。
- `I/K`（boom）和 `J/L`（bucket）通过 `ExcavatorController` 中的
  `boomAcceleration` / `bucketAcceleration` 做缓启动/缓停止，避免速度突变把土带飞。

### ML-Agents 接线现状（2026-06-05）

已在 `Excavator CAT 365 Tracked` 上接入：

- `ExcavatorDigAgent`
  - `controller` -> 同物体上的 `ExcavatorController`
  - `terrain` -> `ExcavationTerrain`
  - `shovel` -> `Bucket (DeformableTerrainShovel)`
  - `bucketTip` -> `BucketTip`
  - `MaxStep` = 5000
  - `targetMassKg` = 1500（满斗约 2000 kg）
  - `massNormalizer` = 2000
  - `liftHeight` = 2（相对本局开始时 bucketTip 的抬升高度，不是世界 Y 绝对值）
- `Behavior Parameters`
  - Behavior Name = `ExcavatorDig`
  - Vector Observation Size = 8
  - Continuous Actions = 3
  - Behavior Type 当前为 Heuristic Only
- `Decision Requester`
  - Decision Period = 5
  - Take Actions Between Decisions = true
- 成功提示
  - 当前验证阶段暂不 EndEpisode、不重置地形、不重置姿态。
  - 成功条件达成后，`successIndicatorRenderer` 指向的地板/平台会短暂变绿。
  - 若未手动指定 Renderer，Agent 会尝试自动查找名为 `Frame` 的对象并取其子 Renderer。

`ExcavatorDigAgent.Heuristic` 已同步新版 Input System 和当前键位：

| 动作 | 按键对 |
|---|---|
| boom | I / K（代码中 K 为正向，I 为反向） |
| stick | T / G（代码中 G 为正向，T 为反向） |
| bucket | J / L（代码中 J 为正向，L 为反向） |

注意：Agent 第一版动作空间是 3 维，只控制 boom/stick/bucket，不控制 swing。

### TrajectoryAgent 接线与训练指标（2026-06-11）

`RLExcavatorTraj` 已与 dig 环境隔离。当前路线开始向 Spinelli 等 2026 的
waypoint-following controller 靠拢：策略不再只跟踪一个当前点，而是根据未来
3 个 waypoint、管道约束和关节/动作历史跟踪一条路径。
2026-06-14 起增加 task mode + bucket attitude conditioning，用同一个低层策略
同时支持“移动到挖掘点”和“带土运输”两类轨迹跟踪姿态需求。

- `ExcavatorTrajectoryAgent`
  - `controller` -> Traj 挖掘机上的 `ExcavatorController`
  - `bucketTip` -> `Traj_BucketTip`
  - `bucketAttitudeReference` -> `Traj_Bucket` 或等价铲斗姿态参考 Transform；为空时自动查找
  - `bucketPitchFrame` -> 随 swing 一起旋转的上车/驾驶室/回转平台 Transform；pitch 会相对该坐标系计算
  - `trajectory` -> `TrajectoryProvider`
  - `environmentRoot` -> 当前 `TrajectoryEnvironment` 或该环境 prefab 的根节点
  - 动作空间：4 维连续动作，boom / stick / bucket / swing
  - 观测空间：37 维
  - 观测内容：bucketTip 局部位置/速度、执行器位置/速度、上一动作、未来 3 个 waypoint 误差、路径进度、是否最后 waypoint、tube signed distance、episode 进度、task mode one-hot、当前/目标/误差 bucket pitch、bucket pitch 速度
  - `taskMode` 支持 `MoveToDig` 和 `CarryMaterial`
  - `randomizeTaskModeOnEpisodeBegin = true` 时，每个 episode 随机采样任务模式
  - `moveToDigTargetBucketPitchDeg` / `carryMaterialTargetBucketPitchDeg` 是姿态目标，需要按当前铲斗坐标系标定
  - `bucketPitchAxisLocal` 定义用于计算 pitch 的铲斗局部参考轴；`bucketPitchFrame` 定义 pitch 参考坐标系，真机 FK 必须使用同一语义
  - `waypointLookahead` 默认保持 3；如果修改，必须同步 Behavior Parameters 的 Vector Observation Space Size
  - `tubeRadius` 只用于 tube 奖励/惩罚；waypoint 到达使用独立的 `targetThreshold`
- `TrajectoryProvider`
  - 支持单点目标和多点路径
  - `randomizePathOnReset = true` 时，每个 episode 自动生成随机 waypoint 路径
  - `randomWaypointCount = 5` 对齐论文 waypoint-following 训练中的 5 点短路径
  - `TrajectoryGroup` 下手动摆放的 waypoint 是基准轨迹；建议先摆成“左 -> 中 -> 右”的目标轨迹
  - `randomPathMode = Trend` 会围绕基准轨迹的每个 waypoint 加随机扰动，适合常规训练
  - `randomPathMode = Independent` 会生成更分散的随机点，适合压力测试
  - `randomPathMode = CalibratedFan` 会在 15 个手动标定的扇形可达空间点内连续采样 waypoint，适合覆盖 180 度工作范围和多方向轨迹续训
  - `randomLocalMin/randomLocalMax` 仅限制 `Independent` 模式的随机路径生成范围
  - `randomMinSegmentLength/randomMaxSegmentLength` 仅用于 `Independent` 模式的相邻 waypoint 距离限制
  - `trendLateralJitter/trendVerticalJitter` 控制基准轨迹周围的水平扰动和高度扰动
  - `calibratedFanFrame` 是 CalibratedFan 标定点所属参考系；为空时默认使用 `TrajectoryGroup` 的父物体，通常就是当前 `TrajectoryEnvironment`
  - `calibratedFanMinDirectionSpan` 控制 CalibratedFan 路径起终点在左右方向上的最小跨度；调大可迫使训练见到更多横向/斜向轨迹
  - `calibratedFanDirectionJitter` / `calibratedFanRangeJitter` / `calibratedFanVerticalJitter` 控制 CalibratedFan 内部 waypoint 的随机扰动
  - `calibratedFanUseCurvedPaths` 和 `calibratedFanCurveStrength` 用于让路径不是简单直线插值，增加方向变化
  - 注意：15 个标定点不是 `TrajectoryGroup.localPosition`，而是环境局部坐标；生成时必须先从 `calibratedFanFrame` 转世界，再转回 `TrajectoryGroup` 局部坐标
  - `SetPlannedPath(Vector3[] worldPoints)` 可在推理/demo 中立即注入 planner 输出的世界坐标 waypoint，内部会转换到 `TrajectoryProvider` 局部系并刷新可视化
  - `QueuePlannedPath(Vector3[] worldPoints)` 可暂存下一段路径，在下一次 `ResetPath()` 生效，用于状态机切段
  - `SetPathVisualsVisible(bool visible)` 可隐藏/显示当前 waypoint 可视化；demo 启动时用于隐藏训练残留路径
  - `TrajectoryGroup` 仍是模块三的 waypoint 缓冲区：训练时承载基准/随机路径，demo 推理时接收 planner 输出
  - Play 时可自动给 waypoint 添加小球可视化
  - 当前目标、待到达目标、已完成目标使用不同颜色
  - 会在随机路径 reset 时自动修复空 waypoint、重复 waypoint、引用到其它环境的 waypoint
- `SimpleTrajectoryPlanner`
  - 交互 demo planner，位于 `Assets/Scripts/Planning/SimpleTrajectoryPlanner.cs`
  - 输入 `startPoint/endPoint`，生成一条起点到终点的 waypoint 路径；demo 展示中两者都应绑定为可见场景物体
  - `plannerAlgorithm` 支持 `ArcInterpolation` 和 `RrtStar`：前者是稳定 fallback，后者在可达扇区内采样、扩展、重连、平滑后输出 waypoint
  - 默认 `waypointCount = 5`，对齐当前策略训练尺度；RRT* 输出也会重采样到该尺度
  - 不再支持 `bucketTip` 作为规划起点；StartPoint / EndPoint 是唯一规划控制点
  - UI 可调用 `SelectStartPoint()` / `SelectEndPoint()` 切换当前编辑控制点
  - UI 可调用 `MoveSelectedPointForward/Backward/Left/Right/Up/Down()` 按 `endpointMoveStep` 移动当前控制点
  - 新增 `SetSelectedPointWorldPosition(...)` / `TryGetSelectedPointWorldPosition(...)`，供 Game 视图点选脚本直接设置当前起点或终点
  - Play 后总会给 StartPoint / EndPoint 添加可见标记，不再提供显示开关
  - `endpointMoveFrame` 控制控制点前后左右移动参考系；为空时使用 planner 物体坐标系
  - 绑定 `reachableWorkspace` 后，控制点移动和规划前可自动夹到最近可达点，避免生成不可达目标
  - RRT* 当前在可达空间二维参数域 `(direction01, range01)` 内规划，而不是在自由 XYZ 空间里规划后吸附到扇形面；可达空间外部作为状态空间边界处理
  - `clampGeneratedWaypointsToWorkspace=true` 只作为非参数域轨迹的保险，例如 `ArcInterpolation` 输出写入 `TrajectoryProvider` 前可投影回可达空间表面
  - `PlanConfiguredPath()` 可从 Inspector Context Menu 触发；`Plan(startWorld, goalWorld)` 供后续 UI / TaskManager 调用
  - RRT* 第一版仅做 Cartesian waypoint 规划；配置 `rrtObstacleMask` 后可用简化球体检测接入障碍物层
- `DemoPointPicker`
  - Game 视图点选脚本，位于 `Assets/Scripts/Planning/DemoPointPicker.cs`
  - 用于把鼠标左键点击/拖拽转换为当前起点或终点的世界坐标更新；滚轮可调整当前控制点高度
  - 优先通过 `Physics.Raycast` 命中规划平台或地面；未命中时回退到当前编辑点高度处的水平平面
  - 正式 demo 建议给可点选地面单独设置 Layer，并把 `pickableSurfaceMask` 只配置到该 Layer，减少误点
  - `autoPlanAfterPointChanged` 可开启点选后自动规划；调试 RRT* 参数时建议关闭
- `ReachableWorkspaceVisualizer`
  - 可达空间可视化/采样组件，位于 `Assets/Scripts/Planning/ReachableWorkspaceVisualizer.cs`
  - 当前默认内置 15 个 `env01` 坐标系下的铲斗尖标定点：5 个方向（左、左前、正前、右前、右）x 3 个距离层（近、中、远）
  - 提供 `SampleWorld` / `ClosestWorldPoint` / `ClosestWorldParameters` 等接口，供 RRT*、控制点夹取和后续规划模块复用
  - 右键菜单 `Create Editable Anchor Objects` 可生成 15 个可拖拽锚点；开启 `syncFromEditableAnchors` 或执行 `Bake Anchors From Editable Objects` 后可回写标定点
  - 推荐挂在 `TrajectoryEnvironment_01` 或与 `env01` 完全重合的空物体上；若挂在其它对象上，必须把 `Workspace Frame` 绑定到 `env01`
  - Scene 中会显示半透明网格、边界线、内部网格和锚点球；如果区域明显偏移，优先检查挂载对象/`Workspace Frame` 是否使用了正确参考系
- `ExcavationDemoTaskManager`
  - 第一版交互 demo 操作台，位于 `Assets/Scripts/Planning/ExcavationDemoTaskManager.cs`
  - Play 后可自动关闭 `ExcavatorController.commandInputEnabled`，让挖掘机静止等待按钮操作
  - UI Button 可绑定 `SetModeMoveToDig`、`SetModeCarryMaterial`、`SelectPlanStartPoint`、`SelectPlanEndPoint`、`MoveSelectedPlanPoint*`、`PlanPath`、`StartTracking`、`StopTracking`、`PlanAndStartTracking`
  - `PlanMoveToDigAndStart` / `PlanCarryMaterialAndStart` 可作为一键测试按钮
  - `bucketTipLocalFrame` 用于状态栏显示 `铲斗尖局部`；建议绑定 `TrajectoryEnvironment` 或 `RLExcavatorTraj`，用于手动标定可达空间范围
  - 可绑定 `pointPicker`；缺失且 `createPointPickerIfMissing=true` 时会自动在 DemoSystem 上创建一个，并连接到 planner
  - 组件只做流程编排，不直接写关节；训练/推理仍由 `ExcavatorTrajectoryAgent -> ExcavatorController` 完成
  - demo 启停只通过 `ExcavatorController.SetCommandInputEnabled(...)` 控制，不再向 `ExcavatorTrajectoryAgent` 增加 demo 专用开关
  - 默认 `requireInferenceOnlyBehavior=true`；训练场景 `Behavior Type=Default` 时不关闭 controller、不改随机 task mode，避免误影响训练
  - 默认 `hideTrajectoryUntilPlannedOnStart=true`，demo Play 后隐藏旧 waypoint 可视化；点击生成轨迹后重新显示
  - 默认 `requirePathBeforeTracking=true`，没有规划路径时不会启动追踪，避免策略追踪 prefab 残留路径
  - 默认 `disableDecisionRequesterWhenStopped=true`，demo 空闲、停止或路径完成后禁用 `DecisionRequester`；开始追踪时再启用
- `ExcavationDemoRuntimeUI`
  - 展示用 runtime UI，位于 `Assets/Scripts/UI/ExcavationDemoRuntimeUI.cs`
  - Play 时自动生成完整 HUD：顶部信息栏、中间三段功能导航、左侧随 tab 切换的控制菜单、右上角态势监控窗口
  - `感知建图` 当前是占位按钮；`生成轨迹` 可通过 UI 选择编辑起点/终点、移动当前控制点、生成轨迹、生成并追踪
  - `轨迹追踪` 绑定 `赴挖掘点模式`、`运土保持模式`、`生成轨迹`、`开始追踪`、`停止追踪`、`生成并追踪`、`重置演示`
  - `重置演示` 调用 `ExcavationDemoTaskManager.ResetDemo()`，会先停 controller，再重载当前 demo scene
  - `Minimap Camera` 可选绑定俯视相机；为空时显示占位态势示意图；已按显示窗口比例创建 RenderTexture 并保持画面比例，避免拉伸变形
  - 当前 HUD 是 runtime 代码生成，适合快速 demo；正式展示阶段建议迁移为 Canvas Prefab 或 UI Toolkit，代码只做事件绑定和数据刷新
  - 只调用 `ExcavationDemoTaskManager` 的 public 方法，不直接控制关节、不影响训练脚本
  - 不自动查找或禁用训练 UI；demo scene 中不需要的训练 Canvas 由场景配置手动删除或禁用
- 多环境并行训练
  - 优先在同一个 scene 中复制多个完整 `TrajectoryEnvironment`
  - 每个环境内的 `ExcavatorTrajectoryAgent.environmentRoot` 必须指向自己的环境根节点
  - 每个环境内的 `UITargetTracking.environmentRoot` 也应指向自己的环境根节点，避免 UI 显示其它 agent
  - `autoFindReferences` 只会在 `environmentRoot` 内查找；训练前仍建议显式绑定 `controller/bucketTip/trajectory/successIndicator`
  - `ExcavatorTrajectoryAgent` 不会自动禁用 `ExcavatorDigAgent`；不同任务环境的启停由 prefab/scene 配置负责
  - 后台训练通过 Unity Project Settings 中的 Run In Background 配置，不再使用额外脚本控制
  - 所有 trajectory agent 保持同一个 Behavior Name：`ExcavatorTrajectory`
- 训练配置
  - `config/trajectory_ppo.yaml`
  - Behavior Name = `ExcavatorTrajectory`
  - Vector Observation Space Size 必须设置为 37
  - Continuous Actions = 4
  - 37 维姿态条件版本不能 resume 旧 31 维 trajectory 模型，需要新 run 或从兼容结构重新初始化
  - 训练环境优先保持稳定；ONNX 导出暂时不作为短期任务
  - mixed task-mode 训练时不要只看总的 `Trajectory/SuccessRate` 和 `Trajectory/BucketPitchErrorDeg`
  - Agent 会额外记录分任务指标：
    - `Trajectory/MoveToDig/SuccessRate`
    - `Trajectory/MoveToDig/WaypointReachRate`
    - `Trajectory/MoveToDig/WaypointTimeoutRate`
    - `Trajectory/MoveToDig/PathProgress`
    - `Trajectory/MoveToDig/BucketPitchErrorDeg`
    - `Trajectory/MoveToDig/FinalDistance`
    - `Trajectory/CarryMaterial/SuccessRate`
    - `Trajectory/CarryMaterial/WaypointReachRate`
    - `Trajectory/CarryMaterial/WaypointTimeoutRate`
    - `Trajectory/CarryMaterial/PathProgress`
    - `Trajectory/CarryMaterial/BucketPitchErrorDeg`
    - `Trajectory/CarryMaterial/FinalDistance`
  - mixed policy 达标时，两组分任务指标都应接近各自 expert；如果总 reward 上升但某一组分任务指标不动，说明策略在偏向其中一个 mode
- 每次代码修改后的快速验证配置
  - `config/trajectory_smoke.yaml`
  - 网络和 buffer 较小，`summary_freq` 更高，用于检查是否能连接、跑 episode、写指标
  - 不用于判断最终策略性能

`ExcavatorTrajectoryAgent` 的 reward 当前由以下部分组成：

- 到当前 waypoint 的距离惩罚和距离减少量奖励
- 粗目标/细目标势能差奖励：只有距离当前 waypoint 变近时才给正奖励，静止靠近目标不会持续刷分
- tube 内推进奖励、tube 外惩罚：在 tube 内但不继续接近当前 waypoint 时不再给持续正奖励
- 动作平方惩罚和相邻动作变化惩罚；当前权重较小，避免策略把“完全不动”学成局部最优
- bucket pitch 姿态误差惩罚和姿态角速度惩罚；用于在 `MoveToDig` / `CarryMaterial` 中保持不同铲斗姿态
- bucket pitch 姿态进步奖励：当 pitch error 变小时给小额正反馈，帮助策略主动调整铲斗姿态
- waypoint/path bonus 姿态缩放：到达 waypoint 时如果 pitch error 仍然很大，只给一部分 waypoint/path 完成奖励，并给小额额外惩罚，避免策略只追点不管姿态
- waypoint 到达奖励和整条路径完成奖励；这是推进路径的主要奖励
- 单个 waypoint 超时惩罚；默认 1200 个决策步仍未到达当前 waypoint 就结束 episode
- 终点稳定惩罚，避免到达最后点后动作过大

`ExcavatorTrajectoryAgent` 会向 TensorBoard 写入以下评价指标：

| 指标 | 含义 |
|---|---|
| `Trajectory/SuccessRate` | episode 是否完成整条轨迹，完成为 1，否则为 0 |
| `Trajectory/WaypointsReached` | episode 内到达的路径点数量 |
| `Trajectory/WaypointReachRate` | 到达路径点数量 / 总路径点数量 |
| `Trajectory/WaypointTimeoutRate` | episode 是否因单个 waypoint 长时间未到达而超时结束 |
| `Trajectory/PathProgress` | episode 结束时路径进度，范围 0 到 1 |
| `Trajectory/FinalDistance` | episode 结束时 bucketTip 到当前目标的距离 |
| `Trajectory/MinDistance` | episode 内 bucketTip 到目标的最近距离 |
| `Trajectory/MeanActionMagnitude` | episode 内平均动作幅值，用于判断策略是否学成完全不动 |
| `Trajectory/MeanPositiveProgress` | episode 内平均正向接近距离，用于判断是否真的向 waypoint 推进 |
| `Trajectory/SignedProgress` | episode 内累计有符号进度，负值说明整体远离目标 |
| `Trajectory/TaskMode` | episode 任务模式，0 为 MoveToDig，1 为 CarryMaterial |
| `Trajectory/BucketPitchErrorDeg` | episode 内平均 bucket pitch 误差，单位为度 |
| `Trajectory/BucketPitchProgress` | episode 内累计 pitch error 改善量，越大说明姿态在向目标靠近 |
| `Trajectory/WaypointBonusScale` | waypoint/path bonus 因姿态误差保留的平均比例，接近 1 表示到点时姿态也接近目标 |

后续判断 trajectory 策略是否真的学会，不应只看 `Environment/Cumulative Reward`，
还要同时看 `Trajectory/SuccessRate` 和 `Trajectory/WaypointReachRate`。

快速 smoke test 命令：

```bash
cd /home/zhaoshuai/workspace_uinty/RL_prj/RLExcavator
mlagents-learn config/trajectory_smoke.yaml --run-id=traj_smoke_$(date +%m%d_%H%M)
```

通过标准：Unity Console 无红错；终端能稳定输出 summary；TensorBoard 里出现
`Trajectory/SuccessRate`、`Trajectory/WaypointReachRate`、`Trajectory/FinalDistance` 等指标。

---

## 4. 核心数据流（我们的最小实现）

```
键盘/RL网络 -> 3个动作值[-1,1]
  -> ExcavatorController.SetNormalizedSpeeds(boom,stick,bucket)
  -> 每个关节 Constraint.GetController<TargetSpeedController>().Speed = 动作 * maxSpeed
  -> AGX 液压缸/铰链运动 -> 铲斗插土
  -> terrain.Native.getDynamicMass(shovel.Native)   // 铲斗内土质量(kg)，奖励主信号
  -> AddReward(Δ质量) -> PPO 更新
```

---

## 5. 已验证的关键 AGX / ML-Agents API（可放心使用）

- DeformableTerrain.Native -> agxTerrain.Terrain；Native.getDynamicMass(shovel.Native) 返回铲斗内土质量(kg)。
- DeformableTerrain.ResetHeights() 复原地形。
- Constraint.GetController<TargetSpeedController>()：.Speed / .Enable / .LockAtZeroSpeed。
- Constraint.GetController<LockController>()：.Position / .Enable（用于 home 锁定）。
- Constraint.GetController<RangeController>().Range -> RangeReal{ float Min; float Max; }（行程，用于位置归一化）。
- Constraint.GetCurrentAngle()：prismatic 返回位移(m)，hinge 返回角度(rad)。
- 已知坑：RangeReal 默认是 ±Infinity，归一化前必须查 float.IsFinite，否则产生 NaN 毁训练
  （ExcavatorController.GetNormalizedPosition 已处理）。

参考：同学工程里 GraphPerceptionPrj/.../Scripts/Control/Execution/ExcavatorMachineController.cs
和 MassVolumeCounter.cs 是上述 API 的可编译实例，可作权威用法参考。

---

## 6. 下一步（按顺序，带通过标准）

### Step A — 编译
切回 Unity 等编译。Console 报红 -> 把完整报错贴出来再改。

### Step B — 找关节并接线（已完成）
1. 官方场景可以用自带 `Excavator Input Controller` 挖土，左上角 `Mass in bucket` 可读。
2. 已从官方 `Excavator` 组件的 `Controlled Constraints` 反推 RL 所需约束，并用
   Hierarchy 中的 `BoomPrismatic1` / `BoomPrismatic2` / `ArmPrismatic` / `BucketPrismatic`
   接入自写 `ExcavatorController`。
3. 已添加 `ExcavatorKeyboardTester`，并将键位调整为 IK / TG / JL / FH。
4. 若后续出现不动/方向不对：
   - 不动 -> 检查是否禁用了官方 `Excavator Input Controller`，以及对应约束是否有 TargetSpeedController。
   - 方向反 -> 优先交换 `ExcavatorKeyboardTester.Axis(...)` 两个按键，或把对应 *MaxSpeed 改负值。

### Step C — 接 RL（组件已接，待干净验证）
1. 组件接线已完成：Agent / Behavior Parameters / Decision Requester / BucketTip 均已在场景中。
2. 下一步需要做一次干净 Heuristic 验证：
   - 禁用官方 `Excavator Input Controller`。
   - 临时禁用 `ExcavatorKeyboardTester`。
   - 保持 `Behavior Parameters` 为 Heuristic Only。
   - Play 后用 I/K、T/G、J/L 控制，确认 Console 无红错、Mass in bucket 上升、成功后地板/平台短暂变绿。

### Step D — 训练
```
cd RLExcavator
mlagents-learn config/excavator_ppo.yaml --run-id=dig01
# 然后回 Unity 点 Play。中断续训加 --resume。
# TensorBoard: tensorboard --logdir results
```
看 Environment/Cumulative Reward 是否上升。学不动先查奖励塑形（见规划文档第 5 节“坑”）。

---

## 7. 约定 & 注意事项（给 Codex）

- 代码风格：参考现有脚本——中文注释、RLExcavator 命名空间、AGXUnity 2 空格缩进风格、
  字段 m_ 前缀私有、public 字段配 [Tooltip]。
- 不要复制 GraphPerceptionPrj 的 Control 系统：保持本项目最小可理解。需要观测/奖励灵感时，可借鉴
  GraphPerceptionPrj/.../Sources/ActProtocol.cs 里的 ActTaskState
  （mass_in_bucket、bucket_depth、距目标距离等字段=现成观测/奖励清单）。
- C# 无法在本环境编译验证：以 Unity Console 为准；改完让用户在编辑器确认。
- .meta 文件：在 Unity 外移动/新建脚本后，回 Unity 让其生成 .meta；不要手写 GUID。
  移动已被 Unity 导入的脚本要连 .meta 一起移，避免丢引用。
- AGX 关节运行时实例化：编辑器停止态可能看不到/引用不到 Constraint；如遇引用丢失，改用代码运行时按名字查找。
- 新增观测维度：改 CollectObservations 后，必须同步更新 Behavior Parameters 的 Observation Space Size。
- 物理对齐：关节驱动与奖励统计放 OnActionReceived/FixedUpdate，勿用 Update 的 deltaTime 积分物理量。

---

## 8. 待办（TODO）

- [x] Step B：编辑器接线 + 键盘验证挖土（人工）
- [ ] Step C：接 ExcavatorDigAgent + Behavior Parameters，Heuristic 验证读数（组件已接，待干净验证）
- [ ] 成功提示验证：成功条件达成后，地板/平台短暂变绿；当前阶段不 reset 地形/姿态
- [ ] Step D：跑 PPO 训练，看奖励曲线
- [ ] 调参：targetMassKg/massNormalizer 用手动测得的真实质量校准；奖励权重调优
- [ ] 性能：单环境跑通后再考虑多环境并行（可变形地形吃 CPU）
- [ ] 可选：补充 bucketTip 相对“目标挖掘点”的观测，替换当前的绝对世界坐标
