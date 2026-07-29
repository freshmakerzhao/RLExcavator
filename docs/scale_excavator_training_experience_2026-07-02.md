# 缩比挖掘机从导入模型到成功训练的经验复盘

日期：2026-07-02  
项目：Unity + AGX Dynamics + ML-Agents 缩比挖掘机轨迹跟踪  
当前成功 run：`scale_v2_long_05_speed06_swing06`

本文不是替代执行手册。唯一权威仍是：

- `docs/rl_excavator_master_execution_plan_2026-06-25.md`
- `../shared/machine_profile.json`

本文记录的是这次从 Cat365 迁移到缩比模型过程中真正踩过的坑、验证顺序、工程经验和下一次接新机型时可复用的流程。

---

## 1. 最终结果先记下来

当前训练还未完全结束，但已经在 `18M+` steps 后稳定成功。

训练命令：

```bash
python tools/mlagents_train_no_onnx.py config/trajectory_scale_long.yaml --run-id=scale_v2_long_05_speed06_swing06 --force
```

关键训练结果：

| 指标 | 当前水平 |
| --- | --- |
| SuccessRate | 约 1.0 |
| WaypointReachRate | 约 1.0 |
| WaypointTimeoutRate | 约 0 |
| PathProgress | 约 1.0 |
| FinalDistance | 约 0.0635 m |
| BucketPitchErrorDeg | 约 13 deg |
| Episode Length | 约 47 |
| Mean Reward | 约 1-3 |

学习里程碑：

| 事件 | 大致 step |
| --- | --- |
| SuccessRate >= 0.2 | 9.58M |
| SuccessRate >= 0.5 | 10.29M |
| SuccessRate >= 0.8 | 10.65M |
| SuccessRate >= 0.95 | 10.90M |
| SuccessRate = 1.0 | 10.95M |

经验：mean reward 只有 1 左右不是失败。当前 reward 标尺和旧 Cat365 不同，持续 tracking/pitch/action/time penalty 会抵消 waypoint 和 complete bonus。应优先看成功率、路径进度、timeout、最终距离和姿态误差。

---

## 2. 这次迁移真正分成了哪些阶段

### 阶段 A：先让缩比模型在 TestModel 中可控

一开始不要急着进训练场景。先在 `TestModel` 中完成：

1. 绑定液压缸和 swing 约束。
2. 用键盘/手动控制确认 boom、stick、bucket、swing 都能动。
3. 确认 RangeController 能限制行程。
4. 确认 TargetSpeedController 线性速度控制能工作。
5. 清理临时 PairTest 一类脚本，避免多个脚本同时写控制器。

当时暴露过一个编译问题：

```text
Exception is an ambiguous reference between AGXUnity.Exception and System.Exception
```

经验：AGX 项目里 C# 要避免裸写 `Exception`，需要明确 `System.Exception`，否则 Unity 直接进不了 Play。

### 阶段 B：控制器归一到 profile

关键原则：所有机型常数只放在：

```text
/home/zhaoshuai/workspace_uinty/RL_prj/shared/machine_profile.json
```

这次 profile 里已经统一了：

- `action_order`
- 每个 actuator 的 `max_speed`
- 每个 actuator 的 `range`
- `sign`
- observation normalizers
- bucket pitch task targets
- reachable workspace 文件路径

当前训练成功使用的速度是：

| actuator | max_speed |
| --- | --- |
| boom | 0.06 m/s |
| stick | 0.06 m/s |
| bucket | 0.06 m/s |
| swing | 0.6 rad/s |

注意：这是仿真训练成功参数，不等于真机标定参数。真机部署前必须重新按硬件实测速度和 FF/PI 标定处理。

### 阶段 C：建立统一坐标系

这次坐标系问题非常关键。

最终原则：

```text
所有算法数据只认 MachineRoot。
```

也就是说：

- 可达区域点：保存为 `MachineRoot` 局部坐标。
- bucket tip：用 `MachineRoot.InverseTransformPoint(bucketTip.position)`。
- 训练场景中移动环境：移动 `ScaleTrajectoryEnvironment_xx`。
- 不单独移动 `ExcavatorScaleDown` 内部模型。
- 不让 Cat365 原始 transform 或旧 prefab transform 混进算法坐标。

