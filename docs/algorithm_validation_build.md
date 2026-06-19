# 算法验证场景打包说明

本文档记录 `ExcavatorAlgorithmValidationScene` 的桌面演示程序打包方式。

## 目标

只打包演示闭环，不包含训练流程：

- Unity + AGX 物理仿真
- 演示 UI
- 轨迹规划与轨迹追踪
- ML-Agents ONNX 推理

训练用的 Python、TensorBoard、`mlagents-learn` 不参与最终 build。

## 构建入口

命令行构建入口：

```text
RLExcavator.Editor.AlgorithmValidationBuild.BuildLinux64
```

该入口会在构建前自动准备演示场景：

- 打开 `Assets/Scenes/ExcavatorAlgorithmValidationScene.unity`
- 只把该 scene 放入 build
- 将第一个 `BehaviorParameters` 设置为 `Inference Only`
- 绑定 `Assets/AIModels/ExcavatorTrajectoryCarryAndMove.onnx`
- 开启 `Deterministic Inference`
- 设置 Standalone scripting backend 为 `Mono`

## Linux 打包命令

```bash
cd /home/zhaoshuai/workspace_uinty/RL_prj/RLExcavator

/home/zhaoshuai/Unity/Hub/Editor/6000.3.16f1/Editor/Unity \
  -batchmode \
  -nographics \
  -quit \
  -projectPath /home/zhaoshuai/workspace_uinty/RL_prj/RLExcavator \
  -executeMethod RLExcavator.Editor.AlgorithmValidationBuild.BuildLinux64 \
  -outputPath Builds/ExcavatorAlgorithmValidation/ExcavatorAlgorithmValidation.x86_64 \
  -logFile Logs/algorithm_validation_build.log
```

## 运行

```bash
cd /home/zhaoshuai/workspace_uinty/RL_prj/RLExcavator
chmod +x Builds/ExcavatorAlgorithmValidation/ExcavatorAlgorithmValidation.x86_64
./Builds/ExcavatorAlgorithmValidation/ExcavatorAlgorithmValidation.x86_64
```

如果需要在终端直接查看 Unity console 日志：

```bash
./Builds/ExcavatorAlgorithmValidation/ExcavatorAlgorithmValidation.x86_64 -logFile -
```

如果不指定 `-logFile -`，Linux player 日志通常写入：

```text
~/.config/unity3d/DefaultCompany/ExcavatorAlgorithmValidation/Player.log
```

## AGX license

当前工程中有：

```text
Assets/agx.lfx
```

AGX 文档说明，build 运行时会从 build 目录作为根目录搜索 `*.lfx`。

本机演示时，如果 build 能正常找到许可证即可运行。若要发到其他机器，需要使用 AGX 的 Runtime Activation Generator 生成 `agx.rtlfx`，并在目标机器首次运行时完成 runtime activation。

## 注意事项

- 不要用 WebGL 作为当前目标，AGX 依赖原生 x86_64 动态库。
- 不要切换到 IL2CPP，AGXUnity 当前构建要求 Mono scripting backend。
- 演示场景只保留一个环境，不要把训练用多环境 scene 打包进去。
- 如果 build 后不能推理，先检查 `Behavior Parameters` 是否为 `Inference Only`，以及 ONNX 是否正确绑定。
- 如果 build 后 AGX 报 native library 或 license 错误，先检查 build 目录中的 AGX native plugins 和 license 文件。
- 如果轨迹点在 Editor 可见但 build 中只显示影子，优先检查 `TargetPointVisual` 的材质和 shader。当前实现会显式创建 URP/Unlit 优先的 runtime 材质，并关闭轨迹点投影，避免默认 primitive 材质在 build 中出现只走 shadow pass 的情况。
