# ExcavatorRuntime 仿真 v1

`Assets/Scenes/ExcavatorRuntime.unity` 是独立的 AGX 挖掘机运行场景；训练场景和缩比 prefab 的油缸方向也已统一为伸长为正。本版提供 `reset`、四轴物理速度 `velocity_reference`、四轴归一化杆量 `manual_action` 和显式物理步进 `step`，返回执行器状态及铲斗尖端相对 `MachineRoot` 的位姿。场景支持 AGX 土体交互，是主项目 Follow/ACT 的仿真后端；策略推理与任务编排留在主项目。

场地位于 `Environment/RealSite`，从 `real2sim-scene-20260807` 的 `Real2Sim_dig` 迁入平台、土箱、土表和材质，资产独立存放于 `Assets/Environment/Real2Sim`。平台高 .533 m，挖机整体相应抬高；路点仍相对 MachineRoot。平台与箱壁有 AGX 碰撞体，MovableTerrain与粒子渲染已启用。ReferenceCamera保留原场景固定参考机位，默认禁用；FrontCamera负责仿真 ACT 的采集与推理图像，不要求复用真机检查点。

`Environment/RealSite/DumpTruck` 是固定卸土目标，来自官方 `com.algoryx.agxunity.machines.bedtruck@1.1.1`，所需模型与材质直接引用固定版本的官方 Unity Package；`Assets/AGXUnity_BedTruck` 中导入的道路、地形和驾驶示例仅保留在本机，不纳入当前提交。当前场景独立复制其外观和 9 个箱形碰撞体，不挂载官方驾驶、轮胎、翻斗控制器或车斗 MovableTerrain；原 prefab 不作修改。模型尺寸统一乘以 0.15，整车约长 1.17 m、宽 0.44 m、高 0.45 m，车斗内底约高 0.21 m。`Visual` 放外观，`Collision` 放静态 AGX 碰撞体，`DumpTarget` 是车斗上方的参考点（Unity 世界坐标约 `[0.70, 0.525, -0.12]`），尚未接入任务配置。比例和摆放依据现场照片估计，未作实物标定。调整位置时移动整个 DumpTruck；不要直接缩放根节点，AGX Box 尺寸需随模型显式调整。2026-09-28 按用户提供的现实场地俯视示意图，将卡车整体移到挖机的 Unity +X 一侧：面向沙土池时位于挖机右侧，车斗朝向土池、车头朝外（-Z）。车体与工作台侧边约间隔 0.23 m，车尾与土池外壁约间隔 0.10 m；仅按相对关系摆放，未作尺寸标定。

![现实场地相对布局俯视图](images/2026-09-28-real-site-layout-top.png)

`ExcavatorRuntimeSoil.cs` 负责土体生命周期：Play时隐藏原SoilTerrain作为模板，创建独立网格的运行实例。`reset` 先等待挖机回位，再销毁旧土体、等待一帧并创建新土体，清除形变、压实状态与散粒；完成后桥接才回复成功。沿用旧ResetTerrain的复位顺序，但不依赖旧Agent、UI或控制器，也不修改AGX代码。土体参数尚未按实物标定，当前验证的是运动和土体交互，不代表真实挖掘力或装载效率。

为保持遥操作流畅，本场景的 `SoilTerrain` 使用独立的 `Assets/Environment/RuntimeSoilSurface.asset`，当前分辨率为 67×33 格、单元边长约 3.47 cm（此前为 52×26 格、约 4.47 cm）。土箱摆放和尺寸不变，土体模板目标尺寸仍为 2.325×1.125 m；AGX 使用正方形网格，运行时第二边长度会按整格取整。原 `Assets/Environment/Real2Sim/SoilSurface.asset` 未改动。要调整性能，在 Unity 中选择 `SoilTerrain`，修改 `Movable Terrain` 的 `Resolution`：数值越小，颗粒越粗、物理计算通常越少，但挖土细节也会减少。不要直接改共享的 AGX 资产。

细化后的性能设置只作用于本场景：`Simulation` 使用独立的 `Assets/Environment/RuntimeSolverSettings.asset`，普通 Resting Iterations 从 50 调整为 16，土粒 PPGS Resting Iterations 保持 18。`Body_Bucket/NONE_Trimesh (16)` 使用 AGX 预计算的简化三角碰撞网格（34,190 → 13,814 个三角形），数据随场景保存；视觉模型、铲斗固定质量和惯量不变。保留 Trimesh 凹形结构，不改为会填满斗腔的凸包。该设置用于流程验证，未标定真实土体受力；不要直接修改原模型或共享求解器资产。

普通 `step` 请求由桥接在 Unity 主线程的 `Update` 中连续计算，不再为每个物理步额外等待一次 `FixedUpdate`。每帧计算达到 50 ms 后，在物理步之间让出主线程；单步本身不能被抢占。物理步长仍约 0.02 s，每一步都检查命令到期并同步状态。`reset` 保留原有 FixedUpdate 回正时序。显式步进可快于墙钟时间，遥操作的 20 Hz 节拍仍由主项目负责；这不改变动作协议或杆量转换。

