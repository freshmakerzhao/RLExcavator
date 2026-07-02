# RLExcavator 缩比模型 主执行计划 v2(接口/维度/奖励全锁定 · Codex 只执行)

生成 2026-06-25 · **本文件是唯一权威执行手册**。接口、维度、奖励、终止、每个任务都已在此**设计死**。
执行者(Codex 等)**照做即可,不做任何设计判断**;凡遇本文件未写死的取舍,**停下来问人,不要自行决定**。

优先级:**P1 仿真闭环 > P2 落地实物 > P3 工业化易扩展**。

---

## 0. 执行须知(给 agent)

**环境**
- 仓库根:`/home/zhaoshuai/workspace_uinty/RL_prj`;其下 `RLExcavator/`(Unity)、`deploy_scale_model/`(ORIN)、`shared/`、`config/`、`tools/` 同级。
- 单一来源数据:`shared/machine_profile.json`(schema 0.2.0)。**机型常数只改这一处**;C#/Python 不得重新硬编码。
- Unity 不能在 Editor 已打开时跑 batchmode 编译;本机无 `dotnet/csc/mcs`。**C# 改完由人到 Unity Console 看红错确认。**
- 训练/导出:`tools/mlagents_train_no_onnx.py`、`tools/mlagents_export_legacy_onnx.py`(导出 `dynamo=False`)。

**纪律(必须遵守)**
1. **不做设计决策**:接口/维度/奖励/终止以本文件 §2–§4 为准,逐字段实现;有歧义就问人。
2. **行号会漂移**:`file:line` 取自 2026-06-25 审计,编辑前先 `grep` 符号确认。
3. **三处一致**:任何接口实现必须让 `Unity CollectObservations/OnActionReceived` ↔ `config/trajectory_ppo.yaml`(obs/action size)↔ 部署观测构造器 完全一致。
4. **红线**:不 revert `Assets/Scenes/TestModel.unity` 用户改动;RL 不直出操纵杆;不套 Cat365 参数;不改第三方包/`site-packages`;真机手感等标定回来再调。
5. **逐任务**:做完先跑该任务「验收」,通过再下一个;C# 改完提示人编译。
6. **默认中文回复。**

---

## 1. 三目标与主线(route-1)

```
感知建图 → RRT* 轨迹规划 → RL 轨迹跟踪 → 速度闭环控制 → 真机执行
```
- RL **不直接出操纵杆**。RL 出 N 维归一化目标速度 `action∈[-1,1]`(N、顺序取自 profile `action_order`)。
- 执行:`v_ref = action × max_speed`。仿真:`TargetSpeedController.Speed = v_ref`(线性,缸 m/s、swing rad/s)。真机:`v_ref → FF(v_ref) → 阀开度`,后续 `u = FF + Kp·e + Ki·∫e`。
- 已确认缩比机为**比例阀/速度控制**(固定指令缸匀速到末端),route-1 适用;阀非线性曲线只放真机 FF,仿真/策略保持线性物理速度单位。
- 模型**必然重训**(正常成本)。接口已为缩比模型**重新设计并锁定**(下面 §2),不沿用 Cat365 的 37 维。

**唯一的接口不变式**(不是"维度固定",而是):① 三处一致;② 任何观测真机也算得出;③ 改设计要整体改 + 重训 + 升 `observation_schema.id`。本文件已锁定 v2 设计,执行者**不再改设计**,除非人明确要求。

---

## 2. 锁定的接口设计(v2,`observation_schema.id = scale_excavator_v2_38d`)

### 2.1 动作 action(4 维)
- `action ∈ [-1,1]^4`,顺序 = profile `action_order` = `[boom, stick, bucket, swing]`。
- `v_ref[i] = action[i] × actuators[i].max_speed`。boom/stick/bucket 单位 m/s(max_speed=0.03),swing 单位 rad/s(max_speed=0.3,待标)。
- Unity:`OnActionReceived` 读 `a[0..3]` 按上序传 `controller.SetNormalizedSpeeds(boom,stick,bucket,swing)`。
- 维度公式(供换机型):`action_dim = len(action_order) = N`。

