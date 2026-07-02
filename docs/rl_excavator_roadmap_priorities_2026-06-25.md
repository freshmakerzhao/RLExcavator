# RLExcavator 缩比模型 后续规划(按优先级 / 可行性导向)

生成 2026-06-25 · 定位:**可执行规划**。优先级由用户明确给定:
**P1 仿真闭环 > P2 落地实物 > P3 工业化易扩展**。全文以可行性为准,不追求一次性理想架构。
代码引用均已核到 file:line。互补文档:`rl_excavator_scale_model_adaptation_plan_2026-06-25`(差异点 R1–R9)、
`rl_machine_platform_architecture_2026-06-25`(P3 抽象细节,本轮降级为第三优先级)。

---

## 0. 总则:三优先级 + 可行性原则

| 优先级 | 目标 | 完成判据 | 本轮投入 |
|---|---|---|---|
| **P1** | 缩比模型在 Unity 跑通"生成轨迹→跟踪→成功/失败" | demo 能稳定走完一条轨迹并正确判定成功/失败 | **主力** |
| **P2** | 同一策略能落到实物缩比机(接口就绪,只差真机标定) | 部署侧接口/传感器/安全/动作顺序就绪,dry-run 单通道正确 | **搭好骨架,标定项延后** |
| **P3** | 解耦成多机型可扩展平台(铲车/推土机) | 加新机型≈加配置+小插件,不动核心 | **只做"集中配置"的增量,不做大重构** |

**可行性原则(贯穿全程):**
1. **不大改、不重写**——P1/P2 在现有结构上推进;P3 用接口包裹,不推倒。
2. **不浪费**——P1 把所有机型相关常数集中到**一份配置**(见 §4),P3 只是把这份配置正式化为 MachineProfile,而非重做。
3. **不天马行空**——凡依赖真机数据 / 未确认硬件特性的事项,**先搭接口、留占位**,标定值等真机回来再填(见 §2 的 FF 标定、执行器类型确认)。
4. **每步有回归门**——改缩比参数前,挖掘机原行为必须可对照,避免悄悄改坏。

---

## 1. P1 仿真闭环(最高优先级)

落实用户最初给定的分阶段路线:约束验证 → 接控制接口 → 接 RRT* → 接 RL → 成/败判定。

### Stage 1 · 物理可控性
- 用 `ExcavatorActuatorPairTester`(`Assets/Scripts/Testing/ExcavatorActuatorPairTester.cs`)+ `logConstraintState` 逐缸验证。
- 确认每个受控约束 **启用了有限 Range**(尤其 `Bucket_R` 实测 rangeEnabled=false,必须打开 —— 对应 R4)。
- **验收**:swing/boom/stick/bucket 四自由度能平滑走完全行程,不爆限位、左右镜像缸协同正确。

### Stage 2 · 接入控制接口
- 把缩比模型约束绑到 `ExcavatorController`(`SetNormalizedSpeeds` @ :137)。
- **maxSpeed 改缩比量级**:Cat365 的 `0.5/0.5/0.6/0.5`(:48-51)→ 缸 `~0.03–0.08`、swing 单列(对应 R3)。
- 键盘 Heuristic 验证 `GetNormalizedPosition`(:240)输出 ∈[-1,1] 且无 NaN(Range 无效会退化为原始位移,:257)。
- **验收**:键盘驱动下,归一化位置/速度量纲正确、无 NaN。

### Stage 3 · 接 RRT*(先固定 waypoint 再 RRT*)
- 先**手填固定 waypoint** 跑通下游(TrajectoryProvider → Agent),验证跟踪链路。
- 重标可达空间:`ReachableWorkspaceVisualizer` 35 个 Cat365 锚点(:1494-1547)→ 缩比模型实测锚点(对应 R5)。先检查 `workspaceFrame` 绑定。
- RRT* 魔数按缩比因子缩放:step1.1/tol0.9/rewire2.2/collision0.18 等(对应 R6)。
- **验收**:点选起终点后 waypoint 自然落在可达空间内,不是直线后吸附、不越界。

### Stage 4 · 接 RL 策略 —— **P1 的可行性关键点**
**现状风险(R2):** 现有 ONNX(如 `traj_fan_reward_0618_01`)在 Cat365 量级训练,`positionNormalizer=10`、`distanceNormalizer=8`,叠加 ML-Agents `normalize:true` 的运行时统计。缩比模型尺寸小一个量级,观测会被压到≈0 → **旧 ONNX 极可能"看着在动、实则无效"**。

