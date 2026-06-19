# 简化自主挖装任务 Demo 系统设计

> 目标：在仿真中先打通一个最小闭环任务，为后续真机传感器接入和系统联调提供统一接口。
> 当前定位：该 demo 不是完整无人挖装系统，而是用于验证“轨迹规划模块 + RL 轨迹跟踪模块 + 固定挖/放 primitive”的系统集成链路。
>
> 本文与三份配套文档强相关，阅读时建议交叉对照：
> - `RL_DIG_DEMO_PLAN.md`：RL 训练主线与里程碑。
> - `docs/HANDOFF.md`：当前训练状态、Unity 接线现状、已验证 API。
> - `docs/real_machine_observation_requirements.md`：低层策略的真机观测/接口需求（与本文第 5 节接口一致）。

---

## 0. 阅读重点（先看这一节）

本 demo 复用的是**已有的低层轨迹跟踪策略 `ExcavatorTrajectoryAgent`**，它原本是按“训练 episode”模式运行的。直接拿训练态行为跑闭环会失败，原因集中在两点，必须先理解：

1. **训练 episode 生命周期会摧毁闭环**（见第 7 节）：默认 `resetPoseOnEpisodeBegin=true` 会在切换轨迹段时把机器 home 复位 → 带土姿态丢失、洒料；`endEpisodeOnTargetReached` + `maxStepsPerWaypoint` 超时会在不该结束的时候结束 episode。
2. **控制器写入权没有仲裁**（见第 8 节）：`FixedDig/FixedDump` primitive 与 Agent 都会写 `ExcavatorController`，同一物理步双写会打架。

其余章节（任务、状态机、模块、接口、坐标系）描述顺流路径与契约；第 7/8/9 节是让闭环真正跑起来的工程约束；第 11 节列出为此需要新增/修改的代码接口。

---

## 1. 任务定义

当前简化任务为一次完整的“挖掘点到放土点”的搬运闭环：

1. 人工点击 UI 按钮 `Go Dig`。
2. 人工在场景中选择一个挖掘点 `dig point`。
3. 轨迹规划模块从当前铲斗尖位置到 `dig point` 生成一条空间轨迹。
4. RL 轨迹跟踪策略以 `MoveToDig` 模式跟踪该轨迹，到达挖掘点。
5. 执行一个固定挖掘动作 primitive，尽量简单地完成入土、收斗、抬升。
6. 挖起土壤后，将任务模式切换为 `CarryMaterial`，铲斗保持运输姿态。
7. 轨迹规划模块从当前铲斗尖位置到 `pool` 正上方生成一条空间轨迹。
8. RL 轨迹跟踪策略以 `CarryMaterial` 模式跟踪该轨迹，到达 `pool` 上方。
9. 执行一个固定放土动作 primitive。
10. 任务结束，进入 `Done`，再经 `Reset` 回到 `Idle`。

> 注意：上述 1–10 是“顺流成功路径”。任何一步的失败处理（跟踪超时、规划失败、primitive 超时）见第 2 节状态机的失败转移，不能只实现顺流路径。

---

## 2. 状态机

推荐使用有限状态机管理该 demo。除顺流转移外，必须包含失败/超时/复位转移，否则闭环会卡死。

```text
                ┌─────────────────────────── Reset ───────────────────────────┐
                │                                                              │
              Idle ──Go Dig + select dig point──▶ PlanToDig                    │
                ▲                                    │ trajectory ready        │
                │                                    ▼                         │
                │                               TrackToDig ──track fail──▶ Recover
                │                                    │ path completed          │
                │                                    ▼                         │
                │                                FixedDig ──primitive fail──▶ Recover
                │                                    │ primitive done          │
                │                                    ▼                         │
                │                               PlanToDump                     │
                │                                    │ trajectory ready        │
                │                                    ▼                         │
                │                               TrackToDump ──track fail──▶ Recover
                │                                    │ path completed          │
                │                                    ▼                         │
                │                                FixedDump ──primitive fail──▶ Recover
                │                                    │ primitive done          │
                │                                    ▼                         │
                └───────────────────────────────── Done ──────────────────────┘

Recover ──(retry < N)──▶ 回到对应 Plan* 重规划
Recover ──(retry ≥ N)──▶ Aborted ──Reset──▶ Idle
```

### 状态说明

| 状态 | 职责 | 控制器写入权 | 正常退出 | 失败/超时退出 |
|---|---|---|---|---|
| `Idle` | 等待人工 UI 操作 | 无（关节锁定/保持） | 点击 `Go Dig` 并选中挖掘点 → `PlanToDig` | — |
| `PlanToDig` | 生成当前铲斗尖→挖掘点轨迹 | 无 | 轨迹生成成功 → `TrackToDig` | 规划失败 → `Recover` |
| `TrackToDig` | RL tracker 跟踪挖掘点轨迹，`task_mode=MoveToDig` | **Agent 独占** | `PathCompleted` → `FixedDig` | waypoint 超时 / 偏离 tube 过大 / 阶段超时 → `Recover` |
| `FixedDig` | 执行固定挖掘动作（入土→收斗→抬升） | **Primitive 独占** | primitive 序列完成（可选：payload>阈值）→ `PlanToDump` | primitive 超时 / payload 不足 → `Recover` |
| `PlanToDump` | 生成当前位置→`pool` 上方轨迹 | 无 | 轨迹生成成功 → `TrackToDump` | 规划失败 → `Recover` |
| `TrackToDump` | RL tracker 跟踪放土轨迹，`task_mode=CarryMaterial` | **Agent 独占** | `PathCompleted` → `FixedDump` | waypoint 超时 / 偏离 / 阶段超时 → `Recover` |
| `FixedDump` | 执行固定放土动作 | **Primitive 独占** | primitive 序列完成 → `Done` | primitive 超时 → `Recover` |
| `Done` | 一次任务完成 | 无 | 人工 / 自动 `Reset` → `Idle` | — |
| `Recover` | 失败处理：停机、判断重试次数 | 无（先 `StopAll`） | retry<N → 回对应 `Plan*`；retry≥N → `Aborted` | — |
| `Aborted` | 超过重试上限，安全停机 | 无 | `Reset` → `Idle` | — |

