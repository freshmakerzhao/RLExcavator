# RLExcavator — 挖掘机 RL 挖土 Demo 规划

> 目标：让 CAT365 挖掘机通过强化学习（ML-Agents + AGX Dynamics 可变形地形）学会把铲斗插入土中、挖起一斗土并抬起。
> 范围（第一版 demo）：固定底盘 + 回转锁定，只训练 大臂(boom) + 小臂(stick) + 铲斗(bucket) 3 关节，连续控制。

---

## 0. 前置：导入 CAT365 模型并跑通物理（不涉及 RL）

CAT365 不在项目里，需从 AGX 安装目录导入（通常在 AGX/data/models/ 或 agxTerrain 教程示例，常见 .agx / OpenPLX 场景）。

- [ ] 找到 AGX 自带 CAT365 模型文件，导入到 Assets/Models/CAT365/
- [ ] 新建场景 Assets/Scenes/DigTraining.unity
- [ ] 放入 DeformableTerrain（用已有 New Terrain.asset 或新建），地形材质选 dirt_1 / soil_loose（已在 Plugins/x86_64/agx/data/TerrainMaterials/）
- [ ] 给铲斗加 DeformableTerrainShovel 组件，配置 TopEdge / CuttingEdge / ToothDirection，并把 shovel 注册到 terrain
- [ ] 手动验证：临时脚本驱动 3 个 Hinge motor，确认能插土、挖起土、铲斗里有土颗粒、地形会凹陷

验收标准：手动操作能挖起一斗土，帧率可接受（先关闭多余渲染压力，Fixed Timestep 0.01~0.02）。

---

## 1. 关节驱动接口（Actuation）

CAT365 每个关节是 AGX Constraint（Hinge）。连续控制用各关节的 Motor（TargetSpeedController）：

- 每个关节：hinge.GetController<TargetSpeedController>()
  - Speed = action * maxJointSpeed[i]，确保 Enable = true、ForceRange 足够大
- 封装 ExcavatorController.cs：持有 boom/stick/bucket 三个 Constraint 引用，提供：
  - SetNormalizedTargets(float[] a) —— a in [-1,1] 映射到目标角速度
  - GetJointStates() —— 返回每个关节 角度(归一化)、角速度
  - ResetToHomePose() —— episode 重置时摆回初始姿态

备选：力矩控制更接近真实液压，但目标速度控制更稳、更易上手，第一版用目标速度。

---

## 2. RL 设计（核心）

继承 Unity.MLAgents.Agent，复用 Turtle Agent 骨架，改成连续动作。

### 2.1 动作空间（Continuous, size = 3）
- a0 -> boom 目标角速度 = a0 * maxBoomSpeed
- a1 -> stick 目标角速度 = a1 * maxStickSpeed
- a2 -> bucket 目标角速度 = a2 * maxBucketSpeed
Behavior Parameters：Continuous Actions = 3，Discrete = 0。

### 2.2 观测空间（VectorSensor，约 13–16 维，归一化到 ~[-1,1]）
- 3x 关节角度（归一化）
- 3x 关节角速度（归一化）
- 3x 铲斗尖(cutting edge)相对目标挖掘点位置 (x,y,z)
- 1x 铲斗装载率 dead-load fraction（shovel.Native.getDeadLoadFraction 之类，导入后核对确切 API 名）
- 1x 铲斗尖埋深（低于地表深度）
- 1x episode 进度 step/maxStep
- (可选) 3x 铲斗朝向
第一版不要用图像/射线观测，纯状态向量训练最快。

### 2.3 奖励设计（最关键）
密集塑形，鼓励 插入->装载->抬起：
- 装载主奖励：+w1 * Δ(dead-load fraction) 每步装载率增量（核心信号）
- 接近奖励：铲斗尖未入土时 +w2 * 靠近目标挖掘点的距离缩减
- 抬升奖励：装载率超阈值后，铲斗抬高于地表 -> +w3 * 抬升高度
- 成功奖励：装载率 >= 目标(如0.6) 且铲斗抬到安全高度 -> +1.0，EndEpisode()
- 惩罚：每步时间惩罚 -1/MaxStep；抖动惩罚 -w4 * Σ|action|；超限/翻车/异常接触力 -> 小负奖励，必要时提前结束
- 权重起点：w1 大(1.0~3.0/单位装载率)、w2 小、w4 很小(0.001 量级)，先跑通再调。

### 2.4 Episode 逻辑
- OnEpisodeBegin：ResetToHomePose()；terrain.ResetHeights() 复原地形；随机化挖掘目标点（类似 Turtle SpawnObjects）
- MaxStep：先设 2000~4000（连续控制需要更多步）
- 结束：成功 / 超时 / 异常（翻车、关节卡死）

### 2.5 Heuristic（必做）
键盘映射 3 个关节，先确认 动作/观测/奖励 闭环正确再开训练。连续控制最容易在这里踩坑。

---

## 3. 训练配置（ML-Agents）

config/excavator_ppo.yaml：
- trainer PPO（先 PPO 稳；学不动再试 SAC）
- batch_size 2048、buffer_size 20480、learning_rate 3e-4、beta 5e-3
- hidden_units 256、num_layers 2、max_steps 先 2e6
- time_horizon 64~128，normalize: true
- 多环境并行：复制 6~12 个挖掘机+地形实例提速；可变形地形开销大，先 1 个跑通再复制
- 命令：mlagents-learn config/excavator_ppo.yaml --run-id=dig01
- TensorBoard 看 Environment/Cumulative Reward 是否上升

---

## 4. 里程碑

- M0 物理跑通：导入CAT365 + 地形 + shovel，手动能挖土 -> 手动挖起一斗土
- M1 控制封装：ExcavatorController.cs 三关节目标速度控制 -> Heuristic 键盘能完成一次挖掘
- M2 RL接入：ExcavatorDigAgent.cs（观测+动作+奖励+episode）-> Heuristic 下读数正确
- M3 单环境训练：PPO 配置，1 个环境 -> 奖励曲线上升、能学会插土
- M4 调优+并行：奖励权重调优、多环境并行 -> 稳定学会挖起一斗并抬起
- M5 推理验证：导出 .onnx，挂回 Behavior 推理 -> 模型能自动挖土

---

## 5. 主要风险 / 坑

1. 可变形地形性能：很吃 CPU，多环境并行前先优化单环境（粒子上限、terrain 分辨率、Fixed Timestep）。
2. 奖励稀疏：纯成功奖励几乎学不会，必须用 2.3 密集塑形（装载率增量是关键）。
3. dead-load fraction API 名：导入后到 agxTerrain.Shovel 核对确切方法名。
4. 连续控制不稳：normalize: true、动作幅度别太大、加小抖动惩罚。
5. 地形重置成本：ResetHeights() 每 episode 调用可能慢，可只重置挖掘区域局部高度。
6. 物理步对齐：关节控制和奖励统计放在 OnActionReceived（与物理 fixed step 对齐），不要用 Update 的 deltaTime 积分物理量。

---

## 6. 复用 Turtle Agent 的部分

- Agent 生命周期骨架（Initialize / OnEpisodeBegin / CollectObservations / OnActionReceived / Heuristic / reward + EndEpisode）直接照搬
- episode 随机化目标点思路（SpawnObjects -> 随机挖掘点）
- 时间惩罚 -x/MaxStep 鼓励尽快完成
- 主要差异：离散->连续动作、简单碰撞奖励->基于装载率的密集塑形、静态地面->可变形地形 + shovel
