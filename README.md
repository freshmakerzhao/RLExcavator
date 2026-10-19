# RLExcavator — Earthmoving Autonomy 仿真端

Unity / AGX 负责挖掘机本体、土体、卡车碰撞、相机和物理步进。技能、策略推理与任务编排在同级 `earthmoving-autonomy` 中；本仓库另外保留训练当前 RL Follow 基线所需的代码。

## 日常运行

用 Unity **6000.3.16f1** 打开项目，打开 `Assets/Scenes/ExcavatorRuntime.unity`，按 Play。在主项目目录执行：

```bash
conda activate earthmoving-autonomy
python -m earthmoving_autonomy follow
# 或使用摇杆：
python -m earthmoving_autonomy teleop
```

Unity 监听 `127.0.0.1:18790`。机器参数仍读取同级 `shared/machine_profile.jsonc`。不要移动整个仓库来改变这个相对位置。
`MachineProfileLoader` 使用项目已有的 Newtonsoft.Json 读取 JSONC，支持 `//` 和 `/* */` 注释；可达工作空间仍是 Unity 导出的标准 JSON。

运行场景的 `MachineRoot` 位于**回转轴线与履带支撑平面的交点**，固定到 `Body_Track`，ROS 轴向为前/左/上，原点身份 `swing_ground_chassis_v1`。在 Hierarchy 搜索 `MachineRoot` 即可查看；调整坐标不移动物理模型。训练场景使用同一定义，当前 Follow 已切换到新原点模型 `ScaleFollowGroundOrigin_20261014.onnx`，Python 模型入口平移为零；旧模型保留用于成对回退。定义见 [ADR-0023](../docs/adr/0023-swing-axis-runtime-origin.md)，模型与运行验证见[本轮验收](../EvaluationReport/2026-10-14_rl_ai_acceptance/README.md)。

在 **Scene 视图打开 Gizmos**，即可看到支撑平面上正式原点的黄色球/圆环与 ROS 坐标轴：红 X 向前、绿 Y 向左、蓝 Z 向上；此前青色候选点不再单独保留。无需选中物体，Play 时也可见，标记可透过机体显示。`MachineRootGizmo` 只在编辑器 Scene 相机绘制，不进入 Game/ACT 相机图像、雷达点云或物理碰撞；关闭 Scene 的 Gizmos 可隐藏。

### AGX 版本与启动环境

当前固定为 **AGXUnity 5.6.1+22bd584a / AGX Dynamics 2.42.2.1**（Ubuntu 24.04 完整包），Unity 版本保持 6000.3.16f1。两者随同一官方包更新；不要混用系统中其他版本的 AGX 动态库或资源路径。

若终端加载过独立 AGX SDK，可从工作区根目录用干净的进程环境打开编辑器；此命令不会改系统环境：

```bash
env -u AGX_DIR -u AGX_PLUGIN_PATH -u AGX_FILE_PATH -u AGX_DEPENDENCIES_DIR -u LD_LIBRARY_PATH \
  "$HOME/Unity/Hub/Editor/6000.3.16f1/Editor/Unity" -projectPath "$PWD/RLExcavator"
```

固定料池 `SoilTerrain` 的 `EnableDynamicMassUpdates` 和 `RenderSides` 保持关闭：前者需要父刚体，后者会新增侧面网格。当前料池无父刚体，并沿用 ACT 采集时的平面土体网格。不要为修复新版本默认值去改速度、模型或奖励。

本地未纳入 Git 的 WheelLoaderTerrain 示例需使用 5.6.1 官方 `Machine Resetter.cs`；旧脚本中四种重定位类型已从 `agxUtil` 移到 `agxModel`，旧版会阻塞整个项目编译。该本地示例独立于当前挖掘闭环。

升级证据、原始日志和前后闭环结果见 `../EvaluationReport/2026-10-10_agx_upgrade/`。

## 目录