**方向/符号约定(可纯配置适配)**:每个执行器在 profile 有两个符号位:
- `sign`(仿真侧语义,默认 +1):定义"+action 对应哪个物理方向"。**训练前拟定并锁定**;改它=改训练参考系→需重训。
- `deploy_sign`(真机侧,默认 +1):真机接线若与仿真反向,**只改这一位即可对齐,不重训**。
仿真按 `sign` 绑定;真机最终命令符号 = `sign × deploy_sign`,观测的位/速也同此符号,保证闭环一致。左右镜像缸的物理反相在 Unity 执行器绑定层各自处理,不影响这里的语义符号。
→ **正方向现在自行拟定即可;以后真机发现反了,改 `deploy_sign` 配置就能适配。**

### 2.2 观测 observation(38 维,逐 index 锁定)
**参考系**:`base_frame` = 底盘固定系(**不随 swing 转**)。铲斗尖位姿 `tip = Rot(swing) · armFK(boom,stick,bucket)`。
Unity `CollectObservations` 与部署观测构造器**都必须严格按下表的 index 顺序与公式**输出。归一化常数全部取自 profile `observation_schema.normalizers`。

| idx | 名称 | 公式(归一化后约 [-1,1]) | 仿真来源 | 真机来源 |
|---|---|---|---|---|
| 0 | boom_pos | `map(pos, range.min, range.max)→[-1,1]` | Constraint.GetCurrentAngle | 拉线缸位移 |
| 1 | boom_vel | `clamp(vel / max_speed, -1, 1)` | 约束速度 | 拉线缸速 |
| 2 | stick_pos | 同上(stick range) | | |
| 3 | stick_vel | 同上 | | |
| 4 | bucket_pos | 同上(bucket range) | | |
| 5 | bucket_vel | 同上 | | |
| 6 | swing_sin | `sin(theta_swing)` | swing 角 | **swing 角传感器(R10)** |
| 7 | swing_cos | `cos(theta_swing)` | | |
| 8 | swing_vel | `clamp(omega / max_speed, -1, 1)` | swing 角速度 | **swing 角速度传感器** |
| 9 | tip_x | `tip_base.x / position_normalizer` | tip 变换@base系 | FK |
| 10 | tip_y | `tip_base.y / position_normalizer` | | |
| 11 | tip_z | `tip_base.z / position_normalizer` | | |
| 12 | tip_vx | `tip_vel_base.x / tip_velocity_scale` | tip 速度 | FK 微分 |
| 13 | tip_vy | `tip_vel_base.y / tip_velocity_scale` | | |
| 14 | tip_vz | `tip_vel_base.z / tip_velocity_scale` | | |
| 15 | wp0_err_x | `(wp[k].x - tip.x)/distance_normalizer` (k=当前+0) | 规划器+tip | 同 |
| 16 | wp0_err_y | | | |
| 17 | wp0_err_z | | | |
| 18 | wp1_err_x | k=当前+1 | | |
| 19 | wp1_err_y | | | |
| 20 | wp1_err_z | | | |
| 21 | wp2_err_x | k=当前+2 | | |
| 22 | wp2_err_y | | | |
| 23 | wp2_err_z | | | |
| 24 | progress | 已完成航点数 / 总航点数 ∈[0,1] | 规划器 | 同 |
| 25 | tube_signed | `clamp(signed_cross_track / tube_radius, -1, 1)` | | |
| 26 | isFinal | 当前目标是最后一个航点 ? 1 : 0 | | |
| 27 | mode_move | taskMode==MoveToDig ? 1 : 0 | 任务管理器 | 已知指令 |
| 28 | mode_carry | taskMode==CarryMaterial ? 1 : 0 | | |
| 29 | ep_progress | step / max_episode_steps ∈[0,1] | | 执行时长占比 |
| 30 | prev_boom | 上一步 action[0] | 策略缓存 | 策略缓存 |
| 31 | prev_stick | 上一步 action[1] | | |
| 32 | prev_bucket | 上一步 action[2] | | |
| 33 | prev_swing | 上一步 action[3] | | |
| 34 | pitch_cur | `pitch_deg / pitch_norm_deg` | FK 铲斗姿态 | FK |
| 35 | pitch_tgt | `pitch_target_deg / pitch_norm_deg` | 任务模式给定 | 同 |
| 36 | pitch_err | `(pitch_deg - pitch_target_deg)/pitch_norm_deg` | | |
| 37 | pitch_vel | `clamp(pitch_rate_deg_s / 180, -1, 1)` | | |