手工检查：Unity按Play后，在终端 `conda activate earthmoving-autonomy`，从主项目运行 `python -m earthmoving_autonomy teleop`，按住使能键挖掘；Ctrl+C退出后，在工作区根目录执行以下命令复位，然后重新进入遥操作。每次启动遥操作也会自动reset。

```bash
echo '{"kind":"reset"}' | python3 RLExcavator/tools/sim_bridge_client.py
```

2026-09-27土体验证（当时为 155×75 格细网格）：旧reset保留5厘米测试凹陷，新reset恢复到零高度；连续挖掘生成约2200个土粒，提斗和开斗可观察土粒运动。复位后散粒为零、仅一个活动土体，模板Mesh未被改写。Follow两路点回归和控制细节见工作区 `EvaluationReport/2026-09-27_soil_interaction_reset.md`。2026-09-28 的粗网格性能对照见 `EvaluationReport/2026-09-28_soil_performance.md`。

运行时需要与 `RLExcavator/` 同级的 `shared/machine_profile.json`。用 Unity 打开本场景并按 **Play**，然后在工作区根目录运行以下命令；客户端只使用 Python 标准库，无需激活 Conda 环境：

另有只读 `observe` 请求：携带当前 `epoch`，返回 `kind=observation`、当前原始状态和 `front_rgb={encoding:"png",width:640,height:480,data_base64:…}`。不推进物理步，图像与状态共享返回的仿真时间和步号。`FrontCamera` 随上车运动，按需通过 URP 渲染；主视图相机不变。EA 的 `observe` 命令负责复位后保存快照。主项目从同一原始状态构造仿真 ACT 11 维观测；仿真示教与推理共享该定义。

```bash
python3 RLExcavator/tools/sim_bridge_client.py --smoke
python3 RLExcavator/tools/sim_bridge_client.py --demo
python3 RLExcavator/tools/sim_bridge_client.py --axis boom
python3 RLExcavator/tools/sim_bridge_client.py --axis stick
python3 RLExcavator/tools/sim_bridge_client.py --axis bucket
python3 RLExcavator/tools/sim_bridge_client.py --manual-axis boom
python3 RLExcavator/tools/sim_bridge_client.py --manual-axis swing --value -0.75
```

`--smoke` 验证复位、命令和步进；`--demo` 在三秒后驱动上车回转，便于切到 Unity Game 窗口观察；`--axis` 分别驱动单个油缸。所有示例每次先复位。手工收发 JSON 时，不带参数运行客户端并向标准输入逐行写请求。服务仅监听 `127.0.0.1:18790`，一行请求对应一行响应。轴序固定 `[boom, stick, bucket, swing]`，前三轴速度单位为 m/s，回转速度单位为 rad/s；命令带当前 `epoch`、递增 `sequence` 和仿真时钟有效期。

`manual_action` 与速度命令使用相同的元数据，但 `values` 是四轴 `[-1, 1]` 归一化杆量；接收回执是 `manual_result`。仿真采用与 F407 相同的 `±0.15` 中位死区，死区以外线性映射到 `shared/machine_profile.json` 的正反向速度上限，再由现有 AGX 执行器驱动。这只是尚未标定液压阀/负载响应时的运动代理，**不代表**真机杆量对应的精确速度。两种命令共享一个目标，新的命令替换旧目标；无效、过期或断连时清零。`--manual-axis` 可直观看到各轴运动，`--value` 可选杆量。

新场景的三轴 AGX 滑动约束已设为**油缸伸长为正、收缩为负**，`actuator_state` 的位置/速度和 `velocity_reference` 都直接采用这个方向。关节角趋势与真机一致：伸长时 boom 角增大、arm 与 bucket 角减小。`manual_action` 保持真机杆量方向：boom、stick 的正杆量对应正物理速度，bucket 的正杆量对应负物理速度，swing 正杆量对应正回转速度。杆量到速度的近似映射仅在 Unity 执行器入口处理，主项目保留摇杆/ACT 原始动作。

2026-09-28 方向核对：直接调整本场景三轴左右共六根 Prismatic 约束的轴向，以及铲斗关节角所用 Hinge 的轴向；没有修改旧 prefab。三轴正、负 `velocity_reference` 和正、负 `manual_action` 共 12 组、回转 4 组实测均满足上述方向关系。Unity 约束两端的实际距离也随三轴正速度增加。四轴 EditMode 测试通过。旧 RL ONNX 是按调整前坐标训练的，重训和重新验收前不能用它证明 Follow 有效。

2026-09-26 最终验收：场景重载后，四轴分别位移 `+0.0150 m`、`+0.0160 m`、`+0.0160 m`、`+0.4800 rad`（各 80 个物理步）；连续复位的铲斗尖端坐标最大差小于 `0.00001 m`。错误轴序被拒绝，速度目标归零。场景副本中的上车装配已绕回转轴校正约 `8.65°`，复位时回转读数接近零。仿真脚本与 AGX 无报错；本机 Unity Editor 曾另报 `UnityEditor.Connect` 的 `services-gateway` 配置异常，与仿真调用栈无关。测试后点击 **Stop** 返回编辑状态。