要点：

- **`Recover` 第一件事是 `ExcavatorController.StopAll()`**，避免失败后关节还在带速度漂移。
- **每个 `TrackTo*`/`Fixed*` 阶段都要有阶段级 wall-clock 超时**，不能只依赖 Agent 内部的 `maxStepsPerWaypoint`（那是训练用的单点超时，语义不同，见第 7 节）。
- `Reset` 负责：地形复原（`DeformableTerrain.ResetHeights()`）、material/payload 复原、机器 home 复位（仅在此处调用 home 复位，见第 7 节）。

---

## 3. 系统模块划分

系统按 4 层划分：

```text
模块 1：感知 / 地图
  ↓
模块 2：轨迹规划
  ↓
模块 3：RL 轨迹跟踪
  ↓
模块 4：底层控制 / 安全保护
```

当前 demo 中，模块 1 和模块 2 可以先用仿真简化实现，模块 3 是当前训练主线（已有 `ExcavatorTrajectoryAgent`），模块 4 已有 `ExcavatorController`。

横切其上的是**任务编排层**（`ExcavationTaskManager`，第 2 节状态机），它不属于 4 层中任何一层，负责驱动各模块、维护 `task_mode`、仲裁控制器写入权。

---

## 4. 模块职责

### 4.1 模块 1：感知 / 地图

长期真机版本：

- 输入激光雷达、深度相机、IMU、车体/上车位姿等传感器数据。
- 输出局部地图、障碍物、地面高度、可通行空间。

当前仿真 demo 版本：

- 可以先不做真实雷达 SLAM。
- 使用 Unity 中的地面、障碍物、目标点 Transform 或 Collider 作为“模拟感知结果”。
- 输出简化地图或障碍物列表，供规划模块查询。
- **`ground_height` 是第一版唯一真正被 planner 使用的字段**：planner 的 safe height = `ground_height + safe_margin`。obstacle_list 在直线 baseline 阶段可暂不参与碰撞检查（RRT* 版本再用）。

推荐输出：

```text
LocalMap
  timestamp
  frame_id
  ground_height
  obstacle_list
  optional heightmap
  optional occupancy_grid
```

### 4.2 模块 2：轨迹规划

职责：

- 根据当前铲斗尖位置、目标点、任务模式和安全约束，生成铲斗尖空间轨迹。
- 输出 waypoint list 给 RL tracker。
- 决定该轨迹段对应的 `task_mode` 和 `target_bucket_pitch_deg`。

当前第一版建议：

- 先不要实现完整 RRT*。
- 使用“安全高度折线路径”作为 planner baseline。
- 接口保持可替换，后续内部可以替换为 RRT* / A* / TrajOpt / spline planner。

第一版轨迹形式（5 点折线）：

```text
p0 = 当前 bucket tip
p1 = p0 上方 safe height
p2 = start 和 goal 中间点，保持 safe height
p3 = goal 上方 safe height
p4 = goal
```

**与 tracker 的兼容约束（重要）**：

- `ExcavatorTrajectoryAgent.waypointLookahead` 在代码中被 `Mathf.Clamp(.,1,3)` 限制为最多前视 3 个点，且观测维度固定按 3 个 waypoint 计算（`3×3=9` 维）。**planner 输出的 waypoint 数可以多于 3，但不要改 `waypointLookahead`，否则观测维度变化、checkpoint 不兼容**。
- 相邻 waypoint 间距应落在策略训练时见过的尺度内（参考 `TrajectoryProvider` 的 `randomMinSegmentLength≈1.0` ~ `randomMaxSegmentLength≈2.4`）。**间距过大或曲率过急，tracker 会跟踪失败**（见第 13 节风险）。
- `goal` 高度要分模式：`ToDig` 的 `p4` 落在地面挖掘点；`ToDump` 的 `p4` 是 `pool` **上方释放高度**，不是 pool 表面。

后续 RRT* 版本：

- 状态空间先定义为铲斗尖 Cartesian space：`[x, y, z]`。
- 碰撞检查先使用简化球体/胶囊体与障碍物、地面之间的 clearance。
- 输出仍然是 waypoint list，不改变下游接口。

### 4.3 模块 3：RL 轨迹跟踪

职责：

- 消费模块 2 输出的 `TrajectoryCommand`。
- 根据当前挖掘机状态、未来 waypoint、任务模式和目标铲斗 pitch，输出 4 维动作：

```text
boom, stick, bucket, swing
```

当前 `ExcavatorTrajectoryAgent` 的 37 维观测包括：

- 铲斗尖局部位置(3)和速度(3)
- 执行器位置(4)和速度(4)
- 上一步动作(4)
- 未来 3 个 waypoint 相对误差(9)
- 路径进度(1)
- 是否最后 waypoint(1)
- tube signed distance(1)
- episode/控制段进度(1)
- `task_mode` one-hot(2)
- 当前 / 目标 / 误差 bucket pitch(3)
- bucket pitch 速度(1)

模块 3 **不负责**：雷达建图、全局任务规划、障碍物识别、固定挖掘/放土 primitive、真机底层安全急停。

**Demo 推理态的 Agent 配置（关键，详见第 7 节）**：闭环 demo 中该 Agent 不是“在训练”，它的 episode 生命周期参数必须改写：