**合计 = 38。** 维度公式(换机型用):
`obs_dim = Σ_actuator(prismatic:2, revolute_continuous:3) + 6(tip) + 3·lookahead + 3(progress,tube,isFinal) + num_task_modes + 1(ep_progress) + N(prevAction) + (tool_attitude.enabled ? 4 : 0)`。
本机:`(2+2+2+3) + 6 + 9 + 3 + 2 + 1 + 4 + 4 = 38`。

**pitch 目标**:MoveToDig → `pitch_target_deg = -90`;CarryMaterial → `0`(取自当前实现,缩比几何如不符由人改 profile/任务参数,执行者不擅自改)。

### 2.3 真机可算性(落地约束已满足)
除拉线缸传感器外,本设计**仅新增一个真机硬件需求:swing 角度 + 角速度传感器**(idx 6/7/8;且 swing 速度环也需要)。其余所有 obs 均可由 拉线缸位移/缸速 + swing 角 + FK 得到。**无任何仿真专属全局真值**。

---

## 3. 锁定的奖励设计(训练用,仅 Unity)

每步奖励 = 下列各项之和(系数为**起始值,锁定先按此训练**;调参是显式任务 P1-T10b,执行者不擅自改):

| 项 | 公式 | 系数 |
|---|---|---|
| 进度 | `+ (progress_t - progress_{t-1})` | `× 5.0` |
| 跟踪 | `- ‖tip - 当前航点‖ / distance_normalizer` | `× 0.3` |
| 管道 | `- max(0, (|tube_signed|·tube_radius - tube_radius)/tube_radius)` | `× 0.5` |
| 姿态 | `- |pitch_deg - pitch_target_deg| / pitch_norm_deg` | `× 0.2` |
| 航点奖励 | 每到达一个航点 `+1.0`(一次性) | — |
| 完成奖励 | 路径全部完成 `+10.0`(一次性,终止) | — |
| 平滑 | `- mean((action - prevAction)^2)` | `× 0.05` |
| 能耗 | `- mean(action^2)` | `× 0.01` |
| 时间 | 每步 `-0.001` | — |
| 失败 | 超时/越界/卡死 `-5.0`(一次性,终止) | — |

"到达航点":`‖tip - wp‖ < target_threshold` 则推进当前目标 index(沿用 `TrajectoryProvider.AdvanceTarget`)。

---

## 4. 锁定的回合/终止设计

- **成功**:`progress==1.0` 且最后航点 `‖tip - wp_last‖ < target_threshold` → +10、终止。
- **失败-超时**:`step > max_episode_steps`(锁定 `1500`)→ -5、终止。
- **失败-越界**:tip 离可达空间最近点 `> 1.5 × distance_normalizer` 连续 `> 30` 步 → -5、终止。
- **失败-卡死**:最近 `200` 步 `progress` 增量 `< 0.01` → -5、终止。
- **复位**:`ExcavatorController.BeginHomePoseReset`(LockController 拉回 home;AGX 不能瞬移)。
- **decision period**:沿用现有 `DecisionRequester`;真机部署 10Hz,训练期保持现 period(数值不在本文件锁定范围,沿用工程现值)。

---