```text
Assets/
  Scenes/
    ExcavatorRuntime.unity        设备仿真：挖机、土箱、卡车与相机
    Training/                    完整 Follow 与短路径预训练场景
  Scripts/
    Runtime/                     执行器、土体、协议、相机与本体坐标
    Training/                    PPO 环境、课程、奖励、采样与训练控制器
    Config/                      读取共享机器参数和可达空间
    Editor/                      训练构建与可达空间编辑
  Prefabs/                       缩比挖机与训练环境
  Models/                        挖机 CAD/AGX 源模型及导入资产
  Environment/                   当前场地、土体、物理材料、天空
  Materials/                     挖机与地面外观
  AIModels/                      活动 Follow ONNX、历史基线及冻结元数据
  Tests/EditMode/                当前运行和训练功能的测试
  AGXUnity/                      第三方物理插件
  Settings/                      Unity 渲染配置
config/                          正式 PPO 与短测配置
tools/                           协议检查、训练、导出、检查点筛查
docs/                            运行说明与基线/训练说明
```

`Packages/` 与 `ProjectSettings/` 管理 Unity 依赖和工程设置。卡车模型与挖机接触材料来自固定版本的官方 bedtruck / cat365 Package。`Library/`、`Temp/`、`Logs/`、`Builds/` 是 Unity 本机产物；`results/` 是训练数据，它们不作为源码提交。

## 阅读代码