**可行路线(推荐,务实):**
1. **先用旧 ONNX 做"管路冒烟测试"**——只验证 Agent 能收观测、出动作、驱动执行器(链路通不通),**不指望跟踪质量**。
2. **再在缩比场景重训**——复用现有 37 维观测 / 4 维动作接口与 `config/trajectory_ppo.yaml`,把 normalizer/threshold/tubeRadius 设成缩比量级,用 `tools/mlagents_train_no_onnx.py` 训练、`tools/mlagents_export_legacy_onnx.py` 导出(流程已验证)。
3. bucket pitch frame 重绑到缩比模型"随 swing 转的上车"(Agent :784 现找 `excavator_CAT365`,对应 R7)。

> 为什么不赌旧 ONNX 直接迁移:归一化的运行统计是 Cat365 的,迁移成败不可控;而重训管线**已经是成熟、已知成本**的事。重训是低风险路径,不是额外冒险。

### Stage 5 · 成功/失败判定
- `ExcavationDemoTaskManager` 现仅 `Completed`(无 Fail,对应 R8)。新增 `DemoState.Failed`,触发条件:超时 / 末端越出可达空间 / 长时间无进展(卡死)。失败时停控制 + UI 提示 + 可 Reset。
- **验收**:正常跟踪进 Completed;人为制造超时/越界,稳定进 Failed 并安全停车。

**P1 完成 = 缩比模型在仿真里能"生成轨迹→跟踪→成/败"闭环跑通。**

---

## 2. P2 落地实物(第二优先级:搭骨架,标定延后)

目标:P1 验证过的策略能跑到实物缩比机上。**能现在做的全做;只把依赖真机数据的标定留占位。**

### 现在就能做(不依赖真机)
1. **修 R1 动作顺序(最高 ROI)**:`deploy_scale_model/rollout_action_send.py` 的 `ACTION_ORDER=(swing,boom,stick,bucket)` 与 Unity/ONNX 的 `[boom,stick,bucket,swing]`(Agent :386-389)不一致 → 全通道错位且不报错。**统一以 Unity/ONNX 顺序为准**,改部署侧。最好让两端读**同一份配置**(见 §4),从构造上消除。
2. **传感器接入 → machine_state**:实物有拉线传感器(缸位移+缸速),但 `excavator.py` 当前**完全开环、不读传感器**。先实现"读拉线 → 缸位移/缸速 → 关节角/角速度"的状态估计。
3. **前向运动学 FK(关键依赖)**:实物要喂出 37 维观测,需要 `缸位移 → 关节角 → 铲斗尖位姿` 的解析 FK(仿真里 AGX 直接给,实物没有)。**先实现并与仿真 FK 对拍校验**。没有它,策略无法在实物上构造观测。
4. **FF+PI 速度环骨架**:`u = FF(v_ref) + Kp·e + Ki·∫e`。**现在先搭结构**:FF=现有开环映射、Kp=Ki=0(等价当前行为),预留 `v_meas` 输入与整定接口。
5. **安全层(实物强制)**:clamp / 死区 / 限速 / anti-windup / 限位保护 / 通信超时归零 / 急停。仿真也启用(便宜),实物必须。
6. **验收(dry-run)**:单通道测试——只给一个通道=1,只有对应执行器动(同时验证 R1 已修)。

### 延后到真机回来再做(标定项)
- **FF(v_ref) 标定**:采集 `操纵量 u → 实测缸速 v_meas`,拟合 FF 表;再整定 Kp/Ki。依赖真机,**现在做不了,留接口即可**。

### ⚠️ P2 可行性必须先确认的一件事(请你拍板)
`excavator.py` 把各轴映射成**舵机角度**(`arm: Y1*45+90 → servo` 等,CH1/2/3 舵机,CH4 swing 电机)。这意味着实物缩比机的缸**可能是 RC 舵机做位置控制**,而不是液压/比例阀做速度控制。这直接影响 route-1(速度指令)能否落地:
- 若是**比例阀/流量≈速度**:舵机角≈速度指令,FF+PI 速度环可直接用。
- 若是**舵机直接顶位置**:需要在部署侧加 `v_ref → 积分 → 位置设定` 的适配层,或对该硬件改成位置指令。