| 字段 | 训练态 | Demo 推理态 | 原因 |
|---|---|---|---|
| `Behavior Type` | Default(训练) | Inference Only(挂 .onnx) | 推理 |
| `resetPoseOnEpisodeBegin` | true | **false** | 切轨迹段时绝不能 home 复位（否则丢带土姿态） |
| `randomizeTaskModeOnEpisodeBegin` | true | **false** | 模式由 TaskManager 按阶段指定 |
| `endEpisodeOnTargetReached` | true | true（配合 reset=false） | 用 EndEpisode 干净地清理段间状态，但不触发 home 复位 |
| `maxStepsPerWaypoint` | 1200 | 由阶段超时接管 / 或调大 | 训练单点超时语义不适用闭环（见第 7 节） |
| `recordTrainingStats` | true | 可关 | 推理不需要写 TensorBoard |
| `taskMode` | 随机 | 由 TaskManager 每段写入 | `MoveToDig` / `CarryMaterial` |

> demo v1 是否启用 bucket pitch 姿态条件化，取决于标定状态：当前 HANDOFF 显示姿态版尚未标定（pitch 误差大、`bucketPitchAxisLocal` 未定）。**v1 建议先固定单一已标定模式或降低姿态权重，用已验证稳定的 trajectory-only 行为兜底**（见第 13 节）。

### 4.4 模块 4：底层控制 / 安全保护

职责：

- 接收 RL 输出的归一化速度命令。
- 映射到 AGX 或真机执行器控制命令。
- 执行动作限幅、速度限幅、加速度限幅、关节限位保护、通信超时保护和急停。

当前仿真实现（`ExcavatorController`，已写完且 API 核对过）：

- `SetNormalizedSpeeds(boom, stick, bucket, swing)`：把归一化动作写入各关节 AGX `TargetSpeedController`；boom/bucket 走 `boomAcceleration`/`bucketAcceleration` 缓启停，stick/swing 直接给速。
- `boom / stick / bucket` 是 Prismatic（液压缸）目标速度，`swing` 是 Hinge 目标角速度。
- `GetNormalizedPosition`/`Boom|Stick|Bucket|SwingNormalizedPosition`：用 `RangeController` 行程把关节位置归一化到 `[-1,1]`（已防 `±Infinity` 行程导致 NaN）。
- `BeginHomePoseReset` / `SetHomeLock` / `StopAll` / `IsHoming`：episode/复位用；闭环 demo 中**只在 `Reset` 状态调用 home 复位**。

真机部署时：该层需要把归一化速度映射成实际液压缸速度、阀控命令或电机命令，并**保留独立于 RL 的安全保护**（不能完全信任 RL 输出）。

---

## 5. 模块间数据交互

### 5.1 UI / Task Manager -> Planner

```text
PlanningRequest
  timestamp
  task_id
  request_type: ToDig | ToDump
  start_bucket_tip_position_base: [x, y, z]
  goal_position_base: [x, y, z]
  task_mode: MoveToDig | CarryMaterial
  target_bucket_pitch_deg
  min_clearance        # = safe_margin，用于 ground_height + min_clearance 求 safe height
```

说明：

- `ToDig` 的 goal 是人工选择的挖掘点；`ToDump` 的 goal 是 `pool` 正上方目标点。
- 当前第一版 goal 可以由 Unity Transform 提供。
- `task_mode` 与 `request_type` 当前一一对应（ToDig↔MoveToDig，ToDump↔CarryMaterial），但分开表达，便于后续解耦。

### 5.2 Perception / Map -> Planner

```text
LocalMap
  timestamp
  frame_id
  ground_height
  obstacle_list:
    - id
      type: Box | Sphere | Mesh | Unknown
      pose
      size
  optional heightmap
  optional occupancy_grid
```

说明：

- 第一版可以只提供地面高度和简单 obstacle boxes。
- 后续真机可替换为雷达/深度相机生成的 heightmap、occupancy map 或 obstacle mask。
- Planner 不应直接依赖 Unity 特定对象，应尽量依赖抽象地图结构。

### 5.3 Planner -> RL Tracker

```text
TrajectoryCommand
  timestamp
  frame_id                              # 必须等于 tracker 使用的车体坐标系，见第 6 节
  task_mode: MoveToDig | CarryMaterial
  target_bucket_pitch_deg
  waypoints_base:
    - [x0, y0, z0]
    - [x1, y1, z1]
    - [x2, y2, z2]
    - ...
  target_threshold                      # 对应 Agent.targetThreshold
  tube_radius                           # 对应 Agent.tubeRadius
```

说明：

- Planner 可以输出完整 waypoint list。
- `TrajectoryProvider` 维护当前 waypoint index（`CurrentIndex` / `AdvanceTarget` / `PathCompleted`）。
- `ExcavatorTrajectoryAgent` 每步只观测未来 3 个 waypoint。
- **轨迹点必须与铲斗尖位置使用同一坐标系**（当前 Agent 用 `agent.transform` 局部系，见第 6 节）。
- 落地方式：TaskManager 把 `waypoints_base` 通过新增的 `TrajectoryProvider.SetPlannedPath(...)` 注入（第 11 节），而不是编辑器摆点或随机生成。

### 5.4 State Estimator -> RL Tracker

```text
ExcavatorState
  timestamp
  frame_id
  actuator_positions:   { boom, stick, bucket, swing }
  actuator_velocities:  { boom, stick, bucket, swing }
  bucket_tip_position_base: [x, y, z]
  bucket_tip_velocity_base: [vx, vy, vz]
  bucket_pitch_deg
  bucket_pitch_velocity_deg_s
```

说明：