## 5. 单一来源:`shared/machine_profile.json`(已更新到 0.2.0)

Unity 与部署共读;机型常数只改此处。已含:`action_order`、四执行器(max_speed/range/ff_table/deploy_pca_channel)、`observation_schema`(id=scale_excavator_v2_38d、total_dim=38、normalizers)、`deploy`(ip/port/rate/deadzone/servo/pump/safety)。
**契约 vs 接线**:物理契约入 JSON;Unity 的 Constraint 引用、部署的 PCA 通道各自本地,用同一 actuator 名对齐。**部署到 ORIN 时 JSON 随包同步,勿手抄。**
归一化常数当前为 scale 临时值,P1-T4 标定可达空间后回写真实值。

---

## 6. 任务分解(纯执行,均引用 §2–§4 锁定设计)

卡片字段:`ID · 优先级 · 依赖 · 文件 · 动作 · 验收`。

### 地基
- **F-T0 单一来源 JSON** — ✅ 已完成(`shared/machine_profile.json` 0.2.0)。
- **F-T1 Unity 读取器** · P1 · 依赖 F-T0 · 新增 `Assets/Scripts/Config/MachineProfile.cs` + `MachineProfileLoader.cs`
  - 动作:数据类映射 JSON(`JsonUtility` 不支持 Dictionary → actuators 用数组+name 字段);Editor 读 `Path.Combine(Application.dataPath,"../../shared/machine_profile.json")`,Build 读 `StreamingAssets`(加 Editor 拷贝步骤);提供 `Load()` 与按名取执行器/归一化常数。
  - 验收:场景启动打印 action_order、三缸 max_speed=0.03、obs total_dim=38,与 JSON 一致。
- **F-T2 部署读取器** · P2 · 依赖 F-T0 · 新增 `deploy_scale_model/machine_profile.py`
  - 动作:`json.load("../shared/machine_profile.json")`;暴露 action_order、actuators(含 ff_table、pca 通道)、normalizers、deploy;`ff_lookup(actuator, v_ref)` 分段线性。
  - 验收:打印加载结果;`ff_lookup("stick",0.01)≈0.15`、`(…,0.03)≈1.0`。

### P1 — 仿真闭环
- **P1-T1 约束验证+启用 Range** · 依赖 F-T0 · `Assets/Scripts/Testing/ExcavatorActuatorPairTester.cs`、`Assets/Scenes/TestModel.unity`
  - 动作:Tester 绑四执行器,`driveSpeed≈0.03`,`logConstraintState=true`;在 TestModel 对每个受控约束启用有限 Range,数值取 JSON `range`(**必开 `Bucket_R`**)。
  - 验收:日志各约束 `rangeEnabled=true`、pos 在 range 内、四自由度走完全行程不爆限位。
- **P1-T2 maxSpeed 从 profile** · 依赖 F-T1 · `Assets/Scripts/Control/ExcavatorController.cs:48-51`
  - 动作:三缸 max_speed=0.03、swing=0.3,从 MachineProfile 读(过渡可 Inspector 填)。保持 `SetNormalizedSpeeds` 签名不变。
  - 验收:Heuristic 驱动缸速 ≤0.03 m/s,无撞限位/拖拽。
- **P1-T3 归一化校验** · 依赖 P1-T1,T2 · `ExcavatorController.cs:137,240,257`
  - 动作:Heuristic 全行程打印 `GetNormalizedPosition`。
  - 验收:各受控约束归一化位置 ∈[-1,1],无 NaN/Infinity。
- **P1-T4 可达空间重标定** · 依赖 P1-T1 · `Assets/Scripts/Planning/ReachableWorkspaceVisualizer.cs:1494-1547`
  - 动作:核对 `workspaceFrame`;右键 `Create Editable Anchor Objects`→拖到缩比实际可达点→`Bake`;Move/Carry 各一套;把测得的最大可达距离回写 JSON `position_normalizer`、`distance_normalizer`。
  - 验收:蓝色可达空间包络与缩比实际范围吻合;`SampleWorld`/`ClosestWorldParameters` 往返一致。
