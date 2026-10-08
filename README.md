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

Unity 监听 `127.0.0.1:18790`。机器参数仍读取同级 `shared/machine_profile.json`。不要移动整个仓库来改变这个相对位置。

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
  AIModels/                      唯一活动 Follow ONNX 基线及冻结元数据
  Tests/EditMode/                当前运行和训练功能的测试
  AGXUnity/                      第三方物理插件
  Settings/                      Unity 渲染配置
config/                          正式 PPO 与短测配置
tools/                           协议检查、训练、导出、检查点筛查
docs/                            运行说明与基线/训练说明
```

`Packages/` 与 `ProjectSettings/` 管理 Unity 依赖和工程设置。卡车模型与挖机接触材料来自固定版本的官方 bedtruck / cat365 Package。`Library/`、`Temp/`、`Logs/`、`Builds/` 是 Unity 本机产物；`results/` 是训练数据，它们不作为源码提交。

## 阅读代码

1. `Scripts/Runtime/ExcavatorSimBridge.cs`：接收 `reset`、`velocity_reference`、`manual_action`、`step`、`observe`，返回状态。
2. `Scripts/Runtime/ExcavatorRuntimeActuators.cs`：把命令交给 AGX 约束，读取四轴状态。
3. `Scripts/Runtime/ExcavatorRuntimeSoil.cs`：复位土体和颗粒；`ExcavatorCameraCapture.cs` 采集图像。
4. `Scripts/Runtime/ExcavatorWallContacts.cs`：只读记录挖掘机与四面沙池墙的 AGX 接触；状态中的 `wall_contact` 自成功 reset 起累计，忽略颗粒、底板和卡车，不修改动作。字段定义见[设备契约](../earthmoving-autonomy/docs/follow-sim-contract.md)。
5. `Scripts/Runtime/ExcavatorFrameRegistry.cs`：本体坐标与关键部件；`RuntimeSceneCameraController.cs` 控制查看场景的镜头。

四轴顺序为 `[boom, stick, bucket, swing]`。速度输入前三轴为 m/s、回转为 rad/s；杆量输入为 `[-1, 1]`。RL 的归一化动作到物理速度转换在主项目完成，Unity 不运行主项目的技能编排。

训练控制器仍保留在 `Scripts/Training`，用于复现现有基线。运行场景使用 `ExcavatorRuntimeActuators`。旧 Unity 内任务循环、RRT 规划、E4 回放、Dig Agent、独立键盘测试器和旧 HUD 已退出当前项目。

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
- [本次清理及验收记录](../EvaluationReport/2026-10-05_unity_cleanup.md)

`docs/rl_excavator_master_execution_plan_2026-06-25.md` 保留为共享配置仍引用的历史契约来源；当前运行入口以本 README 和上述三份说明为准。基线 metadata 的历史路径与哈希不因目录整理而重写。