- 仿真中这些量来自 AGX 和 Unity Transform（Agent 内部直接读 `controller` 与 `bucketTip`）。
- 真机中这些量应来自液压缸传感器、关节编码器、IMU、FK 或状态估计融合（详见 `real_machine_observation_requirements.md`）。

### 5.5 RL Tracker -> Low-level Controller

```text
ExcavatorCommand
  timestamp
  normalized_speeds:
    boom:   [-1, 1]
    stick:  [-1, 1]
    bucket: [-1, 1]
    swing:  [-1, 1]
  control_owner: Agent | Primitive      # 写入权标记，见第 8 节
```

说明：

- 当前仿真中由 `ExcavatorController.SetNormalizedSpeeds()` 消费。
- 真机中由底层控制器映射到液压/电机命令。
- `control_owner` 是仲裁字段：底层在同一物理步只接受当前 owner 的命令（第 8 节）。

### 5.6 Task Manager -> Fixed Primitive

```text
PrimitiveCommand
  primitive_type: FixedDig | FixedDump
  duration
  timeout                               # 阶段级超时，超时进入 Recover
  optional parameters
```

说明：

- 第一版固定挖掘和固定放土动作不需要学习，可用时间脚本化的归一化速度序列实现。
- 只要能视觉上完成“挖一下”和“倒一下”，即可支撑闭环 demo。
- primitive 执行期间**独占控制器写入权**，Agent 必须被 gate 住（第 8 节）。

---

## 6. 坐标系约定

当前建议使用同一套局部车体坐标系作为模块 2 和模块 3 的接口坐标系：

```text
base_frame / upper_body_frame
```

`bucket_tip_position` / `goal_position` / `waypoints` / `obstacle_list` 必须在同一坐标系下表达，或消息里明确 `frame_id` 并由系统提供坐标变换。

**当前实现真相（务必对齐）**：`ExcavatorTrajectoryAgent` 用 `transform.InverseTransformPoint(...)` 把铲斗尖和 waypoint 都转到 **`agent.transform`（挖掘机根节点）局部系**。因此 planner 输出的 `waypoints_base` 必须可被换算到该根节点局部系；最简单的做法是 `SetPlannedPath` 接受世界坐标，由 `TrajectoryProvider` 自行换算（第 11 节）。

### bucket pitch 参考定义（含必做验证）

```text
bucketPitchFrame        = 随 swing 一起旋转的上车/驾驶室/回转平台坐标系
bucketAttitudeReference = 铲斗本体坐标系
bucketPitchAxisLocal    = 铲斗本体中用于定义 pitch 的局部轴
```

当前仿真推荐绑定：

```text
Bucket Tip                = Traj_BucketTip
Bucket Attitude Reference = Traj_Bucket
Bucket Pitch Frame        = <随 swing 旋转的上车节点>   # 见下方警告
```

> ⚠️ **命名坑**：`bucketPitchFrame` **必须绑到随 swing 一起旋转的上车/驾驶室/回转平台**，绝不能绑到不随回转转的下车/底盘。名字里含 “Chassis/底盘” 的对象很可能是**下车**，绑错会让 swing 污染 pitch。Agent 的自动查找会尝试 `Traj_Cabin / Cabin / UpperCarriage / Chassisbody / excavator_CAT365`，但**自动查找只是兜底，必须在 Inspector 显式绑定并人工验证**。

**必做验证（来自 HANDOFF，做 demo 前先过）**：在 `Heuristic Only` 下：

1. 只按 `F/H`（swing）旋转上车 → UI 中 `Bucket pitch` 基本不变；
2. 只按 `J/L`（bucket 液压缸）→ `Bucket pitch` 明显变化。

若 1 不成立，说明 `bucketPitchFrame` 绑错或 `bucketPitchAxisLocal` 未标定——**先修，别训练，也别用姿态条件化跑 demo**。

---

## 7. 闭环推理 vs 训练 episode 生命周期（关键）

`ExcavatorTrajectoryAgent` 是按训练 episode 写的，闭环 demo 必须绕开它的复位逻辑。下面是必须理解的几个行为真相和对应对策。

### 7.1 home 复位会丢带土姿态

`OnEpisodeBegin` 在 `resetPoseOnEpisodeBegin=true` 时调用 `controller.BeginHomePoseReset()`，把机器拉回 home。闭环里如果 Dig→Dump 之间触发它，**带土姿态和载荷会被破坏**。
→ 对策：demo 推理态 `resetPoseOnEpisodeBegin=false`；**home 复位只在状态机 `Reset` 阶段由 TaskManager 显式调用 `controller.BeginHomePoseReset()`**。

### 7.2 段间切换用 EndEpisode，但不复位姿态

完成一段轨迹后需要切到下一段。直接“替换 waypoints 而不开新 episode”行不通，因为以下 episode 级标志只在 `OnEpisodeBegin` 重置：

- `m_episodeCompletedPath`：上一段完成后为 `true`，不重置会让新路径的 `AdvanceTarget` 永远不推进（受 `!m_episodeCompletedPath` 守卫）。
- `m_stepsOnCurrentWaypoint` / `m_previousDistance` / 运动历史等。

→ 推荐模式：**`endEpisodeOnTargetReached=true` + `resetPoseOnEpisodeBegin=false`**。完成一段 → 自然 `EndEpisode` → `OnEpisodeBegin` 干净清理所有 episode 标志，但因 reset=false 不做 home 复位、机器保持当前姿态。TaskManager 在新 episode 的 `OnEpisodeBegin` 之前把下一段 waypoints 和 `taskMode` 准备好（通过 `QueuePlannedPath` 的 pending 路径机制，第 11 节）。

### 7.3 maxStepsPerWaypoint 是训练超时，语义不等于阶段超时

