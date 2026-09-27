# ExcavatorRuntime 仿真 v1

`Assets/Scenes/ExcavatorRuntime.unity` 是独立的 AGX 挖掘机运行场景；原训练场景和原始 prefab 保持不变。本版提供 `reset`、四轴物理速度 `velocity_reference`、四轴归一化杆量 `manual_action` 和显式物理步进 `step`，返回执行器状态及铲斗尖端相对 `MachineRoot` 的位姿。它是后续接入 Follow/ACT 的仿真后端，尚未包含策略推理、挖土或训练任务。

运行时需要与 `RLExcavator/` 同级的 `shared/machine_profile.json`。用 Unity 打开本场景并按 **Play**，然后在工作区根目录运行以下命令；客户端只使用 Python 标准库，无需激活 Conda 环境：

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

杆量路径已在隔离的 Unity 场景副本中通过实际 AGX 步进验证：boom 输入 `+0.75` 后正向运动；原物理速度命令仍有效；`1.1` 杆量被拒绝。EditMode 单测验证了死区和正反向速度尺度。正式场景与旧 prefab 未修改。

2026-09-26 最终验收：场景重载后，四轴分别位移 `+0.0150 m`、`+0.0160 m`、`+0.0160 m`、`+0.4800 rad`（各 80 个物理步）；连续复位的铲斗尖端坐标最大差小于 `0.00001 m`。错误轴序被拒绝，速度目标归零。场景副本中的上车装配已绕回转轴校正约 `8.65°`，复位时回转读数接近零。仿真脚本与 AGX 无报错；本机 Unity Editor 曾另报 `UnityEditor.Connect` 的 `services-gateway` 配置异常，与仿真调用栈无关。测试后点击 **Stop** 返回编辑状态。