**这不是代码能判定的,取决于你那台缩比机的实际作动方式。请确认执行器类型**,我再据此定 P2 的速度环落地方式。这正是"考虑可行性、不天马行空"的点——在搞清楚前不假设速度环能直接用。

---

## 3. P3 工业化易扩展(第三优先级:增量捕获,不大重构)

**本轮不做大重构。** P3 的价值通过 P1/P2 的纪律**顺手拿到**,完整抽象等"单机型管线跑通 + 确有第二台机型上马"再做。

- **增量部分(P1/P2 顺手做,几乎零成本)**:把所有机型相关常数(maxSpeed、Range、动作顺序、normalizer、可达空间锚点、pitch 目标)**集中到一份配置对象/文件**(见 §4),而不是散在各脚本。这本身就是 MachineProfile 的雏形,且 sim/真机共读这份配置可顺带消灭 R1。
- **延后部分(确有需求再做)**:正式的 `MachineProfile` 数据模型、`IActuatorBus / IObservationBuilder / IVelocityController` 接口、asmdef 程序集边界、第二机型(铲车/推土机)接入。细节见 `rl_machine_platform_architecture_2026-06-25`,**作为路线储备,不在本轮执行**。
- **可行性提醒**:铲车/推土机涉及"行走与工具耦合",会突破当前"准静态基座"假设,是真实范围扩张;真要做时,第一个第二机型建议先固定底盘、只验证工具动作。

---

## 4. 跨优先级"省功"原则:一份配置贯穿 sim↔real

为避免 P1 的工作在 P3 被推翻,P1 一开始就把机型常数集中成**单一来源**(哪怕只是一个简单的 ScriptableObject 或一个 config 段):

```
ScaleExcavatorConfig (单一真值)
  ├─ 执行器:id / 类型 / Range(min,max) / maxSpeed / 符号 / 通道
  ├─ 动作顺序:[boom, stick, bucket, swing]      ← Unity 与部署共读 → R1 消失
  ├─ 观测参数:positionNormalizer / distanceNormalizer / threshold / tubeRadius
  ├─ 工具:bucket tip frame / pitch 目标
  └─ 可达空间锚点(Move/Carry)
```

- P1:Unity 读它来配置控制/观测/可达空间。
- P2:导出 JSON 给 ORIN,部署侧读同一份 → 动作顺序/缩放/符号天然一致。
- P3:这份配置直接升格为 `MachineProfile`,不重做。

**这是唯一一处"现在多花一点点、后面省很多"的投入**,其余一律按优先级顺序、能省则省。

---

## 5. 风险与验证

| 风险 | 验证方法 | 所属 |
|---|---|---|
| R2 旧 ONNX 观测被压到≈0 | Heuristic 下打印 37 维观测看量级;不行就重训 | P1 |
| R4 Range 未启用 → 量纲/NaN | `logConstraintState` 看 rangeEnabled + `GetNormalizedPosition`∈[-1,1] | P1 |
| R3 maxSpeed 过大 | 目视 + `rangeForce` 是否饱和爆限位 | P1 |
| R5 可达空间错位 | 轨迹是否落蓝色可达区 / `workspaceFrame` 偏移 | P1 |
| R7 pitch frame 绑错 | swing-only 不改 pitch、bucket-only 显著改 pitch | P1 |
| R1 动作顺序错位 | 单通道 dry-run | P2 |
| FK 不可得 → 实物无观测 | 实物 FK 与仿真 tip 位姿对拍 | P2 |
| **执行器类型未知 → 速度环能否落地** | **向用户确认硬件作动方式(§2)** | P2 |
| FF 标定依赖真机 | 留接口,真机回来采 `u→v_meas` | P2(延后) |
| Unity 编译 | 勿双开 Editor;看 Console 红错 | 全程 |

---

## 6. 立即可做的前三件(按 ROI)
1. **Stage 1 + R4**:Tester 逐缸验证,确认每缸能动且 `Bucket_R` Range 已启用。
2. **Stage 2 + R3**:maxSpeed 降到缩比量级再 Play。
3. **R1 + §4**:核实 testbed JSONL 真实通道顺序,统一到 `[boom,stick,bucket,swing]`,并起一份集中配置(同时服务 P1 与 P2)。

## 7. 红线(不变)
不 revert `TestModel.unity`;不把主路线改成 RL 直出操纵杆;不套用 Cat365 参数;先跑通仿真闭环再谈真机手感;真机回来按 `u→v_meas` 标定;**本轮以规划与小步验证为主,不大改代码**。