`maxStepsPerWaypoint`（默认 1200 决策步）超时会 `EndEpisodeWithStats(false)`。闭环里：

- 若设得太小 → tracker 还没到点就被判超时、被迫切段；
- 完成路径后若 Agent 仍空转等待 TaskManager 切段，`m_stepsOnCurrentWaypoint` 仍在累加，可能误触发超时。

→ 对策：demo 用**状态机阶段级 wall-clock 超时**作为权威失败判据（进入 `Recover`）；`maxStepsPerWaypoint` 调大或交由阶段超时接管，并保证 TaskManager 在路径完成后**及时**切段，不让 Agent 长时间空转。

### 7.4 推荐的段间时序（TaskManager 视角）

```text
进入 TrackToDig / TrackToDump:
  1. agent.taskMode = (MoveToDig | CarryMaterial)        # 不随机
  2. trajectory.QueuePlannedPath(planner.waypoints_world) # 注入下一段规划路径（pending）
  3. 打开 `ExcavatorController.commandInputEnabled`，允许 Agent 写入控制器
  4. 等待 trajectory.PathCompleted == true（或阶段超时 → Recover）
  5. PathCompleted → 进入对应 Fixed* 状态；Agent 完成段后自然 EndEpisode（无 home 复位）
```

---

## 8. 控制器写入权仲裁（关键）

`ExcavatorController.SetNormalizedSpeeds()` 是动作进入物理世界的唯一入口。闭环里有多个潜在写入源：Agent（`TrackTo*`）、Fixed primitive（`FixedDig/FixedDump`）、（调试期）键盘。**同一物理步多个源写入会互相打架**（与 HANDOFF 中“键盘 vs Agent 同时写关节”同类问题）。

第一版先约定一个最小命令闸门：

| 状态 | 唯一写入者 | 其它源处置 |
|---|---|---|
| `Idle` / `Plan*` / `Recover` / `Done` | 无（`StopAll` 或保持锁定） | `commandInputEnabled=false` |
| `TrackToDig` / `TrackToDump` | Agent | primitive 不运行；键盘禁用 |
| `FixedDig` / `FixedDump` | Fixed primitive | **先关闭 Agent/命令闸门**，后续 primitive 独占控制器 |

当前已落地的是简单版本：`ExcavatorController.SetNormalizedSpeeds()` 内部检查
`commandInputEnabled`。训练场景默认保持 `true`；demo 场景由
`ExcavationDemoTaskManager` 在 Play 后关闭，在点击“开始追踪”后打开。

后续如果固定挖掘/放土 primitive 也要直接调用 `SetNormalizedSpeeds()`，再升级为
`controlOwner` 仲裁：`Agent` / `Primitive` / `None` 三种 owner，TaskManager 切状态时切 owner。

> Agent 内部已有一层保护：`OnActionReceived` 在 `controller.IsHoming` 时直接 return。但这只覆盖 home 复位期间，不覆盖 `Fixed*` primitive 期间，所以仍需上面的显式仲裁。

---

## 9. 时序与频率

| 环节 | 触发频率 | 说明 |
|---|---|---|
| 规划（`Plan*`） | 每阶段一次 | 进入状态时算一条 waypoint 路径，不每步重算 |
| RL 决策（`TrackTo*`） | 每决策步 | `DecisionRequester.DecisionPeriod=5`，物理步 `Fixed Timestep≈0.01~0.02s` → 约 10–20 Hz 决策，`Take Actions Between Decisions=true` 时物理层保持动作 |
| 控制器写入 | 每决策步 / primitive 每物理步 | `SetNormalizedSpeeds` 内部按 `boomAcceleration`/`bucketAcceleration` 缓启停 |
| 阶段超时检测 | 每帧/每物理步 | wall-clock 或步数计；超时进入 `Recover` |

该频率与 `real_machine_observation_requirements.md` 第 7 节真机建议一致（状态估计 ≥20–50Hz，策略 10–20Hz 起步），便于后续真机对齐。

---

## 10. 第一版实现建议

先实现最小闭环，不要一开始实现完整 RRT*。

推荐新增组件及最小接口草图（与现有 `ExcavatorController` / `TrajectoryProvider` / `ExcavatorTrajectoryAgent` API 对齐）：

```csharp
// 任务编排：驱动状态机、维护 task_mode、仲裁控制器写入权
public class ExcavationTaskManager : MonoBehaviour {
  public enum TaskState { Idle, PlanToDig, TrackToDig, FixedDig,
                          PlanToDump, TrackToDump, FixedDump, Done, Recover, Aborted }
  public ExcavatorController controller;
  public ExcavatorTrajectoryAgent agent;
  public TrajectoryProvider trajectory;
  public SimpleTrajectoryPlanner planner;
  public FixedExcavationPrimitive primitive;
  public int maxRetriesPerStage = 2;
  public float stageTimeoutSeconds = 30f;
  // OnGoDig(digPoint) / Tick(): 按第 2 节状态机推进；切状态时切 control_owner
}

// 人工点击选挖掘点
public class TaskPointSelector : MonoBehaviour {
  public System.Action<Vector3> OnDigPointSelected;   // 世界坐标
}

// 安全高度折线 planner（第一版 baseline）
public class SimpleTrajectoryPlanner : MonoBehaviour {
  public float safeMargin = 2.0f;          // safe height = ground_height + safeMargin
  // 输入起点(当前 bucketTip 世界坐标)、goal 世界坐标、ground_height
  // 输出 5 点折线世界坐标 Vector3[]（p0..p4，见 4.2）
  public Vector3[] Plan(Vector3 startTipWorld, Vector3 goalWorld, float groundHeight);
}

// 固定挖/放 primitive：时间脚本化的归一化速度序列，独占控制器
public class FixedExcavationPrimitive : MonoBehaviour {
  public ExcavatorController controller;
  // 例：FixedDig = [入土:bucket+stick] → [收斗:bucket+] → [抬升:boom+]，各段定时
  public IEnumerator Run(PrimitiveType type, float timeout);  // 完成/超时回调
}
```