经验：如果在演示场景采集可达区域时，模型根节点有额外旋转/偏移，而训练场景又不一致，可达区域会视觉错位。根本解决办法不是到处补 offset，而是统一 `MachineRoot`。

### 阶段 D：可达区域从手动采样回归到文件

我们尝试过“运行中自动采样可达区域”，后来放弃，回到手动 20 点标定方式。

最后沉淀为：

- 可达区域跟机型绑定。
- 一个机型可以有多个 task mode 的可达区域。
- 可达区域应保存到 JSON 文件。
- 演示场景和训练场景都应读取同一份文件。

经验：辅助工具可以做得方便，但不要让运行时自动采样逻辑混进最终训练链路。明确不用的功能要彻底移除，避免屎山。

### 阶段 E：把演示环境和训练环境拆清楚

演示环境需要：

- 手动控制
- 自由镜头
- 可达区域可视化
- 关键点/边界点编辑
- 轨迹规划/验证 UI

训练环境需要：

- 多个 `ScaleTrajectoryEnvironment`
- 尽量少 UI
- 可达区域可选择隐藏
- policy 训练组件
- 统一 prefab 结构

经验：演示场景和训练场景可以共用 prefab、profile、workspace 文件，但不应该共用所有 UI/编辑脚本。训练场景越干净越好。

### 阶段 F：Agent 改成 38 维观测 + profile normalizers

`ExcavatorTrajectoryAgent.CollectObservations` 已改成手册锁定的 38 维结构。

必须保持一致的三处：

1. Unity `CollectObservations`
2. ML-Agents config observation size
3. 部署侧 observation builder

经验：观测维度不是“能跑就行”，必须逐 index 对齐。以后新增机型时，只能换 profile 和坐标/传感器适配，不能随意改观测协议。

### 阶段 G：奖励和终止按任务重写

已按轨迹跟踪任务重写：

- waypoint reach
- path complete
- progress reward
- tracking penalty
- tube penalty
- pitch penalty
- smooth / energy / time penalty
- timeout / waypoint timeout / out of bounds / stuck

训练早期曾经大量 reset，主要原因不是隐藏控制力，而是：

```text
waypoint_timeout
```

运行时检查确认：

- `IsHoming=False`
- `LockController.Enable=False`
- `commandInput=True`
- `TargetSpeedController` 有非零速度

经验：看到挖机周期性回 home，不一定是“有力拉回”。如果 episode 因 timeout 结束，那么下一局开头 home reset 是正常现象。

### 阶段 H：几个关键 bug / 误判

#### 1. BucketTip 触发问题

一度怀疑 BucketTip 没有 sphere collider / isTrigger 导致 waypoint 不触发。后来确认当前 waypoint 判定主要是距离逻辑，不依赖 trigger。

但给 BucketTip 加 collider 仍然有价值：后面如果做物理触发、可视化拾取或碰撞检测，会更直观。

#### 2. waypoint reach threshold 太小

修过一次逻辑：目标可视化小球半径也应计入 reach threshold。

经验：如果视觉上碰到了 waypoint 但代码没触发，先查 reach threshold 和可视化半径是否一致。

#### 3. MeanActionMagnitude 解释错误

当前统计：

```csharp
abs(boom) + abs(stick) + abs(bucket) + abs(swing)
```

最大值是 4，不是 1。  
所以 `MeanActionMagnitude = 1.0` 实际每轴平均只有约 0.25。

经验：TensorBoard 指标命名要精准。后面应补 `MeanActionAbsPerAxis`。

#### 4. 0.03 m/s 速度训练效果差

最初 boom/stick/bucket 用 `0.03 m/s`，swing 用 `0.3 rad/s`。训练表现为：

- waypoint 几乎追不上
- timeout 高
- 距离有下降但不稳定

把速度实验性提高到：

```text
boom/stick/bucket = 0.06 m/s
swing = 0.6 rad/s
```

