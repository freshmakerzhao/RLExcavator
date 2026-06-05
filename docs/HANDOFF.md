# RLExcavator 交接文档（Handoff）

> 目的：让接手者（人或 Codex 等 AI agent）无需追溯历史对话即可继续工作。
> 项目：用 Unity + AGX Dynamics + ML-Agents 训练挖掘机学会“挖一斗土并抬起”。
> 最后更新：2026-06-05。

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