第一版 planner：`输入当前 bucket tip 和 goal → 生成 5 个 waypoint → SetPlannedPath 写入 TrajectoryProvider`。当前代码已提供
`SimpleTrajectoryPlanner`，用于把起点/终点转换为一条带可选中段抬高的 waypoint 路径。
第二版 planner：`替换为 RRT*，但保持 Planner -> Tracker 的 TrajectoryCommand 接口不变`。

---

## 11. 需要新增 / 修改的代码接口

为支撑闭环，现有代码需要以下小改动（都向后兼容，不影响训练）：

### 11.1 `TrajectoryProvider`：运行时注入规划路径

`TrajectoryProvider` 支持编辑器摆点、随机训练路径，以及 planner 在运行时注入的路径：

```csharp
// 接受世界坐标 waypoint，内部转 localPosition，立即重置当前路径并刷新可视化；
// 适合推理/demo 中当前段马上开始跟踪。
public void SetPlannedPath(Vector3[] worldPoints);

// 接受世界坐标 waypoint，暂存到下一次 ResetPath() 生效；
// 适合“本段完成 -> EndEpisode -> 下一段开始”的状态机时序。
public void QueuePlannedPath(Vector3[] worldPoints);

// Demo 启动时可隐藏 prefab 中残留的默认轨迹；新规划路径写入后会重新显示。
public void SetPathVisualsVisible(bool visible);
```

并在 demo 场景中关闭 `randomizePathOnReset`（否则 `ResetPath` 会用随机路径覆盖 planner 路径）。
`TrajectoryGroup` 不应理解为“训练轨迹对象”，而应理解为模块三的 waypoint 缓冲区：
训练时它可以提供基准/随机路径；demo 推理时它接收 planner 输出并给 RL policy 提供当前目标。

`SimpleTrajectoryPlanner` 第一版接口：

```csharp
public TrajectoryProvider trajectory;
public Transform startPoint;
public Transform endPoint;
public int waypointCount = 5;
public float arcHeight = 0.5f;
public float endpointMoveStep = 0.5f;
public Transform endpointMoveFrame;

public void SelectStartPoint();
public void SelectEndPoint();
public void MoveSelectedPointForward();
public void MoveSelectedPointBackward();
public void MoveSelectedPointLeft();
public void MoveSelectedPointRight();
public void MoveSelectedPointUp();
public void MoveSelectedPointDown();
public void PlanConfiguredPath();
public bool TryPlanConfiguredPath();
public void Plan(Vector3 startWorld, Vector3 goalWorld);
public Vector3[] GeneratePath(Vector3 startWorld, Vector3 goalWorld);
```

第一版只做“起点 -> 终点”的光滑 waypoint 生成；后续替换 RRT* 时保持 `Plan(...) -> Vector3[] -> TrajectoryProvider` 这条接口不变。
交互 demo 中 `StartPoint` 和 `EndPoint` 都应是场景中的可见控制点。UI 先选择当前编辑的是起点还是终点，
再按 `endpointMoveFrame` 的前/后/左/右/上/下方向移动当前控制点；`endpointMoveFrame` 为空时使用 planner 物体坐标系。
控制点总是可见，不再提供“是否显示控制点”的开关；demo 也不再支持把 `bucketTip` 作为规划起点。

`ReachableWorkspaceVisualizer` 用于把手动标定的铲斗尖可达空间画成扇形区域，第一版只做可视化检查，不参与训练、
轨迹规划或策略推理。当前默认内置 15 个 `env01` 局部坐标标定点，排列方式为 5 个方向（左、左前、正前、
右前、右）乘 3 个距离层（近、中、远）。推荐把该组件挂在 `TrajectoryEnvironment_01` 或与 `env01`
完全重合的空物体上；如果挂在其它物体上，则需要把 `Workspace Frame` 显式绑定到 `env01`。确认区域不偏后，
后续 planner/random path 才应复用这组点作为可达空间边界。

训练用随机轨迹现在推荐使用 `TrajectoryProvider.randomPathMode = CalibratedFan`。该模式使用同一组 15 个
标定点做双线性插值，在扇形可达区域内采样起点、终点和中间 waypoint，并通过
`calibratedFanMinDirectionSpan` 强制起终点具有足够左右跨度。与旧的 `Trend` 模式相比，它不再围绕一条固定
“左到右”基准轨迹抖动，更适合训练方向多变的轨迹跟踪策略；与 `Independent` 模式相比，它不会在矩形盒子里采到
明显不可达的点。后续 RRT* 或感知规划模块也应优先复用同一可达空间边界。
注意：15 个标定点是环境局部坐标，不是 `TrajectoryGroup` 局部坐标。`calibratedFanFrame` 为空时默认使用
`TrajectoryGroup` 的父物体作为参考系；如果手动绑定，应绑定当前 `TrajectoryEnvironment`，不要绑定
`TrajectoryGroup` 本身，否则会把 `TrajectoryGroup` 的偏移重复叠加到 waypoint 上，导致目标点飞出平台。

### 11.2 `ExcavatorController`：demo 命令输入闸门

```csharp
public bool commandInputEnabled = true;
public bool CommandInputEnabled { get; }
public void SetCommandInputEnabled(bool enabled);
public void EnableCommandInput();
public void DisableCommandInput();
```

`SetNormalizedSpeeds(...)` 会先检查 `commandInputEnabled`。关闭后立即 `StopAll()`，后续所有
Agent/键盘写入都会被忽略。训练场景保持默认 `true`，交互 demo 由 TaskManager 控制。