- **P1-T5 RRT* 魔数缩放** · 依赖 P1-T4 · `Assets/Scripts/Planning/SimpleTrajectoryPlanner.cs`(Plan≈:302)
  - 动作:`rrtStepSize/GoalTolerance/RewireRadius/CollisionRadius/bounds/arcHeight` 按缩比因子等比缩小;`plannerAlgorithm=RrtStar`。
  - 验收:点选起终点后 waypoint 落在蓝色可达区、不吸附直线、不越界、间距合理。
- **P1-T6 实现 38 维观测(按 §2.2)** · 依赖 F-T1,P1-T4 · `Assets/Scripts/Agents/ExcavatorTrajectoryAgent.cs:338`(CollectObservations)、`config/trajectory_ppo.yaml`
  - 动作:**严格按 §2.2 index 表重写 CollectObservations**(38 项,顺序/公式/归一化常数照表,常数从 profile 读);把 `trajectory_ppo.yaml` 的 obs size 改 **38**、注释更新;确认 action=4 不变。
  - 验收:Heuristic 打印 38 维,逐项量级在 [-1,1] 附近、无≈0 塌缩、无 NaN;yaml 与代码维度一致。
- **P1-T7 bucket pitch frame 重绑** · 依赖 P1-T1 · `ExcavatorTrajectoryAgent.cs:784`
  - 动作:`bucketPitchFrame` 指向缩比模型**随 swing 转的上车** frame(非底盘、非 CAT365);喂 obs idx 34-37。
  - 验收:swing-only 不改 pitch_cur;bucket-only 显著改 pitch_cur。
- **P1-T8 实现奖励+终止(按 §3/§4)** · 依赖 P1-T6 · `ExcavatorTrajectoryAgent.cs`(OnActionReceived/AddReward、回合逻辑)
  - 动作:按 §3 各项与系数实现奖励;按 §4 实现成功/三类失败/复位。
  - 验收:逐项奖励可在日志看到合理量级;构造越界/超时/卡死能触发对应失败并复位。
- **P1-T9 TaskManager 加 Failed(UI 层)** · 依赖 P1-T8 · `Assets/Scripts/Planning/ExcavationDemoTaskManager.cs`(enum、`Update:103-106`)
  - 动作:`enum DemoState` 加 `Failed`;失败时停控制+UI 提示+可 Reset(与 agent 回合终止对应)。
  - 验收:正常→Completed;人为超时/越界→Failed,安全停、可重来。
- **P1-T10a 缩比场景重训** · 依赖 P1-T2..T8 · `config/trajectory_ppo.yaml`(obs=38)、`tools/mlagents_train_no_onnx.py`
  - 动作:`mlagents_train_no_onnx.py config/trajectory_ppo.yaml --run-id=scale_v2_01`;`mlagents_export_legacy_onnx.py ... --resume` 导出。
  - 验收:训练中 tracking/进度 reward 上升;导出 ONNX 能跟住固定 waypoint。
- **P1-T10b 奖励/超参调优(显式授权才做)** · 依赖 P1-T10a
  - 动作:仅当 P1-T10a 收敛不良时,按人指示调 §3 系数或 yaml 超参;**不擅自改**。
  - 验收:跟踪成功率提升,记录改了哪些值。
- **P1-T11 仿真闭环联调** · 依赖 P1-T1..T10
  - 动作:跑通"点选起终点→生成轨迹(先固定 waypoint 再 RRT*)→RL 跟踪→成功/失败"。
  - 验收:**一条轨迹稳定走完进 Completed;失败用例进 Failed。P1 完成。**

### P2 — 落地实物(骨架先行,标定延后)
- **P2-T1 修动作顺序(R1)** · 依赖 F-T2 · `deploy_scale_model/rollout_action_send.py`
  - 动作:删硬编码 `ACTION_ORDER`,改读 profile `action_order`;按 `deploy_pca_channel` 分发。
  - 验收:单通道 dry-run:`[1,0,0,0]` 只 boom 动,依次验四通道。
