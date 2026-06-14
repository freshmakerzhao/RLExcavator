# RLExcavator 交接文档（Handoff）

> 目的：让接手者（人或 Codex 等 AI agent）无需追溯历史对话即可继续工作。
> 项目：用 Unity + AGX Dynamics + ML-Agents 训练挖掘机学会“挖一斗土并抬起”。
> 最后更新：2026-06-12。

---

## 1. 一句话现状

代码骨架（控制器 + RL Agent + 键盘测试 + PPO 配置）已全部写好并通过 AGX API 核对；
Unity 里已确认官方 Excavator 组件的受控约束映射，已接入 ExcavatorController、
ExcavatorKeyboardTester、ExcavatorDigAgent、Behavior Parameters、Decision Requester 和 BucketTip。
下一步是做一次干净的 Heuristic 验证，然后启动 PPO 训练。

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
| Assets/Scripts/Control/ExcavatorController.cs | 关节控制器：归一化动作→AGX 目标速度；读关节位置；home 复位 | 写完，API 核对过 |
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

- `ExcavatorTrajectoryAgent`
  - `controller` -> Traj 挖掘机上的 `ExcavatorController`
  - `bucketTip` -> `Traj_BucketTip`
  - `trajectory` -> `TrajectoryProvider`
  - `environmentRoot` -> 当前 `TrajectoryEnvironment` 或该环境 prefab 的根节点
  - 动作空间：4 维连续动作，boom / stick / bucket / swing
  - 观测空间：31 维
  - 观测内容：bucketTip 局部位置/速度、关节位置/速度、上一动作、未来 3 个 waypoint 误差、路径进度、是否最后 waypoint、tube signed distance、episode 进度
  - `waypointLookahead` 默认保持 3；如果修改，必须同步 Behavior Parameters 的 Vector Observation Space Size
  - `tubeRadius` 只用于 tube 奖励/惩罚；waypoint 到达使用独立的 `targetThreshold`
- `TrajectoryProvider`
  - 支持单点目标和多点路径
  - `randomizePathOnReset = true` 时，每个 episode 自动生成随机 waypoint 路径
  - `randomWaypointCount = 5` 对齐论文 waypoint-following 训练中的 5 点短路径
  - `TrajectoryGroup` 下手动摆放的 waypoint 是基准轨迹；建议先摆成“左 -> 中 -> 右”的目标轨迹
  - `randomPathMode = Trend` 会围绕基准轨迹的每个 waypoint 加随机扰动，适合常规训练
  - `randomPathMode = Independent` 会生成更分散的随机点，适合压力测试
  - `randomLocalMin/randomLocalMax` 仅限制 `Independent` 模式的随机路径生成范围
  - `randomMinSegmentLength/randomMaxSegmentLength` 仅用于 `Independent` 模式的相邻 waypoint 距离限制
  - `trendLateralJitter/trendVerticalJitter` 控制基准轨迹周围的水平扰动和高度扰动
  - Play 时可自动给 waypoint 添加小球可视化
  - 当前目标、待到达目标、已完成目标使用不同颜色
  - 会在随机路径 reset 时自动修复空 waypoint、重复 waypoint、引用到其它环境的 waypoint
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
  - Vector Observation Space Size 必须设置为 31
  - Continuous Actions = 4
  - 训练环境优先保持稳定；ONNX 导出暂时不作为短期任务
- 每次代码修改后的快速验证配置
  - `config/trajectory_smoke.yaml`
  - 网络和 buffer 较小，`summary_freq` 更高，用于检查是否能连接、跑 episode、写指标
  - 不用于判断最终策略性能

`ExcavatorTrajectoryAgent` 的 reward 当前由以下部分组成：

- 到当前 waypoint 的距离惩罚和距离减少量奖励
- 粗目标/细目标势能差奖励：只有距离当前 waypoint 变近时才给正奖励，静止靠近目标不会持续刷分
- tube 内推进奖励、tube 外惩罚：在 tube 内但不继续接近当前 waypoint 时不再给持续正奖励
- 动作平方惩罚和相邻动作变化惩罚；当前权重较小，避免策略把“完全不动”学成局部最优
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