### 11.3 `ExcavatorTrajectoryAgent`：训练/推理策略脚本

`ExcavatorTrajectoryAgent` 不再承载 demo 专用开关，避免训练脚本和 demo 脚本耦合。
它只负责：

```csharp
public TrajectoryTaskMode taskMode;
public bool randomizeTaskModeOnEpisodeBegin;
public bool resetPoseOnEpisodeBegin;
```

按第 4.3 节表格设置 Inspector/Behavior 参数即可（`resetPoseOnEpisodeBegin=false`、`randomizeTaskModeOnEpisodeBegin=false`、`taskMode` 由 TaskManager 写、Behavior Type=Inference）。
TaskManager 在每段开始前直接写 `agent.taskMode`，并保持 `randomizeTaskModeOnEpisodeBegin=false`。

训练 reward 中姿态项分为两层：每步 `wBucketAttitude` 按 pitch error 给惩罚，
`wBucketAttitudeProgress` 在 pitch error 变小时给进步奖励；到达 waypoint 和完成路径时，
`scaleWaypointBonusByBucketAttitude=true` 会根据当前 pitch error 缩放 `waypointBonus/pathCompleteBonus`。
这样模型仍然能通过接近目标获得进度奖励，但不能长期只靠追点拿满 waypoint/path bonus。

### 11.4 `ExcavationDemoTaskManager`：交互 demo 操作台

新增 `Assets/Scripts/Planning/ExcavationDemoTaskManager.cs`，用于把 UI 按钮和底层模块解耦。

推荐接线：

```text
agent      -> ExcavatorTrajectoryAgent
controller -> ExcavatorController
planner    -> SimpleTrajectoryPlanner
trajectory -> TrajectoryProvider
bucketTipLocalFrame -> 用于显示铲斗尖局部坐标的参考系，建议绑定 TrajectoryEnvironment 或 RLExcavatorTraj
statusText -> 可选 Text，用于显示状态
```

默认 `requireInferenceOnlyBehavior=true`，只有当绑定的 Agent `Behavior Type=Inference Only`
时才会执行 demo 启停控制。这样即使误把该组件留在训练场景中，`Behavior Type=Default`
时也不会关闭 controller 命令输入或修改随机 task mode。

默认 `hideTrajectoryUntilPlannedOnStart=true`，Play 后会隐藏 `TrajectoryProvider` 中残留的默认
waypoint 可视化，避免 demo 一开始显示训练用路径。点击“生成轨迹”后，新轨迹会重新显示。
默认 `requirePathBeforeTracking=true`，没有规划路径时点击“开始追踪”不会启动控制。
默认 `disableDecisionRequesterWhenStopped=true`，demo 空闲、停止或轨迹完成后会禁用
`DecisionRequester`，开始追踪时再启用，避免策略在停止状态持续推理。
状态栏会显示 `铲斗尖局部`，计算方式为
`bucketTipLocalFrame.InverseTransformPoint(agent.bucketTip.position)`。该值用于手动标定可达空间范围；
如果 `bucketTipLocalFrame` 为空，会自动优先使用 `agent.environmentRoot`，再退到 `TrajectoryProvider`。

可绑定到 Unity UI Button 的 public 方法：

```csharp
SetModeMoveToDig()
SetModeCarryMaterial()
SelectPlanStartPoint()
SelectPlanEndPoint()
MoveSelectedPlanPointForward()
MoveSelectedPlanPointBackward()
MoveSelectedPlanPointLeft()
MoveSelectedPlanPointRight()
MoveSelectedPlanPointUp()
MoveSelectedPlanPointDown()
PlanPath()
StartTracking()
StopTracking()
PlanAndStartTracking()
PlanMoveToDigAndStart()
PlanCarryMaterialAndStart()
```

### 11.5 `ExcavationDemoRuntimeUI`：展示用 HUD

新增 `Assets/Scripts/UI/ExcavationDemoRuntimeUI.cs`，用于在 Play 时自动生成一个演示 HUD。
推荐把它挂在 `DemoSystem` 上，并把 `Task Manager` 指向同一个物体上的
`ExcavationDemoTaskManager`。

HUD 结构：

```text
顶部信息栏：
  左侧标题 = 算法验证
  右侧单位/Logo 文本 = organizationText

中间功能导航：
  感知建图 / 生成轨迹 / 轨迹追踪
  当前 tab 使用蓝色高亮和发光边框

左侧控制菜单：
  随当前 tab 切换。
  感知建图暂为占位按钮。
  生成轨迹可通过 UI 选择编辑 StartPoint / EndPoint，并对当前控制点进行前/后/左/右/上/下移动。
  生成轨迹可直接调用 SimpleTrajectoryPlanner 生成 waypoint，也可一键生成并进入追踪。
  轨迹追踪绑定“生成轨迹 / 开始追踪 / 停止追踪 / 重置演示”等功能。

右上角态势监控窗口：
  绑定 Minimap Camera 后显示俯视画面。
  未绑定时显示占位态势示意图。
```

该脚本只调用 `ExcavationDemoTaskManager` 的 public 方法，不直接控制关节，也不修改训练
Agent。若场景中没有 `EventSystem`，它会自动创建一个，避免按钮无法点击。小地图如需显示真实俯视画面，
在 Inspector 中把 `Minimap Camera` 绑定到一个俯视相机即可。
小地图使用与显示窗口一致的宽高比例创建 RenderTexture，并在 UI 中保持画面比例显示，避免俯视画面被拉伸变形。