后训练成功。

经验：缩比模型单位是米，但模型运动范围很小，速度尺度对 RL 探索非常敏感。速度过小会让策略在有限 episode 内看不到足够有效反馈。

---

## 3. 下次接新机型建议按这个顺序做

### 3.1 不要一上来训练

先完成：

1. 模型导入。
2. 层级整理。
3. MachineRoot / Frames。
4. actuator 约束绑定。
5. 手动控制。
6. Range 验证。
7. BucketTip / bucket pitch frame 验证。
8. Telemetry observer 验证。

只有手动控制完全可信后，再进入训练。

### 3.2 profile 先行

新机型第一件事不是改代码，而是补 profile：

- `action_order`
- `actuators`
- `max_speed`
- `range`
- `sign`
- `normalizers`
- task pitch targets
- workspace file

Unity 和部署都只读 profile。

### 3.3 可达区域必须和 MachineRoot 对齐

采样时要确认：

- 挖机内部模型没有额外局部 transform override。
- workspace 参考系是 MachineRoot。
- 训练场景和演示场景读同一份 workspace 文件。

### 3.4 训练前做 smoke test

训练前至少确认：

- Unity 能进 Play。
- 没有红色 Console error。
- 4 个 actuator 都能写速度。
- waypoint 能触发。
- episode reset reason 可见。
- TensorBoard 有 SuccessRate / ReachRate / TimeoutRate / FinalDistance。

### 3.5 长训前先判断失败模式

如果训练失败，不要先调 reward。优先分辨：

| 现象 | 优先检查 |
| --- | --- |
| 一直不动 | commandInput、DecisionRequester、BehaviorType、TargetSpeedController |
| 周期性回 home | reset reason 是否 timeout/stuck/out_of_bounds |
| 视觉碰到 waypoint 但不触发 | reach threshold、target visual radius、坐标系 |
| 轨迹在蓝色区域外 | workspace frame / MachineRoot |
| 动作顺序怪 | profile action_order、部署 action_order |
| pitch 永远不对 | bucketAttitudeReference、bucketPitchFrame、target pitch |

---

## 4. 当前后续计划

### P1：仿真闭环

1. 等当前训练自然跑完 20M。
2. 导出 ONNX：

   ```bash
   python tools/mlagents_export_legacy_onnx.py config/trajectory_scale_long.yaml --run-id=scale_v2_long_05_speed06_swing06 --resume
   ```

3. 在 Unity 中做推理验证：
   - MoveToDig
   - CarryMaterial
   - 多 seed
   - 多起点/目标点
   - 可视化轨迹完成情况
   - bucket pitch 统计

4. 补训练诊断指标：
   - per-axis action magnitude
   - joint min/max per episode
   - homing step count
   - reset reason count

5. 确认是否冻结为 P1-T10a 成果。

### P2：真机部署准备

在硬件数据未就绪前，只做接口和骨架，不造假数据：

- 部署侧 profile loader
- action order dry run
- FF/PI 接口
- state estimator 接口
- FK 接口
- 38 维 observation builder
- safety layer

真机速度、FF 表、PI 参数必须等标定。

### P3：扩展到多机型

P1/P2 稳定后再做：

- profile 剩余硬编码收敛
- MachineRoot / FrameRegistry 规范化
- prefab 层级模板化
- Unity adapter / deploy adapter 分层
- 第二机型接入验证

---

## 5. 给未来接手者的 handoff

当前不要再从“缩比模型为什么不动”开始排查。那一轮已经解决。

下一步最应该做的是：

1. 等 `scale_v2_long_05_speed06_swing06` 到 20M。
2. 导出 ONNX。
3. 做 Unity inference validation。
4. 把成功策略和当前 profile 作为仿真候选冻结。
5. 开始 P1-T11 仿真闭环联调。

仍需牢记：

- 当前速度是仿真实验值，不是实物标定值。
- reward 数值不能和旧 Cat365 直接比。
- 训练成功不等于部署可用。
- 任何常数改动必须进 `shared/machine_profile.json`。
- Unity 编译结果只相信 Unity Console。