- **P2-T2 FF 前馈层** · 依赖 F-T2,P2-T1 · `deploy_scale_model/excavator.py`
  - 动作:输入改 `v_ref`(m/s);`axis=ff_lookup(act,v_ref)`→`servo=axis*45+90`;预留 `u=FF+Kp·e+Ki·∫e`(Kp=Ki=0)。
  - 验收:给定 v_ref 产出 servo 角与 ff_table 一致;dry-run 速度量级大致对。
- **P2-T3 传感器+状态估计** · 依赖 硬件记录脚本 · 新增 `deploy_scale_model/state_estimator.py`
  - 动作:读拉线(缸位移/缸速)**+ swing 角/角速度(R10 需硬件支持)**→组装 machine_state(对齐 06-24 handoff §2)。
  - 验收:50–100Hz 输出缸位移/缸速/swing 角与角速度。
- **P2-T4 前向运动学 FK** · 依赖 P2-T3 · 新增 `deploy_scale_model/kinematics.py`
  - 动作:`缸位移+swing角 → 关节角 → 铲斗尖位姿@base系 + bucket pitch`(喂 §2.2 obs)。
  - 验收:同姿态真机 FK tip 位姿/pitch 与 Unity 仿真误差在容差内。
- **P2-T5 部署观测构造器(按 §2.2)** · 依赖 P2-T4 · 新增 `deploy_scale_model/observation.py`
  - 动作:**严格按 §2.2 同一 38-index 表**构造 obs(与 Unity 逐项一致),供真机推理。
  - 验收:对同一 machine_state,与 Unity 对拍的 obs 各 index 量级一致。
- **P2-T6 安全层** · 依赖 P2-T1,T2 · `excavator.py`+新增 `safety.py`
  - 动作:clamp/死区/限速/anti-windup/限位/通信超时归零(`comms_timeout_to_zero_s`)/急停。
  - 验收:断通信→超时内归零;急停→立停;超限被 clamp。
- **P2-T7 真机标定执行(§7 协议)** · 依赖 硬件记录脚本,P2-T3 · 产出回写 JSON
  - 动作:按 §7 采集→拟合 per-joint/per-direction `ff_table`、真实 max_speed/range、swing 映射;`calibrated=true`。
  - 验收:每缸独立 ff 曲线;0–10mm/s 低速补点;swing 角速度映射建立。
- **P2-T8 PI 整定** · 依赖 P2-T7,P2-T3 · `excavator.py`
  - 动作:用阶跃响应整定 Kp/Ki,按需姿态调度 FF。
  - 验收:负载/姿态变化下缸速跟踪误差较开环明显下降。

### P3 — 工业化(延后,不在本轮执行)
- **P3-T1** 残余硬编码继续收敛进 profile。
- **P3-T2** 抽 `IActuatorBus/IObservationBuilder/IVelocityController`,现有类适配包裹,回归门:挖掘机 obs 逐元素一致。
- **P3-T3** 拆 asmdef:`Machine.Core`(禁 AGX)/`Physics.Agx`/`Planning`/`RL`/`Deploy`,边界编译期强制。
- **P3-T4** 接第二机型(铲车/推土机),**先固定底盘只验证工具动作**(§2 维度公式与工具姿态开关已为此预留)。

---

## 7. 真机标定协议(P2-T7 依赖)