当前 HUD 仍采用 runtime 代码生成，优点是便于快速迭代和保持 demo 脚本自包含；如果后续进入正式展示阶段，
更推荐把布局迁移为 Canvas Prefab 或 UI Toolkit 资产，代码只负责绑定事件和刷新数据。

“重置演示”由 `ExcavationDemoTaskManager.ResetDemo()` 提供，语义是重载当前 demo scene，
尽量接近“重新启动一次演示”。进入 reset 前会先关闭 controller 命令输入并停止关节目标速度。

为保证模块解耦，`ExcavationDemoRuntimeUI` 不会自动查找或禁用训练 UI。展示场景中如果
不需要训练指标和键盘说明，应在 `ExcavatorDemoScene` 内手动删除或禁用对应 Canvas，例如：

- `UITargetTracking` 所在的训练指标 Canvas
- 包含 `Keyboard controls` / `Gamepad controls` 文本的说明 Canvas

第一版 demo 操作流程：

```text
Play
  -> controller.commandInputEnabled=false，挖掘机静止
  -> 进入“生成轨迹”tab
  -> 点击“编辑起点”，通过控制点前/后/左/右/上/下按钮调整 StartPoint
  -> 点击“编辑终点”，通过控制点前/后/左/右/上/下按钮调整 EndPoint
  -> 点击“生成轨迹”或“生成并追踪”
  -> 如只生成轨迹，再进入“轨迹追踪”tab 点击“开始追踪”
  -> 轨迹完成后自动 Stop
```

---

## 12. 成功标准

### 12.1 定性最小标准（一次顺流闭环）

- 点击 `Go Dig` 后可以选中挖掘点。
- 系统自动生成到挖掘点的轨迹，RL tracker 跟踪到挖掘点附近。
- 固定挖掘 primitive 执行完整动作序列。
- 系统自动生成到 `pool` 上方的轨迹，RL tracker 以 `CarryMaterial` 模式跟踪到位。
- 固定放土 primitive 执行完整动作序列。
- 状态机进入 `Done`，且 `Reset` 后能回到 `Idle` 重复下一轮。

### 12.2 量化门槛（复用现有 `Trajectory/*` 指标与阶段定义）

| 项 | 门槛 |
|---|---|
| `TrackToDig` / `TrackToDump` 到位 | bucketTip 到终点距离 ≤ `targetThreshold`(+可视半径)，即 `trajectory.PathCompleted=true` |
| 段内不超时 | 阶段 wall-clock < `stageTimeoutSeconds`，未进入 `Recover` |
| 闭环成功率 | 连续 N 轮（建议 N≥10）顺流完成率统计，记录进入 `Recover`/`Aborted` 的次数与阶段 |
| 姿态（若启用条件化） | `Trajectory/BucketPitchErrorDeg` 在该模式目标附近（标定后再设阈值） |
| 带土（可选） | `FixedDig` 后 payload/`mass_in_bucket` > 阈值，否则视为挖掘失败进入 `Recover` |

不要求第一版满足：自动识别最优挖掘点、真实雷达 SLAM、完整避障 RRT*、最优挖掘动作、真机闭环控制。

---

## 13. 风险与对策

| 风险 | 对策 |
|---|---|
| **[最高] 训练 episode 生命周期摧毁闭环**（home 复位丢姿态、误超时） | 严格按第 7 节配置 demo 推理态参数，home 复位只在 `Reset` |
| **[最高] 控制器双写打架**（Agent vs primitive） | 按第 8 节做写入权仲裁 |
| RL tracker 加入 bucket pitch 后跟不稳 | demo v1 用已稳定的 trajectory-only 行为 / 固定单模式 / 降低姿态权重；姿态标定通过后再开混合模式 |
| `bucketPitchFrame` 绑错（绑到下车） | 按第 6 节必做验证；显式绑定上车节点 |
| planner waypoint 太稀或曲率太大 | 间距控制在训练尺度（约 1.0–2.4m）；折线在 safe height 平滑过渡 |
| 坐标系不统一 | 轨迹点与 bucketTip 统一到 `agent.transform` 局部系；`SetPlannedPath` 负责换算 |
| 固定 primitive 太复杂拖慢验证 | primitive 用最简时间脚本化序列，先保证“挖一下/倒一下”视觉完成 |
| 过早接入真实 RRT*/雷达地图 | 先直线 baseline，接口可替换，把时间留给闭环联调而非碰撞检测调试 |
| 可变形地形性能 | 单环境先跑通；`Reset` 仅复原挖掘区域局部高度，避免全图重置卡顿 |

推荐原则：

```text
先跑通最小闭环，再逐步替换模块内部实现。
顺序：1) 修 episode 生命周期 + 写入权仲裁  2) 直线 planner + 固定 primitive 串成闭环
      3) 姿态标定后开条件化  4) 再谈 RRT*/感知
```

---

## 14. 分期路线图

| 阶段 | 目标 | 依赖 |
|---|---|---|
| **P0 前置校准** | bucket pitch 参考系验证（第 6 节）；demo 推理态参数（第 4.3 节） | 现有 Agent，无新代码 |
| **P1 接口改造** | `TrajectoryProvider.SetPlannedPath`；`ExcavatorController` 写入权仲裁（第 11 节） | 小改动 |
| **P2 最小闭环** | `ExcavationTaskManager` 状态机 + `SimpleTrajectoryPlanner` 直线 + `FixedExcavationPrimitive`；单模式跑通顺流 | P1 |
| **P3 失败处理** | `Recover`/`Aborted`/`Reset` 转移、阶段超时、N 轮成功率统计 | P2 |
| **P4 姿态条件化** | 标定 `MoveToDig`/`CarryMaterial` 目标 pitch，开混合模式 | P2 + 标定 |
| **P5 替换感知/规划** | RRT* / 真实地图，接口不变 | P3 |