1. `Scripts/Runtime/ExcavatorSimBridge.cs`：接收 `reset`、`velocity_reference`、`manual_action`、`step`、`observe`、`read_effect`、`read_terrain`、`configure_lidar`、`read_lidar`，返回状态。
2. `Scripts/Runtime/ExcavatorRuntimeActuators.cs`：把命令交给 AGX 约束，读取四轴状态。
3. `Scripts/Runtime/ExcavatorRuntimeSoil.cs`：复位土体和颗粒；`ExcavatorCameraCapture.cs` 采集图像。
4. `Scripts/Runtime/ExcavatorWallContacts.cs`：只读记录挖掘机与四面沙池墙的 AGX 接触；状态中的 `wall_contact` 自成功 reset 起累计，忽略颗粒、底板和卡车，不修改动作。字段定义见[设备契约](../earthmoving-autonomy/docs/follow-sim-contract.md)。
5. `Scripts/Runtime/ExcavatorFrameRegistry.cs`：本体坐标与关键部件；`RuntimeSceneCameraController.cs` 控制查看场景的镜头。
6. `Scripts/Runtime/ExcavatorTerrainTelemetry.cs`：在停止交接处按需读取活动土表及最低可挖层，输出机器 ROS 米制网格和独立有效性掩码；不推进物理、不采集图像，不包含独立土粒。协议见[设备契约第 8 节](../earthmoving-autonomy/docs/follow-sim-contract.md#8-按需地形观测开发契约2026-10-12)。

四轴顺序为 `[boom, stick, bucket, swing]`。速度输入前三轴为 m/s、回转为 rad/s；杆量输入为 `[-1, 1]`。RL 的归一化动作到物理速度转换在主项目完成，Unity 不运行主项目的技能编排。

训练控制器仍保留在 `Scripts/Training`，用于复现现有基线。运行场景使用 `ExcavatorRuntimeActuators`。旧 Unity 内任务循环、RRT 规划、E4 回放、Dig Agent、独立键盘测试器和旧 HUD 已退出当前项目。

## 仿真雷达

运行场景采用 **左右两颗竖直朝下的 Airy 视场代理＋大臂下缘 E1R 视场代理**，为 2026-10-13 用户确认的固定布置。Airy 随上车转动，E1R 还随大臂俯仰。Hierarchy 搜索 `Lidar`，选中后按 F 定位；蓝色为 `LidarAiryRight`，绿色为 `LidarAiryLeft`，橙色为 `LidarE1RFront`。这些实体和支架仅用于示意，不参与碰撞或光线遮挡，也不是厂家外形模型。它们位于 `SensorIllustration` 层，从 ACT 前相机排除并关闭投影，避免示意几何改变冻结策略的视觉输入；真机外壳与相机的安装间距仍须单独验证。

| 节点 | 初始 Unity 世界坐标（m） | 安装与扫描 |
|---|---|---|
| LidarAiryRight | (0.215, 1.01, 0.10) | 右侧外缘，半球中心轴竖直向下 |
| LidarAiryLeft | (-0.215, 1.01, 0.10) | 左侧外缘，半球向下 |
| LidarE1RFront | 约 (0, 1.07006, 0.56996) | `Body_Boom` 中段下缘，初始中心视线向前下俯约 59.1°；随大臂运动 |

AGX 当前未提供这两个型号的预设，使用 `GenericHorizontalSweep` 验证视场和场景遮挡：Airy 水平 360°、倒装前原生垂直 0～90°、360×48 射线/周期、0.1～60 m；E1R 水平 120°、垂直 90°、192×135 射线/周期、0.1～30 m。均为 10 Hz，累计 0.2 s 后提供完整窗口。**Airy 射线密度被简化，E1R 使用扫描式代理，未复现真实 Flash 时序、测距噪声或精度。** 不能据此直接确定真机安装效果。

当前参数、重新采集的安装图/三台点云/处理阶段图和验收数据集中在[固定雷达布置记录](../EvaluationReport/2026-10-13_fixed_lidar_layout/README.md)。此前的[右 Airy 倾斜试验](../EvaluationReport/2026-10-13_airy_side_aim/README.md)和[大臂安装试验](../EvaluationReport/2026-10-13_boom_lidar/README.md)仅保留为历史对照，其中倾斜右 Airy 的覆盖数字不代表当前固定布置。

两台 Airy 位于 `Body_Swing` 下，E1R 位于 `Body_Boom` 下，每台使用 `AGXUnity.Sensor.LidarSensor` 和 `ExcavatorLidarTelemetry`。桥接器 `lidarSensors` 绑定三台不同传感器、共用机器根；遥测按采样物理步的传感器变换将点转到机器根坐标，不能将 E1R 相对机器根的外参当常量。`read_lidar` 返回 `lidar_scan_set_v1`，保留每台的 `scan_model`、点、位置和时间窗；Python 校验共同窗口后合并，缺一台时整体不可用。只读不再次扫描；reset 清空三台缓存。

每次 reset 后，Python 可发送 `{"kind":"configure_lidar","epoch":当前回合,"enabled":true或false}`。该配置不推进物理、不读取 RGB、不消耗动作序号；关闭会禁用三台 AGX `LidarSensor` 的原生射线扫描并清空缓存，`read_lidar` 明确返回 `lidar_disabled_by_configuration`。重新开启也清空旧缓存，需正常推进物理形成新的完整 0.2 s 窗口；reset 本身只清缓存，不更改开关。请求必须包含真正的 JSON 布尔值，并完整绑定当前三台雷达。

AGX SensorEnvironment 要求 `Simulation.PreIntegratePositions=true`，已写入运行场景。雷达未加入 RL/ACT 策略输入，RL 训练场景不启用这套雷达。主项目运行 `python -m earthmoving_autonomy lidar` 可生成高程图与真值对照；命令会 reset，不能与遥操作或循环任务同时使用。详细契约见[第 9 节](../earthmoving-autonomy/docs/follow-sim-contract.md#9-仿真-lidar-只读扫描2026-10-12)，安装覆盖及回归见[三雷达验证记录](../EvaluationReport/2026-10-12_airy_e1r_layout/README.md)。尚未实现料池/卡车定位、车厢装满判断或人员识别。

## 检查与训练

Unity Play 后，从本仓库目录检查设备协议：

```bash
python tools/sim_bridge_client.py --smoke
python tools/sim_bridge_client.py --manual-axis boom
python -m unittest discover -s tools/tests -v
```

Unity Test Runner 的 EditMode 覆盖当前协议、杆量、机器参数、训练课程、奖励、可达空间和构建接线。清理后还应实际运行 Follow 验证物理闭环。

- [仿真场景和接口](docs/excavator_runtime_v1.md)
- [RL Follow 基线](docs/rl_baseline_v1.md)
- [训练环境、分段训练与导出](docs/mlagents_training_environment.md)
- [V4 扩大连续路径课程、采样范围与训练记录](../EvaluationReport/2026-10-19_continuous_path_rl_v4/README.md)：独立 Expanded 场景与 player，从 V3 权重继续训练。
- [本次清理及验收记录](../EvaluationReport/2026-10-05_unity_cleanup.md)

`docs/rl_excavator_master_execution_plan_2026-06-25.md` 保留为共享配置仍引用的历史契约来源；当前运行入口以本 README 和上述三份说明为准。基线 metadata 的历史路径与哈希不因目录整理而重写。