**实验**:每缸单独,固定指令→匀速段→量速度;指令小到大、正反两向扫。
**采集字段**(CSV/JSONL,传感器 50–100Hz):`t`(单调时间)、`actuator`、`cmd_axis`、`servo_angle`、`pump_cmd`、`cyl_disp_raw`、`cyl_disp_mm`、`direction`、`pose_tag`、`load`、`oil_temp`、`supply_v`、`pressure`(若有)、`run_id`。swing 另记 `swing_angle`、`swing_omega`。
**控制变量**:①一次一缸;②泵速固定/精确记录(优先恒速);③正反各扫;④多姿态×负载(boom 抬/落、arm 伸/收、bucket 空/载);⑤每工况重复 2–3 次;⑥高频记位移、离线求导。
**指令点位**:`axis=±0.05,0.10,0.15,0.2,0.3,…,1.0`,正反各一(中位附近密,找死区)。
**前置标定**:拉线 原始→mm(已知位置静标);记各缸全伸/全缩端点→真机 range。
**第二阶段(PI 用)**:阶跃响应记速度上升延迟/时间常数。
**回流**:per-joint/per-direction `ff_table`、真实 max_speed/range、低速补点、swing 映射 → 回写 `shared/machine_profile.json`,**结构不变,只换数值**。

---

## 8. 风险登记

| ID | 风险 | 验证 | 处理任务 |
|---|---|---|---|
| R1 | 动作顺序 Unity↔部署不一致 | 单通道 dry-run | P2-T1(+JSON 单一来源根除) |
| R2 | 观测量级塌缩 | Heuristic 打印 38 维量级 | §2.2 profile 归一化(从构造上消除)+ P1-T6 |
| R3 | maxSpeed 过大 | 目视 + rangeForce 饱和 | P1-T2(实测 0.03) |
| R4 | Range 未启用→量纲/NaN | logConstraintState + 归一化∈[-1,1] | P1-T1 |
| R5 | 可达空间锚点 Cat365 | 轨迹落蓝色区/workspaceFrame | P1-T4 |
| R6 | RRT* 魔数米级 | 轨迹吸附/越界 | P1-T5 |
| R7 | pitch frame 绑 CAT365 | swing/bucket 单动对 pitch 影响 | P1-T7 |
| R8 | 无失败状态 | 人为超时/越界 | P1-T8/T9 |
| R9 | 真机无速度反馈/标定 | FK 对拍、标定数据 | P2-T3/T4/T7 |
| R10 | swing 角/角速度传感器 | ✅ 已确认真机有 swing 角度传感器,可读实时角度+角速度 | obs idx 6/7/8 真机可算;P2-T3 接入 |
| H1 | 执行器=比例阀速度控制 | 固定指令匀速到末端 ✅ 已确认 | route-1 适用 |

---

## 9. 术语
- **action**:N 维∈[-1,1],顺序 `action_order`;`v_ref=action×max_speed`。
- **base_frame**:底盘固定系,不随 swing 转;`tip=Rot(swing)·armFK`。
- **machine_state**:body 位姿、swing 角/角速度、关节角、缸位/缸速、tip 位姿/速度。
- **观测契约 id**:`scale_excavator_v2_38d`;改 obs 必升此 id。
- **单一来源**:`shared/machine_profile.json`。

---

## 10. 执行顺序(依赖拓扑)
```
F-T0(✅) → F-T1 ─┐
P1-T1 ───────────┼→ P1-T2 → P1-T3
P1-T4 ───────────┘        └→ P1-T5
F-T1,P1-T4 → P1-T6 → P1-T7 → P1-T8 → P1-T9 → P1-T10a →(必要时)P1-T10b → P1-T11  ← P1 完成
F-T2 → P2-T1 → P2-T2 → P2-T6        （部署骨架,可与 P1 并行）
P2-T3(需硬件+R10) → P2-T4 → P2-T5
P2-T3 → P2-T7（需记录脚本）→ P2-T8   ← 真机标定回来后
P3-T1..T4：P1/P2 稳定后启动
```
**立即三件**:P1-T1(开 Range)、P1-T2(maxSpeed=0.03)、P2-T1(R1+读 JSON)。
**已确认**:swing 角/角速度传感器(R10 解决)。**自行拟定、后续纯配置适配**:执行器正方向——训练前用 profile `sign` 锁定;真机若反向只改 `deploy_sign`,不重训(见 §2.1 符号约定)。
```
